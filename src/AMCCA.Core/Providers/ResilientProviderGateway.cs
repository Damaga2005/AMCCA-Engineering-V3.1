using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace AMCCA.Core.Providers;

public sealed record ProviderResilienceOptions(
    int MaxRetries,
    TimeSpan BaseDelay,
    TimeSpan MaxDelay,
    double CircuitFailureRatio,
    int CircuitMinimumThroughput,
    TimeSpan CircuitSamplingDuration,
    TimeSpan CircuitBreakDuration)
{
    public static ProviderResilienceOptions Default => new(
        MaxRetries: 3,
        BaseDelay: TimeSpan.FromMilliseconds(500),
        MaxDelay: TimeSpan.FromSeconds(30),
        CircuitFailureRatio: 0.5,
        CircuitMinimumThroughput: 5,
        CircuitSamplingDuration: TimeSpan.FromSeconds(30),
        CircuitBreakDuration: TimeSpan.FromSeconds(15));
}

/// <summary>
/// Wraps one <see cref="IProviderGateway"/> with a Polly pipeline (SPEC/23): retry with exponential
/// backoff + jitter on transient / rate-limited errors, honouring an HTTP <c>Retry-After</c> when the
/// provider gave one; and a per-provider circuit breaker that fails fast once a provider is
/// consistently failing. Compose several of these inside a <see cref="FailoverProviderGateway"/> so
/// failover happens only after a provider's own retries and breaker are exhausted.
///
/// Probes are diagnostic and pass straight through — the caller (model verification) wants the real,
/// un-retried result.
/// </summary>
public sealed class ResilientProviderGateway : IProviderGateway
{
    private readonly IProviderGateway _inner;
    private readonly ResiliencePipeline _pipeline;

    public string ProviderId => _inner.ProviderId;

    public ResilientProviderGateway(IProviderGateway inner, ProviderResilienceOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        var o = options ?? ProviderResilienceOptions.Default;

        var builder = new ResiliencePipelineBuilder();

        if (o.MaxRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmccaException>(IsRetryable),
                MaxRetryAttempts = o.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = o.BaseDelay,
                MaxDelay = o.MaxDelay,
                DelayGenerator = args =>
                {
                    if (args.Outcome.Exception is AmccaException { RetryAfter: { } retryAfter })
                    {
                        var capped = retryAfter > o.MaxDelay ? o.MaxDelay : retryAfter;
                        return new ValueTask<TimeSpan?>(capped);
                    }
                    return new ValueTask<TimeSpan?>((TimeSpan?)null); // fall back to exponential + jitter
                },
            });
        }

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<AmccaException>(IsProviderFault),
            FailureRatio = o.CircuitFailureRatio,
            MinimumThroughput = o.CircuitMinimumThroughput,
            SamplingDuration = o.CircuitSamplingDuration,
            BreakDuration = o.CircuitBreakDuration,
        });

        _pipeline = builder.Build();
    }

    public async Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async token => await _inner.GenerateTextAsync(request, token), ct);
        }
        catch (BrokenCircuitException ex)
        {
            throw new AmccaException(
                AmccaErrors.Ai001,
                ErrorCategory.Provider,
                $"Provider '{_inner.ProviderId}' circuit is open after repeated failures; the call was not attempted.",
                retryable: true,
                innerException: ex);
        }
    }

    public Task<ProviderProbeResult> ProbeCapabilityAsync(string provider, string modelId, string capability, CancellationToken ct = default)
        => _inner.ProbeCapabilityAsync(provider, modelId, capability, ct);

    public IAsyncEnumerable<string> StreamTextAsync(GatewayTextRequest request, CancellationToken ct = default)
        // A stream cannot be safely retried once bytes have been yielded, so it passes straight through
        // to the inner gateway (which still maps HTTP faults to AmccaException).
        => _inner.StreamTextAsync(request, ct);

    private static bool IsRetryable(AmccaException ex)
        => ex.Category is ErrorCategory.Transient or ErrorCategory.RateLimited;

    private static bool IsProviderFault(AmccaException ex)
        => ex.Category is ErrorCategory.Transient or ErrorCategory.RateLimited or ErrorCategory.Provider;
}
