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

public class OmniRoutersGatewayAdapter : IProviderGateway, IDisposable
{
    public string ProviderId => "omnirouters";
    private readonly string _baseUrl;
    private readonly ISecretStore _secretStore;
    private readonly SecretReference _apiKeyRef;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// SEC-01: the credential is supplied as a <c>secret://vault/name</c> reference and resolved
    /// through <see cref="ISecretStore"/> at call time. A literal API key is rejected by
    /// <see cref="SecretReference.Parse"/> (AMCCA-SEC-002) and never accepted as a Bearer token.
    /// </summary>
    public OmniRoutersGatewayAdapter(
        string baseUrl,
        ISecretStore secretStore,
        string apiKeySecretRef)
        : this(baseUrl, secretStore, apiKeySecretRef, httpClient: null)
    {
    }

    // SEC-11: HttpClient injection is test-only. Production always uses the SSRF-safe handler.
    internal OmniRoutersGatewayAdapter(
        string baseUrl,
        ISecretStore secretStore,
        string apiKeySecretRef,
        HttpClient? httpClient)
    {
        _baseUrl = baseUrl?.TrimEnd('/') ?? string.Empty;
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
                $"OmniRouters credential '{_apiKeyRef}' is not present in the secret store.");
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
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            return new ProviderProbeResult(
                Success: false,
                LatencyMs: 0,
                ErrorMessage: "BaseUrl is missing.");
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
            var requestUri = $"{_baseUrl}/chat/completions";
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
                    ErrorMessage: $"Capability probe failed with HTTP {code} on OmniRouters.");
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
            var safeMessage = ex.Message.Replace(apiKey, "[REDACTED]");
            return new ProviderProbeResult(
                Success: false,
                LatencyMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"OmniRouters probe error: {safeMessage}");
        }
    }

    public async Task<GatewayTextResponse> GenerateTextAsync(
        GatewayTextRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            throw new AmccaException(
                AmccaErrors.Ai001,
                ErrorCategory.Configuration,
                "OmniRouters baseUrl is unconfigured.");
        }

        var apiKey = await ResolveApiKeyAsync(ct);

        var requestUri = $"{_baseUrl}/chat/completions";
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
                $"OmniRouters HTTP transport failure: {safeMsg}");
        }

        using (httpResponse)
        {
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Auth,
                    "Authentication failed (HTTP 401) with OmniRouters. Check credentials.");
            }

            if (httpResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Auth,
                    "Authorization forbidden (HTTP 403) with OmniRouters.");
            }

            if ((int)httpResponse.StatusCode == 429)
            {
                throw new AmccaException(
                    AmccaErrors.Ai002,
                    ErrorCategory.RateLimited,
                    "Rate limit exceeded (HTTP 429) on OmniRouters.");
            }

            if ((int)httpResponse.StatusCode >= 500)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Provider,
                    $"OmniRouters returned server error (HTTP {(int)httpResponse.StatusCode}).");
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new AmccaException(
                    AmccaErrors.Ai001,
                    ErrorCategory.Provider,
                    $"OmniRouters returned unsuccessful status code: {(int)httpResponse.StatusCode}.");
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
                        "Malformed response from OmniRouters: missing choices or message content.");
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
                    "Malformed response from OmniRouters: invalid JSON payload.");
            }
        }
    }
}
