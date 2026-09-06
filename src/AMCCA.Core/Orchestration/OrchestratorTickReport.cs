using System.Collections.Generic;

namespace AMCCA.Core.Orchestration;

/// <summary>One committed state transition the orchestrator made this tick.</summary>
public sealed record OrchestratorAction(
    string ProductionId,
    string FromState,
    string ToState,
    StageOutcomeKind Outcome,
    string? ReasonCode);

/// <summary>A production the orchestrator tried and failed to advance this tick (transition threw).</summary>
public sealed record OrchestratorError(string ProductionId, string State, string Message);

/// <summary>What <see cref="OrchestratorEngine.RunTickAsync"/> did on one pass.</summary>
public sealed class OrchestratorTickReport
{
    public bool KillSwitchEngaged { get; set; }
    public int Considered { get; set; }

    /// <summary>MANUAL productions, and states the engine does not drive (REWORK, no forward transition).</summary>
    public int Skipped { get; set; }

    /// <summary>Gate/publish states an ASSISTED (or any) run parks for an operator decision.</summary>
    public int AwaitingApproval { get; set; }

    /// <summary>Handlers that returned Noop — work in progress, nothing to commit.</summary>
    public int Noop { get; set; }

    public List<OrchestratorAction> Actions { get; } = new();
    public List<OrchestratorError> Errors { get; } = new();

    public int TransitionsCommitted => Actions.Count;
}
