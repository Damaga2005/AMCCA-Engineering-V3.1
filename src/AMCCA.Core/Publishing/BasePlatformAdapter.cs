using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Publishing;

public abstract class BasePlatformAdapter : IPlatformAdapter
{
    protected readonly HttpClient HttpClient;
    public Action<string>? OnUnauthorizedCallback { get; set; }

    public abstract string PlatformId { get; }
    protected abstract string BaseApiUrl { get; }

    protected BasePlatformAdapter(HttpClient? httpClient = null)
    {
        HttpClient = httpClient ?? new HttpClient();
    }

    public virtual async Task<PublicationEvidenceResult> PollAuthoritativeEvidenceAsync(string externalId, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(externalId, ct);
        return new PublicationEvidenceResult(
            IsPublished: status.State == "PUBLISHED",
            ExternalUrl: status.ExternalUrl ?? $"{BaseApiUrl}/v/{externalId}",
            EvidenceSource: "OFFICIAL_API",
            RetrievedAt: DateTimeOffset.UtcNow.ToString("O")
        );
    }

    public abstract Task<IReadOnlyList<string>> VerifyCapabilitiesAsync(string accountId, CancellationToken ct = default);
    public abstract Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default);
    public abstract Task<PublicationStatusResult> GetStatusAsync(string externalId, CancellationToken ct = default);
    public abstract Task<IReadOnlyDictionary<string, double>> GetMetricsAsync(string externalId, CancellationToken ct = default);
    public abstract Task<bool> ApplySyntheticLabelAsync(string externalId, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<RecentPublicationItem>> ListRecentAsync(string accountId, int limit = 10, CancellationToken ct = default);

    protected (bool Handled, UploadResult? Result) HandleCommonErrors(HttpResponseMessage response, string accountId)
    {
        if (response.IsSuccessStatusCode)
        {
            return (false, null);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            OnUnauthorizedCallback?.Invoke(accountId);
            return (true, new UploadResult(false, null, null, AmccaErrors.Plt002));
        }

        if (response.StatusCode == (HttpStatusCode)429)
        {
            var retryAfter = ParseRetryAfter(response.Headers);
            return (true, new UploadResult(false, null, null, AmccaErrors.Plt003, retryAfter));
        }

        return (true, new UploadResult(false, null, null, AmccaErrors.Plt001));
    }

    protected static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.RetryAfter != null)
        {
            if (headers.RetryAfter.Delta.HasValue)
                return headers.RetryAfter.Delta.Value;
            if (headers.RetryAfter.Date.HasValue)
            {
                var delta = headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(1);
            }
        }

        if (headers.TryGetValues("x-rate-limit-reset", out var resetVals))
        {
            var val = resetVals.GetEnumerator();
            if (val.MoveNext() && long.TryParse(val.Current, out var epochSec))
            {
                var resetTime = DateTimeOffset.FromUnixTimeSeconds(epochSec);
                var diff = resetTime - DateTimeOffset.UtcNow;
                return diff > TimeSpan.Zero ? diff : TimeSpan.FromSeconds(1);
            }
        }

        return TimeSpan.FromSeconds(60); // default backoff
    }
}
