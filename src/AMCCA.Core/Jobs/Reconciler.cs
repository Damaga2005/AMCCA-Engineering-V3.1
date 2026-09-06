using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Jobs;

public enum IntentReconciliationOutcome
{
    /// <summary>The provider confirms the side effect ran. Intent -> CONFIRMED.</summary>
    Executed,

    /// <summary>The provider confirms the side effect did NOT run. Intent -> REFUTED (safe to retry).</summary>
    NotExecuted,

    /// <summary>The provider says the operation failed definitively. Intent -> ABANDONED.</summary>
    Failed,

    /// <summary>Still no authoritative answer. Intent stays UNKNOWN for the next pass.</summary>
    StillUnknown,
}

/// <summary>The authoritative result of asking a provider/platform what really happened to an intent.</summary>
public sealed record IntentReconciliation(IntentReconciliationOutcome Outcome, string Method, string? EvidenceRef, string Detail);

/// <summary>
/// SPEC/16 / SPEC/44: queries the provider/platform for the true state of a DISPATCHED or UNKNOWN
/// intent. This is what replaces the fabricated "STARTUP_STATUS_PROBE -> CONFIRMED" evidence — a real
/// implementation calls the platform's status API. Without one, RecoveryService leaves unknown intents
/// untouched rather than guessing.
/// </summary>
public interface IReconciler
{
    Task<IntentReconciliation> ReconcileIntentAsync(string intentId, CancellationToken ct = default);
}
