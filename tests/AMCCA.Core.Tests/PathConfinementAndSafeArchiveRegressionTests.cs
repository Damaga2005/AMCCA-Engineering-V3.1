using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using AMCCA.Core.Contracts;
using AMCCA.Core.Media;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class PathConfinementAndSafeArchiveRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _rootDir;
    private readonly string _siblingDir;

    public PathConfinementAndSafeArchiveRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_PATH_DEF012_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _rootDir = Path.Combine(_testDir, "base_app");
        Directory.CreateDirectory(_rootDir);

        _siblingDir = Path.Combine(_testDir, "base_app_evil");
        Directory.CreateDirectory(_siblingDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void DEF012_SiblingPrefixEscape_MustBeBlocked()
    {
        var renderer = new MediaRenderer(_rootDir);

        // A sibling directory whose name begins with the base dir path (base_app vs base_app_evil)
        var evilSiblingFile = Path.Combine(_siblingDir, "payload.mp4");

        var act = () => renderer.ValidatePathConfinement(evilSiblingFile);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec001, "Sibling directory with common prefix must NOT bypass path confinement (DEF-012)");
    }

    [Fact]
    public void DEF012_DotDotTraversal_MustBeBlocked()
    {
        var renderer = new MediaRenderer(_rootDir);
        var traversalPath = Path.Combine(_rootDir, "..", "base_app_evil", "payload.mp4");

        var act = () => renderer.ValidatePathConfinement(traversalPath);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec001);
    }

    [Fact]
    public void DEF012_ValidNestedPath_And_MixedSlashes_AllowedWhenConfined()
    {
        var renderer = new MediaRenderer(_rootDir);

        // Mixed slashes within confined dir
        var validNested = Path.Combine(_rootDir, "subfolder/media\\clip.mp4");
        var act = () => renderer.ValidatePathConfinement(validNested);

        act.Should().NotThrow();
    }

    [Fact]
    public void DEF013_ZipSlip_ThrowsSec004()
    {
        var targetDir = Path.Combine(_rootDir, "extracted");
        Directory.CreateDirectory(targetDir);

        // Create zip stream with zip-slip entry
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../../escaped.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("malicious");
        }
        ms.Position = 0;

        var act = () => SafeArchiveExtractor.ExtractZipSafely(ms, targetDir);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec004, "ZipSlip traversal must throw AMCCA-SEC-004");
    }

    [Fact]
    public void DEF013_ValidZip_ExtractsSafely()
    {
        var targetDir = Path.Combine(_rootDir, "valid_extract");
        Directory.CreateDirectory(targetDir);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry1 = archive.CreateEntry("doc.txt");
            using (var writer = new StreamWriter(entry1.Open()))
            {
                writer.Write("hello world");
            }

            var entry2 = archive.CreateEntry("sub/file.txt");
            using (var writer = new StreamWriter(entry2.Open()))
            {
                writer.Write("nested hello");
            }
        }
        ms.Position = 0;

        SafeArchiveExtractor.ExtractZipSafely(ms, targetDir);

        File.Exists(Path.Combine(targetDir, "doc.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "sub", "file.txt")).Should().BeTrue();
    }

    [Fact]
    public void DEF013_TooManyEntries_AbortsWithSec004()
    {
        var targetDir = Path.Combine(_rootDir, "extract_limit");
        Directory.CreateDirectory(targetDir);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < 15; i++)
            {
                var entry = archive.CreateEntry($"entry_{i}.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("x");
            }
        }
        ms.Position = 0;

        // Set max entries to 10
        var options = new SafeArchiveOptions { MaxEntries = 10 };
        var act = () => SafeArchiveExtractor.ExtractZipSafely(ms, targetDir, options);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec004);
    }

    [Fact]
    public void DEF013_TotalBytesExceeded_AbortsWithSec004()
    {
        var targetDir = Path.Combine(_rootDir, "extract_bytes");
        Directory.CreateDirectory(targetDir);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("big.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(new string('A', 5000));
        }
        ms.Position = 0;

        // Set max uncompressed bytes to 1000
        var options = new SafeArchiveOptions { MaxTotalUncompressedBytes = 1000 };
        var act = () => SafeArchiveExtractor.ExtractZipSafely(ms, targetDir, options);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec004);
    }
}
