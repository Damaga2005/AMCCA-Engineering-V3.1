namespace AMCCA.Core.Providers;

public record ProviderProbeResult(
    bool Success,
    long LatencyMs,
    string? ErrorMessage = null);

public record GatewayTextRequest(
    string ModelId,
    string Prompt,
    double Temperature,
    int MaxTokens,
    string CorrelationId);

public record GatewayTextResponse(
    string Text,
    string? ProviderRequestId,
    long InputTokens,
    long OutputTokens);

public class ModelRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string ConstraintsJson { get; set; } = "{}";
    public string? PricingSnapshotId { get; set; }
    public string? LastVerifiedAt { get; set; }
    public long FallbackOrder { get; set; } = 100;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
