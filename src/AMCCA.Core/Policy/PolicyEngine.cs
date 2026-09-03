using System;
using System.Collections.Concurrent;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;

namespace AMCCA.Core.Policy;

public class PolicyEngine
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly BudgetManager _budgetManager;
    private readonly ApprovalManager _approvalManager;

    private volatile bool _globalKillSwitch;
    private readonly ConcurrentDictionary<string, bool> _platformKillSwitches = new(StringComparer.OrdinalIgnoreCase);

    public PolicyEngine(
        DatabaseConnectionFactory connectionFactory,
        BudgetManager budgetManager,
        ApprovalManager approvalManager)
    {
        _connectionFactory = connectionFactory;
        _budgetManager = budgetManager;
        _approvalManager = approvalManager;
    }

    public void SetGlobalKillSwitch(bool active)
    {
        _globalKillSwitch = active;
    }

    public void SetPlatformKillSwitch(string platform, bool active)
    {
        _platformKillSwitches[platform] = active;
    }

    public PolicyDecisionResult EvaluateAction(string productionId, string action, string? platform = null)
    {
        // SPEC/08 evaluation order: Emergency stop (Kill switch) -> Security -> Safety -> Rights -> Compliance -> Budget -> Autonomy

        // 1. Emergency stop (Global kill switch)
        if (_globalKillSwitch)
        {
            return new PolicyDecisionResult("BLOCK", "emergency_stop.global_kill_switch", AmccaErrors.Pol003, "Global kill switch is active.");
        }

        // 2. Per-platform kill switch
        if (!string.IsNullOrEmpty(platform) && _platformKillSwitches.TryGetValue(platform, out var active) && active)
        {
            return new PolicyDecisionResult("BLOCK", "emergency_stop.platform_kill_switch", AmccaErrors.Pol003, $"Kill switch active for platform '{platform}'.");
        }

        return new PolicyDecisionResult("ALLOW", "policy.default_allow");
    }
}
