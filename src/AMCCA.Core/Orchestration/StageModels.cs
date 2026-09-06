using AMCCA.Core.Domain;

namespace AMCCA.Core.Orchestration;

/// <summary>What a stage handler decided should happen to the production it just worked on.</summary>
public enum StageOutcomeKind
{
    /// <summary>Nothing to do this tick (e.g. still waiting on a job). Leave the production where it is.</summary>
    Noop,

    /// <summary>The stage's work succeeded; advance along the canonical forward transition.</summary>
    Advance,

    /// <summary>The stage produced a defect; route to REWORK (SPEC/37).</summary>
    Defect,

    /// <summary>A policy / evidence / budget rule blocks progress; route to BLOCKED for an operator.</summary>
    Blocked,

    /// <summary>A permanent, unrecoverable error; route to FAILED.</summary>
    Failed,

    /// <summary>An external side effect had an ambiguous result; route to UNKNOWN_EXTERNAL_STATE (SPEC/44).</summary>
    Ambiguous,
}

/// <summary>
/// A stage handler's decision. <see cref="ReasonCode"/> is a `SPEC/05` code shown to the operator for
/// every non-<see cref="StageOutcomeKind.Advance"/>/<see cref="StageOutcomeKind.Noop"/> outcome.
/// </summary>
public sealed record StageResult(StageOutcomeKind Kind, string? ReasonCode = null, string? Detail = null)
{
    public static StageResult Noop(string? detail = null) => new(StageOutcomeKind.Noop, null, detail);
    public static StageResult Advance(string? detail = null) => new(StageOutcomeKind.Advance, null, detail);
    public static StageResult Defect(string reasonCode, string detail) => new(StageOutcomeKind.Defect, reasonCode, detail);
    public static StageResult Blocked(string reasonCode, string detail) => new(StageOutcomeKind.Blocked, reasonCode, detail);
    public static StageResult Failed(string reasonCode, string detail) => new(StageOutcomeKind.Failed, reasonCode, detail);
    public static StageResult Ambiguous(string reasonCode, string detail) => new(StageOutcomeKind.Ambiguous, reasonCode, detail);
}

/// <summary>Everything a stage handler needs to do one step of work for one production.</summary>
public sealed record StageContext(Production Production, string CorrelationId);
