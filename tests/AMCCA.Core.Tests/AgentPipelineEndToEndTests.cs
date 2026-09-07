using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
using AMCCA.Core.Monetization;
using AMCCA.Core.Operator;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.Policy;
using AMCCA.Core.Providers;
using AMCCA.Core.Research;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// M5: one production driven through <see cref="OrchestratorEngine.RunTickAsync"/> across the real
/// RESEARCHING → RESEARCH_VERIFIED → CONCEPT_SELECTED → SCRIPTING → SCRIPT_VERIFIED chain, using the
/// real <see cref="AgentResearchAgent"/>, <see cref="ConceptSelectionStageHandler"/> and
/// <see cref="AgentScriptAgent"/> against a scripted <see cref="IProviderGateway"/>. Real state
/// transitions, a persisted SCRIPT artifact, a selected concept, a reserved budget and a settled
/// model-cost row — no fakes between the engine and the database.
/// </summary>
public class AgentPipelineEndToEndTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly StateMachineRegistry _registry;
    private readonly ProductionService _productions;
    private readonly ArtifactStore _artifacts;
    private readonly OperatorControlService _operatorControl;
    private readonly PolicyGate _policyGate;
    private readonly ApprovalManager _approvals;
    private readonly BudgetManager _budgets;
    private readonly AuditStore _audit;

    public AgentPipelineEndToEndTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_PIPE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "pipe.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();

        _registry = new StateMachineRegistry(File.ReadAllText(Path.Combine(FindRepoRoot(), "SCHEMAS", "state-machine.json")));
        _productions = new ProductionService(_factory, _registry, new EventStore(_factory));
        _artifacts = new ArtifactStore(_factory, Path.Combine(_testDir, "data"));
        _audit = new AuditStore(_factory);
        _approvals = new ApprovalManager(_factory);
        _budgets = new BudgetManager(_factory);
        var jobManager = new JobManager(_factory);
        var policyEngine = new PolicyEngine(_factory, _budgets, _approvals);
        _operatorControl = new OperatorControlService(_factory, _audit, policyEngine, _approvals, jobManager);
        _policyGate = new PolicyGate(_factory, policyEngine, _audit);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private static string FindRepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(d) && !File.Exists(Path.Combine(d, "BUILD_ORDER.md"))) d = Directory.GetParent(d)?.FullName;
        return d ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>One queue serving both agents in turn: research asks first, then script.</summary>
    private sealed class ScriptedGateway : IProviderGateway
    {
        private readonly Queue<string> _r;
        public string ProviderId => "scripted";
        public int Calls { get; private set; }
        public ScriptedGateway(params string[] r) => _r = new Queue<string>(r);
        public Task<ProviderProbeResult> ProbeCapabilityAsync(string p, string m, string c, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, 1));
        public Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest req, CancellationToken ct = default)
        {
            Calls++;
            var body = _r.Count > 0 ? _r.Dequeue() : "{\"final\": \"x\"}";
            return Task.FromResult(new GatewayTextResponse(body, $"req-{Calls}", 1_000_000, 1_000_000));
        }
    }

    private static ModelPricingConfig Price(string modelId) => new()
    {
        ModelId = modelId,
        InputPer1MTokens = "2.000000",
        OutputPer1MTokens = "3.000000",
        Currency = "EUR",
        RetrievedAt = "2026-09-01T00:00:00.0000000Z",
        SourceRef = "https://provider.example/pricing",
    };

    [Fact]
    public async Task DrivesResearchThroughScript_OnRealHandlers_WithRealAgents()
    {
        // 1. AUTONOMOUS production at INIT.
        var pid = (await _productions.CreateProductionAsync("A researched topic", "en", "AUTONOMOUS", "corr-pipe")).Id;

        // 2. Seed the facts the deterministic gates check against: one VERIFIED material claim (the
        //    RESEARCHING exit criterion, SPEC/26), one SCORED opportunity and a production budget (the
        //    CONCEPT_SELECTED gate, D-035 / T-004).
        var claimId = UlidGenerator.NewUlid();
        using (var c = await _factory.CreateOpenConnectionAsync())
        {
            await c.ExecuteAsync(@"INSERT INTO niches (id, name, language, state, created_at, updated_at)
                VALUES ('n-pipe', 'n-pipe', 'en', 'CANDIDATE', datetime('now'), datetime('now'));");
            await c.ExecuteAsync(@"
                INSERT INTO claims (id, production_id, text, status, materiality, subject_class, contains_personal_data, schema_version, created_at)
                VALUES (@Id, @Pid, 'the verified fact', 'VERIFIED', 'MATERIAL', 'GENERAL', 0, '3.1.0', @Now);",
                new { Id = claimId, Pid = pid, Now = DateTimeOffset.UtcNow.ToString("O") });
            await c.ExecuteAsync(@"
                INSERT INTO opportunities (id, niche_id, state, score, score_breakdown_json, expected_revenue,
                                           expected_cost, risk_penalty, currency, scored_at, created_at, updated_at)
                VALUES ('opp-pipe', 'n-pipe', 'SCORED', 0.9, '{""trend"":0.8}', '80.000000', '5.000000', 0.1, 'EUR',
                        datetime('now'), datetime('now'), datetime('now'));");
        }
        await _budgets.CreateBudgetAsync("bud-pipe", "PRODUCTION", pid, 50.000000m);

        // 3. Real agents. The research agent finishes immediately (0 tool calls) — the seeded claim is
        //    the exit criterion; its generative half is covered by AgentResearchAgentContractTests. The
        //    script agent returns a schema-valid script whose only material line maps to the claim.
        var scriptFinal = JsonSerializer.Serialize(new
        {
            final = new
            {
                estimated_spoken_duration_sec = 30,
                lines = new object[]
                {
                    new { line_number = 1, text = "the verified fact", claim_id = claimId, is_material_fact = true, uncertainty_wording_present = false },
                },
            },
        });
        var gateway = new ScriptedGateway("{\"final\": \"research complete\"}", scriptFinal);

        var pricing = new PricingSnapshotModelPricing(_factory, "scripted", new[] { Price("gpt-4o-mini") });
        var costStore = new ModelCostStore(_factory);

        var research = new AgentResearchAgent(
            _productions, new ResearchService(_factory), _audit, gateway,
            options: null, modelPricing: pricing, modelCostStore: costStore);
        var script = new AgentScriptAgent(
            _productions, _factory, _audit, gateway, _artifacts,
            options: null, modelPricing: pricing, modelCostStore: costStore);

        var handlers = new StageHandlerRegistry()
            .Register("INIT", new InitStageHandler())
            .Register("RESEARCHING", new ResearchStageHandler(_factory, research))
            .Register("RESEARCH_VERIFIED", new NoWorkAdvanceHandler())
            .Register("CONCEPT_SELECTED", new ConceptSelectionStageHandler(_factory, _budgets, _audit))
            .Register("SCRIPTING", new ScriptStageHandler(_factory, script));

        var engine = new OrchestratorEngine(_registry, _productions, handlers, _operatorControl, _policyGate, _approvals);

        // 4. Tick until SCRIPT_VERIFIED (or give up).
        string state = "";
        for (int i = 0; i < 8; i++)
        {
            await engine.RunTickAsync();
            state = (await _productions.GetProductionAsync(pid))!.State;
            if (state is "SCRIPT_VERIFIED" or "BLOCKED" or "REWORK" or "FAILED") break;
        }

        state.Should().Be("SCRIPT_VERIFIED");

        using var conn = await _factory.CreateOpenConnectionAsync();

        var transitions = (await conn.QueryAsync<string>(
            "SELECT to_state FROM state_transitions WHERE production_id = @Id ORDER BY occurred_at ASC;",
            new { Id = pid })).ToList();
        transitions.Should().ContainInOrder(
            "RESEARCHING", "RESEARCH_VERIFIED", "CONCEPT_SELECTED", "SCRIPTING", "SCRIPT_VERIFIED");

        (await _artifacts.GetCurrentTextAsync(pid, "SCRIPT")).Should().NotBeNull("the script agent persisted the SCRIPT artifact");

        var oppState = await conn.ExecuteScalarAsync<string>("SELECT state FROM opportunities WHERE id = 'opp-pipe';");
        oppState.Should().Be("SELECTED", "the concept gate locked the opportunity");
        var linked = await conn.ExecuteScalarAsync<string>("SELECT opportunity_id FROM productions WHERE id = @Id;", new { Id = pid });
        linked.Should().Be("opp-pipe");
        Money.Parse((await conn.ExecuteScalarAsync<string>("SELECT reserved FROM budgets WHERE id = 'bud-pipe';"))!)
            .Should().Be(5.000000m, "the scripting budget reservation is the concept's expected_cost");

        var settled = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cost_events WHERE production_id = @Id AND kind = 'SETTLEMENT' AND reconciliation_state = 'RECONCILED';",
            new { Id = pid });
        settled.Should().BeGreaterThanOrEqualTo(1, "the script agent's model spend was priced and settled through the engine (H1)");
    }
}
