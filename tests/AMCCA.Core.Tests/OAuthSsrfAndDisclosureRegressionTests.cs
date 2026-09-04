using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// SEC-02, SEC-03, SEC-04, SEC-10, SEC-11 — OAuth traffic is confined to the SSRF-safe HTTP
/// pipeline, every endpoint is validated before connecting, redirects are re-validated hop by
/// hop, and remote error bodies are never disclosed in exceptions.
/// </summary>
public class OAuthSsrfAndDisclosureRegressionTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly InMemorySecretStore _secrets = new();

    public OAuthSsrfAndDisclosureRegressionTests()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AMCCA_OAUTHSEC_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
        _factory = new DatabaseConnectionFactory(System.IO.Path.Combine(_dir, "t.db"));
        new MigrationService(_factory, _dir).UpgradeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps;
        public List<HttpRequestMessage> Requests { get; } = new();

        public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
            => _steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(steps);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = _steps.Count > 1 ? _steps.Dequeue() : _steps.Peek();
            return Task.FromResult(step(request));
        }
    }

    private OAuthManager Manager(HttpMessageHandler handler, bool redirectGuard = false)
        => new(_factory, _secrets, new FakeSafeHttpClientFactory(handler, redirectGuard));

    // ---- SEC-11: the insecure HttpClient injection point is gone -------------------------

    [Fact]
    public void OAuthManager_HasNoHttpClientConstructorParameter()
    {
        var ctorParams = typeof(OAuthManager).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        ctorParams.Should().NotContain(typeof(HttpClient));
        ctorParams.Should().Contain(typeof(ISafeHttpClientFactory));
    }

    // ---- SEC-03: endpoint validation before any connection ------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/token")]
    [InlineData("http://localhost/token")]
    [InlineData("http://169.254.169.254/token")]
    [InlineData("http://10.0.0.5/token")]
    [InlineData("http://192.168.1.10/token")]
    [InlineData("http://172.16.9.9/token")]
    [InlineData("http://[::1]/token")]
    [InlineData("ftp://example.com/token")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-an-absolute-uri")]
    public async Task ExchangeCode_RejectsUnsafeTokenEndpoint(string endpoint)
    {
        var mgr = Manager(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var act = async () => await mgr.ExchangeCodeAsync("youtube", "acc", endpoint, "cid", "code", "verifier", "http://127.0.0.1/cb/");

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    [Theory]
    [InlineData("http://169.254.169.254/authorize")]
    [InlineData("http://localhost/authorize")]
    [InlineData("javascript:alert(1)")]
    public void InitiateAuthorization_RejectsUnsafeAuthorizationEndpoint(string endpoint)
    {
        var mgr = Manager(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        Action act = () => mgr.InitiateAuthorization("youtube", endpoint, "cid", "http://127.0.0.1:9000/cb/", new[] { "scope" });

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    [Fact]
    public async Task Revoke_RejectsUnsafeRevocationEndpoint()
    {
        var mgr = Manager(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var act = async () => await mgr.RevokeTokenAsync("tiktok", "acc", "http://192.168.0.1/revoke", "cid");

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    // ---- SEC-04: redirects re-validated hop by hop -------------------------------------

    [Fact]
    public async Task ExchangeCode_RedirectToPrivateIp_IsBlocked()
    {
        var handler = new SequenceHandler(
            _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                r.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
                return r;
            });

        var mgr = Manager(handler, redirectGuard: true);

        var act = async () => await mgr.ExchangeCodeAsync("youtube", "acc", "https://oauth.example.com/token", "cid", "code", "verifier", "http://127.0.0.1/cb/");

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    [Fact]
    public async Task ExchangeCode_ExceedsMaxRedirects_IsBlocked()
    {
        var handler = new SequenceHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://another.public.example.com/loop");
            return r;
        });

        var mgr = Manager(handler, redirectGuard: true);

        var act = async () => await mgr.ExchangeCodeAsync("youtube", "acc", "https://oauth.example.com/token", "cid", "code", "verifier", "http://127.0.0.1/cb/");

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    // ---- SEC-10: remote error body is never disclosed --------------------------------

    [Fact]
    public async Task ExchangeCode_Failure_DoesNotLeakRemoteBody()
    {
        const string leaked = "SUPER_SECRET_LEAK_9ac1";
        var body = "{\"error\":\"invalid_grant\",\"error_description\":\"token " + leaked + "\",\"access_token\":\"" + leaked + "\"}";
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        var mgr = Manager(handler);

        AmccaException? caught = null;
        try
        {
            await mgr.ExchangeCodeAsync("youtube", "acc", "https://oauth.example.com/token", "cid", "code", "verifier", "http://127.0.0.1/cb/");
        }
        catch (AmccaException ex) { caught = ex; }

        caught.Should().NotBeNull();
        caught!.ErrorCode.Should().Be(AmccaErrors.Plt002);
        caught.Message.Should().NotContain(leaked);
        caught.ToString().Should().NotContain(leaked);
        caught.Message.Should().Contain("400");
        caught.Message.Should().Contain("invalid_grant");   // the whitelisted OAuth error code only
        caught.Message.Should().Contain("youtube");
    }

    [Fact]
    public async Task ExchangeCode_Failure_HostileErrorField_IsNotEchoed()
    {
        // A provider trying to smuggle data through the 'error' field: contains spaces/punctuation,
        // so it fails the strict whitelist and is dropped entirely.
        var body = "{\"error\":\"here is a secret KJ8s.dfp/leak with spaces\"}";
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        var mgr = Manager(handler);

        AmccaException? caught = null;
        try
        {
            await mgr.ExchangeCodeAsync("tiktok", "acc", "https://oauth.example.com/token", "cid", "code", "verifier", "http://127.0.0.1/cb/");
        }
        catch (AmccaException ex) { caught = ex; }

        caught.Should().NotBeNull();
        caught!.Message.Should().NotContain("secret");
        caught.Message.Should().NotContain("leak");
        caught.Message.Should().Contain("400");
    }
}
