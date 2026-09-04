using System;
using System.Collections.Generic;
using System.Linq;

namespace AMCCA.Core.Research;

public static class ClaimValidator
{
    public static string EvaluateClaimStatus(
        Claim claim,
        IEnumerable<(Source Source, string Relation)> sources,
        int minSources = 2)
    {
        var sourceList = sources.ToList();

        // Rule 1: Any contradicting source marks the claim DISPUTED (never VERIFIED)
        if (sourceList.Any(s => string.Equals(s.Relation, "CONTRADICTS", StringComparison.OrdinalIgnoreCase)))
        {
            return "DISPUTED";
        }

        // Supporting sources only
        var supportingSources = sourceList
            .Where(s => string.Equals(s.Relation, "SUPPORTS", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Source)
            .ToList();

        if (string.Equals(claim.Materiality, "MATERIAL", StringComparison.OrdinalIgnoreCase))
        {
            // Rule 2: A source with no retrieved_at cannot support any claim
            // Rule 3: UNRATED sources cannot support a MATERIAL claim
            // Rule 4: Independence means distinct publishers, not distinct URLs
            var independentPublishers = supportingSources
                .Where(s => !string.IsNullOrWhiteSpace(s.RetrievedAt) &&
                            !string.IsNullOrWhiteSpace(s.Publisher) &&
                            !string.Equals(s.TrustTier, "UNRATED", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Publisher!.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            if (independentPublishers.Count < minSources)
            {
                // Material claim without sufficient independent sources cannot reach VERIFIED (SPEC/26)
                return "ESTIMATED";
            }

            return "VERIFIED";
        }

        // Non-material claims (e.g. background/context) with supporting evidence can be VERIFIED
        return supportingSources.Count > 0 ? "VERIFIED" : "UNKNOWN";
    }
}
