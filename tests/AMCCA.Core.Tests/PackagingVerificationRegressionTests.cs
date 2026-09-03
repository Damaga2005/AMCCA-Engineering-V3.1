using System;
using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class PackagingVerificationRegressionTests
{
    [Fact]
    public void DEF021_PackagingConfiguration_ConformsToSpec76()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var appCsprojPath = Path.Combine(repoRoot, "src", "AMCCA.App", "AMCCA.App.csproj");
        var coreCsprojPath = Path.Combine(repoRoot, "src", "AMCCA.Core", "AMCCA.Core.csproj");

        File.Exists(appCsprojPath).Should().BeTrue("AMCCA.App.csproj must exist");
        File.Exists(coreCsprojPath).Should().BeTrue("AMCCA.Core.csproj must exist");

        // 1. Check AMCCA.App project properties
        var appXml = XDocument.Load(appCsprojPath);
        var outputType = appXml.Root?.Element("PropertyGroup")?.Element("OutputType")?.Value;
        outputType.Should().Be("Exe", "AMCCA.App must have OutputType=Exe to produce AMCCA.exe (SPEC/76)");

        var targetFramework = appXml.Root?.Element("PropertyGroup")?.Element("TargetFramework")?.Value;
        targetFramework.Should().Be("net8.0", "TargetFramework must be net8.0 LTS (SPEC/76, D-002)");

        var assemblyName = appXml.Root?.Element("PropertyGroup")?.Element("AssemblyName")?.Value;
        assemblyName.Should().Be("AMCCA", "AssemblyName must produce AMCCA.exe binary (SPEC/76)");

        // 2. SPEC/76 Section 21: FFmpeg is NOT bundled in binary distribution
        var appContent = File.ReadAllText(appCsprojPath);
        var coreContent = File.ReadAllText(coreCsprojPath);
        appContent.Should().NotContain("ffmpeg", "FFmpeg is an external preflight requirement and MUST NOT be bundled (SPEC/76, S-08)");
        coreContent.Should().NotContain("ffmpeg.exe", "FFmpeg binaries must not be committed or embedded into DLLs");
    }
}
