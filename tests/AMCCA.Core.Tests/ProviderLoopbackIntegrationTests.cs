using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Providers;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class ProviderLoopbackIntegrationTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly string _serverUrl;
    private readonly CancellationTokenSource _serverCts;
    private readonly Task _serverLoopTask;

    public ProviderLoopbackIntegrationTests()
    {
        _port = GetFreePort();
        _serverUrl = $"http://127.0.0.1:{_port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_serverUrl);
        _listener.Start();

        _serverCts = new CancellationTokenSource();
        _serverLoopTask = Task.Run(async () =>
        {
            while (!_serverCts.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = HandleRequestAsync(ctx);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _serverCts.Cancel();
            if (_listener.IsListening) _listener.Stop();
            _listener.Close();
        }
        catch { }
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "";

        if (path.Contains("/rate_limited"))
        {
            resp.StatusCode = 429;
            resp.Headers.Add("Retry-After", "15");
            var bytes = Encoding.UTF8.GetBytes("{\"error\":\"rate_limited\"}");
            resp.ContentType = "application/json";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
            resp.Close();
            return;
        }

        if (path.Contains("/server_error"))
        {
            resp.StatusCode = 500;
            var bytes = Encoding.UTF8.GetBytes("{\"error\":\"internal_error\"}");
            resp.ContentType = "application/json";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
            resp.Close();
            return;
        }

        if (path.Contains("/abort_midstream"))
        {
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream";
            var chunk1 = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Part1 \"}}]}\n\n");
            await resp.OutputStream.WriteAsync(chunk1);
            await resp.OutputStream.FlushAsync();

            // Abruptly sever connection
            resp.Abort();
            return;
        }

        if (path.EndsWith("/chat/completions"))
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            bool isStream = body.Contains("\"stream\":true");

            if (isStream)
            {
                resp.StatusCode = 200;
                resp.ContentType = "text/event-stream";

                var events = new[]
                {
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}\n\n",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"world \"}}]}\n\n",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"from AMCCA!\"}}]}\n\n",
                    "data: [DONE]\n\n"
                };

                foreach (var ev in events)
                {
                    var b = Encoding.UTF8.GetBytes(ev);
                    await resp.OutputStream.WriteAsync(b);
                    await resp.OutputStream.FlushAsync();
                    await Task.Delay(20);
                }

                resp.Close();
                return;
            }
            else
            {
                resp.StatusCode = 200;
                resp.ContentType = "application/json";
                var jsonResp = JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-loopback-123",
                    choices = new[]
                    {
                        new { message = new { role = "assistant", content = "Synthesized response from loopback server." } }
                    },
                    usage = new { prompt_tokens = 10, completion_tokens = 15 }
                });
                var b = Encoding.UTF8.GetBytes(jsonResp);
                resp.ContentLength64 = b.Length;
                await resp.OutputStream.WriteAsync(b);
                resp.Close();
                return;
            }
        }

        resp.StatusCode = 404;
        resp.Close();
    }

    [Fact]
    public async Task LoopbackProvider_RealHttpRequest_ReturnsValidResponse()
    {
        using var client = new HttpClient();
        using var adapter = new DirectOpenAiCompatibleGatewayAdapter(_serverUrl, "test-api-key", client);

        var request = new GatewayTextRequest(
            ModelId: "gpt-4o-mini",
            Prompt: "Tell me about autonomous agents.",
            Temperature: 0.7,
            MaxTokens: 100);

        var response = await adapter.GenerateTextAsync(request);

        response.Should().NotBeNull();
        response.Text.Should().Contain("Synthesized response from loopback server");
        response.ProviderRequestId.Should().Be("chatcmpl-loopback-123");
        response.InputTokens.Should().Be(10);
        response.OutputTokens.Should().Be(15);
    }

    [Fact]
    public async Task LoopbackProvider_RealSseStreaming_YieldsTokensInOrder()
    {
        using var client = new HttpClient();
        using var adapter = new DirectOpenAiCompatibleGatewayAdapter(_serverUrl, "test-api-key", client);

        var request = new GatewayTextRequest(
            ModelId: "gpt-4o-mini",
            Prompt: "Stream me a message.",
            Temperature: 0.5,
            MaxTokens: 50);

        var receivedTokens = new List<string>();
        await foreach (var token in adapter.StreamTextAsync(request))
        {
            receivedTokens.Add(token);
        }

        receivedTokens.Should().NotBeEmpty();
        string.Join("", receivedTokens).Should().Be("Hello world from AMCCA!");
    }

    [Fact]
    public async Task LoopbackProvider_RateLimit429_MapsToAmccaAi002()
    {
        using var client = new HttpClient();
        using var adapter = new DirectOpenAiCompatibleGatewayAdapter($"{_serverUrl}rate_limited", "test-api-key", client);

        var request = new GatewayTextRequest("gpt-4o", "Hello", 0.7, 50);

        var act = async () => await adapter.GenerateTextAsync(request);
        var ex = await act.Should().ThrowAsync<AmccaException>();

        ex.Which.ErrorCode.Should().Be(AmccaErrors.Ai002);
        ex.Which.Category.Should().Be(ErrorCategory.RateLimited);
    }

    [Fact]
    public async Task LoopbackProvider_StreamAbortedMidway_ThrowsProviderException()
    {
        using var client = new HttpClient();
        using var adapter = new DirectOpenAiCompatibleGatewayAdapter($"{_serverUrl}abort_midstream", "test-api-key", client);

        var request = new GatewayTextRequest("gpt-4o", "Hello", 0.7, 50);

        var act = async () =>
        {
            await foreach (var _ in adapter.StreamTextAsync(request))
            {
                // Reading stream
            }
        };

        var ex = await act.Should().ThrowAsync<AmccaException>();
        ex.Which.Category.Should().Be(ErrorCategory.Provider);
    }

    [Fact]
    public async Task FailoverProviderGateway_PrimaryFailsWith500_SeamlesslyFallsBackToSecondary()
    {
        using var client = new HttpClient();
        using var primaryFailing = new DirectOpenAiCompatibleGatewayAdapter($"{_serverUrl}server_error", "key-1", client);
        using var secondarySucceeding = new DirectOpenAiCompatibleGatewayAdapter(_serverUrl, "key-2", client);

        var failover = new FailoverProviderGateway(new[] { primaryFailing, secondarySucceeding });

        var request = new GatewayTextRequest("gpt-4o-mini", "Failover test", 0.7, 50);
        var response = await failover.GenerateTextAsync(request);

        response.Should().NotBeNull();
        response.Text.Should().Contain("Synthesized response from loopback server");
        failover.FallbackCount.Should().Be(1, "should have fallen back once from primary to secondary");
    }

    [Fact]
    public void SsrfValidation_PrivateIpRanges_AreStrictlyBlocked()
    {
        // SPEC/71 & AUDIT-005 / AUDIT-010: Private targets must be rejected
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("127.0.0.1")).Should().BeTrue();
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("10.0.0.1")).Should().BeTrue();
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("192.168.1.1")).Should().BeTrue();
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("172.16.0.5")).Should().BeTrue();
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("169.254.169.254")).Should().BeTrue("cloud metadata endpoint must be blocked");
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("::1")).Should().BeTrue();

        // Public IPs must be permitted
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("8.8.8.8")).Should().BeFalse();
        SsrfValidator.IsPrivateOrReservedIp(IPAddress.Parse("93.184.216.34")).Should().BeFalse();
    }
}
