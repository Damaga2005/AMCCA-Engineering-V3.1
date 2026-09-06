using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Media;

public class MediaRenderer
{
    private readonly string _dataRoot;

    public MediaRenderer(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public string ComputeDeterministicMediaHash(
        TimelineDefinition timeline,
        MediaProfile profile,
        SyntheticDisclosure disclosure,
        int randomSeed)
    {
        // Canonical payload for deterministic fingerprinting (D-002, SPEC/33)
        var canonicalData = new
        {
            production_id = timeline.ProductionId,
            duration_ms = timeline.DurationMs,
            tracks = timeline.Tracks,
            profile = new
            {
                id = profile.ProfileId,
                vcodec = profile.VideoCodec,
                acodec = profile.AudioCodec,
                w = profile.Width,
                h = profile.Height,
                fps = profile.Fps,
                bitrate = profile.BitrateKbps,
                loudness = profile.LoudnessTargetLufs
            },
            disclosure = new
            {
                synthetic_v = disclosure.HasSyntheticVisuals,
                synthetic_a = disclosure.HasSyntheticAudio,
                model = disclosure.GeneratorModelId,
                text = disclosure.DisclosureText
            },
            seed = randomSeed
        };

        var json = JsonSerializer.Serialize(canonicalData);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// The full ffmpeg argument list for a candidate render (SPEC/33): scale+pad to the profile
    /// resolution, EBU R128 loudness normalisation to the profile target, an optional synthetic-
    /// disclosure caption burnt into the bottom of frame, the profile's codecs/bitrate/fps, and an
    /// optional hard duration cap. stdin is closed (<c>-nostdin</c>).
    /// </summary>
    public static IReadOnlyList<string> BuildFfmpegArguments(
        string inputPath,
        string outputPath,
        MediaProfile profile,
        SyntheticDisclosure? disclosure = null,
        long? maxDurationMs = null)
    {
        var vf = $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease," +
                 $"pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2";

        if (disclosure is { } d && (d.HasSyntheticVisuals || d.HasSyntheticAudio) && !string.IsNullOrWhiteSpace(d.DisclosureText))
        {
            var text = d.DisclosureText.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'");
            vf += $",drawtext=text='{text}':x=(w-tw)/2:y=h-(2*lh):fontcolor=white:fontsize=24:box=1:boxcolor=black@0.5:boxborderw=8";
        }

        var args = new List<string>
        {
            "-nostdin", "-y",
            "-i", inputPath,
            "-vf", vf,
            "-af", $"loudnorm=I={profile.LoudnessTargetLufs.ToString(System.Globalization.CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11",
            "-c:v", profile.VideoCodec,
            "-b:v", $"{profile.BitrateKbps}k",
            "-r", profile.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", profile.AudioCodec,
        };

        if (maxDurationMs is > 0)
        {
            args.Add("-t");
            args.Add((maxDurationMs.Value / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add(outputPath);
        return args;
    }

    public ProcessStartInfo BuildFfmpegProcessStartInfo(
        string inputPath,
        string outputPath,
        MediaProfile profile)
    {
        // SPEC/33: "FFmpeg is invoked through ProcessStartInfo with an argument list. String concatenation into a shell is forbidden (D-008)"
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _dataRoot
        };

        // Populate ArgumentList directly
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add(profile.VideoCodec);
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add(profile.AudioCodec);
        startInfo.ArgumentList.Add("-b:v");
        startInfo.ArgumentList.Add($"{profile.BitrateKbps}k");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(profile.Fps.ToString());
        startInfo.ArgumentList.Add(outputPath);

        return startInfo;
    }

    public void ValidatePathConfinement(string candidatePath)
    {
        // SEC-09: reject a path that escapes the data root textually or via a symlink/junction.
        AMCCA.Core.Security.PathConfinement.EnsureConfinedNoReparsePoint(candidatePath, _dataRoot, AmccaErrors.Sec001);
    }
}
