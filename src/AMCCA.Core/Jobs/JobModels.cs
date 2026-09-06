using AMCCA.Core.Contracts;

namespace AMCCA.Core.Jobs;

public class JobRecord
{
    public string Id { get; set; } = string.Empty;
    public string? ProductionId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public long Priority { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long Attempt { get; set; }
    public long MaxAttempts { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "3.1.0";
}

/// <summary>
/// One row of the operator-facing job queue (SPEC/14, SPEC/62): the job plus whatever lease currently
/// holds it, so leasing, heartbeating and fencing are visible rather than implied.
/// </summary>
public class JobQueueEntry
{
    public string Id { get; set; } = string.Empty;
    public string? ProductionId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public long Priority { get; set; }
    public long Attempt { get; set; }
    public long MaxAttempts { get; set; }
    public string? CorrelationId { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string? LeaseOwnerId { get; set; }
    public string? LeaseUntil { get; set; }
    public string? HeartbeatAt { get; set; }
    public long? FenceToken { get; set; }

    public bool IsDeadLettered => string.Equals(State, "DEAD_LETTER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SPEC/60 obligation 6: no screen shows a terminal state without a SPEC/05 error code. DEAD_LETTER
    /// is the only terminal state that isn't just "the operator asked for this" (CANCELLED) or "the
    /// domain layer already produced its own code" (FAILED, UNKNOWN_EXTERNAL_STATE) -- it always means
    /// exactly one thing, SPEC/14's "Bounded by max_attempts... on exhaustion the job moves to
    /// DEAD_LETTER with AMCCA-JOB-003", so it is computed here rather than stored, the same as
    /// IsDeadLettered above.
    /// </summary>
    public string? ReasonCode => IsDeadLettered ? AmccaErrors.Job003 : null;
}

public class JobClaim
{
    public string JobId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public long FenceToken { get; set; }
    public string LeaseUntil { get; set; } = string.Empty;
}

public class JobLease : JobClaim { }

public class IntentRecord
{
    public string Id { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public string? ProductionId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? ExternalRequestId { get; set; }
    public long AttemptCount { get; set; }
    public string? DispatchedAt { get; set; }
    public string? ResolvedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public record RecoveryReport(
    int ExpiredLeasesRecovered,
    int UnknownIntentsProcessed,
    string Message);
