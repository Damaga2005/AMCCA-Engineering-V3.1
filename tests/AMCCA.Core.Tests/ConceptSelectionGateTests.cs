using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.Policy;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// M4 / D-035: CONCEPT_SELECTED is a real gate. It selects a concept, reserves the scripting budget
/// and records the decision — or it BLOCKs. It never advances silently.
/// </summary>
public class ConceptSelectionGateTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly BudgetManager _budgets;
    private readonly ConceptSelectionStageHandler _handler;

    public ConceptSelectionGateTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_CONCEPT_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "concept.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _budgets = new BudgetManager(_factory);
        _handler = new ConceptSelectionStageHandler(_factory, _budgets, new AuditStore(_factory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private Production Prod(string id, string mode, string? oppId = null, string? nicheId = null)
        => new() { Id = id, State = "CONCEPT_SELECTED", AutonomyMode = mode, Language = "en", OpportunityId = oppId, NicheId = nicheId };

    private async Task SeedProductionRowAsync(Production p)
    {
        using var c = await _factory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(@"
            INSERT INTO productions (id, state, autonomy_mode, language, niche_id, opportunity_id,
                                     schema_version, created_at, updated_at, aggregate_version)
            VALUES (@Id, @State, @Mode, 'en', @Niche, @Opp, '3.1.0', datetime('now'), datetime('now'), 0);",
            new { p.Id, p.State, Mode = p.AutonomyMode, Niche = p.NicheId, Opp = p.OpportunityId });
    }

    private async Task SeedNicheAsync(string id)
    {
        using var c = await _factory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(@"INSERT INTO niches (id, name, language, state, created_at, updated_at)
            VALUES (@Id, @Id, 'en', 'CANDIDATE', datetime('now'), datetime('now'));", new { Id = id });
    }

    private async Task SeedOpportunityAsync(string id, string nicheId, string state, double score,
        string expectedRevenue = "50.000000", string expectedCost = "5.000000")
    {
        using var c = await _factory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(@"
            INSERT INTO opportunities (id, niche_id, state, score, score_breakdown_json, expected_revenue,
                                       expected_cost, risk_penalty, currency, scored_at, created_at, updated_at)
            VALUES (@Id, @Niche, @State, @Score, '{""trend"":0.7}', @Rev, @Cost, 0.1, 'EUR',
                    datetime('now'), datetime('now'), datetime('now'));",
            new { Id = id, Niche = nicheId, State = state, Score = score, Rev = expectedRevenue, Cost = expectedCost });
    }

    private async Task<string?> OppState(string id)
    {
        using var c = await _factory.CreateOpenConnectionAsync();
        return await c.ExecuteScalarAsync<string>("SELECT state FROM opportunities WHERE id = @Id;", new { Id = id });
    }

    [Fact]
    public async Task Autonomous_NoOpportunityAnywhere_Blocks()
    {
        var r = await _handler.HandleAsync(new StageContext(Prod("p-1", "AUTONOMOUS"), "corr-1"));
        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Pol001);
    }

    [Fact]
    public async Task NonAutonomous_WithoutOperatorSelection_Blocks()
    {
        var r = await _handler.HandleAsync(new StageContext(Prod("p-2", "MANUAL"), "corr-2"));
        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Pol004);
    }

    [Fact]
    public async Task Autonomous_ScoredOpportunityExists_ButNoProductionBudget_Blocks()
    {
        await SeedNicheAsync("n-3");
        await SeedOpportunityAsync("opp-3", "n-3", "SCORED", 0.8);

        var r = await _handler.HandleAsync(new StageContext(Prod("p-3", "AUTONOMOUS"), "corr-3"));

        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Bud002, "no production budget exists to reserve scripting against");
        (await OppState("opp-3")).Should().Be("SCORED", "a blocked gate must not have mutated the opportunity");
    }

    [Fact]
    public async Task Autonomous_PicksHighestScore_ReservesBudget_RecordsDecision_Advances()
    {
        await SeedNicheAsync("n-4");
        await SeedOpportunityAsync("opp-low", "n-4", "SCORED", 0.30, expectedCost: "3.000000");
        await SeedOpportunityAsync("opp-high", "n-4", "SCORED", 0.90, expectedCost: "4.000000");
        await SeedOpportunityAsync("opp-rejected", "n-4", "REJECTED", 0.99);
        await _budgets.CreateBudgetAsync("bud-p4", "PRODUCTION", "p-4", 20.000000m);

        var prod = Prod("p-4", "AUTONOMOUS");
        await SeedProductionRowAsync(prod);
        var r = await _handler.HandleAsync(new StageContext(prod, "corr-4"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
        (await OppState("opp-high")).Should().Be("SELECTED");
        (await OppState("opp-low")).Should().Be("SCORED", "only the chosen concept is locked");

        using var c = await _factory.CreateOpenConnectionAsync();
        var linked = await c.ExecuteScalarAsync<string>("SELECT opportunity_id FROM productions WHERE id = 'p-4';");
        linked.Should().Be("opp-high");
        var reserved = await c.ExecuteScalarAsync<string>("SELECT reserved FROM budgets WHERE id = 'bud-p4';");
        Money.Parse(reserved!).Should().Be(4.000000m, "the chosen concept's expected_cost is reserved for scripting");
        var audits = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE action = 'CONCEPT_SELECTED' AND subject_id = 'opp-high' AND outcome = 'ALLOWED';");
        audits.Should().Be(1);
    }

    [Fact]
    public async Task OperatorSelectedOpportunity_InRejectedState_Blocks()
    {
        await SeedNicheAsync("n-5");
        await SeedOpportunityAsync("opp-5", "n-5", "REJECTED", 0.5);
        await _budgets.CreateBudgetAsync("bud-p5", "PRODUCTION", "p-5", 20.000000m);

        var r = await _handler.HandleAsync(new StageContext(Prod("p-5", "AUTONOMOUS", oppId: "opp-5"), "corr-5"));

        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Pol001);
    }

    [Fact]
    public async Task OperatorSelectedOpportunity_Scored_WithBudget_Advances()
    {
        await SeedNicheAsync("n-6");
        await SeedOpportunityAsync("opp-6", "n-6", "SCORED", 0.55, expectedCost: "6.000000");
        await _budgets.CreateBudgetAsync("bud-p6", "PRODUCTION", "p-6", 20.000000m);

        var prod = Prod("p-6", "ASSISTED", oppId: "opp-6");
        await SeedProductionRowAsync(prod);
        var r = await _handler.HandleAsync(new StageContext(prod, "corr-6"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
        (await OppState("opp-6")).Should().Be("SELECTED");
    }
}
