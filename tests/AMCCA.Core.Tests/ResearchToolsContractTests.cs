using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Research;
using AMCCA.Core.Tools;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ResearchToolsContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ResearchService _research;

    public ResearchToolsContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_RTOOLS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "rtools.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _research = new ResearchService(_factory);
    }

    public void Dispose()
    {
        _research.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private async Task<string> NewProductionAsync()
    {
        var id = UlidGenerator.NewUlid();
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
              VALUES (@Id, 'RESEARCHING', 0, 0, 'AUTONOMOUS', 'en', '3.1.0', @Now, @Now);",
            new { Id = id, Now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private ToolExecutionContext Ctx(string productionId) => new("corr", IntentId: null, ProductionId: productionId);

    private async Task<string> SeedSourceAsync(string publisher, string trustTier)
    {
        var s = new Source
        {
            Id = UlidGenerator.NewUlid(), Url = $"https://{publisher}.example/a", Publisher = publisher,
            RetrievedAt = DateTimeOffset.UtcNow.ToString("O"), ContentHash = new string('a', 64), TrustTier = trustTier,
        };
        await _research.InsertSourceAsync(s);
        return s.Id;
    }

    [Fact]
    public async Task RecordClaim_LinksAllGivenSources_LeavingStatusUnknown()
    {
        var pid = await NewProductionAsync();
        var s1 = await SeedSourceAsync("pubone", "SECONDARY");
        var s2 = await SeedSourceAsync("pubtwo", "SECONDARY");
        var tool = new RecordClaimTool(_research);
        var input = JsonSerializer.Serialize(new
        {
            text = "a material fact", materiality = "MATERIAL", subject_class = "GENERAL",
            sources = new[] { new { source_id = s1, relation = "SUPPORTS" }, new { source_id = s2, relation = "SUPPORTS" } },
        });

        var outJson = await tool.ExecuteAsync(input, Ctx(pid));
        var claimId = JsonDocument.Parse(outJson).RootElement.GetProperty("claim_id").GetString();

        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM claim_sources WHERE claim_id = @Id;", new { Id = claimId }))
            .Should().Be(2);
        (await conn.ExecuteScalarAsync<string>("SELECT status FROM claims WHERE id = @Id;", new { Id = claimId }))
            .Should().Be("UNKNOWN");
    }

    [Fact]
    public async Task RecordClaim_WithNoSources_Rejects()
    {
        var pid = await NewProductionAsync();
        var tool = new RecordClaimTool(_research);
        var act = async () => await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { text = "x", sources = Array.Empty<object>() }), Ctx(pid));

        await act.Should().ThrowAsync<AmccaException>();
    }

    [Fact]
    public async Task EvaluateClaims_MarksAMaterialClaimVerified_WhenItHasTwoIndependentSources()
    {
        var pid = await NewProductionAsync();
        var s1 = await SeedSourceAsync("independentone", "SECONDARY");
        var s2 = await SeedSourceAsync("independenttwo", "SECONDARY");
        var record = new RecordClaimTool(_research);
        await record.ExecuteAsync(JsonSerializer.Serialize(new
        {
            text = "verified fact", materiality = "MATERIAL",
            sources = new[] { new { source_id = s1, relation = "SUPPORTS" }, new { source_id = s2, relation = "SUPPORTS" } },
        }), Ctx(pid));

        var outJson = await new EvaluateClaimsTool(_research).ExecuteAsync("{}", Ctx(pid));
        var summary = JsonDocument.Parse(outJson).RootElement;

        summary.GetProperty("verified").GetInt32().Should().Be(1);
        summary.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task EvaluateClaims_LeavesAMaterialClaimEstimated_WithOnlyOneSource()
    {
        var pid = await NewProductionAsync();
        var s1 = await SeedSourceAsync("lonely", "SECONDARY");
        await new RecordClaimTool(_research).ExecuteAsync(JsonSerializer.Serialize(new
        {
            text = "under-sourced fact", materiality = "MATERIAL",
            sources = new[] { new { source_id = s1, relation = "SUPPORTS" } },
        }), Ctx(pid));

        var outJson = await new EvaluateClaimsTool(_research).ExecuteAsync("{}", Ctx(pid));

        JsonDocument.Parse(outJson).RootElement.GetProperty("estimated").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task EvaluateClaims_MarksDisputed_WhenAContradictingSourceExists()
    {
        var pid = await NewProductionAsync();
        var s1 = await SeedSourceAsync("supporter", "SECONDARY");
        var s2 = await SeedSourceAsync("refuter", "SECONDARY");
        await new RecordClaimTool(_research).ExecuteAsync(JsonSerializer.Serialize(new
        {
            text = "contested fact", materiality = "MATERIAL",
            sources = new[] { new { source_id = s1, relation = "SUPPORTS" }, new { source_id = s2, relation = "CONTRADICTS" } },
        }), Ctx(pid));

        var outJson = await new EvaluateClaimsTool(_research).ExecuteAsync("{}", Ctx(pid));

        JsonDocument.Parse(outJson).RootElement.GetProperty("disputed").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task FetchSource_WithoutAUrl_Rejects()
    {
        var pid = await NewProductionAsync();
        var act = async () => await new FetchSourceTool(_research).ExecuteAsync("{}", Ctx(pid));
        await act.Should().ThrowAsync<AmccaException>();
    }
}
