using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Providers;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class AiProviderRealIntegrationTests
{
    private class ControlledHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (Handler != null)
            {
                return await Handler(request, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task DEF006_01_To_05_RequestSent_AuthHeader_Payload_ResponseParsing_TokenUsage()
    {
        var mockHandler = new ControlledHttpMessageHandler();
        mockHandler.Handler = (req, ct) =>
        {
            var responseJson = @"{
                ""id"": ""chatcmpl-test-123"",
                ""choices"": [
                    { ""message"": { ""role"": ""assistant"", ""content"": ""Paris is the capital of France."" } }
                ],
                ""usage"": {
                    ""prompt_tokens"": 14,
                    ""completion_tokens"": 8
                }
            }";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        };

        var httpClient = new HttpClient(mockHandler);
        var secretApiKey = "sk-super-secret-key-12345";
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", secretApiKey), "secret://test/openai", httpClient);

        var request = new GatewayTextRequest(
            ModelId: "gpt-4o",
            Prompt: "What is the capital of France?",
            Temperature: 0.7,
            MaxTokens: 100,
            CorrelationId: "corr-ai-01");

        // Act
        var response = await adapter.GenerateTextAsync(request);

        // 1. Request really sent
        mockHandler.LastRequest.Should().NotBeNull();
        mockHandler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/chat/completions");

        // 2. Auth header correct
        mockHandler.LastRequest.Headers.Authorization.Should().NotBeNull();
        mockHandler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        mockHandler.LastRequest.Headers.Authorization.Parameter.Should().Be(secretApiKey);

        // 3. Payload correct
        mockHandler.LastRequestBody.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o");
        doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("What is the capital of France?");

        // 4. Response parsing
        response.Text.Should().Be("Paris is the capital of France.");
        response.ProviderRequestId.Should().Be("chatcmpl-test-123");

        // 5. Token usage
        response.InputTokens.Should().Be(14);
        response.OutputTokens.Should().Be(8);
    }

    [Fact]
    public async Task DEF006_06_Http401_ThrowsAuthException()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", "bad-key"), "secret://test/openai", new HttpClient(mockHandler));

        var act = async () => await adapter.GenerateTextAsync(new GatewayTextRequest("gpt-4o", "test", 0.5, 50, "c-1"));

        var ex = await act.Should().ThrowAsync<AmccaException>();
        ex.Which.ErrorCode.Should().Be(AmccaErrors.Ai001);
        ex.Which.Category.Should().Be(ErrorCategory.Auth);
    }

    [Fact]
    public async Task DEF006_07_Http403_ThrowsAuthException()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", "key"), "secret://test/openai", new HttpClient(mockHandler));

        var act = async () => await adapter.GenerateTextAsync(new GatewayTextRequest("gpt-4o", "test", 0.5, 50, "c-1"));

        var ex = await act.Should().ThrowAsync<AmccaException>();
        ex.Which.ErrorCode.Should().Be(AmccaErrors.Ai001);
        ex.Which.Category.Should().Be(ErrorCategory.Auth);
    }

    [Fact]
    public async Task DEF006_08_Http429_ThrowsRateLimitException()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429))
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", "key"), "secret://test/openai", new HttpClient(mockHandler));

        var act = async () => await adapter.GenerateTextAsync(new GatewayTextRequest("gpt-4o", "test", 0.5, 50, "c-1"));

        var ex = await act.Should().ThrowAsync<AmccaException>();
        ex.Which.ErrorCode.Should().Be(AmccaErrors.Ai002);
        ex.Which.Category.Should().Be(ErrorCategory.RateLimited);
    }

    [Fact]
    public async Task DEF006_09_Http500_ThrowsProviderServerErrorException()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", "key"), "secret://test/openai", new HttpClient(mockHandler));

        var act = async () => await adapter.GenerateTextAsync(new GatewayTextRequest("gpt-4o", "test", 0.5, 50, "c-1"));

        var ex = await act.Should().ThrowAsync<AmccaException>();
        ex.Which.ErrorCode.Should().Be(AmccaErrors.Ai001);
        ex.Which.Category.Should().Be(ErrorCategory.Provider);
    }

    [Fact]
    public async Task DEF006_10_MalformedResponse_ThrowsValidationException()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ not valid json", Encoding.UTF8, "application/json")
            })
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", "key"), "secret://test/openai", new HttpClient(mockHandler));

        var act = async () => await adapter.GenerateTextAsync(new GatewayTextRequest("gpt-4o", "test", 0.5, 50, "c-1"));

        var ex = await act.Should().ThrowAsync<AmccaException>();
        ex.Which.ErrorCode.Should().Be(AmccaErrors.Ai001);
        ex.Which.Category.Should().Be(ErrorCategory.Validation);
    }

    [Fact]
    public async Task DEF006_11_And_12_Timeout_And_Cancellation()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", "key"), "secret://test/openai", new HttpClient(mockHandler));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await adapter.GenerateTextAsync(new GatewayTextRequest("gpt-4o", "test", 0.5, 50, "c-1"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DEF006_13_UnavailableProvider_ReturnsFailureProbe()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => throw new HttpRequestException("DNS resolution failed: name does not exist")
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://nonexistent.ai.gateway", TestSecretStores.With("secret://test/openai", "key"), "secret://test/openai", new HttpClient(mockHandler));

        var probe = await adapter.ProbeCapabilityAsync("openai", "gpt-4o", "chat");

        probe.Success.Should().BeFalse();
        probe.ErrorMessage.Should().Contain("DNS resolution failed");
    }

    [Fact]
    public async Task DEF006_14_WrongModel_ReturnsFailureProbe()
    {
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(@"{""error"": {""message"": ""The model 'non-existent-model' does not exist""}}", Encoding.UTF8, "application/json")
            })
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", "key"), "secret://test/openai", new HttpClient(mockHandler));

        var probe = await adapter.ProbeCapabilityAsync("openai", "non-existent-model", "chat");

        probe.Success.Should().BeFalse();
        probe.ErrorMessage.Should().Contain("404");
    }

    [Fact]
    public async Task DEF006_15_SecretNeverLogged_OrExposedInException()
    {
        var sensitiveSecret = "SK_CLASSIFIED_TOKEN_99999_DO_NOT_LEAK";
        var mockHandler = new ControlledHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Invalid authentication token", Encoding.UTF8, "text/plain")
            })
        };
        var adapter = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.com/v1", TestSecretStores.With("secret://test/openai", sensitiveSecret), "secret://test/openai", new HttpClient(mockHandler));

        try
        {
            await adapter.GenerateTextAsync(new GatewayTextRequest("gpt-4o", "test", 0.5, 50, "c-1"));
        }
        catch (Exception ex)
        {
            ex.Message.Should().NotContain(sensitiveSecret);
            ex.ToString().Should().NotContain(sensitiveSecret);
        }
    }
}
