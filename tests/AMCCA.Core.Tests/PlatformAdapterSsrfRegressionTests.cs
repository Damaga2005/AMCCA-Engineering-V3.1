using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Publishing;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// SEC-11 (final closure) — the platform adapters obtain their HTTP transport from the
/// SSRF-safe pipeline (SsrfValidator + SafeRedirectHandler + coupled-DNS ConnectCallback),
/// exactly like OAuthManager. No production constructor accepts an arbitrary HttpClient, every
/// outbound call is SSRF-validated, and redirects are re-validated hop by hop.
/// </summary>
public class PlatformAdapterSsrfRegressionTests
{
    private static readonly Type[] AdapterTypes =
    {
        typeof(YouTubePlatformAdapter),
        typeof(TikTokPlatformAdapter),
        typeof(InstagramPlatformAdapter),
        typeof(TwitterPlatformAdapter),
    };

    public static IEnumerable<object[]> Adapters => AdapterTypes.Select(t => new object[] { t });

    private static BasePlatformAdapter Create(Type adapterType, ISafeHttpClientFactory factory, string baseEndpoint)
        => (BasePlatformAdapter)Activator.CreateInstance(adapterType, factory, baseEndpoint)!;

    private sealed class RespondingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_fn(request));
    }

    // ---- 6.1: no production HttpClient injection point ------------------------------

    [Fact]
    public void NoPlatformAdapterConstructor_AcceptsRawHttpClient()
    {
        var offenders = AdapterTypes
            .Append(typeof(BasePlatformAdapter))
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            .Where(c => c.IsPublic || c.IsFamily) // public or protected — reachable from production
            .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpClient)))
            .Select(c => c.DeclaringType!.Name)
            .ToList();

        offenders.Should().BeEmpty("no production-reachable constructor may take an HttpClient (SEC-11)");
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void PublicConstructor_TakesSafeHttpClientFactory(Type adapterType)
    {
        adapterType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Should().Contain(typeof(ISafeHttpClientFactory));
    }

    // ---- 6.2: SSRF endpoint rejection (real SafeHttpClientFactory) ------------------

    [Theory]
    [InlineData("http://127.0.0.1/v")]
    [InlineData("http://localhost/v")]
    [InlineData("http://[::1]/v")]
    [InlineData("http://169.254.169.254/v")]
    [InlineData("http://10.0.0.1/v")]
    [InlineData("http://172.16.0.1/v")]
    [InlineData("http://192.168.1.1/v")]
    [InlineData("http://[fc00::1]/v")]
    public async Task EveryAdapter_RejectsUnsafeBaseEndpoint(string badEndpoint)
    {
        foreach (var type in AdapterTypes)
        {
            var adapter = Create(type, SafeHttpClientFactory.Default, badEndpoint);

            var act = async () => await adapter.VerifyCapabilitiesAsync("acc");

            (await act.Should().ThrowAsync<AmccaException>($"{type.Name} must block {badEndpoint}"))
                .Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Adapter_UploadPath_AlsoRejectsUnsafeEndpoint(Type adapterType)
    {
        var adapter = Create(adapterType, SafeHttpClientFactory.Default, "http://169.254.169.254/meta");

        var req = new UploadRequest("acc", "t", "d", "https://storage.local/v.mp4", "idem-" + Guid.NewGuid().ToString("N"));
        var act = async () => await adapter.UploadAsync(req);

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    // ---- 6.3: redirect re-validation (real SafeRedirectHandler over a mock inner) ---

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Adapter_RedirectToLoopback_IsBlocked(Type adapterType)
    {
        var handler = new RespondingHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("http://127.0.0.1/internal");
            return r;
        });
        var adapter = Create(adapterType, new FakeSafeHttpClientFactory(handler, wrapInRedirectGuard: true),
            "https://public-endpoint.invalid");

        var act = async () => await adapter.VerifyCapabilitiesAsync("acc");

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Adapter_RedirectToPrivateIp_IsBlocked(Type adapterType)
    {
        var handler = new RespondingHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("http://10.1.2.3/x");
            return r;
        });
        var adapter = Create(adapterType, new FakeSafeHttpClientFactory(handler, wrapInRedirectGuard: true),
            "https://public-endpoint.invalid");

        var act = async () => await adapter.VerifyCapabilitiesAsync("acc");

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    [Fact]
    public async Task Adapter_RedirectChainOverLimit_IsBlocked()
    {
        var handler = new RespondingHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://still-public.invalid/next");
            return r;
        });
        var adapter = Create(typeof(YouTubePlatformAdapter),
            new FakeSafeHttpClientFactory(handler, wrapInRedirectGuard: true), "https://public-endpoint.invalid");

        var act = async () => await adapter.VerifyCapabilitiesAsync("acc");

        (await act.Should().ThrowAsync<AmccaException>()).Which.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    // ---- compatibility: legit traffic still flows through the safe transport --------

    [Fact]
    public async Task Adapter_NormalRequest_StillReachesTheEndpoint_ThroughSafeTransport()
    {
        HttpRequestMessage? seen = null;
        var handler = new RespondingHandler(req =>
        {
            seen = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var adapter = Create(typeof(YouTubePlatformAdapter),
            new FakeSafeHttpClientFactory(handler, wrapInRedirectGuard: true), "https://public-endpoint.invalid");

        var caps = await adapter.VerifyCapabilitiesAsync("acc");

        seen.Should().NotBeNull();
        seen!.RequestUri!.ToString().Should().StartWith("https://public-endpoint.invalid/channels");
        caps.Should().Contain("VIDEO_UPLOAD");
    }
}
