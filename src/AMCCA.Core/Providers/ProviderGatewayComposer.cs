using System;
using System.Collections.Generic;
using AMCCA.Core.Configuration;
using AMCCA.Core.Security;

namespace AMCCA.Core.Providers;

/// <summary>
/// Builds the runtime <see cref="IProviderGateway"/> from configuration: each configured provider is
/// wrapped in a <see cref="ResilientProviderGateway"/> (retry + circuit breaker, SPEC/23), and multiple
/// providers are composed behind a <see cref="FailoverProviderGateway"/> so failover happens only after
/// a provider's own retries and breaker are exhausted.
///
/// Returns null when no provider is enabled/complete — the caller (a stage-handler agent) then blocks
/// the production rather than inventing a model call.
/// </summary>
public static class ProviderGatewayComposer
{
    public static IProviderGateway? Compose(
        AmccaConfig config, ISecretStore secretStore, ProviderResilienceOptions? resilience = null)
    {
        var resilient = new List<IProviderGateway>();
        foreach (var gw in EnabledGateways(config))
        {
            resilient.Add(new ResilientProviderGateway(BuildAdapter(gw, secretStore), resilience));
        }

        return resilient.Count switch
        {
            0 => null,
            1 => resilient[0],
            _ => new FailoverProviderGateway(resilient),
        };
    }

    private static IEnumerable<GatewayConfig> EnabledGateways(AmccaConfig config)
    {
        // AmccaConfig models one gateway today; yielding here keeps the composer ready for a list.
        var gw = config.Providers?.Gateway;
        if (gw is not null
            && gw.Enabled
            && !string.IsNullOrWhiteSpace(gw.BaseUrl)
            && !string.IsNullOrWhiteSpace(gw.ApiKeySecretRef))
        {
            yield return gw;
        }
    }

    private static IProviderGateway BuildAdapter(GatewayConfig gw, ISecretStore secretStore)
        => string.Equals(gw.Id, "omnirouters", StringComparison.OrdinalIgnoreCase)
            ? new OmniRoutersGatewayAdapter(gw.BaseUrl, secretStore, gw.ApiKeySecretRef!)
            : new DirectOpenAiCompatibleGatewayAdapter(gw.BaseUrl, secretStore, gw.ApiKeySecretRef!);
}
