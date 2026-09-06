using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Agents;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Monetization;
using AMCCA.Core.Providers;
using AMCCA.Core.Tools;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>H1: model spend is priced from config, folded into the agent budget, and settled to
/// cost_events. Covers success, missing price (fail-safe), and MaxCost enforcement on model spend.</summary>
public class AgentCostAccountingTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly AgentRuntime _runtimeNoCost;

    public AgentCostAccountingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_AGCOST_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "agcost.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _runtimeNoCost = new AgentRuntime(new ToolRegistry(), new AuditStore(_factory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    // Fixed token usage per turn so cost is deterministic.
    private sealed class FixedUsageGateway : IProviderGateway
    {
        private readonly Queue<string> _responses;
        private readonly long _in, _out;
        public string ProviderId => "testprov";
        public int Turns { get; private set; }

        public FixedUsageGateway(IEnumerable<string> responses, long inTokens, long outTokens)
        {
            _responses = new Queue<string>(responses);
            _in = inTokens; _out = outTokens;
        }

        public Task<ProviderProbeResult> ProbeCapabilityAsync(string p, string m, string c, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, 1));

        public Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default)
        {
            Turns++;
            var text = _responses.Count > 0 ? _responses.Dequeue() : "{\"final\": \"end\"}";
            return Task.FromResult(new GatewayTextResponse(text, $"req-{Turns}", _in, _out));
        }
    }

    private sealed class NoopTool : ITool
    {
        public ToolDefinition Definition { get; } = new("noop", "1.0", SideEffectClass.PURE, Array.Empty<string>(), 30);
        public Task<string> ExecuteAsync(string inputJson, ToolExecutionContext c, CancellationToken ct = default)
            => Task.FromResult("{\"ok\": true}");
    }

    private static ToolRegistry ToolsWithNoop()
    {
        var r = new ToolRegistry();
        r.RegisterTool(new NoopTool());
        return r;
    }

    private static AgentContract Contract(decimal maxCost)
        => new("cost-agent", "1.0", new HashSet<string> { "noop" }, new HashSet<string>(), maxCost, 30, null, null);

    private static ToolExecutionContext Ctx(string productionId) => new("corr-cost", null, productionId);

    private IModelPricing PricingWith(params ModelPricingConfig[] prices)
        => new PricingSnapshotModelPricing(_factory, "testprov", prices);

    private static ModelPricingConfig Price(string modelId, string inPer1m, string outPer1m) => new()
    {
        ModelId = modelId,
        InputPer1MTokens = inPer1m,
        OutputPer1MTokens = outPer1m,
        Currency = "EUR",
        RetrievedAt = "2026-09-01T00:00:00.0000000Z",
        SourceRef = "https://provider.example/pricing",
    };

    private async Task SeedProductionAsync(string id)
    {
        using var c = await _factory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(@"
            INSERT INTO productions (id, state, autonomy_mode, language, schema_version, created_at, updated_at, aggregate_version)
            VALUES (@Id, 'RESEARCHING', 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'), 0);",
            new { Id = id });
    }

    [Theory]
    [InlineData(1_000_000, 500_000, "2.000000", "3.000000", "3.500000")] // 2.0 + 1.5
    [InlineData(1, 0, "0.100000", "0.000000", "0.000001")]               // rounds up, never to zero
    [InlineData(-5, -5, "1.000000", "1.000000", "0.000000")]             // negatives clamp
    public void ModelCostCalculator_IsDecimalAndRoundsUp(long tin, long tout, string inP, string outP, string expected)
    {
        var got = ModelCostCalculator.Compute(tin, tout, Money.Parse(inP), Money.Parse(outP));
        Money.Format(got).Should().Be(expected);
    }

    [Fact]
    public async Task PricePresent_ComputesCost_FoldsIntoBudget_AndSettlesCostEvent()
    {
        await SeedProductionAsync("prod-cost-ok");
        // 1,000,000 in @ 2.0/1M + 1,000,000 out @ 3.0/1M = 5.000000 per turn; two model turns.
        var gw = new FixedUsageGateway(new[] { "{\"tool\": \"noop\", \"input\": {}}", "{\"final\": \"done\"}" }, 1_000_000, 1_000_000);
        var pricing = PricingWith(Price("m-cost", "2.000000", "3.000000"));
        var costStore = new ModelCostStore(_factory);
        var runtime = new AgentRuntime(ToolsWithNoop(), new AuditStore(_factory), pricing, costStore);

        var contract = Contract(maxCost: 100m);
        var session = new AgentRunSession(contract);
        var result = await runtime.RunAgentAsync(contract, "sys", Ctx("prod-cost-ok"), gw, "m-cost", session);

        result.Status.Should().Be(AgentRunStatus.Completed);
        result.ModelCost.Should().Be(10.000000m, "two turns at 5.000000 each");
        result.ModelPricingComplete.Should().BeTrue();
        session.AccumulatedCost.Should().Be(10.000000m);

        using var c = await _factory.CreateOpenConnectionAsync();
        var row = await c.QuerySingleAsync<(string Kind, string Amount, string Currency, string State, string ModelId, string Snap, string Req)>(@"
            SELECT kind AS Kind, amount AS Amount, currency AS Currency, reconciliation_state AS State,
                   model_id AS ModelId, pricing_snapshot_id AS Snap, provider_request_id AS Req
            FROM cost_events WHERE production_id = 'prod-cost-ok';");
        row.Kind.Should().Be("SETTLEMENT");
        row.Amount.Should().Be("10.000000");
        row.Currency.Should().Be("EUR");
        row.State.Should().Be("RECONCILED");
        row.ModelId.Should().Be("m-cost");
        row.Snap.Should().NotBeNullOrEmpty("the cost is tied to a materialised pricing snapshot");
        row.Req.Should().Be("req-2");
    }

    [Fact]
    public async Task PriceAbsent_RunStillCompletes_CostEventIsEstimatedUnreconciled()
    {
        await SeedProductionAsync("prod-cost-noprice");
        var gw = new FixedUsageGateway(new[] { "{\"final\": \"done\"}" }, 500_000, 250_000);
        var pricing = PricingWith(); // no configured prices at all
        var runtime = new AgentRuntime(new ToolRegistry(), new AuditStore(_factory), pricing, new ModelCostStore(_factory));

        var contract = Contract(maxCost: 100m);
        var session = new AgentRunSession(contract);
        var result = await runtime.RunAgentAsync(contract, "sys", Ctx("prod-cost-noprice"), gw, "m-unpriced", session);

        result.Status.Should().Be(AgentRunStatus.Completed);
        result.ModelPricingComplete.Should().BeFalse("no pricing snapshot was on file");
        result.ModelCost.Should().Be(0m);
        session.ModelInputTokens.Should().Be(500_000);

        using var c = await _factory.CreateOpenConnectionAsync();
        var (state, amount) = await c.QuerySingleAsync<(string State, string Amount)>(
            "SELECT reconciliation_state AS State, amount AS Amount FROM cost_events WHERE production_id = 'prod-cost-noprice';");
        state.Should().Be("ESTIMATED_UNRECONCILED", "SPEC/21: a known unknown carried on the books");
        amount.Should().Be("0.000000");
    }

    [Fact]
    public async Task ModelSpend_ExceedingMaxCost_FailsTheRunWithBudgetCode()
    {
        await SeedProductionAsync("prod-cost-over");
        // 5.000000 per turn, MaxCost 6.000000: turn 1 ok (5.0), turn 2 loop-top still ok (5.0 < 6.0),
        // turn 2 adds another 5.0 -> 10.0, turn 3 loop-top check trips.
        var toolCall = "{\"tool\": \"noop\", \"input\": {}}";
        var gw = new FixedUsageGateway(new[] { toolCall, toolCall, toolCall, "{\"final\": \"x\"}" }, 1_000_000, 1_000_000);
        var pricing = PricingWith(Price("m-over", "2.000000", "3.000000"));
        var runtime = new AgentRuntime(ToolsWithNoop(), new AuditStore(_factory), pricing, new ModelCostStore(_factory));

        var contract = Contract(maxCost: 6m);
        var session = new AgentRunSession(contract);
        var result = await runtime.RunAgentAsync(contract, "sys", Ctx("prod-cost-over"), gw, "m-over", session);

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.ReasonCode.Should().Be(AmccaErrors.Cst002);
        session.AccumulatedCost.Should().BeGreaterThanOrEqualTo(6m);
        gw.Turns.Should().Be(2, "the third model call is refused by the budget gate before it is made");
    }

    [Fact]
    public async Task NoPricingWired_IsInert_NoCostEventNoBudgetChange()
    {
        await SeedProductionAsync("prod-cost-inert");
        var gw = new FixedUsageGateway(new[] { "{\"final\": \"done\"}" }, 1_000_000, 1_000_000);

        var contract = Contract(maxCost: 1m);
        var session = new AgentRunSession(contract);
        var result = await _runtimeNoCost.RunAgentAsync(contract, "sys", Ctx("prod-cost-inert"), gw, "m", session);

        result.Status.Should().Be(AgentRunStatus.Completed);
        session.AccumulatedCost.Should().Be(0m, "no pricing wired => model calls do not touch the budget");
        session.ModelInputTokens.Should().Be(1_000_000, "usage is still captured");

        using var c = await _factory.CreateOpenConnectionAsync();
        var n = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cost_events WHERE production_id = 'prod-cost-inert';");
        n.Should().Be(0);
    }
}
