using System;
using System.IO;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

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

        // Prevent absolute paths or drive letters in entries
        if (Path.IsPathRooted(entryPath) || entryPath.StartsWith('/') || entryPath.StartsWith('\\') || entryPath.Contains(':'))
        {
            throw new AmccaException(
                AmccaErrors.Sec004,
                ErrorCategory.Security,
                $"Archive entry '{entryPath}' contains an absolute path or drive specifier (SPEC/72 S-10).");
        }

        var fullTargetDir = Path.GetFullPath(targetDirectory);
        var combinedPath = Path.GetFullPath(Path.Combine(fullTargetDir, entryPath));

        if (!combinedPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                AmccaErrors.Sec004,
                ErrorCategory.Security,
                $"Archive entry '{entryPath}' attempts path traversal escaping target directory '{fullTargetDir}' (SPEC/72 S-10).");
        }
    }
}
