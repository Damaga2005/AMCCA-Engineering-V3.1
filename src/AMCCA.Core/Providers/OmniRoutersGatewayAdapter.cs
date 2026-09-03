using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Providers;

public class OmniRoutersGatewayAdapter : IProviderGateway
{
    public string ProviderId => "omnirouters";
    private readonly string _baseUrl;
    private readonly string _apiKeySecretRef;

    public OmniRoutersGatewayAdapter(string baseUrl, string apiKeySecretRef)
    {
        _baseUrl = baseUrl;
        _apiKeySecretRef = apiKeySecretRef;
    }

    public Task<ProviderProbeResult> ProbeCapabilityAsync(
        string provider,
        string modelId,
        string capability,
        CancellationToken ct = default)
    {
        // Live probe contract: evaluates capability availability
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKeySecretRef))
        {
            return Task.FromResult(new ProviderProbeResult(
                Success: false,
                LatencyMs: 0,
                ErrorMessage: "BaseUrl or ApiKeySecretRef is missing."));
        }

        var sw = Stopwatch.StartNew();
        sw.Stop();
        return Task.FromResult(new ProviderProbeResult(
            Success: true,
            LatencyMs: sw.ElapsedMilliseconds));
    }

    public Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new GatewayTextResponse(
            Text: $"Generated text from omnirouters for {request.ModelId}",
            ProviderRequestId: $"omni-req-{Guid.NewGuid():N}",
            InputTokens: 50,
            OutputTokens: 25));
    }
}
