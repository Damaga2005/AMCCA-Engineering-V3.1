namespace AMCCA.Core.Publishing;

public class PublicationRecord
{
    public string Id { get; set; } = string.Empty;
    public string ProductionId { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string ContentVersionId { get; set; } = string.Empty;
    public string State { get; set; } = "QUEUED";
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? ProviderRequestId { get; set; }
    public string? ExternalId { get; set; }
    public string? ExternalUrl { get; set; }
    public string? EvidenceSource { get; set; }
    public string? EvidenceRetrievedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public record PublicationEvidenceResult(
    bool IsPublished,
    string ExternalUrl,
    string EvidenceSource,
    string RetrievedAt);

public record UploadRequest(
    string AccountId,
    string Title,
    string Description,
    string VideoPath,
    string IdempotencyKey,
    bool IsSynthetic = false
);

public record UploadResult(
    bool Success,
    string? ExternalId,
    string? ExternalUrl,
    string? ErrorCode,
    TimeSpan? RetryAfter = null
);

public record PublicationStatusResult(
    string State,
    string? ExternalUrl,
    string? ErrorCode
);

public record RecentPublicationItem(
    string ExternalId,
    string Title,
    string PublishedAt,
    string ExternalUrl
);
