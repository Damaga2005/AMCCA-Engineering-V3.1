using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Publishing;

public class InstagramPlatformAdapter : BasePlatformAdapter
{
    private readonly string _baseEndpoint;

    public override string PlatformId => "instagram";
    protected override string BaseApiUrl => _baseEndpoint;

    public InstagramPlatformAdapter(HttpClient? httpClient = null, string? baseEndpoint = null)
        : base(httpClient)
    {
        _baseEndpoint = baseEndpoint ?? "https://graph.facebook.com/v19.0";
    }

    public override async Task<IReadOnlyList<string>> VerifyCapabilitiesAsync(string accountId, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/{accountId}?fields=id,name", ct);
        var (handled, err) = HandleCommonErrors(response, accountId);
        if (handled) return Array.Empty<string>();

        return new[] { "REELS_UPLOAD", "FEED_VIDEO", "INSIGHTS", "SYNTHETIC_LABEL" };
    }

    public override async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default)
    {
        // 1. Create media container
        var containerPayload = JsonSerializer.Serialize(new
        {
            media_type = "REELS",
            video_url = request.VideoPath,
            caption = request.Description,
            thumb_offset = 1000
        });

        var containerRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseEndpoint}/{request.AccountId}/media")
        {
            Content = new StringContent(containerPayload, Encoding.UTF8, "application/json")
        };
        containerRequest.Headers.Add("X-Idempotency-Key", request.IdempotencyKey);

        var containerResponse = await HttpClient.SendAsync(containerRequest, ct);
        var (handled, errResult) = HandleCommonErrors(containerResponse, request.AccountId);
        if (handled) return errResult!;

        var json = await containerResponse.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var creationId = doc.RootElement.GetProperty("id").GetString()!;

        // 2. Publish container
        var publishPayload = JsonSerializer.Serialize(new { creation_id = creationId });
        var publishResponse = await HttpClient.PostAsync(
            $"{_baseEndpoint}/{request.AccountId}/media_publish",
            new StringContent(publishPayload, Encoding.UTF8, "application/json"), ct);

        var (handledPub, errPub) = HandleCommonErrors(publishResponse, request.AccountId);
        if (handledPub) return errPub!;

        var pubJson = await publishResponse.Content.ReadAsStringAsync(ct);
        using var pubDoc = JsonDocument.Parse(pubJson);
        var mediaId = pubDoc.RootElement.GetProperty("id").GetString()!;

        if (request.IsSynthetic)
        {
            await ApplySyntheticLabelAsync(mediaId, ct);
        }

        return new UploadResult(true, mediaId, $"https://instagram.com/reel/{mediaId}", null);
    }

    public override async Task<PublicationStatusResult> GetStatusAsync(string externalId, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/{externalId}?fields=status_code,permalink", ct);
        if (!response.IsSuccessStatusCode)
        {
            return new PublicationStatusResult("UNKNOWN", null, response.StatusCode.ToString());
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var statusCode = doc.RootElement.TryGetProperty("status_code", out var sc) ? sc.GetString() : "FINISHED";
        var permalink = doc.RootElement.TryGetProperty("permalink", out var p) ? p.GetString() : null;

        var state = statusCode switch
        {
            "FINISHED" => "PUBLISHED",
            "ERROR" => "FAILED",
            _ => "PROCESSING"
        };

        return new PublicationStatusResult(state, permalink ?? $"https://instagram.com/p/{externalId}", null);
    }

    public override async Task<IReadOnlyDictionary<string, double>> GetMetricsAsync(string externalId, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/{externalId}/insights?metric=reach,saved,likes", ct);
        if (!response.IsSuccessStatusCode) return new Dictionary<string, double>();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var metrics = new Dictionary<string, double>();

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                if (item.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
                {
                    metrics[name] = vals[0].GetProperty("value").GetDouble();
                }
            }
        }

        return metrics;
    }

    public override async Task<bool> ApplySyntheticLabelAsync(string externalId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { is_ai_generated = true });
        var response = await HttpClient.PostAsync(
            $"{_baseEndpoint}/{externalId}/content_disclosure",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

        return response.IsSuccessStatusCode;
    }

    public override async Task<IReadOnlyList<RecentPublicationItem>> ListRecentAsync(string accountId, int limit = 10, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/{accountId}/media?fields=id,caption,timestamp,permalink&limit={limit}", ct);
        if (!response.IsSuccessStatusCode) return Array.Empty<RecentPublicationItem>();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<RecentPublicationItem>();

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var caption = item.TryGetProperty("caption", out var c) ? c.GetString()! : "";
                var ts = item.TryGetProperty("timestamp", out var t) ? t.GetString()! : "";
                var link = item.TryGetProperty("permalink", out var pl) ? pl.GetString()! : "";
                list.Add(new RecentPublicationItem(id, caption, ts, link));
            }
        }

        return list;
    }
}
