using System;
using System.Collections.Generic;
using System.IO;
using AMCCA.Core.Contracts;
using AMCCA.Core.Media;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class MediaPipelineAndDisclosureContractTests
{
    private readonly string _dataRoot;

    public MediaPipelineAndDisclosureContractTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "AMCCA_DATA_ROOT_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
    }

    [Fact]
    public void IdenticalInputs_ProduceIdenticalMediaHashes()
    {
        // Exit criterion: "Identical inputs produce identical media hashes" (D-002, SPEC/33)
        var profile = new MediaProfile(
            ProfileId: "youtube-shorts",
            Version: "1.0",
            Container: "mp4",
            VideoCodec: "h264",
            AudioCodec: "aac",
            Width: 1080,
            Height: 1920,
            Fps: 30,
            BitrateKbps: 4500,
            LoudnessTargetLufs: -14.0,
            SourceRef: "https://support.google.com/youtube/answer/6373554",
            RetrievedAt: "2026-01-15T00:00:00Z");

        var track = new TimelineTrack("v1", "video", new List<TimelineItem>
        {
            new("asset-1", StartMs: 0, DurationMs: 5000, ContentHash: "hash-asset-1"),
            new("asset-2", StartMs: 5000, DurationMs: 10000, ContentHash: "hash-asset-2")
        });

        var timeline = new TimelineDefinition("prod-1", DurationMs: 15000, new List<TimelineTrack> { track });

        var disclosure = new SyntheticDisclosure(
            HasSyntheticVisuals: true,
            HasSyntheticAudio: true,
            GeneratorModelId: "flux-schnell",
            DisclosureText: "AI generated content");

        var renderer = new MediaRenderer(_dataRoot);

        // Two independent render calculations with identical inputs
        var hash1 = renderer.ComputeDeterministicMediaHash(timeline, profile, disclosure, randomSeed: 42);
        var hash2 = renderer.ComputeDeterministicMediaHash(timeline, profile, disclosure, randomSeed: 42);

        hash1.Should().NotBeNullOrWhiteSpace();
        hash1.Should().Be(hash2, "identical inputs must produce identical media hashes (D-002)");
    }

    [Fact]
    public void AlteredInput_ProducesDifferentMediaHash()
    {
        var profile = new MediaProfile("yt-shorts", "1.0", "mp4", "h264", "aac", 1080, 1920, 30, 4500, -14.0, "ref", "2026-01-01T00:00:00Z");
        var disclosure = new SyntheticDisclosure(true, false, "flux-schnell", "AI generated");
        var renderer = new MediaRenderer(_dataRoot);

        var timeline1 = new TimelineDefinition("prod-1", 5000, new List<TimelineTrack>
        {
            new("v1", "video", new List<TimelineItem> { new("asset-1", 0, 5000, "hash-original") })
        });

        var timeline2 = new TimelineDefinition("prod-1", 5000, new List<TimelineTrack>
        {
            new("v1", "video", new List<TimelineItem> { new("asset-1", 0, 5000, "hash-MODIFIED") })
        });

        var hash1 = renderer.ComputeDeterministicMediaHash(timeline1, profile, disclosure, randomSeed: 42);
        var hash2 = renderer.ComputeDeterministicMediaHash(timeline2, profile, disclosure, randomSeed: 42);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void FfmpegInvocation_UsesArgumentListWithoutShellConcatenation()
    {
        // SPEC/33: "FFmpeg is invoked through ProcessStartInfo with an argument list. String concatenation into a shell is forbidden (D-008)"
        var profile = new MediaProfile("yt", "1.0", "mp4", "libx264", "aac", 1080, 1920, 30, 4500, -14.0, "ref", "2026-01-01T00:00:00Z");
        var renderer = new MediaRenderer(_dataRoot);

        var startInfo = renderer.BuildFfmpegProcessStartInfo("input.mp4", "output.mp4", profile);

        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.Arguments.Should().BeEmpty("must not use string concatenation in Arguments; must use ArgumentList");
        startInfo.ArgumentList.Should().Contain("-y");
        startInfo.ArgumentList.Should().Contain("input.mp4");
        startInfo.ArgumentList.Should().Contain("output.mp4");
    }

    [Fact]
    public void InputPath_OutsideDataRoot_IsRejectedByPathConfinement()
    {
        // SPEC/33: "working directory confined beneath data_root. Input paths are canonicalised and validated"
        var renderer = new MediaRenderer(_dataRoot);
        var outsidePath = @"C:\Windows\System32\cmd.exe";

        var act = () => renderer.ValidatePathConfinement(outsidePath);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec001);
    }

    [Fact]
    public void SyntheticDisclosure_GeneratesC2paManifestAssertions()
    {
        var disclosure = new SyntheticDisclosure(
            HasSyntheticVisuals: true,
            HasSyntheticAudio: true,
            GeneratorModelId: "flux-schnell",
            DisclosureText: "AI generated content");

        var manifest = disclosure.GenerateC2paManifest("prod-123");

        manifest.Should().Contain("c2pa.actions");
        manifest.Should().Contain("c2pa.created");
        manifest.Should().Contain("flux-schnell");
        manifest.Should().Contain("AI generated content");
    }
}
