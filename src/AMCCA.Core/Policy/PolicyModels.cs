namespace AMCCA.Core.Policy;

public record PolicyDecisionResult(
    string Decision, // ALLOW, REQUIRE_APPROVAL, BLOCK
    string RuleKey,
    string? ReasonCode = null,
    string? Reason = null);

public record PolicyEvaluationContext(
    string ProductionId,
    string Action,
    string AutonomyMode = "ASSISTED",
    string? Platform = null,
    string[]? SecurityFlags = null,
    string[]? SafetyFlags = null,
    string[]? RightsFlags = null,
    string[]? ComplianceFlags = null,
    bool BudgetExceeded = false,
    bool HasApprovedHumanGate = false,
    bool ProviderDisabled = false,
    decimal RequestedCost = 0m);

public class BudgetRecord
{
    public string Id { get; set; } = string.Empty;
    public string Window { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public decimal LimitAmount { get; set; }
    public decimal Reserved { get; set; }
    public decimal Spent { get; set; }
    public string Currency { get; set; } = "EUR";
}

public class ApprovalRecord
{
    public string Id { get; set; } = string.Empty;
    public string ProductionId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ScopeJson { get; set; } = "{}";
    public string State { get; set; } = "PENDING";
    public bool SingleUse { get; set; } = true;
    public string? DecidedBy { get; set; }
    public string? DecidedAt { get; set; }
    public string? ConsumedAt { get; set; }
    public string ExpiresAt { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}
