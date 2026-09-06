using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Research;
using AMCCA.Core.Scripts;
using Dapper;

namespace AMCCA.Core.QA;

internal static class QaScoring
{
    public static CriticalScores Uniform(double v) => new(v, v, v, v, v);

    public static QaFinding Finding(string checkId, CheckKind kind, CheckStatus status, Severity severity, string message,
        string? remediation = null, string? expected = null, string? actual = null)
        => new(Id: "", ReportId: "", CheckId: checkId, CheckKind: kind, Status: status, Severity: severity,
               ResponsibleArtifactVersionId: "", RemediationCode: remediation, Expected: expected, Actual: actual, Message: message);

    /// <summary>Overall = 10 minus a penalty per finding; a CRITICAL finding forces FAIL in QaVerdictEvaluator anyway.</summary>
    public static double Overall(IEnumerable<QaFinding> findings)
    {
        double score = 10.0;
        foreach (var f in findings)
        {
            score -= f.Severity switch
            {
                Severity.CRITICAL => 6.0,
                Severity.HIGH => 2.5,
                Severity.MEDIUM => 1.0,
                Severity.LOW => 0.3,
                _ => 0.0,
            };
        }
        return Math.Max(0.0, score);
    }
}

/// <summary>
/// CONTENT_QA deterministic checks (SPEC/35): every material line maps to a non-UNKNOWN claim with the
/// right uncertainty wording (via <see cref="ScriptValidator"/>), and no line contains a prohibited term.
/// </summary>
public sealed class ContentQaCheck : IQaStageCheck
{
    private static readonly string[] ProhibitedTerms =
    {
        "guaranteed returns", "guaranteed profit", "miracle cure", "get rich quick",
        "no risk", "risk-free investment", "cure for cancer",
    };

    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly ArtifactStore _artifacts;

    public string ArtifactKind => "SCRIPT";
    public bool HasDeterministicChecks => true;

    public ContentQaCheck(DatabaseConnectionFactory connectionFactory, ArtifactStore artifacts)
    {
        _connectionFactory = connectionFactory;
        _artifacts = artifacts;
    }

    public async Task<QaCheckResult> RunAsync(string productionId, string artifactVersionId, CancellationToken ct = default)
    {
        var body = await _artifacts.GetCurrentTextAsync(productionId, "SCRIPT", ct)
            ?? throw new AmccaException(AmccaErrors.Qa001, ErrorCategory.Internal, "CONTENT_QA: SCRIPT artifact body is missing.");

        var script = ScriptDocumentSerializer.Deserialize(productionId, body);
        var claims = await LoadClaimsAsync(productionId, ct);
        var findings = new List<QaFinding>();

        try
        {
            ScriptValidator.ValidateScriptAssertions(script, claims);
        }
        catch (AmccaException ex)
        {
            findings.Add(QaScoring.Finding("content.claim_mapping", CheckKind.DETERMINISTIC, CheckStatus.FAIL,
                Severity.CRITICAL, ex.Message, remediation: "REWORK_SCRIPT"));
        }

        foreach (var line in script.Lines)
        {
            foreach (var term in ProhibitedTerms)
            {
                if (line.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(QaScoring.Finding("content.prohibited_term", CheckKind.DETERMINISTIC, CheckStatus.FAIL,
                        Severity.HIGH, $"Line {line.LineNumber} contains a prohibited term: '{term}'.",
                        remediation: "REWORK_SCRIPT", actual: term));
                }
            }
        }

        var crit = findings.Any(f => f.CheckId == "content.claim_mapping")
            ? new CriticalScores(FactualAccuracy: 4.5, Rights: 9.0, TechnicalIntegrity: 9.0, AudioIntelligibility: 9.0, VisualIntegrity: 9.0)
            : QaScoring.Uniform(9.0);

        return new QaCheckResult(QaScoring.Overall(findings), crit, findings);
    }

    private async Task<IDictionary<string, Claim>> LoadClaimsAsync(string productionId, CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Claim>(new CommandDefinition(
            @"SELECT id AS Id, production_id AS ProductionId, text AS Text, status AS Status,
                     materiality AS Materiality, subject_class AS SubjectClass,
                     contains_personal_data AS ContainsPersonalData, schema_version AS SchemaVersion, created_at AS CreatedAt
              FROM claims WHERE production_id = @P;", new { P = productionId }, cancellationToken: ct));
        var map = new Dictionary<string, Claim>();
        foreach (var c in rows) map[c.Id] = c;
        return map;
    }
}

/// <summary>
/// COMPLIANCE_QA deterministic check (SPEC/35): every rights_record for the production is GREEN. A real
/// analyzer would also screen platform policy against the finished video — that is the AI-assisted seam.
/// </summary>
public sealed class ComplianceQaCheck : IQaStageCheck
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    public string ArtifactKind => "RENDER";
    public bool HasDeterministicChecks => true;

    public ComplianceQaCheck(DatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<QaCheckResult> RunAsync(string productionId, string artifactVersionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var notGreen = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM rights_records WHERE production_id = @P AND status <> 'GREEN';",
            new { P = productionId }, cancellationToken: ct));

        var findings = new List<QaFinding>();
        if (notGreen > 0)
        {
            findings.Add(QaScoring.Finding("compliance.rights_not_green", CheckKind.DETERMINISTIC, CheckStatus.FAIL,
                Severity.CRITICAL, $"{notGreen} rights record(s) are not GREEN.", remediation: "RESOLVE_RIGHTS"));
        }

        var crit = notGreen > 0
            ? new CriticalScores(9.0, 3.0, 9.0, 9.0, 9.0)
            : QaScoring.Uniform(9.0);
        return new QaCheckResult(QaScoring.Overall(findings), crit, findings);
    }
}

/// <summary>
/// The media QA stages (TECHNICAL/VISUAL/AUDIO/RETENTION_QA): today just "a CURRENT render exists and is
/// non-empty". A real media analyzer (container/codec/black frames/loudness/pacing) is the seam — swap
/// this <see cref="IQaStageCheck"/> for it. ponytail: thin deterministic check pending that analyzer.
/// </summary>
public sealed class RenderPresenceQaCheck : IQaStageCheck
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    public string ArtifactKind => "RENDER";
    public bool HasDeterministicChecks => true;

    public RenderPresenceQaCheck(DatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<QaCheckResult> RunAsync(string productionId, string artifactVersionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var bytes = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT bytes FROM artifact_versions WHERE id = @Id;", new { Id = artifactVersionId }, cancellationToken: ct));

        var findings = new List<QaFinding>();
        if (bytes <= 0)
        {
            findings.Add(QaScoring.Finding("media.empty_render", CheckKind.DETERMINISTIC, CheckStatus.FAIL,
                Severity.CRITICAL, "The CURRENT render artifact is empty.", remediation: "RERENDER"));
        }
        return new QaCheckResult(QaScoring.Overall(findings),
            findings.Count == 0 ? QaScoring.Uniform(9.0) : QaScoring.Uniform(4.0), findings);
    }
}

/// <summary>
/// SCORING (SPEC/13 gate): the aggregate score is the minimum overall_score across the production's QA
/// reports; any FAIL report fails scoring. Anchors on the SCRIPT artifact (always present).
/// </summary>
public sealed class ScoringCheck : IQaStageCheck
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    public string ArtifactKind => "SCRIPT";
    public bool HasDeterministicChecks => true;

    public ScoringCheck(DatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<QaCheckResult> RunAsync(string productionId, string artifactVersionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var reports = (await connection.QueryAsync<(string Stage, double Score, string Verdict)>(new CommandDefinition(
            "SELECT stage AS Stage, overall_score AS Score, verdict AS Verdict FROM qa_reports WHERE production_id = @P;",
            new { P = productionId }, cancellationToken: ct))).ToList();

        var findings = new List<QaFinding>();
        if (reports.Count == 0)
        {
            findings.Add(QaScoring.Finding("scoring.no_qa_reports", CheckKind.DETERMINISTIC, CheckStatus.FAIL,
                Severity.CRITICAL, "No QA reports were produced for this production.", remediation: "RUN_QA"));
            return new QaCheckResult(0.0, QaScoring.Uniform(0.0), findings);
        }

        foreach (var r in reports.Where(r => r.Verdict == "FAIL"))
        {
            findings.Add(QaScoring.Finding("scoring.stage_failed", CheckKind.DETERMINISTIC, CheckStatus.FAIL,
                Severity.CRITICAL, $"QA stage {r.Stage} failed.", remediation: "REWORK"));
        }

        var overall = reports.Min(r => r.Score);
        return new QaCheckResult(overall, QaScoring.Uniform(findings.Count == 0 ? overall : 4.0), findings);
    }
}
