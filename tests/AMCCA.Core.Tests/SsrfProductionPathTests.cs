using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Research;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class SsrfProductionPathTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ResearchService _service;
    private readonly ResearchScraper _scraper;

    public SsrfProductionPathTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_SSRF_PROD_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "ssrf_prod.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _service = new ResearchService(_factory);
        _scraper = new ResearchScraper(_factory);
    }

    public void Dispose()
    {
        _service.Dispose();
        _scraper.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData("http://127.0.0.1/status")]
    [InlineData("http://127.0.0.254:9000/")]
    [InlineData("http://localhost:5000/")]
    public async Task ResearchService_Loopback_IsStrictlyBlocked(string url)
    {
        var act = async () => await _service.FetchAndIngestSourceAsync(url, "test_pub", "TIER_1", true);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Category == ErrorCategory.Security);
    }

    [Theory]
    [InlineData("http://[::1]/status")]
    [InlineData("http://[0000:0000:0000:0000:0000:0000:0000:0001]/secret")]
    public async Task ResearchService_IPv6Loopback_IsStrictlyBlocked(string url)
    {
        var act = async () => await _service.FetchAndIngestSourceAsync(url, "test_pub", "TIER_1", true);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Category == ErrorCategory.Security);
    }

    [Theory]
    [InlineData("http://10.0.0.1/admin")]
    [InlineData("http://10.255.255.255/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://192.168.1.254/")]
    public async Task ResearchService_PrivateIPv4_IsStrictlyBlocked(string url)
    {
        var act = async () => await _service.FetchAndIngestSourceAsync(url, "test_pub", "TIER_1", true);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Category == ErrorCategory.Security);
    }

    [Theory]
    [InlineData("http://169.254.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public async Task ResearchService_LinkLocal_IsStrictlyBlocked(string url)
    {
        var act = async () => await _service.FetchAndIngestSourceAsync(url, "test_pub", "TIER_1", true);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Category == ErrorCategory.Security);
    }

    [Theory]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456:789a:1::1]/")]
    [InlineData("http://[fe80::1]/")]
    public async Task ResearchService_IPv6PrivateAndLocal_IsStrictlyBlocked(string url)
    {
        var act = async () => await _service.FetchAndIngestSourceAsync(url, "test_pub", "TIER_1", true);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Category == ErrorCategory.Security);
    }

    [Fact]
    public async Task ResearchService_DnsResolvingToPrivateIp_IsStrictlyBlocked()
    {
        // 127.0.0.1.nip.io or localhost
        var act = async () => await _service.FetchAndIngestSourceAsync("http://127.0.0.1.nip.io/data", "test_pub", "TIER_1", true);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Category == ErrorCategory.Security);
    }

    [Fact]
    public async Task SafeRedirectHandler_RedirectToPrivateIp_IsStrictlyBlocked()
    {
        // Test redirect interception: a public response sending 302 to 127.0.0.1 must be caught
        var mockInnerHandler = new MockRedirectHttpMessageHandler(
            initialStatus: HttpStatusCode.Found,
            redirectLocation: "http://127.0.0.1:8080/internal-secrets");

        var safeRedirectHandler = new SafeRedirectHandler(mockInnerHandler);
        using var client = new HttpClient(safeRedirectHandler);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/public-endpoint");

        var act = async () => await client.SendAsync(request);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Message.Contains("loopback address prohibited"));
    }

    [Fact]
    public async Task SafeRedirectHandler_RedirectChainToPrivateIp_IsStrictlyBlocked()
    {
        // Test redirect chain: public -> 302 public2 -> 302 private
        var mockInnerHandler = new MockChainRedirectHttpMessageHandler(
            firstRedirect: "https://example.com/second-hop",
            secondRedirect: "http://192.168.1.1/admin-panel");

        var safeRedirectHandler = new SafeRedirectHandler(mockInnerHandler);
        using var client = new HttpClient(safeRedirectHandler);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/first-hop");

        var act = async () => await client.SendAsync(request);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003);
    }

    [Fact]
    public void ArchitectureInvariance_ResearchServiceDoesNotAcceptArbitraryHttpClient()
    {
        // DEF-CERT-003: Verify that ResearchService constructors DO NOT accept raw HttpClient
        var ctors = typeof(ResearchService).GetConstructors();
        foreach (var ctor in ctors)
        {
            foreach (var param in ctor.GetParameters())
            {
                param.ParameterType.Should().NotBe(typeof(HttpClient), 
                    "DEF-CERT-003 VIOLATION: ResearchService must never accept raw HttpClient directly to prevent SSRF bypass.");
            }
        }
    }

    [Fact]
    public async Task ResearchScraper_InheritsAllSsrfProtections()
    {
        var act = async () => await _scraper.FetchAndIngestSourceAsync("http://127.0.0.1/admin", "test_pub", "TIER_1", true);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003);
    }
}

internal class MockRedirectHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _initialStatus;
    private readonly string _redirectLocation;

    public MockRedirectHttpMessageHandler(HttpStatusCode initialStatus, string redirectLocation)
    {
        _initialStatus = initialStatus;
        _redirectLocation = redirectLocation;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_initialStatus);
        response.Headers.Location = new Uri(_redirectLocation);
        return Task.FromResult(response);
    }
}

internal class MockChainRedirectHttpMessageHandler : HttpMessageHandler
{
    private readonly string _firstRedirect;
    private readonly string _secondRedirect;
    private int _step = 0;

    public MockChainRedirectHttpMessageHandler(string firstRedirect, string secondRedirect)
    {
        _firstRedirect = firstRedirect;
        _secondRedirect = secondRedirect;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_step == 0)
        {
            _step++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(_firstRedirect);
            return Task.FromResult(response);
        }
        else
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(_secondRedirect);
            return Task.FromResult(response);
        }
    }
}
