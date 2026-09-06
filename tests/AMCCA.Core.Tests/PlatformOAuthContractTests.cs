using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Publishing;
using AMCCA.Core.Security;
using Dapper;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class PlatformOAuthContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly MigrationService _migrationService;
    private readonly ISecretStore _secretStore;
    private readonly OAuthManager _oauthManager;

    public PlatformOAuthContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_OAUTH_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "oauth_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);
        _migrationService = new MigrationService(_factory, _testDir);
        _migrationService.UpgradeAsync().GetAwaiter().GetResult();

        _secretStore = new InMemorySecretStore();
        _oauthManager = new OAuthManager(_factory, _secretStore);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    #region OAuth Loopback & Lifecycle Tests (SPEC/43)

    [Fact]
    public async Task OAuthLoopback_ReceivesValidCallback_ValidatesStateAndReturnsCode()
    {
        using var receiver = new OAuthLoopbackReceiver();
        receiver.Start();

        var state = "secret-state-xyz-123";
        var expectedCode = "auth-code-777";

        var listenTask = receiver.WaitForCallbackAsync(state, TimeSpan.FromSeconds(15));

        // Simulate browser redirect from platform
        // Fail in 10s with a clear client-side error instead of letting a stalled loopback
        // request ride the default 100s HttpClient.Timeout and drag the CI job with it.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var callbackUrl = $"{receiver.RedirectUri}?code={expectedCode}&state={state}";
        var response = await client.GetAsync(callbackUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("AMCCA Authorization Succeeded");

        var result = await listenTask;
        result.Success.Should().BeTrue();
        result.AuthorizationCode.Should().Be(expectedCode);
        result.State.Should().Be(state);
    }

    [Fact]
    public async Task OAuthLoopback_MismatchedState_RejectsWithCsrfError()
    {
        using var receiver = new OAuthLoopbackReceiver();
        receiver.Start();

        var state = "valid-state-abc";
        var forgedState = "malicious-forged-state";

        var listenTask = receiver.WaitForCallbackAsync(state, TimeSpan.FromSeconds(15));

        // Fail in 10s with a clear client-side error instead of letting a stalled loopback
        // request ride the default 100s HttpClient.Timeout and drag the CI job with it.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var callbackUrl = $"{receiver.RedirectUri}?code=code123&state={forgedState}";
        var response = await client.GetAsync(callbackUrl);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await listenTask;
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Mismatched OAuth state token");
    }

    [Fact]
    public void OAuthManager_InitiatesAuthorization_GeneratesValidPkceAndState()
    {
        var authReq = _oauthManager.InitiateAuthorization(
            platform: "youtube",
            authorizationEndpoint: "https://accounts.google.com/o/oauth2/v2/auth",
            clientId: "test-client-id",
            redirectUri: "http://127.0.0.1:8080/callback/",
            scopes: new[] { "https://www.googleapis.com/auth/youtube.upload", "https://www.googleapis.com/auth/youtube.readonly" }
        );

        authReq.State.Should().NotBeNullOrWhiteSpace().And.HaveLength(43);
        authReq.CodeVerifier.Should().NotBeNullOrWhiteSpace();
        authReq.AuthorizationUrl.Should().Contain("code_challenge_method=S256");
        authReq.AuthorizationUrl.Should().Contain($"state={authReq.State}");
        authReq.AuthorizationUrl.Should().Contain("youtube.upload");
    }

    [Fact]
    public async Task OAuthManager_TokenRefreshFails_TransitionsAccountToReauthRequiredAndAudits()
    {
        // Setup connected account
        var accountId = "acc-oauth-1";
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO platform_accounts (id, platform, account_handle, credential_secret_ref, state, created_at, updated_at)
                VALUES (@Id, 'youtube', '@channel1', 'secret://platform/youtube_acc-oauth-1', 'CONNECTED', datetime('now'), datetime('now'));
            ", new { Id = accountId });
        }

        // Store initial expired tokens
        var initialTokens = new OAuthTokenBundle("expired_access", "valid_refresh", DateTimeOffset.UtcNow.AddMinutes(-5));
        await _oauthManager.StoreTokensAsync("youtube", accountId, initialTokens);

        // Setup mock HTTP handler that returns 401 invalid_grant
        var mockHttp = new MockHttpHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
            };
        });

        var oauthWithMock = new OAuthManager(_factory, _secretStore, new FakeSafeHttpClientFactory(mockHttp));
        var result = await oauthWithMock.RefreshTokenAsync("youtube", accountId, "https://oauth.platform.com/token", "client-id");

        result.Should().BeNull();

        // Verify account transitioned to REAUTH_REQUIRED (SPEC/43)
        using (var verifyConn = await _factory.CreateOpenConnectionAsync())
        {
            var state = await verifyConn.ExecuteScalarAsync<string>("SELECT state FROM platform_accounts WHERE id = @Id", new { Id = accountId });
            state.Should().Be("REAUTH_REQUIRED");

            // Verify audit_log entry created
            var auditCount = await verifyConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_log WHERE subject_id = @Id AND reason_code = 'AMCCA-PLT-002'",
                new { Id = accountId });
            auditCount.Should().Be(1, "refresh failure must be recorded in audit_log");
        }
    }

    [Fact]
    public async Task OAuthManager_RevokeAccess_DeletesSecretsAndMarksDisconnected()
    {
        var accountId = "acc-revoke-1";
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO platform_accounts (id, platform, account_handle, credential_secret_ref, state, created_at, updated_at)
                VALUES (@Id, 'tiktok', '@creator', 'secret://platform/tiktok_acc-revoke-1', 'CONNECTED', datetime('now'), datetime('now'));
            ", new { Id = accountId });
        }

        await _oauthManager.StoreTokensAsync("tiktok", accountId, new OAuthTokenBundle("tok_to_revoke", "ref_to_revoke", DateTimeOffset.UtcNow.AddHours(1)));

        var mockHttp = new MockHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        var oauth = new OAuthManager(_factory, _secretStore, new FakeSafeHttpClientFactory(mockHttp));

        await oauth.RevokeTokenAsync("tiktok", accountId, "https://open.tiktokapis.com/v2/oauth/revoke/", "client-id");

        // Verify secret deleted
        var tokens = await oauth.GetStoredTokensAsync("tiktok", accountId);
        tokens.Should().BeNull();

        // Verify account marked DISCONNECTED (SPEC/43)
        using (var verifyConn = await _factory.CreateOpenConnectionAsync())
        {
            var state = await verifyConn.ExecuteScalarAsync<string>("SELECT state FROM platform_accounts WHERE id = @Id", new { Id = accountId });
            state.Should().Be("DISCONNECTED");

            var auditOutcome = await verifyConn.ExecuteScalarAsync<string>(
                "SELECT outcome FROM audit_log WHERE action = 'OAUTH_REVOKED' AND subject_id = @Id", new { Id = accountId });
            auditOutcome.Should().Be("ALLOWED", "a clean revocation is audited as ALLOWED");
        }
    }

    [Fact]
    public async Task OAuthManager_RevokeAccess_RemoteRevocationFails_StillDisconnectsAndAuditsError()
    {
        var accountId = "acc-revoke-degraded-1";
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO platform_accounts (id, platform, account_handle, credential_secret_ref, state, created_at, updated_at)
                VALUES (@Id, 'tiktok', '@creator', 'secret://platform/tiktok_acc-revoke-degraded-1', 'CONNECTED', datetime('now'), datetime('now'));
            ", new { Id = accountId });
        }

        await _oauthManager.StoreTokensAsync("tiktok", accountId, new OAuthTokenBundle("tok_to_revoke", "ref_to_revoke", DateTimeOffset.UtcNow.AddHours(1)));

        // Provider rejects the revocation. The local disconnect must still complete so the user
        // can drop a broken account, and the failure must be recorded rather than swallowed.
        var mockHttp = new MockHttpHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var oauth = new OAuthManager(_factory, _secretStore, new FakeSafeHttpClientFactory(mockHttp));

        await oauth.RevokeTokenAsync("tiktok", accountId, "https://open.tiktokapis.com/v2/oauth/revoke/", "client-id");

        (await oauth.GetStoredTokensAsync("tiktok", accountId)).Should().BeNull();

        using (var verifyConn = await _factory.CreateOpenConnectionAsync())
        {
            var state = await verifyConn.ExecuteScalarAsync<string>("SELECT state FROM platform_accounts WHERE id = @Id", new { Id = accountId });
            state.Should().Be("DISCONNECTED");

            var auditOutcome = await verifyConn.ExecuteScalarAsync<string>(
                "SELECT outcome FROM audit_log WHERE action = 'OAUTH_REVOKED' AND subject_id = @Id", new { Id = accountId });
            auditOutcome.Should().Be("ERROR", "a failed remote revocation is audited as ERROR, not hidden");
        }
    }

    #endregion

    #region Platform Adapters Tests (YouTube, TikTok, Instagram, Twitter)

    [Theory]
    [InlineData("youtube")]
    [InlineData("tiktok")]
    [InlineData("instagram")]
    [InlineData("twitter")]
    public async Task PlatformAdapters_SuccessUploadAndVerify_ReturnsPublishedEvidence(string platform)
    {
        var mockHttp = new MockHttpHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"item_123\",\"items\":[{\"id\":\"item_123\",\"status\":{\"uploadStatus\":\"uploaded\"}}],\"data\":{\"id\":\"item_123\",\"publish_id\":\"item_123\",\"status\":\"PUBLISH_COMPLETE\"}}", Encoding.UTF8, "application/json")
            };
        });

        IPlatformAdapter adapter = platform switch
        {
            "youtube" => new YouTubePlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.yt.com"),
            "tiktok" => new TikTokPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.tiktok.com"),
            "instagram" => new InstagramPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.ig.com"),
            "twitter" => new TwitterPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.x.com"),
            _ => throw new ArgumentException()
        };

        var uploadReq = new UploadRequest(
            AccountId: "acc-1",
            Title: "Test Video",
            Description: "A great test video",
            VideoPath: "https://storage.amcca.local/videos/rendered.mp4",
            IdempotencyKey: "idem-" + Guid.NewGuid().ToString("N"),
            IsSynthetic: true
        );

        var uploadResult = await adapter.UploadAsync(uploadReq);
        uploadResult.Success.Should().BeTrue();
        uploadResult.ExternalId.Should().NotBeNullOrWhiteSpace();

        var statusResult = await adapter.GetStatusAsync(uploadResult.ExternalId!);
        statusResult.State.Should().Be("PUBLISHED");

        var evidence = await adapter.PollAuthoritativeEvidenceAsync(uploadResult.ExternalId!);
        evidence.IsPublished.Should().BeTrue();
        evidence.EvidenceSource.Should().Be("OFFICIAL_API");
    }

    [Theory]
    [InlineData("youtube")]
    [InlineData("tiktok")]
    [InlineData("instagram")]
    [InlineData("twitter")]
    public async Task PlatformAdapters_On401Unauthorized_TriggersCallbackAndReturnsPlt002(string platform)
    {
        var mockHttp = new MockHttpHandler(req => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        string? unauthorizedAccountId = null;

        BasePlatformAdapter adapter = platform switch
        {
            "youtube" => new YouTubePlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.yt.com"),
            "tiktok" => new TikTokPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.tiktok.com"),
            "instagram" => new InstagramPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.ig.com"),
            "twitter" => new TwitterPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.x.com"),
            _ => throw new ArgumentException()
        };

        adapter.OnUnauthorizedCallback = acc => unauthorizedAccountId = acc;

        var uploadReq = new UploadRequest(
            AccountId: "acc-unauth",
            Title: "Test",
            Description: "Test",
            VideoPath: "path",
            IdempotencyKey: "idem-unauth"
        );

        var result = await adapter.UploadAsync(uploadReq);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(AmccaErrors.Plt002);
        unauthorizedAccountId.Should().Be("acc-unauth");
    }

    [Theory]
    [InlineData("youtube")]
    [InlineData("tiktok")]
    [InlineData("instagram")]
    [InlineData("twitter")]
    public async Task PlatformAdapters_On429RateLimit_ParsesRetryAfterHeader(string platform)
    {
        var mockHttp = new MockHttpHandler(req =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return resp;
        });

        IPlatformAdapter adapter = platform switch
        {
            "youtube" => new YouTubePlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.yt.com"),
            "tiktok" => new TikTokPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.tiktok.com"),
            "instagram" => new InstagramPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.ig.com"),
            "twitter" => new TwitterPlatformAdapter(new FakeSafeHttpClientFactory(mockHttp), "https://mock.x.com"),
            _ => throw new ArgumentException()
        };

        var uploadReq = new UploadRequest("acc-rate", "Title", "Desc", "path", "idem-rate");
        var result = await adapter.UploadAsync(uploadReq);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(AmccaErrors.Plt003);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(45));
    }

    #endregion

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
