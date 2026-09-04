using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AMCCA.Core.Contracts;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// SEC-08 — <see cref="SafeArchiveExtractor.ExtractZipSafely"/> extracts into a private staging
/// directory and only commits to the target when every entry has passed. Any failure deletes the
/// staging directory, so a rejected archive never leaves a partially-extracted tree.
/// </summary>
public class ArchiveExtractionStagingRegressionTests : IDisposable
{
    private readonly string _root;
    private readonly string _target;

    public ArchiveExtractionStagingRegressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AMCCA_SEC08_" + Guid.NewGuid().ToString("N"));
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static MemoryStream Zip(params (string name, string content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = archive.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private void TargetShouldBeEmptyOfExtractedContent()
    {
        // No committed files, and no leftover staging directory.
        var remaining = Directory.GetFileSystemEntries(_target, "*", SearchOption.AllDirectories);
        remaining.Should().BeEmpty();
        Directory.GetDirectories(_target, "__amcca_staging_*").Should().BeEmpty();
    }

    [Fact]
    public void FailureOnFirstEntry_LeavesTargetEmpty()
    {
        using var zip = Zip(("../escape.txt", "x"), ("ok.txt", "y"));

        var act = () => SafeArchiveExtractor.ExtractZipSafely(zip, _target);

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec004);
        TargetShouldBeEmptyOfExtractedContent();
    }

    [Fact]
    public void FailureOnMiddleEntry_LeavesTargetEmpty()
    {
        using var zip = Zip(("a.txt", "aaaa"), ("../evil.txt", "x"), ("c.txt", "cccc"));

        var act = () => SafeArchiveExtractor.ExtractZipSafely(zip, _target);

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec004);
        TargetShouldBeEmptyOfExtractedContent();
        File.Exists(Path.Combine(_target, "a.txt")).Should().BeFalse("earlier entries must not be committed");
    }

    [Fact]
    public void FailureOnLastEntry_LeavesTargetEmpty()
    {
        using var zip = Zip(("a.txt", "aaaa"), ("b.txt", "bbbb"), ("../evil.txt", "x"));

        var act = () => SafeArchiveExtractor.ExtractZipSafely(zip, _target);

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec004);
        TargetShouldBeEmptyOfExtractedContent();
    }

    [Fact]
    public void SizeLimitBreachMidExtraction_LeavesTargetEmpty()
    {
        using var zip = Zip(("small.txt", "hi"), ("big.txt", new string('A', 5000)));
        var options = new SafeArchiveOptions { MaxTotalUncompressedBytes = 1000 };

        var act = () => SafeArchiveExtractor.ExtractZipSafely(zip, _target, options);

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec004);
        TargetShouldBeEmptyOfExtractedContent();
    }

    [Fact]
    public void ZipBomb_LeavesTargetEmpty()
    {
        // High compression ratio entry.
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = archive.CreateEntry("bomb.txt", CompressionLevel.SmallestSize);
            using var w = new StreamWriter(e.Open());
            w.Write(new string('A', 2_000_000));
        }
        ms.Position = 0;

        var act = () => SafeArchiveExtractor.ExtractZipSafely(ms, _target,
            new SafeArchiveOptions { MaxCompressionRatio = 10.0, MaxTotalUncompressedBytes = 1000 });

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec004);
        TargetShouldBeEmptyOfExtractedContent();
        ms.Dispose();
    }

    [Fact]
    public void ValidArchive_CommitsEveryEntry()
    {
        using var zip = Zip(("root.txt", "r"), ("sub/nested.txt", "n"), ("sub/deep/leaf.txt", "l"));

        SafeArchiveExtractor.ExtractZipSafely(zip, _target);

        File.ReadAllText(Path.Combine(_target, "root.txt")).Should().Be("r");
        File.ReadAllText(Path.Combine(_target, "sub", "nested.txt")).Should().Be("n");
        File.ReadAllText(Path.Combine(_target, "sub", "deep", "leaf.txt")).Should().Be("l");
        Directory.GetDirectories(_target, "__amcca_staging_*").Should().BeEmpty("staging must be cleaned up after a successful commit");
    }

    [Fact]
    public void ValidArchive_MergesIntoPreexistingTargetContent()
    {
        File.WriteAllText(Path.Combine(_target, "keep.txt"), "keep");
        using var zip = Zip(("added.txt", "added"));

        SafeArchiveExtractor.ExtractZipSafely(zip, _target);

        File.Exists(Path.Combine(_target, "keep.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_target, "added.txt")).Should().BeTrue();
    }
}
