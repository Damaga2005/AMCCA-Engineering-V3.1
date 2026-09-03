using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Research;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ResearchAndClaimValidationContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ResearchService _researchService;

    public ResearchAndClaimValidationContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_RES_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "research_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _researchService = new ResearchService(_factory);
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
    public void MaterialClaim_WithInsufficientSources_CannotReachVerified()
    {
        // Exit criterion: "A material claim without sufficient sources cannot reach VERIFIED"
        var claim = new Claim
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-1",
            Text = "Global inflation slowed to 2.8% in Q2.",
            Materiality = "MATERIAL",
            SubjectClass = "FINANCE",
            ContainsPersonalData = false
        };

        // Only 1 source provided, but policy.min_sources = 2
        var sources = new List<(Source Source, string Relation)>
        {
            (new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = "https://reuters.example/news/1",
                Publisher = "Reuters",
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = "hash-1",
                TrustTier = "PRIMARY"
            }, "SUPPORTS")
        };

        var evaluatedStatus = ClaimValidator.EvaluateClaimStatus(claim, sources, minSources: 2);

        evaluatedStatus.Should().NotBe("VERIFIED");
        evaluatedStatus.Should().Be("ESTIMATED");
    }

    [Fact]
    public void MaterialClaim_WithMultipleUrlsFromSamePublisher_CountsAsOneSourceAndCannotReachVerified()
    {
        // Rule: Independence means distinct publishers, not distinct URLs
        var claim = new Claim
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-1",
            Text = "Company X acquired Startup Y for $500M.",
            Materiality = "MATERIAL",
            SubjectClass = "TECH",
            ContainsPersonalData = false
        };

        // 2 URLs, but both from "TechWire"
        var sources = new List<(Source Source, string Relation)>
        {
            (new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = "https://techwire.example/article/1",
                Publisher = "TechWire",
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = "hash-1",
                TrustTier = "SECONDARY"
            }, "SUPPORTS"),
            (new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = "https://techwire.example/article/2",
                Publisher = "TechWire", // duplicate publisher
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = "hash-2",
                TrustTier = "SECONDARY"
            }, "SUPPORTS")
        };

        var evaluatedStatus = ClaimValidator.EvaluateClaimStatus(claim, sources, minSources: 2);

        evaluatedStatus.Should().NotBe("VERIFIED", "multiple URLs from same publisher are not independent sources");
    }

    [Fact]
    public void MaterialClaim_WithSufficientIndependentPublishers_ReachesVerified()
    {
        var claim = new Claim
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-1",
            Text = "EU AI Act enters into force today.",
            Materiality = "MATERIAL",
            SubjectClass = "LAW",
            ContainsPersonalData = false
        };

        var sources = new List<(Source Source, string Relation)>
        {
            (new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = "https://europa.eu/press/ai-act",
                Publisher = "European Commission",
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = "hash-eu",
                TrustTier = "PRIMARY"
            }, "SUPPORTS"),
            (new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = "https://bbc.example/ai-act-live",
                Publisher = "BBC News",
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = "hash-bbc",
                TrustTier = "SECONDARY"
            }, "SUPPORTS")
        };

        var evaluatedStatus = ClaimValidator.EvaluateClaimStatus(claim, sources, minSources: 2);

        evaluatedStatus.Should().Be("VERIFIED");
    }

    [Fact]
    public void Claim_WithContradictingSource_BecomesDisputedNeverVerified()
    {
        // Rule: A claim with any CONTRADICTS source becomes DISPUTED, never VERIFIED
        var claim = new Claim
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-1",
            Text = "Study shows product Z cures condition W.",
            Materiality = "MATERIAL",
            SubjectClass = "HEALTH",
            ContainsPersonalData = false
        };

        var sources = new List<(Source Source, string Relation)>
        {
            (new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = "https://journal.example/1",
                Publisher = "Medical Journal A",
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = "h1",
                TrustTier = "PRIMARY"
            }, "SUPPORTS"),
            (new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = "https://journal.example/2",
                Publisher = "Medical Journal B",
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = "h2",
                TrustTier = "PRIMARY"
            }, "CONTRADICTS") // Contradicting source
        };

        var evaluatedStatus = ClaimValidator.EvaluateClaimStatus(claim, sources, minSources: 1);

        evaluatedStatus.Should().Be("DISPUTED");
    }

    [Fact]
    public async Task ResearchService_PersistsSourcesClaimsAndLinks_Correctly()
    {
        var source1 = new Source
        {
            Id = UlidGenerator.NewUlid(),
            Url = "https://who.int/news/item",
            Publisher = "WHO",
            RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
            ContentHash = "content-hash-who",
            TrustTier = "PRIMARY",
            RobotsAllowed = true
        };
        await _researchService.InsertSourceAsync(source1);

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at) VALUES ('prod-200', 'RESEARCH', 0, 1, 'FULL_AUTONOMY', 'es', '3.1.0', datetime('now'), datetime('now'));";
            await cmd.ExecuteNonQueryAsync();
        }

        var claim = new Claim
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-200",
            Text = "Health guidelines updated.",
            Status = "VERIFIED",
            Materiality = "MATERIAL",
            SubjectClass = "HEALTH",
            ContainsPersonalData = false
        };
        await _researchService.InsertClaimWithSourceAsync(claim, source1.Id, "SUPPORTS");

        var retrievedClaim = await _researchService.GetClaimAsync(claim.Id);
        retrievedClaim.Should().NotBeNull();
        retrievedClaim!.Text.Should().Be("Health guidelines updated.");
        retrievedClaim.Status.Should().Be("VERIFIED");
    }
}
