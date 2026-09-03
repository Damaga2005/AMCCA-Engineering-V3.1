using System;
using System.IO;
using System.Net;
using AMCCA.Core.Contracts;
using AMCCA.Core.Media;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class SecurityHardeningSpec72ContractTests
{
    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://127.0.0.2:8080")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.1.50/internal")]
    [InlineData("http://172.16.0.1/status")]
    [InlineData("http://192.168.1.1/router")]
    [InlineData("http://[::1]/")]
    [InlineData("http://localhost/api")]
    public void S06_ResearchFetch_TargetingPrivateOrMetadataRanges_IsRejectedWithSec003(string url)
    {
        // SPEC/72 S-06: "Research fetch targeting 127.0.0.1, 169.254.0.0/16, 10.0.0.0/8, ::1 -> Rejected with AMCCA-SEC-003"
        var uri = new Uri(url);

        var act = () => SsrfValidator.ValidateDestinationUri(uri);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec003);
    }

    [Theory]
    [InlineData("https://www.google.com")]
    [InlineData("https://en.wikipedia.org/wiki/Main_Page")]
    [InlineData("https://api.github.com")]
    public void S06_PublicDomains_PassSsrfValidation(string url)
    {
        var uri = new Uri(url);

        var act = () => SsrfValidator.ValidateDestinationUri(uri);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("../evil.sh")]
    [InlineData("sub/../../etc/passwd")]
    [InlineData("/absolute/path/file.txt")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void S10_ArchiveEntry_WithTraversalPath_IsRejectedWithSec004(string entryPath)
    {
        // SPEC/72 S-10: "Archive with traversal paths, excessive entry count, excessive uncompressed size -> Rejected with AMCCA-SEC-004"
        var act = () => SafeArchiveExtractor.ValidateEntryPath(entryPath, targetDirectory: @"C:\safe\dir");

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec004);
    }

    [Fact]
    public void S11_ArtifactPath_ContainingTraversal_IsRejected()
    {
        // SPEC/72 S-11: "Artifact path containing traversal sequences -> Rejected; write confined to data_root"
        var safeRoot = Path.Combine(Path.GetTempPath(), "safe_root");
        Directory.CreateDirectory(safeRoot);

        var renderer = new MediaRenderer(safeRoot);
        var outsideTarget = Path.Combine(safeRoot, "../outside_file.txt");

        var act = () => renderer.ValidatePathConfinement(outsideTarget);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec001);
    }

    [Fact]
    public void S12_FfmpegArgument_WithShellMetacharacters_IsKeptLiteralInArgumentList()
    {
        // SPEC/72 S-12: "FFmpeg argument containing shell metacharacters -> Passed as a literal argument; no shell interpretation"
        var renderer = new MediaRenderer(Path.GetTempPath());
        var profile = new MediaProfile("yt", "1.0", "mp4", "h264", "aac", 1080, 1920, 30, 4500, -14.0, "ref", "2026-01-01T00:00:00Z");

        var maliciousInput = "input; rm -rf /; $(calc.exe).mp4";
        var startInfo = renderer.BuildFfmpegProcessStartInfo(maliciousInput, "output.mp4", profile);

        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.Arguments.Should().BeEmpty();
        startInfo.ArgumentList.Should().Contain(maliciousInput, "the entire malicious string must remain a single, unescaped literal argument without shell execution");
    }

    [Fact]
    public void S17_CompiledSqlSurface_ContainsNoUpdateOrDeleteAgainstEventsTable()
    {
        // SPEC/72 S-17: "Scan the compiled SQL surface for UPDATE/DELETE against events -> None found"
        var violations = SqlSurfaceAuditor.FindViolationsInAssembly(typeof(AmccaErrors).Assembly);

        violations.Should().BeEmpty("the events table is strictly append-only; no UPDATE or DELETE statements are permitted in the entire codebase (S-17)");
    }
}
