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
