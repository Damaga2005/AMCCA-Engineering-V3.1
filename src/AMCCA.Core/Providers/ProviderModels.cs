using AMCCA.Core.Database;

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
    string CorrelationId = "");

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

public class ProviderHealthStore
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly System.Collections.Generic.Dictionary<string, int> _consecutiveFailures = new();

    public ProviderHealthStore(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task RecordCallAsync(string provider, bool isSuccess, bool isTimeout)
    {
        lock (_consecutiveFailures)
        {
            if (isSuccess)
            {
                _consecutiveFailures[provider] = 0;
            }
            else
            {
                _consecutiveFailures[provider] = _consecutiveFailures.GetValueOrDefault(provider) + 1;
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsProviderHealthyAsync(string provider)
    {
        lock (_consecutiveFailures)
        {
            return Task.FromResult(_consecutiveFailures.GetValueOrDefault(provider) < 3);
        }
    }
}
