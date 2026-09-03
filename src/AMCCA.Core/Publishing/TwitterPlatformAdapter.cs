using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Publishing;

public class TwitterPlatformAdapter : BasePlatformAdapter
{
    private readonly string _baseEndpoint;

    public override string PlatformId => "twitter";
    protected override string BaseApiUrl => _baseEndpoint;

    public TwitterPlatformAdapter(HttpClient? httpClient = null, string? baseEndpoint = null)
        : base(httpClient)
    {
        _baseEndpoint = baseEndpoint ?? "https://api.twitter.com/2";
    }

    public override async Task<IReadOnlyList<string>> VerifyCapabilitiesAsync(string accountId, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/users/me", ct);
        var (handled, err) = HandleCommonErrors(response, accountId);
        if (handled) return Array.Empty<string>();

        return new[] { "TWEET_WRITE", "MEDIA_UPLOAD", "METRICS", "SYNTHETIC_LABEL" };
    }

    public override async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default)
    {
        var text = request.Title;
        if (request.IsSynthetic)
        {
            text += " #AIGenerated";
        }

        var payload = JsonSerializer.Serialize(new
        {
            text = text
        });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseEndpoint}/tweets")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("X-Idempotency-Key", request.IdempotencyKey);

        var response = await HttpClient.SendAsync(httpRequest, ct);
        var (handled, errResult) = HandleCommonErrors(response, request.AccountId);
        if (handled) return errResult!;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var tweetId = doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        return new UploadResult(true, tweetId, $"https://twitter.com/i/status/{tweetId}", null);
    }

    public override async Task<PublicationStatusResult> GetStatusAsync(string externalId, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/tweets/{externalId}?tweet.fields=created_at", ct);
        if (!response.IsSuccessStatusCode)
        {
            return new PublicationStatusResult("UNKNOWN", null, response.StatusCode.ToString());
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            return new PublicationStatusResult("UNKNOWN", null, "NOT_FOUND");
        }

        return new PublicationStatusResult("PUBLISHED", $"https://twitter.com/i/status/{externalId}", null);
    }

    public override async Task<IReadOnlyDictionary<string, double>> GetMetricsAsync(string externalId, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/tweets/{externalId}?tweet.fields=public_metrics", ct);
        if (!response.IsSuccessStatusCode) return new Dictionary<string, double>();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var metrics = new Dictionary<string, double>();

        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("public_metrics", out var m))
        {
            if (m.TryGetProperty("impression_count", out var imp)) metrics["impressions"] = imp.GetDouble();
            if (m.TryGetProperty("like_count", out var l)) metrics["likes"] = l.GetDouble();
            if (m.TryGetProperty("retweet_count", out var rt)) metrics["retweets"] = rt.GetDouble();
        }

        return metrics;
    }

    public override async Task<bool> ApplySyntheticLabelAsync(string externalId, CancellationToken ct = default)
    {
        // On X/Twitter, synthetic disclosure is declared in tweet text metadata
        return await Task.FromResult(true);
    }

    public override async Task<IReadOnlyList<RecentPublicationItem>> ListRecentAsync(string accountId, int limit = 10, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/users/{accountId}/tweets?max_results={limit}", ct);
        if (!response.IsSuccessStatusCode) return Array.Empty<RecentPublicationItem>();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<RecentPublicationItem>();

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var text = item.TryGetProperty("text", out var t) ? t.GetString()! : "";
                list.Add(new RecentPublicationItem(id, text, DateTimeOffset.UtcNow.ToString("O"), $"https://twitter.com/i/status/{id}"));
            }
        }

        return list;
    }
}
