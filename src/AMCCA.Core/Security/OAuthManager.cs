using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Security;

public class OAuthManager
{
    private readonly DatabaseConnectionFactory _factory;
    private readonly ISecretStore _secretStore;
    private readonly HttpClient _httpClient;

    public OAuthManager(DatabaseConnectionFactory factory, ISecretStore secretStore, HttpClient? httpClient = null)
    {
        _factory = factory;
        _secretStore = secretStore;
        _httpClient = httpClient ?? new HttpClient();
    }

    public OAuthAuthorizationRequest InitiateAuthorization(
        string platform,
        string authorizationEndpoint,
        string clientId,
        string redirectUri,
        IReadOnlyList<string> scopes)
    {
        // 1. Generate cryptographically random state & PKCE verifier + challenge (SPEC/43)
        var state = GenerateRandomString(32);
        var verifier = GenerateRandomString(64);
        var challenge = GeneratePkceChallenge(verifier);

        var scopeStr = Uri.EscapeDataString(string.Join(" ", scopes));
        var url = $"{authorizationEndpoint}?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(clientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&scope={scopeStr}" +
                  $"&state={state}" +
                  $"&code_challenge={challenge}" +
                  $"&code_challenge_method=S256";

        return new OAuthAuthorizationRequest(url, state, verifier, redirectUri);
    }

    public async Task<OAuthTokenBundle> ExchangeCodeAsync(
        string platform,
        string accountId,
        string tokenEndpoint,
        string clientId,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri
        };

        var response = await _httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(parameters), ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new AmccaException(AmccaErrors.Plt002, ErrorCategory.Auth, $"Token exchange failed with {response.StatusCode}: {err}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var bundle = ParseTokenBundle(json);

        // Store encrypted tokens in SecretStore (SPEC/43)
        await StoreTokensAsync(platform, accountId, bundle, ct);
        return bundle;
    }

    public async Task<OAuthTokenBundle?> RefreshTokenAsync(
        string platform,
        string accountId,
        string tokenEndpoint,
        string clientId,
        CancellationToken ct = default)
    {
        var currentBundle = await GetStoredTokensAsync(platform, accountId, ct);
        if (currentBundle == null || string.IsNullOrEmpty(currentBundle.RefreshToken))
        {
            await MarkReauthRequiredAsync(accountId, "Missing refresh token", ct);
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = currentBundle.RefreshToken
        };

        var response = await _httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(parameters), ct);
        if (!response.IsSuccessStatusCode)
        {
            // SPEC/43: A refresh failure moves the account to REAUTH_REQUIRED, blocks autonomous publication, and audits
            await MarkReauthRequiredAsync(accountId, $"Refresh failed with {response.StatusCode}", ct);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var newBundle = ParseTokenBundle(json, fallbackRefreshToken: currentBundle.RefreshToken);

        await StoreTokensAsync(platform, accountId, newBundle, ct);
        return newBundle;
    }

    public async Task RevokeTokenAsync(
        string platform,
        string accountId,
        string revocationEndpoint,
        string clientId,
        CancellationToken ct = default)
    {
        var currentBundle = await GetStoredTokensAsync(platform, accountId, ct);
        if (currentBundle != null)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["token"] = currentBundle.AccessToken
                };
                await _httpClient.PostAsync(revocationEndpoint, new FormUrlEncodedContent(parameters), ct);
            }
            catch { }
        }

        // SPEC/43: Disconnecting deletes it from secret store and marks account DISCONNECTED
        var secretRef = SecretReference.Parse($"secret://platform/{platform}_{accountId}");
        await _secretStore.SetSecretAsync(secretRef, "", ct);

        using var conn = await _factory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE platform_accounts
            SET state = 'DISCONNECTED', updated_at = datetime('now')
            WHERE id = @AccountId;
        ", new { AccountId = accountId });
    }

    public async Task<OAuthTokenBundle?> GetStoredTokensAsync(string platform, string accountId, CancellationToken ct = default)
    {
        var secretRef = SecretReference.Parse($"secret://platform/{platform}_{accountId}");
        var secretVal = await _secretStore.GetSecretAsync(secretRef, ct);
        if (string.IsNullOrEmpty(secretVal)) return null;

        return JsonSerializer.Deserialize<OAuthTokenBundle>(secretVal);
    }

    public async Task StoreTokensAsync(string platform, string accountId, OAuthTokenBundle bundle, CancellationToken ct = default)
    {
        var secretRef = SecretReference.Parse($"secret://platform/{platform}_{accountId}");
        var secretVal = JsonSerializer.Serialize(bundle);
        await _secretStore.SetSecretAsync(secretRef, secretVal, ct);
    }

    public async Task MarkReauthRequiredAsync(string accountId, string reason, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateOpenConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(@"
            UPDATE platform_accounts
            SET state = 'REAUTH_REQUIRED', updated_at = datetime('now')
            WHERE id = @AccountId;
        ", new { AccountId = accountId }, transaction: tx);

        // Audit log event for REAUTH_REQUIRED (SPEC/43)
        var auditId = "aud-" + UlidGenerator.NewUlid();
        await conn.ExecuteAsync(@"
            INSERT INTO audit_log (audit_id, action, actor_type, actor_id, subject_type, subject_id, outcome, reason_code, correlation_id, schema_version, occurred_at)
            VALUES (@AuditId, 'OAUTH_REAUTH_TRIGGERED', 'SYSTEM', 'oauth_manager', 'PLATFORM_ACCOUNT', @AccountId, 'REAUTH_REQUIRED', 'AMCCA-PLT-002', @CorrId, '3.1.0', datetime('now'));
        ", new { AuditId = auditId, AccountId = accountId, CorrId = "corr-oauth-" + accountId }, transaction: tx);

        tx.Commit();
    }

    private static OAuthTokenBundle ParseTokenBundle(string json, string? fallbackRefreshToken = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : fallbackRefreshToken;
        var tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer";

        int expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        var scopes = new List<string>();
        if (root.TryGetProperty("scope", out var sc) && sc.GetString() != null)
        {
            scopes.AddRange(sc.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        return new OAuthTokenBundle(accessToken, refreshToken, expiresAt, tokenType, scopes);
    }

    private static string GenerateRandomString(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string GeneratePkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
