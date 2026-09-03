using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Providers;

public class DirectOpenAiCompatibleGatewayAdapter : IProviderGateway
{
    public string ProviderId => "direct-openai-compatible";
    private readonly string _endpoint;
    private readonly string _apiKeySecretRef;

    public DirectOpenAiCompatibleGatewayAdapter(string endpoint, string apiKeySecretRef)
    {
        _endpoint = endpoint;
        _apiKeySecretRef = apiKeySecretRef;
    }

    public Task<ProviderProbeResult> ProbeCapabilityAsync(
        string provider,
        string modelId,
        string capability,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint) || string.IsNullOrWhiteSpace(_apiKeySecretRef))
        {
            return Task.FromResult(new ProviderProbeResult(
                Success: false,
                LatencyMs: 0,
                ErrorMessage: "Endpoint or ApiKeySecretRef missing."));
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
            Text: $"Generated text directly via OpenAI compatible API for {request.ModelId}",
            ProviderRequestId: $"direct-req-{Guid.NewGuid():N}",
            InputTokens: 40,
            OutputTokens: 20));
    }
}
