using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.QA;
using Dapper;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// A QA stage (SPEC/35): finds the artifact the stage checks, runs <see cref="IQaStageCheck"/>,
/// computes the verdict with <see cref="QaVerdictEvaluator"/> against the configured thresholds,
/// persists a <c>qa_reports</c> row plus its <c>qa_findings</c>, and advances (PASS) or routes to
/// REWORK (FAIL, AMCCA-QA-001) with the responsible artifact recorded.
/// </summary>
public sealed class QaStageHandler : IStageHandler
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly string _stage;
    private readonly IQaStageCheck _check;
    private readonly QaThresholdProfileRegistry _thresholds;

    public QaStageHandler(
        DatabaseConnectionFactory connectionFactory, string stage, IQaStageCheck check, QaThresholdProfileRegistry thresholds)
    {
        _connectionFactory = connectionFactory;
        _stage = stage;
        _check = check;
        _thresholds = thresholds;
    }

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        var productionId = context.Production.Id;

        string? artifactVersionId;
        using (var connection = await _connectionFactory.CreateOpenConnectionAsync(ct))
        {
            artifactVersionId = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                @"SELECT av.id FROM artifact_versions av
                  JOIN artifacts a ON a.id = av.artifact_id
                  WHERE a.production_id = @P AND a.kind = @K AND av.state = 'CURRENT';",
                new { P = productionId, K = _check.ArtifactKind }, cancellationToken: ct));
        }

        if (artifactVersionId is null)
        {
            return StageResult.Blocked(AmccaErrors.Qa001,
                $"{_stage} has no CURRENT {_check.ArtifactKind} artifact to check.");
        }

        var check = await _check.RunAsync(productionId, artifactVersionId, ct);
        var profile = _thresholds.Resolve("default");

        var verdict = QaVerdictEvaluator.EvaluateVerdict(
            check.OverallScore, check.CriticalScores, check.Findings,
            minOverall: profile.OverallMin, minCritical: profile.CriticalMin,
            hasDeterministicChecks: _check.HasDeterministicChecks);

        await PersistReportAsync(productionId, artifactVersionId, verdict, check, ct);

        return verdict == "PASS"
            ? StageResult.Advance($"{_stage} PASS (overall {check.OverallScore:0.0}, {check.Findings.Count} finding(s)).")
            : StageResult.Defect(AmccaErrors.Qa001,
                $"{_stage} FAIL (overall {check.OverallScore:0.0}); routing to REWORK.");
    }

    private async Task PersistReportAsync(
        string productionId, string artifactVersionId, string verdict, QaCheckResult check, CancellationToken ct)
    {
        var reportId = UlidGenerator.NewUlid();
        var now = System.DateTimeOffset.UtcNow.ToString("O");
        var criticalJson = JsonSerializer.Serialize(check.CriticalScores);

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        await connection.ExecuteAsync(
            @"INSERT INTO qa_reports
                (report_id, production_id, artifact_version_id, stage, overall_score, critical_scores_json,
                 verdict, threshold_profile_id, schema_version, evaluated_at)
              VALUES (@Rid, @Pid, @Avid, @Stage, @Score, @Crit, @Verdict, 'default', '3.1.0', @Now);",
            new { Rid = reportId, Pid = productionId, Avid = artifactVersionId, Stage = _stage,
                  Score = check.OverallScore, Crit = criticalJson, Verdict = verdict, Now = now }, tx);

        foreach (var f in check.Findings)
        {
            await connection.ExecuteAsync(
                @"INSERT INTO qa_findings
                    (id, report_id, check_id, check_kind, status, severity, responsible_artifact_version_id,
                     remediation_code, expected, actual, message)
                  VALUES (@Id, @Rid, @Cid, @Kind, @Status, @Sev, @Resp, @Rem, @Exp, @Act, @Msg);",
                new
                {
                    Id = UlidGenerator.NewUlid(), Rid = reportId, Cid = f.CheckId,
                    Kind = f.CheckKind.ToString(), Status = f.Status.ToString(), Sev = f.Severity.ToString(),
                    Resp = string.IsNullOrEmpty(f.ResponsibleArtifactVersionId) ? artifactVersionId : f.ResponsibleArtifactVersionId,
                    Rem = f.RemediationCode, Exp = f.Expected, Act = f.Actual, Msg = f.Message,
                }, tx);
        }

        tx.Commit();
    }
}
