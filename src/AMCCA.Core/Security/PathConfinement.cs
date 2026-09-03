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
}
