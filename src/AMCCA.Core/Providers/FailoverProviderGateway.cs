using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Providers;

public class FailoverProviderGateway : IProviderGateway
{
    private readonly IReadOnlyList<IProviderGateway> _gateways;
    public string ProviderId => "failover-router";

    public int FallbackCount { get; private set; }

    public FailoverProviderGateway(IReadOnlyList<IProviderGateway> gateways)
    {
        if (gateways == null || gateways.Count == 0)
            throw new ArgumentException("At least one provider gateway must be supplied.", nameof(gateways));
        _gateways = gateways;
    }

    public async Task<ProviderProbeResult> ProbeCapabilityAsync(string provider, string modelId, string capability, CancellationToken ct = default)
    {
        foreach (var gw in _gateways)
        {
            var res = await gw.ProbeCapabilityAsync(provider, modelId, capability, ct);
            if (res.Success) return res;
        }
        return new ProviderProbeResult(false, 0, "All providers failed capability probe.");
    }

    public async Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default)
    {
        Exception? lastException = null;

        for (int i = 0; i < _gateways.Count; i++)
        {
            var gw = _gateways[i];
            try
            {
                var response = await gw.GenerateTextAsync(request, ct);
                if (i > 0)
                {
                    FallbackCount++;
                }
                return response;
            }
            catch (AmccaException ex) when (ex.Category == ErrorCategory.Transient ||
                                           ex.Category == ErrorCategory.RateLimited ||
                                           ex.Category == ErrorCategory.Provider)
            {
                lastException = ex;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new AmccaException(AmccaErrors.Ai001, ErrorCategory.Provider, "All failover providers exhausted.");
    }

    public async IAsyncEnumerable<string> StreamTextAsync(GatewayTextRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Exception? lastException = null;

        for (int i = 0; i < _gateways.Count; i++)
        {
            var gw = _gateways[i];
            IAsyncEnumerator<string>? enumerator = null;
            bool started = false;

            try
            {
                enumerator = gw.StreamTextAsync(request, ct).GetAsyncEnumerator(ct);
                started = await enumerator.MoveNextAsync();
                if (i > 0 && started)
                {
                    FallbackCount++;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (enumerator != null) await enumerator.DisposeAsync();
                continue;
            }

            if (started)
            {
                yield return enumerator.Current;
                while (await enumerator.MoveNextAsync())
                {
                    yield return enumerator.Current;
                }
                await enumerator.DisposeAsync();
                yield break;
            }
        }

        throw lastException ?? new AmccaException(AmccaErrors.Ai001, ErrorCategory.Provider, "All failover providers exhausted during stream.");
    }
}
