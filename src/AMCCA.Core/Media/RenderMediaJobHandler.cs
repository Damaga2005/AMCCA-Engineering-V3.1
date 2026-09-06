using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Jobs;

namespace AMCCA.Core.Media;

/// <summary>
/// Executes a <c>RENDER</c> job (SPEC/33): builds the ffmpeg command from the job payload, runs it,
/// and stores the output as the production's CURRENT <c>RENDER</c> artifact. ffmpeg exiting non-zero,
/// timing out, or producing no file all fail the job (which then requeues / dead-letters via the
/// worker pool). Payload:
/// <code>
/// { "input_path": "...", "profile": { ...MediaProfile snake_case... },
///   "disclosure": { "has_synthetic_visuals": true, ... } | null, "max_duration_ms": 60000 }
/// </code>
/// </summary>
public sealed class RenderMediaJobHandler : IJobHandler
{
    // ponytail: fixed render timeout; move to the payload or a media config block when one exists.
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(20);

    private readonly ArtifactStore _artifacts;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly string _dataRoot;

    public RenderMediaJobHandler(ArtifactStore artifacts, IFfmpegRunner ffmpeg, string dataRoot)
    {
        _artifacts = artifacts;
        _ffmpeg = ffmpeg;
        _dataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Path.GetTempPath(), "amcca-artifacts")
            : Path.GetFullPath(dataRoot);
    }

    public async Task<JobResult> HandleAsync(JobExecutionContext context, CancellationToken ct = default)
    {
        var productionId = context.Job.ProductionId;
        if (string.IsNullOrEmpty(productionId))
        {
            return JobResult.Failure("RENDER job has no production_id.");
        }

        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(context.Job.PayloadJson).RootElement;
        }
        catch (JsonException ex)
        {
            return JobResult.Failure($"RENDER payload is not valid JSON: {ex.Message}");
        }

        var inputPath = Str(payload, "input_path");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return JobResult.Failure("RENDER payload needs 'input_path'.");
        }

        var inputAbs = Path.IsPathRooted(inputPath) ? inputPath : Path.Combine(_dataRoot, inputPath);
        if (!File.Exists(inputAbs))
        {
            return JobResult.Failure($"RENDER input not found: {inputAbs}");
        }

        if (!payload.TryGetProperty("profile", out var profileEl) || profileEl.ValueKind != JsonValueKind.Object)
        {
            return JobResult.Failure("RENDER payload needs a 'profile' object.");
        }

        MediaProfile profile;
        try
        {
            profile = ParseProfile(profileEl);
        }
        catch (Exception ex)
        {
            return JobResult.Failure($"RENDER profile is incomplete: {ex.Message}");
        }

        SyntheticDisclosure? disclosure = null;
        if (payload.TryGetProperty("disclosure", out var dEl) && dEl.ValueKind == JsonValueKind.Object)
        {
            disclosure = new SyntheticDisclosure(
                HasSyntheticVisuals: Bool(dEl, "has_synthetic_visuals"),
                HasSyntheticAudio: Bool(dEl, "has_synthetic_audio"),
                GeneratorModelId: Str(dEl, "generator_model_id") ?? "",
                DisclosureText: Str(dEl, "disclosure_text") ?? "");
        }

        long? maxDurationMs = payload.TryGetProperty("max_duration_ms", out var md) && md.ValueKind == JsonValueKind.Number
            ? md.GetInt64() : null;

        var outputAbs = Path.Combine(_dataRoot, "renders", productionId, $"candidate-{context.Job.Id}.{profile.Container}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputAbs)!);

        var args = MediaRenderer.BuildFfmpegArguments(inputAbs, outputAbs, profile, disclosure, maxDurationMs);
        var result = await _ffmpeg.RunAsync(args, _dataRoot, RenderTimeout, ct);

        if (result.TimedOut)
        {
            return JobResult.Failure($"ffmpeg render exceeded {RenderTimeout.TotalMinutes:0} min.");
        }
        if (result.ExitCode != 0)
        {
            return JobResult.Failure($"ffmpeg exited {result.ExitCode}: {Trim(result.StdErrTail)}");
        }
        if (!File.Exists(outputAbs) || new FileInfo(outputAbs).Length == 0)
        {
            return JobResult.Failure("ffmpeg exited 0 but produced no output file.");
        }

        var versionId = await _artifacts.PutExistingFileVersionAsync(
            productionId, "RENDER", outputAbs, profile.Container, generatorModelId: profile.ProfileId, ct: ct);
        return JobResult.Success($"rendered {new FileInfo(outputAbs).Length} bytes -> artifact_version {versionId}");
    }

    private static MediaProfile ParseProfile(JsonElement e) => new(
        ProfileId: Str(e, "profile_id") ?? throw new InvalidOperationException("profile_id"),
        Version: Str(e, "version") ?? "1.0",
        Container: Str(e, "container") ?? "mp4",
        VideoCodec: Str(e, "video_codec") ?? "libx264",
        AudioCodec: Str(e, "audio_codec") ?? "aac",
        Width: Int(e, "width"),
        Height: Int(e, "height"),
        Fps: Int(e, "fps"),
        BitrateKbps: Int(e, "bitrate_kbps"),
        LoudnessTargetLufs: e.TryGetProperty("loudness_target_lufs", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : -14.0,
        SourceRef: Str(e, "source_ref") ?? "",
        RetrievedAt: Str(e, "retrieved_at") ?? "");

    private static string? Str(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static bool Bool(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    private static string Trim(string s) => s.Length <= 500 ? s : s[^500..];
}
