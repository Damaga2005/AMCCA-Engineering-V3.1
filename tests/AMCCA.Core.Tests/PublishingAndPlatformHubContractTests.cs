using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Publishing;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class PublishingAndPlatformHubContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly PlatformHub _platformHub;

    public PublishingAndPlatformHubContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_PUB_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "publishing_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _platformHub = new PlatformHub(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp dir
        }
    }

    [Fact]
    public async Task VerifiedPublication_WithoutAuthoritativeEvidence_IsRejectedByDbConstraint()
    {
        // Exit criterion: "A verified publication requires platform-authoritative evidence"
        // CHECK(state <> 'VERIFIED' OR (evidence_source IS NOT NULL AND evidence_retrieved_at IS NOT NULL))
        var accountId = await _platformHub.RegisterAccountAsync("youtube", "@techchannel", "secret://vault/yt-token");

        var pub = new PublicationRecord
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-1",
            Platform = "youtube",
            AccountId = accountId,
            ContentVersionId = "c-v1",
            State = "VERIFIED", // Invalid without evidence_source and evidence_retrieved_at!
            IdempotencyKey = "key-1",
            EvidenceSource = null,
            EvidenceRetrievedAt = null
        };

        var act = async () => await _platformHub.InsertPublicationDirectAsync(pub);

        await act.Should().ThrowAsync<SqliteException>()
            .Where(e => e.SqliteErrorCode == 19); // Constraint violation
    }

    [Fact]
    public async Task PollingAuthoritativeEvidence_SetsEvidenceAndTransitionsToVerified()
    {
        var accountId = await _platformHub.RegisterAccountAsync("youtube", "@techchannel", "secret://vault/yt-token");
        var pub = await _platformHub.CreatePublicationAsync("prod-2", "youtube", accountId, "c-v1", "key-2");

        var adapter = new FakePlatformAdapter(
            shouldVerify: true,
            evidenceSource: "OFFICIAL_API",
            externalUrl: "https://youtube.com/shorts/video123");

        var verified = await _platformHub.VerifyPublicationAsync(pub.Id, "video123", adapter);

        verified.Should().BeTrue();
        var retrieved = await _platformHub.GetPublicationAsync(pub.Id);
        retrieved.Should().NotBeNull();
        retrieved!.State.Should().Be("VERIFIED");
        retrieved.EvidenceSource.Should().Be("OFFICIAL_API");
        retrieved.EvidenceRetrievedAt.Should().NotBeNullOrWhiteSpace();
        retrieved.ExternalUrl.Should().Be("https://youtube.com/shorts/video123");
    }

    [Fact]
    public async Task DuplicatePublication_ForSameTarget_IsPreventedByUniqueConstraint()
    {
        // SPEC/44: "UNIQUE(production_id, platform, account_id, content_version_id) makes a duplicate row impossible (I-17)"
        var accountId = await _platformHub.RegisterAccountAsync("youtube", "@channel", "secret://vault/key");

        await _platformHub.CreatePublicationAsync("prod-3", "youtube", accountId, "c-v1", "key-first");

        // Attempting second publication of same content version to same account
        var act = async () => await _platformHub.CreatePublicationAsync("prod-3", "youtube", accountId, "c-v1", "key-second");

        await act.Should().ThrowAsync<SqliteException>()
            .Where(e => e.SqliteErrorCode == 19);
    }

    [Fact]
    public void PublicationAccount_CredentialMustBeSecretUriReference()
    {
        // SPEC/40: "An account carries a secret:// credential reference, never a token. CHECK(credential_secret_ref LIKE 'secret://%')"
        var act = async () => await _platformHub.RegisterAccountAsync("tiktok", "@tok", "literal_token_12345");

        act.Should().ThrowAsync<SqliteException>();
    }

    private class FakePlatformAdapter : IPlatformAdapter
    {
        private readonly bool _shouldVerify;
        private readonly string _evidenceSource;
        private readonly string _externalUrl;

        public string PlatformId => "youtube";

        public FakePlatformAdapter(bool shouldVerify, string evidenceSource, string externalUrl)
        {
            _shouldVerify = shouldVerify;
            _evidenceSource = evidenceSource;
            _externalUrl = externalUrl;
        }

        public Task<PublicationEvidenceResult> PollAuthoritativeEvidenceAsync(string externalId, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(new PublicationEvidenceResult(
                IsPublished: _shouldVerify,
                ExternalUrl: _externalUrl,
                EvidenceSource: _evidenceSource,
                RetrievedAt: DateTimeOffset.UtcNow.ToString("O")));
        }
    }
}
