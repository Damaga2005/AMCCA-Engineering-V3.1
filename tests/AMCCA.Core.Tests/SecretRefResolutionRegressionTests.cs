using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Providers;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// SEC-01 — the provider gateway credential must flow
/// <c>SecretReference → ISecretStore → resolved secret → Authorization: Bearer</c>.
/// A literal API key or an invalid reference is rejected; the <c>secret://</c> reference
/// itself is never sent as a Bearer token; the resolved secret never appears in an exception.
/// </summary>
public class SecretRefResolutionRegressionTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? SentAuthorizationParameter { get; private set; }
        public Func<HttpResponseMessage>? Responder { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentAuthorizationParameter = request.Headers.Authorization?.Parameter;
            var resp = Responder?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"x\",\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}",
                    Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    private static GatewayTextRequest Req() => new("gpt-4o", "hello", 0.5, 16, "corr-sec01");

    [Fact]
    public async Task ResolvedSecret_IsUsedAsBearer_NotTheReference()
    {
        const string realSecret = "sk-RESOLVED-VALUE-9f8e7d";
        var store = TestSecretStores.With("secret://vault/openai", realSecret);
        var handler = new CapturingHandler();
        using var adapter = new DirectOpenAiCompatibleGatewayAdapter(
            "https://api.example/v1", store, "secret://vault/openai", new HttpClient(handler));

        await adapter.GenerateTextAsync(Req());

        handler.SentAuthorizationParameter.Should().Be(realSecret);
        handler.SentAuthorizationParameter.Should().NotContain("secret://");
    }

    [Theory]
    [InlineData("sk-a-literal-api-key")]
    [InlineData("plainstring")]
    [InlineData("secret://only-one-segment")]
    [InlineData("http://vault/name")]
    [InlineData("")]
    public void LiteralOrMalformedReference_IsRejectedAtConstruction(string badRef)
    {
        var store = TestSecretStores.With("secret://vault/openai", "unused");

        Action act = () => new DirectOpenAiCompatibleGatewayAdapter(
            "https://api.example/v1", store, badRef, new HttpClient(new CapturingHandler()));

        act.Should().Throw<AmccaException>()
            .Which.ErrorCode.Should().Be(AmccaErrors.Sec002);
    }

    [Fact]
    public async Task SecretStoreMissingEntry_FailsClosed_WithoutCallingProvider()
    {
        var emptyStore = new InMemorySecretStore();
        var handler = new CapturingHandler();
        using var adapter = new DirectOpenAiCompatibleGatewayAdapter(
            "https://api.example/v1", emptyStore, "secret://vault/openai", new HttpClient(handler));

        var act = async () => await adapter.GenerateTextAsync(Req());

        var ex = await act.Should().ThrowAsync<AmccaException>();
        ex.Which.ErrorCode.Should().Be(AmccaErrors.Ai001);
        ex.Which.Category.Should().Be(ErrorCategory.Configuration);
        handler.SentAuthorizationParameter.Should().BeNull("the provider must not be contacted without a credential");
    }

    [Fact]
    public async Task Probe_MissingSecret_ReturnsFailure_DoesNotThrow()
    {
        var adapter = new DirectOpenAiCompatibleGatewayAdapter(
            "https://api.example/v1", new InMemorySecretStore(), "secret://vault/openai", new HttpClient(new CapturingHandler()));

        var probe = await adapter.ProbeCapabilityAsync("openai", "gpt-4o", "chat");

        probe.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ResolvedSecret_NeverAppearsInTransportException()
    {
        const string realSecret = "sk-DO-NOT-LEAK-abc123";
        var store = TestSecretStores.With("secret://vault/openai", realSecret);
        var handler = new CapturingHandler
        {
            Responder = () => throw new HttpRequestException($"connect failed using token {realSecret}")
        };
        using var adapter = new DirectOpenAiCompatibleGatewayAdapter(
            "https://api.example/v1", store, "secret://vault/openai", new HttpClient(handler));

        try
        {
            await adapter.GenerateTextAsync(Req());
            Assert.Fail("expected throw");
        }
        catch (Exception ex)
        {
            ex.Message.Should().NotContain(realSecret);
            ex.ToString().Should().NotContain(realSecret);
        }
    }

    [Fact]
    public async Task OmniRoutersAdapter_AlsoResolvesThroughSecretStore()
    {
        const string realSecret = "or-RESOLVED-key-42";
        var store = TestSecretStores.With("secret://vault/omni", realSecret);
        var handler = new CapturingHandler();
        using var adapter = new OmniRoutersGatewayAdapter(
            "https://omni.example", store, "secret://vault/omni", new HttpClient(handler));

        await adapter.GenerateTextAsync(Req());

        handler.SentAuthorizationParameter.Should().Be(realSecret);
    }
}
