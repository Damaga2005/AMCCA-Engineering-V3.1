using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Providers;
using FluentAssertions;
using Polly.CircuitBreaker;
using Xunit;

namespace AMCCA.Core.Tests;

public class ProviderResilienceContractTests
{
    private sealed class ScriptedGateway : IProviderGateway
    {
        private readonly Queue<Func<GatewayTextResponse>> _script;
        public int Calls { get; private set; }
        public int ProbeCalls { get; private set; }
        public string ProviderId => "scripted";

        public ScriptedGateway(IEnumerable<Func<GatewayTextResponse>> script) => _script = new(script);

        public Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default)
        {
            Calls++;
            var next = _script.Count > 0 ? _script.Dequeue() : (() => Ok());
            return Task.FromResult(next()); // Func may throw
        }

        public Task<ProviderProbeResult> ProbeCapabilityAsync(string p, string m, string c, CancellationToken ct = default)
        {
            ProbeCalls++;
            return Task.FromResult(new ProviderProbeResult(false, 0, "probe not retried"));
        }

        private static GatewayTextResponse Ok() => new("ok", "req", 1, 1);
        public static Func<GatewayTextResponse> Success => Ok;
        public static Func<GatewayTextResponse> Transient
            => () => throw new AmccaException(AmccaErrors.Ai001, ErrorCategory.Transient, "blip");
        public static Func<GatewayTextResponse> Provider
            => () => throw new AmccaException(AmccaErrors.Ai001, ErrorCategory.Provider, "provider down");
        public static Func<GatewayTextResponse> Validation
            => () => throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation, "bad output");
        public static Func<GatewayTextResponse> RateLimited(TimeSpan retryAfter)
            => () => throw new AmccaException(AmccaErrors.Ai002, ErrorCategory.RateLimited, "429", retryAfter: retryAfter);
    }

    private static readonly GatewayTextRequest Req = new("m1", "hi", 0.2, 64, "corr");

    private static ProviderResilienceOptions FastRetry(int maxRetries = 3) => new(
        MaxRetries: maxRetries,
        BaseDelay: TimeSpan.FromMilliseconds(5),
        MaxDelay: TimeSpan.FromSeconds(2),
        CircuitFailureRatio: 0.5,
        CircuitMinimumThroughput: 100,           // effectively off for the retry tests
        CircuitSamplingDuration: TimeSpan.FromSeconds(60),
        CircuitBreakDuration: TimeSpan.FromSeconds(60));

    private static ProviderResilienceOptions BreakerOnly => new(
        MaxRetries: 0,
        BaseDelay: TimeSpan.FromMilliseconds(5),
        MaxDelay: TimeSpan.FromSeconds(1),
        CircuitFailureRatio: 0.5,
        CircuitMinimumThroughput: 3,
        CircuitSamplingDuration: TimeSpan.FromSeconds(60),
        CircuitBreakDuration: TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Retry_RecoversAfterTransientFailures()
    {
        var gw = new ScriptedGateway(new[] { ScriptedGateway.Transient, ScriptedGateway.Transient, ScriptedGateway.Success });
        var resilient = new ResilientProviderGateway(gw, FastRetry());

        var resp = await resilient.GenerateTextAsync(Req);

        resp.Text.Should().Be("ok");
        gw.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Retry_ExhaustsThenRethrows()
    {
        var gw = new ScriptedGateway(new[]
        {
            ScriptedGateway.Transient, ScriptedGateway.Transient, ScriptedGateway.Transient, ScriptedGateway.Transient,
        });
        var resilient = new ResilientProviderGateway(gw, FastRetry(maxRetries: 2));

        var act = async () => await resilient.GenerateTextAsync(Req);

        await act.Should().ThrowAsync<AmccaException>();
        gw.Calls.Should().Be(3, "1 attempt + 2 retries");
    }

    [Fact]
    public async Task NonRetryableError_PassesThroughOnFirstAttempt()
    {
        var gw = new ScriptedGateway(new[] { ScriptedGateway.Validation });
        var resilient = new ResilientProviderGateway(gw, FastRetry());

        var act = async () => await resilient.GenerateTextAsync(Req);

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Ai003);
        gw.Calls.Should().Be(1, "a validation error is not transient — no retry");
    }

    [Fact]
    public async Task RetryAfter_FromA429_IsHonoured_OverTheBaseDelay()
    {
        var gw = new ScriptedGateway(new[] { ScriptedGateway.RateLimited(TimeSpan.FromMilliseconds(300)), ScriptedGateway.Success });
        var resilient = new ResilientProviderGateway(gw, FastRetry()); // BaseDelay is only 5ms

        var sw = Stopwatch.StartNew();
        var resp = await resilient.GenerateTextAsync(Req);
        sw.Stop();

        resp.Text.Should().Be("ok");
        sw.ElapsedMilliseconds.Should().BeGreaterThan(250, "the retry waited the provider's Retry-After, not the 5ms base delay");
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterConsistentFailures_ThenFailsFastWithoutCallingTheProvider()
    {
        var gw = new ScriptedGateway(new[]
        {
            ScriptedGateway.Provider, ScriptedGateway.Provider, ScriptedGateway.Provider,
        });
        var resilient = new ResilientProviderGateway(gw, BreakerOnly);

        for (int i = 0; i < 3; i++)
        {
            var act = async () => await resilient.GenerateTextAsync(Req);
            await act.Should().ThrowAsync<AmccaException>();
        }
        gw.Calls.Should().Be(3);

        // 4th call: circuit is open, provider is not touched.
        var fastFail = async () => await resilient.GenerateTextAsync(Req);
        var thrown = await fastFail.Should().ThrowAsync<AmccaException>();
        thrown.Which.Message.Should().Contain("circuit is open");
        thrown.Which.Retryable.Should().BeTrue();
        thrown.Which.InnerException.Should().BeOfType<BrokenCircuitException>();
        gw.Calls.Should().Be(3, "the open circuit short-circuits before reaching the provider");
    }

    [Fact]
    public async Task ProbeCapability_IsNotRetried_NorBrokenByTheBreaker()
    {
        var gw = new ScriptedGateway(Array.Empty<Func<GatewayTextResponse>>());
        var resilient = new ResilientProviderGateway(gw, FastRetry());

        var result = await resilient.ProbeCapabilityAsync("openai", "gpt-x", "text");

        result.Success.Should().BeFalse();
        gw.ProbeCalls.Should().Be(1, "probes are diagnostic — the caller wants the real, un-retried result");
    }
}
