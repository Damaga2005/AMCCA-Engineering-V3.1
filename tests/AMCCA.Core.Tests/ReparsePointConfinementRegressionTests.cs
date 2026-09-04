using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using AMCCA.Core.Contracts;
using AMCCA.Core.Media;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// SEC-09 — path confinement must also reject a path that is only "inside" the root because it
/// passes through a Windows reparse point (junction / symlink / mount). The detector is unit
/// tested unconditionally; the end-to-end cases use a directory junction, which Windows allows
/// without elevation, and are skipped (with the limitation stated) if even that is unavailable.
/// </summary>
public class ReparsePointConfinementRegressionTests : IDisposable
{
    private readonly string _root;

    public ReparsePointConfinementRegressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AMCCA_SEC09_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0 && (Directory.Exists(linkPath) || File.Exists(linkPath));
        }
        catch
        {
            return false;
        }
    }

    // ---- detector unit tests (always run) --------------------------------------------

    [Fact]
    public void IsReparsePoint_PlainDirectory_IsFalse()
    {
        var dir = Path.Combine(_root, "plain");
        Directory.CreateDirectory(dir);
        PathConfinement.IsReparsePoint(dir).Should().BeFalse();
    }

    [Fact]
    public void IsReparsePoint_PlainFile_IsFalse()
    {
        var file = Path.Combine(_root, "plain.txt");
        File.WriteAllText(file, "x");
        PathConfinement.IsReparsePoint(file).Should().BeFalse();
    }

    [Fact]
    public void IsReparsePoint_NonexistentPath_IsFalse()
        => PathConfinement.IsReparsePoint(Path.Combine(_root, "nope")).Should().BeFalse();

    [Fact]
    public void EnsureConfinedNoReparsePoint_PlainConfinedPath_Passes()
    {
        var inside = Path.Combine(_root, "a", "b", "c.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        var act = () => PathConfinement.EnsureConfinedNoReparsePoint(inside, _root, AmccaErrors.Sec001);
        act.Should().NotThrow();
    }

    // ---- end-to-end junction cases (skipped if junctions unavailable) ----------------

    [Fact]
    public void JunctionEscapingRoot_IsRejected()
    {
        var outside = Path.Combine(_root, "OUTSIDE");
        Directory.CreateDirectory(outside);
        var confinedRoot = Path.Combine(_root, "confined");
        Directory.CreateDirectory(confinedRoot);

        var junction = Path.Combine(confinedRoot, "link");
        if (!TryCreateJunction(junction, outside))
        {
            // LIMITATION: this environment cannot create a directory junction; detector is
            // still covered by the IsReparsePoint_* unit tests above.
            return;
        }

        var act = () => PathConfinement.EnsureConfinedNoReparsePoint(
            Path.Combine(junction, "payload.txt"), confinedRoot, AmccaErrors.Sec001);

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec001);
    }

    [Fact]
    public void MediaRenderer_RejectsPathThroughJunction()
    {
        var outside = Path.Combine(_root, "MR_OUTSIDE");
        Directory.CreateDirectory(outside);
        var dataRoot = Path.Combine(_root, "mr_data");
        Directory.CreateDirectory(dataRoot);

        var junction = Path.Combine(dataRoot, "escape");
        if (!TryCreateJunction(junction, outside)) return; // limitation stated in class summary

        var renderer = new MediaRenderer(dataRoot);
        var act = () => renderer.ValidatePathConfinement(Path.Combine(junction, "out.mp4"));

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec001);
    }

    [Fact]
    public void ArchiveExtraction_IntoJunctionedSubpath_IsRejected_NothingEscapes()
    {
        var outside = Path.Combine(_root, "ZIP_OUTSIDE");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(_root, "zip_target");
        Directory.CreateDirectory(target);

        var junction = Path.Combine(target, "evil");
        if (!TryCreateJunction(junction, outside)) return; // limitation stated in class summary

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = archive.CreateEntry("evil/payload.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("escaped!");
        }
        ms.Position = 0;

        var act = () => SafeArchiveExtractor.ExtractZipSafely(ms, target);

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec004);
        Directory.GetFiles(outside).Should().BeEmpty("no archive content may be written through the junction");
    }
}
