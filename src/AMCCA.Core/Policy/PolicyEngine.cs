using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    private static readonly HashSet<string> ProtectedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "publication.dispatch",
        "publish",
        "money.spend",
        "external.mutate",
        "credentials.access",
        "capability.enable",
        "policy.mutate"
    };

    private static readonly HashSet<string> KnownAllowableActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "media.render",
        "source.fetch",
        "research.query",
        "script.validate",
        "qa.evaluate",
        "job.claim",
        "job.heartbeat",
        "job.complete",
        "job.fail"
    };

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

    public bool IsGlobalKillSwitchActive() => _globalKillSwitch;

    public void SetPlatformKillSwitch(string platform, bool active)
    {
        _platformKillSwitches[platform] = active;
    }

    public PolicyDecisionResult EvaluateAction(string productionId, string action, string? platform = null)
    {
        var context = new PolicyEvaluationContext(
            ProductionId: productionId,
            Action: action,
            AutonomyMode: "ASSISTED",
            Platform: platform);

        return EvaluateAction(context);
    }

    public PolicyDecisionResult EvaluateAction(PolicyEvaluationContext ctx)
    {
        // SPEC/08 evaluation order: Emergency stop -> Security -> Safety -> Rights -> Compliance -> Platform -> Budget -> Autonomy -> Operator config -> Strategy

        // 1. Missing required policy data check (Fail closed)
        if (string.IsNullOrWhiteSpace(ctx.Action) || string.IsNullOrWhiteSpace(ctx.ProductionId))
        {
            return new PolicyDecisionResult("BLOCK", "policy.missing_required_data", AmccaErrors.Pol001, "Action or ProductionId is missing or empty.");
        }

        // 2. Emergency stop (Global kill switch)
        if (_globalKillSwitch)
        {
            return new PolicyDecisionResult("BLOCK", "emergency_stop.global_kill_switch", AmccaErrors.Pol003, "Global kill switch is active.");
        }

        // 3. Per-platform kill switch
        if (!string.IsNullOrEmpty(ctx.Platform) && _platformKillSwitches.TryGetValue(ctx.Platform, out var active) && active)
        {
            return new PolicyDecisionResult("BLOCK", "emergency_stop.platform_kill_switch", AmccaErrors.Pol003, $"Kill switch active for platform '{ctx.Platform}'.");
        }

        // 4. Security Check
        if (ctx.SecurityFlags != null && ctx.SecurityFlags.Length > 0)
        {
            return new PolicyDecisionResult("BLOCK", "security.denied", AmccaErrors.Sec001, $"Security flags violated: {string.Join(", ", ctx.SecurityFlags)}");
        }

        // 5. Safety Check
        if (ctx.SafetyFlags != null && ctx.SafetyFlags.Length > 0)
        {
            return new PolicyDecisionResult("BLOCK", "safety.denied", "AMCCA-SAF-001", $"Safety flags violated: {string.Join(", ", ctx.SafetyFlags)}");
        }

        // 6. Rights Check
        if (ctx.RightsFlags != null && ctx.RightsFlags.Length > 0)
        {
            return new PolicyDecisionResult("BLOCK", "rights.denied", "AMCCA-RIG-001", $"Rights violation flags: {string.Join(", ", ctx.RightsFlags)}");
        }

        // 7. Compliance Check
        if (ctx.ComplianceFlags != null && ctx.ComplianceFlags.Length > 0)
        {
            return new PolicyDecisionResult("BLOCK", "compliance.denied", "AMCCA-CMP-002", $"Compliance violation flags: {string.Join(", ", ctx.ComplianceFlags)}");
        }

        // 8. Provider / Platform restrictions
        if (ctx.ProviderDisabled)
        {
            return new PolicyDecisionResult("BLOCK", "provider.disabled", "AMCCA-PRV-001", "Target provider is currently disabled.");
        }

        // 9. Budget Check
        if (ctx.BudgetExceeded)
        {
            return new PolicyDecisionResult("BLOCK", "budget.exceeded", AmccaErrors.Cst002, "Configured budget window exceeded.");
        }

        // 10. Human Approval Gate for Protected Actions
        if (ProtectedActions.Contains(ctx.Action))
        {
            if (!ctx.HasApprovedHumanGate)
            {
                return new PolicyDecisionResult("REQUIRE_APPROVAL", "approval.human_gate_required", AmccaErrors.Pol004, $"Action '{ctx.Action}' requires prior human approval.");
            }
        }

        // 11. Explicit Allowed Actions
        if (KnownAllowableActions.Contains(ctx.Action) || (ProtectedActions.Contains(ctx.Action) && ctx.HasApprovedHumanGate))
        {
            return new PolicyDecisionResult("ALLOW", "policy.explicit_allow");
        }

        // 12. Fail-Closed Default (NEVER allow unknown actions)
        return new PolicyDecisionResult("BLOCK", "policy.unknown_action_blocked", AmccaErrors.Pol001, $"Action '{ctx.Action}' is not explicitly permitted by normative policy.");
    }
}
