using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Security;

namespace AMCCA.Core.Publishing;

public class TikTokPlatformAdapter : BasePlatformAdapter
{
    private readonly string _baseEndpoint;

    public override string PlatformId => "tiktok";
    protected override string BaseApiUrl => _baseEndpoint;

    public TikTokPlatformAdapter(ISafeHttpClientFactory? httpClientFactory = null, string? baseEndpoint = null)
        : base(httpClientFactory)
    {
        _baseEndpoint = baseEndpoint ?? "https://open.tiktokapis.com/v2";
    }

    public override async Task<IReadOnlyList<string>> VerifyCapabilitiesAsync(string accountId, CancellationToken ct = default)
    {
        var response = await HttpClient.GetAsync($"{_baseEndpoint}/user/info/", ct);
        var (handled, err) = HandleCommonErrors(response, accountId);
        if (handled) return Array.Empty<string>();

        return new[] { "VIDEO_UPLOAD", "DIRECT_POST", "ANALYTICS", "SYNTHETIC_LABEL" };
    }

    public override async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            post_info = new
            {
                title = request.Title,
                privacy_level = "PUBLIC_TO_EVERYONE",
                is_aigc = request.IsSynthetic
            },
            source_info = new
            {
                source = "FILE_UPLOAD",
                video_url = request.VideoPath
            }
        });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseEndpoint}/post/publish/video/init/")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("X-Idempotency-Key", request.IdempotencyKey);

        var response = await HttpClient.SendAsync(httpRequest, ct);
        var (handled, errResult) = HandleCommonErrors(response, request.AccountId);
        if (handled) return errResult!;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var publishId = doc.RootElement.GetProperty("data").GetProperty("publish_id").GetString()!;

        return new UploadResult(true, publishId, $"https://tiktok.com/@creator/video/{publishId}", null);
    }

    public override async Task<PublicationStatusResult> GetStatusAsync(string externalId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { publish_id = externalId });
        var response = await HttpClient.PostAsync(
            $"{_baseEndpoint}/post/publish/status/fetch/",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

        if (!response.IsSuccessStatusCode)
        {
            return new PublicationStatusResult("UNKNOWN", null, response.StatusCode.ToString());
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("status", out var s)
            ? s.GetString()
            : "PUBLISHED";

        var state = status switch
        {
            "PUBLISH_COMPLETE" => "PUBLISHED",
            "FAILED" => "FAILED",
            _ => "PROCESSING"
        };

        return new PublicationStatusResult(state, $"https://tiktok.com/@creator/video/{externalId}", null);
    }

    public override async Task<IReadOnlyDictionary<string, double>> GetMetricsAsync(string externalId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { filters = new { video_ids = new[] { externalId } } });
        var response = await HttpClient.PostAsync(
            $"{_baseEndpoint}/video/query/",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

        if (!response.IsSuccessStatusCode) return new Dictionary<string, double>();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var metrics = new Dictionary<string, double>();

        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("videos", out var videos) && videos.GetArrayLength() > 0)
        {
            var vid = videos[0];
            if (vid.TryGetProperty("view_count", out var v)) metrics["views"] = v.GetDouble();
            if (vid.TryGetProperty("like_count", out var l)) metrics["likes"] = l.GetDouble();
            if (vid.TryGetProperty("share_count", out var s)) metrics["shares"] = s.GetDouble();
        }

        return metrics;
    }

    public override async Task<bool> ApplySyntheticLabelAsync(string externalId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            publish_id = externalId,
            aigc_info = new { is_aigc = true, label_type = "CREATOR_DECLARED" }
        });

        var response = await HttpClient.PostAsync(
            $"{_baseEndpoint}/post/publish/aigc/declare/",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

        return response.IsSuccessStatusCode;
    }

    public override async Task<IReadOnlyList<RecentPublicationItem>> ListRecentAsync(string accountId, int limit = 10, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { max_count = limit });
        var response = await HttpClient.PostAsync(
            $"{_baseEndpoint}/video/list/",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

        if (!response.IsSuccessStatusCode) return Array.Empty<RecentPublicationItem>();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<RecentPublicationItem>();

        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("videos", out var videos))
        {
            foreach (var item in videos.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var title = item.TryGetProperty("title", out var t) ? t.GetString()! : "";
                var publishedAt = item.TryGetProperty("create_time", out var time) ? time.GetInt64().ToString() : "";
                list.Add(new RecentPublicationItem(id, title, publishedAt, $"https://tiktok.com/@creator/video/{id}"));
            }
        }

        return list;
    }
}
