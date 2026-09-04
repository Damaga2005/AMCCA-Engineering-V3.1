namespace AMCCA.Core.Operator;

public record SystemStatusSummary(
    bool GlobalKillSwitchActive,
    string AutonomyMode,
    int PendingApprovalsCount,
    int ActiveProductionsCount);
