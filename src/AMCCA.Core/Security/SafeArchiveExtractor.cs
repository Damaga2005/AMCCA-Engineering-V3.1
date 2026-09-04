using System;
using System.IO;
using System.IO.Compression;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

public class SafeArchiveOptions
{
    public int MaxEntries { get; set; } = 1000;
    public long MaxTotalUncompressedBytes { get; set; } = 100 * 1024 * 1024; // 100 MB
    public long MaxSingleEntryBytes { get; set; } = 50 * 1024 * 1024; // 50 MB
    public double MaxCompressionRatio { get; set; } = 100.0;
}

public static class SafeArchiveExtractor
{
    public static void ValidateEntryPath(string entryPath, string targetDirectory)
    {
        // SPEC/72 S-10: "Archive with traversal paths, excessive entry count, excessive uncompressed size -> Rejected with AMCCA-SEC-004"
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            throw new AmccaException(
                AmccaErrors.Sec004,
                ErrorCategory.Security,
                "Archive entry path cannot be empty.");
        }

        // Prevent absolute paths, drive letters, or initial separators
        if (Path.IsPathRooted(entryPath) || entryPath.StartsWith('/') || entryPath.StartsWith('\\') || entryPath.Contains(':'))
        {
            throw new AmccaException(
                AmccaErrors.Sec004,
                ErrorCategory.Security,
                $"Archive entry '{entryPath}' contains an absolute path or drive specifier (SPEC/72 S-10).");
        }

        var fullTargetDir = Path.GetFullPath(targetDirectory);
        var combinedPath = Path.Combine(fullTargetDir, entryPath);

        // Confinement check ensuring it does not escape target directory (DEF-012, DEF-013)
        PathConfinement.EnsureConfined(combinedPath, fullTargetDir, AmccaErrors.Sec004);
    }

    public static void ExtractZipSafely(
        Stream zipStream,
        string targetDirectory,
        SafeArchiveOptions? options = null)
    {
        options ??= new SafeArchiveOptions();
        var fullTargetDir = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(fullTargetDir);

        // SEC-08: extract into a private staging directory, validate every entry, and only then
        // commit into the target. Any failure deletes the staging directory, so a rejected
        // archive never leaves a partially-extracted tree behind.
        var stagingDir = Path.Combine(fullTargetDir, "__amcca_staging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        try
        {
            ExtractIntoStaging(zipStream, stagingDir, fullTargetDir, options);
            CommitStaging(stagingDir, fullTargetDir);
        }
        catch
        {
            SafeDeleteDirectory(stagingDir);
            throw;
        }
        finally
        {
            SafeDeleteDirectory(stagingDir);
        }
    }

    private static void ExtractIntoStaging(
        Stream zipStream,
        string stagingDir,
        string fullTargetDir,
        SafeArchiveOptions options)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        if (archive.Entries.Count > options.MaxEntries)
        {
            throw new AmccaException(
                AmccaErrors.Sec004,
                ErrorCategory.Security,
                $"Archive contains {archive.Entries.Count} entries, exceeding limit of {options.MaxEntries} (DEF-013, S-10).");
        }

        long totalUncompressedBytes = 0;
        var buffer = new byte[8192];

        foreach (var entry in archive.Entries)
        {
            // Confine against BOTH the real target (security semantics) and the staging directory.
            ValidateEntryPath(entry.FullName, fullTargetDir);
            PathConfinement.EnsureConfined(Path.Combine(stagingDir, entry.FullName), stagingDir, AmccaErrors.Sec004);

            // Check header compression ratio if reported
            if (entry.CompressedLength > 0 && entry.Length > 0)
            {
                double ratio = (double)entry.Length / entry.CompressedLength;
                if (ratio > options.MaxCompressionRatio)
                {
                    throw new AmccaException(
                        AmccaErrors.Sec004,
                        ErrorCategory.Security,
                        $"Archive entry '{entry.FullName}' compression ratio {ratio:F1} exceeds safe threshold of {options.MaxCompressionRatio:F1} (ZipBomb protection, DEF-013).");
                }
            }

            var destinationPath = Path.Combine(stagingDir, entry.FullName);

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                // Directory entry
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var parentDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            using var entryStream = entry.Open();
            using var outputStream = File.Create(destinationPath);

            long entryBytes = 0;
            int bytesRead;

            while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryBytes += bytesRead;
                totalUncompressedBytes += bytesRead;

                if (entryBytes > options.MaxSingleEntryBytes)
                {
                    outputStream.Close();
                    File.Delete(destinationPath);
                    throw new AmccaException(
                        AmccaErrors.Sec004,
                        ErrorCategory.Security,
                        $"Archive entry '{entry.FullName}' exceeded maximum single entry size of {options.MaxSingleEntryBytes} bytes.");
                }

                if (totalUncompressedBytes > options.MaxTotalUncompressedBytes)
                {
                    outputStream.Close();
                    File.Delete(destinationPath);
                    throw new AmccaException(
                        AmccaErrors.Sec004,
                        ErrorCategory.Security,
                        $"Total uncompressed bytes exceeded limit of {options.MaxTotalUncompressedBytes} bytes (DEF-013, S-10).");
                }

                outputStream.Write(buffer, 0, bytesRead);
            }
        }
    }

    private static void CommitStaging(string stagingDir, string fullTargetDir)
    {
        foreach (var dir in Directory.GetDirectories(stagingDir))
        {
            MergeInto(dir, Path.Combine(fullTargetDir, Path.GetFileName(dir)));
        }
        foreach (var file in Directory.GetFiles(stagingDir))
        {
            File.Move(file, Path.Combine(fullTargetDir, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void MergeInto(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            MergeInto(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Move(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void SafeDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best effort: the staging directory carries a random name and holds no committed state.
        }
    }
}
