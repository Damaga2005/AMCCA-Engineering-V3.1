using System;
using System.IO;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

public static class PathConfinement
{
    public static string EnsureConfined(string candidatePath, string rootDirectory, string errorCode = AmccaErrors.Sec001)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new AmccaException(errorCode, ErrorCategory.Security, "Path cannot be null or empty.");
        }

        if (candidatePath.IndexOf('\0') >= 0)
        {
            throw new AmccaException(errorCode, ErrorCategory.Security, "Null bytes in file paths are prohibited.");
        }

        var fullRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidatePath);

        var rootWithSep = fullRoot + Path.DirectorySeparatorChar;

        // Path is only valid if it matches rootDirectory exactly or starts with rootDirectory + separator
        if (!string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullCandidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                errorCode,
                ErrorCategory.Security,
                $"Path '{candidatePath}' resolves to '{fullCandidate}' which escapes boundary directory '{fullRoot}' (DEF-012, S-11).");
        }

        return fullCandidate;
    }

    /// <summary>
    /// SEC-09: textual confinement plus a check that no path component between <paramref name="rootDirectory"/>
    /// (exclusive) and <paramref name="candidatePath"/> (inclusive) is a Windows reparse point
    /// (symlink / junction / mount point). A reparse point can redirect an apparently-confined path
    /// outside the root at the moment of the real filesystem operation.
    /// </summary>
    public static string EnsureConfinedNoReparsePoint(string candidatePath, string rootDirectory, string errorCode = AmccaErrors.Sec001)
    {
        var fullCandidate = EnsureConfined(candidatePath, rootDirectory, errorCode);
        var fullRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var current = fullCandidate;
        while (!string.IsNullOrEmpty(current) &&
               !string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(current))
            {
                throw new AmccaException(
                    errorCode,
                    ErrorCategory.Security,
                    $"Path component '{current}' is a reparse point (symlink/junction/mount) and may redirect outside '{fullRoot}' (SEC-09).");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        return fullCandidate;
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            // If attributes can't be read, treat as non-reparse; the textual confinement check still applies.
            return false;
        }
    }
}
