using AMCCA.Core.Configuration;
using AMCCA.Core.Providers;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class ProviderGatewayComposerTests
{
    private static readonly ISecretStore Store = new InMemorySecretStore();

    private static AmccaConfig ConfigWith(GatewayConfig gw)
        => new() { Providers = new ProvidersConfig { Gateway = gw } };

    [Fact]
    public void NoGateway_WhenDisabled()
    {
        var cfg = ConfigWith(new GatewayConfig { Id = "omnirouters", Enabled = false, BaseUrl = "https://api.example/v1", ApiKeySecretRef = "secret://amcca/key" });

        ProviderGatewayComposer.Compose(cfg, Store).Should().BeNull();
    }

    [Fact]
    public void NoGateway_WhenApiKeyRefMissing()
    {
        var cfg = ConfigWith(new GatewayConfig { Id = "omnirouters", Enabled = true, BaseUrl = "https://api.example/v1", ApiKeySecretRef = null });

        ProviderGatewayComposer.Compose(cfg, Store).Should().BeNull();
    }

    [Fact]
    public void BuildsAResilientOmniRoutersGateway_WhenConfigured()
    {
        var cfg = ConfigWith(new GatewayConfig { Id = "omnirouters", Enabled = true, BaseUrl = "https://api.omnirouters.example", ApiKeySecretRef = "secret://amcca/key" });

        var gateway = ProviderGatewayComposer.Compose(cfg, Store);

        gateway.Should().BeOfType<ResilientProviderGateway>();
        gateway!.ProviderId.Should().Be("omnirouters", "the resilient wrapper exposes the inner provider id");
    }

    [Fact]
    public void BuildsADirectAdapter_ForANonOmniRoutersId()
    {
        var cfg = ConfigWith(new GatewayConfig { Id = "openai", Enabled = true, BaseUrl = "https://api.openai.example/v1", ApiKeySecretRef = "secret://amcca/key" });

        var gateway = ProviderGatewayComposer.Compose(cfg, Store);

        gateway!.ProviderId.Should().Be("direct-openai-compatible");
    }
}
