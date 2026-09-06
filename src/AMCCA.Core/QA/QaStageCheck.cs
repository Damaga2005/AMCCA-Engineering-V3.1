using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.QA;

public sealed record QaCheckResult(double OverallScore, CriticalScores CriticalScores, IReadOnlyList<QaFinding> Findings);

/// <summary>
/// One QA stage's checks (SPEC/35). Deterministic checks are mandatory for a PASS to be reachable
/// (<see cref="QaVerdictEvaluator"/> raises AMCCA-QA-002 otherwise). AI-assisted checks are a seam for
/// a real analyzer / model to add findings.
/// </summary>
public interface IQaStageCheck
{
    /// <summary>The artifact kind this stage runs against ("SCRIPT", "RENDER", …).</summary>
    string ArtifactKind { get; }

    bool HasDeterministicChecks { get; }

    Task<QaCheckResult> RunAsync(string productionId, string artifactVersionId, CancellationToken ct = default);
}
