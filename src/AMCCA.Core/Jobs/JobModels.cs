namespace AMCCA.Core.Jobs;

public class JobRecord
{
    public string Id { get; set; } = string.Empty;
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
}

public class JobClaim
{
    public string JobId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public long FenceToken { get; set; }
    public string LeaseUntil { get; set; } = string.Empty;
}

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
