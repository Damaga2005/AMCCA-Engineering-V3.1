using System;
using System.Collections.Generic;
using AMCCA.Core.Contracts;
using AMCCA.Core.QA;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class QaEngineAndDagReworkContractTests
{
    [Fact]
    public void Production_FailingCriticalDimension_CannotPassQa()
    {
        // Exit criterion: "A production failing critical QA cannot proceed" (D-005, SPEC/35)
        var criticalScores = new CriticalScores(
            FactualAccuracy: 7.5, // Below minimum 8.0!
            Rights: 9.0,
            TechnicalIntegrity: 9.5,
            AudioIntelligibility: 8.5,
            VisualIntegrity: 8.5);

        var findings = new List<QaFinding>
        {
            new("f-1", "rep-1", "check_facts", CheckKind.DETERMINISTIC, CheckStatus.WARN, Severity.MEDIUM, "art-script-1", "REWORK_SCRIPT", ">=8.0", "7.5", "Fact accuracy marginal")
        };

        var verdict = QaVerdictEvaluator.EvaluateVerdict(overallScore: 8.6, criticalScores, findings, minOverall: 8.5, minCritical: 8.0);

        verdict.Should().Be("FAIL", "critical dimension below 8.0 MUST cause QA failure");
    }

    [Fact]
    public void Production_WithCriticalSeverityFinding_FailsQa()
    {
        var criticalScores = new CriticalScores(8.5, 9.0, 9.5, 9.0, 9.0);
        var findings = new List<QaFinding>
        {
            new("f-crit", "rep-1", "rights_check", CheckKind.DETERMINISTIC, CheckStatus.FAIL, Severity.CRITICAL, "art-asset-1", "REPLACE_ASSET", "GREEN", "RED", "Asset has no license")
        };

        var verdict = QaVerdictEvaluator.EvaluateVerdict(overallScore: 9.0, criticalScores, findings, minOverall: 8.5, minCritical: 8.0);

        verdict.Should().Be("FAIL", "any CRITICAL finding must force FAIL verdict");
    }

    [Fact]
    public void EvaluatingPass_SolelyFromAiFindings_RaisesQa002()
    {
        // SPEC/35: "A PASS is unreachable from AI findings alone (D-024, I-19). Attempting to set a verdict from an AI-assisted finding raises AMCCA-QA-002."
        var criticalScores = new CriticalScores(9.0, 9.0, 9.0, 9.0, 9.0);
        var findings = new List<QaFinding>
        {
            new("f-ai", "rep-1", "ai_coherence", CheckKind.AI_ASSISTED, CheckStatus.PASS, Severity.INFO, "art-render-1", null, null, null, "AI says looks good")
        };

        var act = () => QaVerdictEvaluator.EvaluateVerdict(overallScore: 9.5, criticalScores, findings, minOverall: 8.5, minCritical: 8.0, hasDeterministicChecks: false);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Qa002);
    }

    [Fact]
    public void Production_MeetingAllCriteriaAndThresholds_PassesQa()
    {
        var criticalScores = new CriticalScores(8.8, 9.0, 9.5, 8.5, 9.0);
        var findings = new List<QaFinding>
        {
            new("f-det", "rep-1", "codec_check", CheckKind.DETERMINISTIC, CheckStatus.PASS, Severity.INFO, "art-render-1", null, "h264", "h264", "Codec matches profile")
        };

        var verdict = QaVerdictEvaluator.EvaluateVerdict(overallScore: 8.9, criticalScores, findings, minOverall: 8.5, minCritical: 8.0, hasDeterministicChecks: true);

        verdict.Should().Be("PASS");
    }

    [Fact]
    public void DagRework_InvalidatesDownstreamDescendants_WithoutDeletingThem()
    {
        // SPEC/37: "Locate X in the artifact DAG. Compute all descendants through artifact_edges. Mark descendants INVALIDATED. Never delete."
        var dag = new ArtifactDag();
        dag.AddNode("script-v1", "SCRIPT");
        dag.AddNode("storyboard-v1", "STORYBOARD");
        dag.AddNode("render-v1", "RENDER");

        dag.AddEdge("script-v1", "storyboard-v1");
        dag.AddEdge("storyboard-v1", "render-v1");

        // Defect in script-v1
        var invalidated = dag.InvalidateDescendants("script-v1");

        invalidated.Should().Contain("storyboard-v1");
        invalidated.Should().Contain("render-v1");

        dag.GetNodeStatus("storyboard-v1").Should().Be("INVALIDATED");
        dag.GetNodeStatus("render-v1").Should().Be("INVALIDATED");
        dag.NodeExists("render-v1").Should().BeTrue("invalidated nodes must never be deleted (I-08)");
    }

    [Fact]
    public void ExceedingMaxReworkAttempts_HaltsAndRoutesToFailed()
    {
        // SPEC/37: "Verify rework_attempts < policy.rework.max_attempts; otherwise transition to FAILED"
        var resolver = new DagReworkResolver(maxReworkAttempts: 3);

        var canReworkAttempt1 = resolver.CanAttemptRework(currentAttempts: 2);
        canReworkAttempt1.Should().BeTrue();

        var canReworkAttempt3 = resolver.CanAttemptRework(currentAttempts: 3);
        canReworkAttempt3.Should().BeFalse("exceeding max rework attempts must halt rework loop");
    }
}
