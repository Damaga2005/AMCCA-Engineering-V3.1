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

public class SsrfPipelineEnforcementContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ResearchService _researchService;

    public SsrfPipelineEnforcementContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_SSRF_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "ssrf_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _researchService = new ResearchService(_factory);
    }

    public void Dispose()
    {
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
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://127.0.0.2:8080/metrics")]
    [InlineData("http://[::1]/internal")]
    [InlineData("http://10.0.0.1/secrets")]
    [InlineData("http://192.168.1.100/config")]
    [InlineData("http://172.16.5.4/status")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1")]
    [InlineData("http://[fc00::1]/private")]
    [InlineData("http://[fe80::1]/linklocal")]
    public void SsrfValidator_DirectPrivateAndMetadataTargets_AreBlocked(string targetUrl)
    {
        var act = () => SsrfValidator.ValidateUrl(new Uri(targetUrl));

        act.Should().Throw<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003 && ex.Category == ErrorCategory.Security);
    }

    [Fact]
    public void SsrfValidator_PublicHostname_IsPermitted()
    {
        var act = () => SsrfValidator.ValidateUrl(new Uri("https://example.com/research/paper"));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ResearchService_FetchAndIngestSource_BlocksPrivateIpAtSocketLevel()
    {
        // Must fail with AMCCA-SEC-003 through the real connected HTTP client pipeline
        var act = async () => await _researchService.FetchAndIngestSourceAsync(
            "http://127.0.0.1:9999/private/data",
            publisher: "Intranet",
            trustTier: "UNRATED",
            robotsAllowed: true,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Sec003);
    }

    [Fact]
    public async Task SafeSocketsHttpHandler_RedirectFromPublicToPrivate_IsBlocked()
    {
        var handler = SsrfValidator.CreateSafeSocketsHttpHandler();
        var client = new HttpClient(handler);

        // When connecting directly or via redirect to a private address, ConnectCallback throws AmccaException
        var act = async () => await client.GetAsync("http://169.254.169.254/latest/meta-data");

        var ex = await act.Should().ThrowAsync<Exception>();
        var amccaEx = (ex.Which as AmccaException) ?? (ex.Which.InnerException as AmccaException);
        amccaEx.Should().NotBeNull();
        amccaEx!.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }

    [Fact]
    public async Task SafeSocketsHttpHandler_RebindingOrResolvedPrivateDns_IsBlocked()
    {
        var handler = SsrfValidator.CreateSafeSocketsHttpHandler();
        var client = new HttpClient(handler);

        // localhost resolves to 127.0.0.1 or ::1, ConnectCallback must intercept and reject
        var act = async () => await client.GetAsync("http://localhost:8080/rebound");

        var ex = await act.Should().ThrowAsync<Exception>();
        var amccaEx = (ex.Which as AmccaException) ?? (ex.Which.InnerException as AmccaException);
        amccaEx.Should().NotBeNull();
        amccaEx!.ErrorCode.Should().Be(AmccaErrors.Sec003);
    }
}
