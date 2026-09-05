using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Domain;
using AMCCA.Core.Operator;
using AMCCA.Core.Policy;
using AMCCA.Core.StateMachine;

namespace AMCCA.Core.Orchestration;

/// <summary>
/// Drives productions forward through the canonical state machine (SPEC/12, SPEC/13), one step per
/// production per tick. It is the sole state committer (DEF-008): a stage handler decides the outcome,
/// the engine commits the transition through <see cref="ProductionService.TransitionAsync"/>.
///
/// This is deliberately hosting-agnostic — <see cref="RunTickAsync"/> is one pass; a BackgroundService
/// (see AMCCA.App) loops it. That keeps the whole engine unit-testable without a host.
/// </summary>
public sealed class OrchestratorEngine
{
    private readonly StateMachineRegistry _registry;
    private readonly ProductionService _productions;
    private readonly StageHandlerRegistry _handlers;
    private readonly OperatorControlService _operatorControl;
    private readonly PolicyGate _policyGate;
    private readonly ApprovalManager _approvals;
    private readonly int _batchLimit;

    private const string PublicationDispatchAction = "publication.dispatch";

    // Outbound Orchestrator transitions whose trigger means "something went wrong" rather than
    // "the happy path continues". Used to find the single forward transition for a state.
    private static readonly HashSet<string> FailureTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "policy_block", "permanent_error", "ambiguous_side_effect", "defect_detected",
        "insufficient_evidence", "rework_exhausted", "rework_budget_exhausted",
        "unreconcilable", "reconciled_failed", "definitive_rejection", "processing_rejected",
    };

    private static readonly HashSet<string> NonForwardTargets = new(StringComparer.Ordinal)
    {
        "BLOCKED", "FAILED", "UNKNOWN_EXTERNAL_STATE", "REWORK",
    };

    // The engine does not drive out of these: BLOCKED/UNKNOWN wait for an operator or the
    // reconciliation service; REWORK's regenerate_node fan-out is DAG-rework logic (SPEC/37), not a
    // single forward step.
    private static readonly HashSet<string> NotDriven = new(StringComparer.Ordinal)
    {
        "BLOCKED", "UNKNOWN_EXTERNAL_STATE", "REWORK",
    };

    public OrchestratorEngine(
        StateMachineRegistry registry,
        ProductionService productions,
        StageHandlerRegistry handlers,
        OperatorControlService operatorControl,
        PolicyGate policyGate,
        ApprovalManager approvals,
        int batchLimit = 100)
    {
        _registry = registry;
        _productions = productions;
        _handlers = handlers;
        _operatorControl = operatorControl;
        _policyGate = policyGate;
        _approvals = approvals;
        _batchLimit = batchLimit;
    }

    public async Task<OrchestratorTickReport> RunTickAsync(CancellationToken ct = default)
    {
        var report = new OrchestratorTickReport();

        // Emergency stop, read from the persisted kill_switch_state (SPEC/49 gate 10, SPEC/53) so it
        // holds even though the orchestrator runs in a different process from the operator console.
        if (await _operatorControl.IsGlobalKillSwitchEngagedAsync(ct))
        {
            report.KillSwitchEngaged = true;
            return report;
        }

        var drivableStates = _registry.States
            .Select(s => s.Name)
            .Where(name => !_registry.TerminalStates.Contains(name) && !NotDriven.Contains(name))
            .ToArray();

        var productions = await _productions.ListInStatesAsync(drivableStates, _batchLimit, ct);
        report.Considered = productions.Count;

        foreach (var prod in productions)
        {
            ct.ThrowIfCancellationRequested();

            // MANUAL: the operator commits every transition from the UI; the engine never touches it.
            if (string.Equals(prod.AutonomyMode, "MANUAL", StringComparison.OrdinalIgnoreCase))
            {
                report.Skipped++;
                continue;
            }

            var forward = ForwardTransition(prod.State);
            if (forward is null)
            {
                report.Skipped++;
                continue;
            }

            var stateKind = _registry.States.FirstOrDefault(s => s.Name == prod.State)?.Kind ?? "";
            bool assisted = string.Equals(prod.AutonomyMode, "ASSISTED", StringComparison.OrdinalIgnoreCase);
            var correlationId = $"orc-{Guid.NewGuid():N}";

            // ASSISTED parks at decision gates for operator sign-off — an autonomy rule, not a policy
            // decision.
            if (assisted && string.Equals(stateKind, "gate", StringComparison.OrdinalIgnoreCase))
            {
                report.AwaitingApproval++;
                continue;
            }

            // Publishing is a protected action (SPEC/08). Evaluate it through PolicyGate, which records
            // the decision to policy_decisions + audit_log; only an ALLOW lets the transition proceed.
            if (IsPublishBoundary(forward))
            {
                var hasGate = await _approvals.HasApprovedGateAsync(prod.Id, PublicationDispatchAction, ct);
                var decision = await _policyGate.EvaluateAndRecordAsync(
                    new PolicyEvaluationContext(prod.Id, PublicationDispatchAction, prod.AutonomyMode, HasApprovedHumanGate: hasGate),
                    correlationId, ct: ct);

                if (!decision.IsAllowed)
                {
                    if (_registry.FindTransition(prod.State, "BLOCKED") is null)
                    {
                        report.Errors.Add(new OrchestratorError(prod.Id, prod.State, "publish policy denied but no BLOCKED edge."));
                        continue;
                    }
                    await _productions.TransitionAsync(prod.Id, "BLOCKED", "Orchestrator", correlationId, causationId: null, ct: ct);
                    report.Actions.Add(new OrchestratorAction(
                        prod.Id, prod.State, "BLOCKED", StageOutcomeKind.Blocked, decision.Result.ReasonCode));
                    continue;
                }
                // ALLOWed: fall through and drive the publish transition.
            }

            StageResult result;
            try
            {
                result = await _handlers.Resolve(prod.State)
                    .HandleAsync(new StageContext(prod, correlationId), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = StageResult.Blocked(
                    AmccaErrors.Orc002,
                    $"Stage handler for '{prod.State}' threw: {ex.Message}");
            }

            if (result.Kind == StageOutcomeKind.Noop)
            {
                report.Noop++;
                continue;
            }

            var (targetState, reasonCode) = ResolveTarget(prod.State, forward, result);
            if (targetState is null)
            {
                report.Errors.Add(new OrchestratorError(
                    prod.Id, prod.State,
                    $"No legal transition for outcome {result.Kind} and no BLOCKED edge from '{prod.State}'."));
                continue;
            }

            try
            {
                await _productions.TransitionAsync(
                    prod.Id, targetState, actorType: "Orchestrator",
                    correlationId: correlationId, causationId: null, ct: ct);

                report.Actions.Add(new OrchestratorAction(
                    prod.Id, prod.State, targetState, result.Kind, reasonCode));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Errors.Add(new OrchestratorError(prod.Id, prod.State, ex.Message));
            }
        }

        return report;
    }

    /// <summary>
    /// The single "happy path" Orchestrator transition out of <paramref name="fromState"/>, or null if
    /// there is not exactly one (terminal, control, or a fan-out state the engine does not drive).
    /// </summary>
    internal TransitionDefinition? ForwardTransition(string fromState)
    {
        var candidates = _registry.Transitions
            .Where(t => string.Equals(t.From, fromState, StringComparison.Ordinal)
                        && string.Equals(t.Actor, "Orchestrator", StringComparison.OrdinalIgnoreCase)
                        && !NonForwardTargets.Contains(t.To)
                        && !FailureTriggers.Contains(t.Trigger))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private bool IsPublishBoundary(TransitionDefinition forward)
    {
        var toKind = _registry.States.FirstOrDefault(s => s.Name == forward.To)?.Kind ?? "";
        return string.Equals(toKind, "publish", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a stage outcome to the state to move to, falling back to BLOCKED when the outcome's natural
    /// target (e.g. REWORK from a `verified` state that has no REWORK edge) is not a legal transition.
    /// </summary>
    private (string? TargetState, string? ReasonCode) ResolveTarget(
        string fromState, TransitionDefinition forward, StageResult result)
    {
        string desired = result.Kind switch
        {
            StageOutcomeKind.Advance => forward.To,
            StageOutcomeKind.Defect => "REWORK",
            StageOutcomeKind.Blocked => "BLOCKED",
            StageOutcomeKind.Failed => "FAILED",
            StageOutcomeKind.Ambiguous => "UNKNOWN_EXTERNAL_STATE",
            _ => "BLOCKED",
        };

        if (_registry.FindTransition(fromState, desired) is not null)
        {
            return (desired, result.ReasonCode);
        }

        // Outcome has no legal edge from here — block instead so nothing is lost.
        if (_registry.FindTransition(fromState, "BLOCKED") is not null)
        {
            return ("BLOCKED", result.ReasonCode ?? AmccaErrors.Orc002);
        }

        return (null, null);
    }
}
