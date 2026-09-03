using System.Collections.Generic;
using System.Linq;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.QA;

public static class QaVerdictEvaluator
{
    public static string EvaluateVerdict(
        double overallScore,
        CriticalScores criticalScores,
        IReadOnlyList<QaFinding> findings,
        double minOverall = 8.5,
        double minCritical = 8.0,
        bool hasDeterministicChecks = true)
    {
        // Rule: A PASS is unreachable from AI findings alone (D-024, I-19, SPEC/35)
        if (!hasDeterministicChecks)
        {
            throw new AmccaException(
                AmccaErrors.Qa002,
                ErrorCategory.Validation,
                "A PASS verdict is unreachable from AI-assisted findings alone. Deterministic checks are required (SPEC/35, D-024).");
        }

        // Rule: No CRITICAL finding allowed for PASS
        if (findings.Any(f => f.Severity == Severity.CRITICAL))
        {
            return "FAIL";
        }

        // Rule: Any failing deterministic check forces FAIL
        if (findings.Any(f => f.CheckKind == CheckKind.DETERMINISTIC && f.Status == CheckStatus.FAIL))
        {
            return "FAIL";
        }

        // Rule: overall_score >= policy.qa.overall_min
        if (overallScore < minOverall)
        {
            return "FAIL";
        }

        // Rule: Every critical dimension >= policy.qa.critical_min
        if (criticalScores.FactualAccuracy < minCritical ||
            criticalScores.Rights < minCritical ||
            criticalScores.TechnicalIntegrity < minCritical ||
            criticalScores.AudioIntelligibility < minCritical ||
            criticalScores.VisualIntegrity < minCritical)
        {
            return "FAIL";
        }

        return "PASS";
    }
}
