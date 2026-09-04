using System;
using System.Collections.Generic;
using AMCCA.Core.Contracts;
using AMCCA.Core.Research;

namespace AMCCA.Core.Scripts;

public static class ScriptValidator
{
    public static void ValidateScriptAssertions(ScriptDocument script, IDictionary<string, Claim> claims)
    {
        foreach (var line in script.Lines)
        {
            if (!line.IsMaterialFact) continue;

            // Rule 1: Every material factual line maps to a claim id (SPEC/32, D-017)
            if (string.IsNullOrWhiteSpace(line.ClaimId) || !claims.TryGetValue(line.ClaimId, out var claim))
            {
                throw new AmccaException(
                    AmccaErrors.Res001,
                    ErrorCategory.Validation,
                    $"Material line {line.LineNumber} ('{line.Text}') has no backing claim id. Every factual assertion must map to a claim (SPEC/32, D-017).");
            }

            // Rule 2: No line asserts an UNKNOWN claim
            if (string.Equals(claim.Status, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                throw new AmccaException(
                    AmccaErrors.Res001,
                    ErrorCategory.Validation,
                    $"Material line {line.LineNumber} asserts an UNKNOWN claim '{claim.Text}'. UNKNOWN claims cannot appear in script (SPEC/32).");
            }

            // Rule 3: ESTIMATED and DISPUTED claims carry explicit uncertainty wording
            if (string.Equals(claim.Status, "ESTIMATED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Status, "DISPUTED", StringComparison.OrdinalIgnoreCase))
            {
                if (!line.UncertaintyWordingPresent)
                {
                    throw new AmccaException(
                        AmccaErrors.Res001,
                        ErrorCategory.Validation,
                        $"Material line {line.LineNumber} asserts a {claim.Status} claim ('{claim.Text}') without explicit uncertainty wording (SPEC/32).");
                }
            }
        }
    }
}
