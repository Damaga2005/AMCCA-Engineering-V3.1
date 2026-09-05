using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Security;

namespace AMCCA.Core.Providers;

public class DirectOpenAiCompatibleGatewayAdapter : IProviderGateway, IDisposable
{
    public string ProviderId => "direct-openai-compatible";
    private readonly string _endpoint;
    private readonly ISecretStore _secretStore;
    private readonly SecretReference _apiKeyRef;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// SEC-01: the credential is supplied as a <c>secret://vault/name</c> reference and resolved
    /// through <see cref="ISecretStore"/> at call time. A literal API key is rejected by
    /// <see cref="SecretReference.Parse"/> (AMCCA-SEC-002) and never accepted as a Bearer token.
    /// </summary>
    public DirectOpenAiCompatibleGatewayAdapter(
        string endpoint,
        ISecretStore secretStore,
        string apiKeySecretRef)
        : this(endpoint, secretStore, apiKeySecretRef, httpClient: null)
    {
    }

    // SEC-11: HttpClient injection is test-only. Production always uses the SSRF-safe handler.
    internal DirectOpenAiCompatibleGatewayAdapter(
        string endpoint,
        ISecretStore secretStore,
        string apiKeySecretRef,
        HttpClient? httpClient)
    {
        _endpoint = endpoint?.TrimEnd('/') ?? string.Empty;
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _apiKeyRef = SecretReference.Parse(apiKeySecretRef);
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient(SsrfValidator.CreateSafeSocketsHttpHandler());
            _ownsHttpClient = true;
        }
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken ct)
    {
        var key = await _secretStore.GetSecretAsync(_apiKeyRef, ct);
        if (string.IsNullOrEmpty(key))
        {
            throw new AmccaException(
                AmccaErrors.Ai001,
                ErrorCategory.Configuration,
                $"Model provider credential '{_apiKeyRef}' is not present in the secret store.");
        }
        return key;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<ProviderProbeResult> ProbeCapabilityAsync(
        string provider,
        string modelId,
        string capability,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            return new ProviderProbeResult(
                Success: false,
                LatencyMs: 0,
                ErrorMessage: "Endpoint missing.");
        }

        var sw = Stopwatch.StartNew();
        string apiKey;
        try
        {
            apiKey = await ResolveApiKeyAsync(ct);
        }
        catch (AmccaException ex)
        {
            sw.Stop();
            return new ProviderProbeResult(Success: false, LatencyMs: 0, ErrorMessage: ex.Message);
        }

        try
        {
            // Perform real lightweight capability probe using chat completions with max_tokens=1
            var requestUri = $"{_endpoint}/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, requestUri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var probeBody = new
            {
                model = modelId,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1
            };
            req.Content = new StringContent(JsonSerializer.Serialize(probeBody), Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(req, ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                return new ProviderProbeResult(
                    Success: false,
                    LatencyMs: sw.ElapsedMilliseconds,
                    ErrorMessage: $"Capability probe failed with HTTP {code}. Model '{modelId}' may be invalid or unauthorized.");
            }

            return new ProviderProbeResult(
                Success: true,
                LatencyMs: sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Sanitize message: never leak secret key
            var safeMessage = ex.Message.Replace(apiKey, "[REDACTED]");
            return new ProviderProbeResult(
                Success: false,
                LatencyMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"Provider probe error: {safeMessage}");
        }
    }

    public async Task<GatewayTextResponse> GenerateTextAsync(
        GatewayTextRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            throw new AmccaException(
                AmccaErrors.Ai001,
                ErrorCategory.Configuration,
                "Model provider endpoint is unconfigured.");
        }

        var apiKey = await ResolveApiKeyAsync(ct);

        var requestUri = $"{_endpoint}/chat/completions";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Add("X-Correlation-Id", request.CorrelationId);

        var payload = new
        {
            model = request.ModelId,
            messages = new[]
            {
                new { role = "user", content = request.Prompt }
            },
            temperature = request.Temperature,
            max_tokens = request.MaxTokens
        };

        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.SendAsync(httpRequest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var safeMsg = ex.Message.Replace(apiKey, "[REDACTED]");
            throw new AmccaException(
                AmccaErrors.Ai001,
                ErrorCategory.Provider,
                $"Model provider HTTP transport failure: {safeMsg}");
        }

        using (httpResponse)
        {
            // Map HTTP status codes to normative AMCCA errors
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Auth,
                    "Authentication failed (HTTP 401) with model provider. Check credentials.");
            }

            if (httpResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Auth,
                    "Authorization forbidden (HTTP 403) with model provider.");
            }

            if ((int)httpResponse.StatusCode == 429)
            {
                throw new AmccaException(
                    AmccaErrors.Ai002,
                    ErrorCategory.RateLimited,
                    "Rate limit exceeded (HTTP 429) on AI model gateway.",
                    retryAfter: ReadRetryAfter(httpResponse));
            }

            if ((int)httpResponse.StatusCode >= 500)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Provider,
                    $"Model provider returned server error (HTTP {(int)httpResponse.StatusCode}).");
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Provider,
                    $"Model provider returned unsuccessful status code: {(int)httpResponse.StatusCode}.");
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                if (!root.TryGetProperty("choices", out var choicesProp) ||
                    choicesProp.GetArrayLength() == 0 ||
                    !choicesProp[0].TryGetProperty("message", out var msgProp) ||
                    !msgProp.TryGetProperty("content", out var contentProp))
                {
                    throw new AmccaException(
                        AmccaErrors.Ai001,
                        ErrorCategory.Validation,
                        "Malformed response from model provider: missing choices or message content.");
                }

                var text = contentProp.GetString() ?? string.Empty;

                long promptTokens = 0;
                long completionTokens = 0;

                if (root.TryGetProperty("usage", out var usageProp))
                {
                    if (usageProp.TryGetProperty("prompt_tokens", out var pt))
                    {
                        promptTokens = pt.GetInt64();
                    }
                    if (usageProp.TryGetProperty("completion_tokens", out var ctProp))
                    {
                        completionTokens = ctProp.GetInt64();
                    }
                }

                return new GatewayTextResponse(
                    Text: text,
                    ProviderRequestId: id,
                    InputTokens: promptTokens,
                    OutputTokens: completionTokens);
            }
            catch (JsonException)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Validation,
                    "Malformed response from model provider: invalid JSON payload.");
            }
        }
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        GatewayTextRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            throw new AmccaException(
                AmccaErrors.Ai001,
                ErrorCategory.Configuration,
                "Model provider endpoint missing.");
        }

        var apiKey = await ResolveApiKeyAsync(ct);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var payload = new
        {
            model = request.ModelId,
            messages = new[]
            {
                new { role = "user", content = request.Prompt }
            },
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            stream = true
        };

        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var safeMsg = ex.Message.Replace(apiKey, "[REDACTED]");
            throw new AmccaException(
                AmccaErrors.Ai001,
                ErrorCategory.Provider,
                $"Model provider HTTP transport failure during stream: {safeMsg}");
        }

        using (httpResponse)
        {
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AmccaException(AmccaErrors.Ai001, ErrorCategory.Auth, "Authentication failed (HTTP 401).");
            }
            if ((int)httpResponse.StatusCode == 429)
            {
                throw new AmccaException(AmccaErrors.Ai002, ErrorCategory.RateLimited, "Rate limit exceeded (HTTP 429).",
                    retryAfter: ReadRetryAfter(httpResponse));
            }
            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new AmccaException(AmccaErrors.Ai001, ErrorCategory.Provider, $"Provider error (HTTP {(int)httpResponse.StatusCode}).");
            }

            using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (System.IO.IOException ex)
                {
                    throw new AmccaException(AmccaErrors.Ai001, ErrorCategory.Provider, $"Stream connection aborted: {ex.Message}");
                }

                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:")) continue;

                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;

                string? deltaText = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var contentProp))
                    {
                        deltaText = contentProp.GetString();
                    }
                }
                catch (JsonException) { }

                if (!string.IsNullOrEmpty(deltaText))
                {
                    yield return deltaText;
                }
            }
        }
    }

    /// <summary>The HTTP <c>Retry-After</c> as a delay, whether given as delta-seconds or an HTTP date.</summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (ra.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }
}
