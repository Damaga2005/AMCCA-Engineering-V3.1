using System.Collections.Generic;

namespace AMCCA.Core.QA;

public enum CheckKind
{
    DETERMINISTIC,
    AI_ASSISTED
}

public enum CheckStatus
{
    PASS,
    WARN,
    FAIL
}

public enum Severity
{
    INFO,
    LOW,
    MEDIUM,
    HIGH,
    CRITICAL
}

public record QaFinding(
    string Id,
    string ReportId,
    string CheckId,
    CheckKind CheckKind,
    CheckStatus Status,
    Severity Severity,
    string ResponsibleArtifactVersionId,
    string? RemediationCode,
    string? Expected,
    string? Actual,
    string? Message);

public record CriticalScores(
    double FactualAccuracy,
    double Rights,
    double TechnicalIntegrity,
    double AudioIntelligibility,
    double VisualIntegrity);

public record QaReport(
    string ReportId,
    string ProductionId,
    string ArtifactVersionId,
    string Stage,
    double OverallScore,
    CriticalScores CriticalScores,
    string Verdict,
    IReadOnlyList<QaFinding> Findings);
