using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.Research;
using AMCCA.Core.Scripts;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class StageHandlerContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ProductionService _productions;

    public StageHandlerContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_STAGE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "stage.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        var repoRoot = FindRepoRoot();
        var registry = new StateMachineRegistry(File.ReadAllText(Path.Combine(repoRoot, "SCHEMAS", "state-machine.json")));
        _productions = new ProductionService(_factory, registry, new EventStore(_factory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<string> NewProductionAsync()
        => (await _productions.CreateProductionAsync("t", "en", "AUTONOMOUS", "corr")).Id;

    private async Task SeedClaimAsync(string productionId, string status, string materiality = "MATERIAL")
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO claims (id, production_id, text, status, materiality, subject_class, contains_personal_data, schema_version, created_at)
              VALUES (@Id, @Pid, 'a fact', @Status, @Mat, 'GENERAL', 0, '3.1.0', @Now);",
            new { Id = UlidGenerator.NewUlid(), Pid = productionId, Status = status, Mat = materiality, Now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private StageContext Ctx(string id, string state) => new(
        new Production { Id = id, State = state, AutonomyMode = "AUTONOMOUS" }, "corr-stage");

    // ---- ResearchStageHandler --------------------------------------------

    [Fact]
    public async Task Research_NoClaims_Blocks()
    {
        var id = await NewProductionAsync();
        var r = await new ResearchStageHandler(_factory).HandleAsync(Ctx(id, "RESEARCHING"));

        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Res002);
    }

    [Fact]
    public async Task Research_AllMaterialClaimsVerified_Advances()
    {
        var id = await NewProductionAsync();
        await SeedClaimAsync(id, "VERIFIED");
        await SeedClaimAsync(id, "VERIFIED");
        await SeedClaimAsync(id, "UNKNOWN", "INCIDENTAL"); // ignored — not material

        var r = await new ResearchStageHandler(_factory).HandleAsync(Ctx(id, "RESEARCHING"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
    }

    [Fact]
    public async Task Research_SomeMaterialClaimUnverified_Defects()
    {
        var id = await NewProductionAsync();
        await SeedClaimAsync(id, "VERIFIED");
        await SeedClaimAsync(id, "ESTIMATED");

        var r = await new ResearchStageHandler(_factory).HandleAsync(Ctx(id, "RESEARCHING"));

        r.Kind.Should().Be(StageOutcomeKind.Defect);
        r.ReasonCode.Should().Be(AmccaErrors.Res001);
    }

    [Fact]
    public async Task Research_WithAnAgent_RunsItThenVerifies()
    {
        var id = await NewProductionAsync();
        var agent = new FnResearchAgent(async pid => await SeedClaimAsync(pid, "VERIFIED"));

        var r = await new ResearchStageHandler(_factory, agent).HandleAsync(Ctx(id, "RESEARCHING"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
        agent.Called.Should().BeTrue();
    }

    // ---- ScriptStageHandler --------------------------------------------

    [Fact]
    public async Task Script_NoAgent_Blocks()
    {
        var id = await NewProductionAsync();
        var r = await new ScriptStageHandler(_factory).HandleAsync(Ctx(id, "SCRIPTING"));

        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Res001);
    }

    [Fact]
    public async Task Script_AgentProducesAValidScript_Advances()
    {
        var id = await NewProductionAsync();
        var claimId = UlidGenerator.NewUlid();
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO claims (id, production_id, text, status, materiality, subject_class, contains_personal_data, schema_version, created_at)
                  VALUES (@Id, @Pid, 'verified fact', 'VERIFIED', 'MATERIAL', 'GENERAL', 0, '3.1.0', @Now);",
                new { Id = claimId, Pid = id, Now = DateTimeOffset.UtcNow.ToString("O") });
        }
        var script = new ScriptDocument(id, new[]
        {
            new ScriptLine(1, "intro", null, false, false),
            new ScriptLine(2, "verified fact", claimId, true, false),
        });

        var r = await new ScriptStageHandler(_factory, new FnScriptAgent(_ => script))
            .HandleAsync(Ctx(id, "SCRIPTING"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
    }

    [Fact]
    public async Task Script_AgentProducesAnUnbackedMaterialFact_Defects()
    {
        var id = await NewProductionAsync();
        var script = new ScriptDocument(id, new[]
        {
            new ScriptLine(1, "an unbacked material claim", null, true, false),
        });

        var r = await new ScriptStageHandler(_factory, new FnScriptAgent(_ => script))
            .HandleAsync(Ctx(id, "SCRIPTING"));

        r.Kind.Should().Be(StageOutcomeKind.Defect);
        r.ReasonCode.Should().Be(AmccaErrors.Res001);
    }

    // ---- test doubles -------------------------------------------------

    private sealed class FnResearchAgent : IResearchAgent
    {
        private readonly Func<string, Task> _fn;
        public bool Called { get; private set; }
        public FnResearchAgent(Func<string, Task> fn) => _fn = fn;
        public async Task PerformResearchAsync(string productionId, string correlationId, CancellationToken ct = default)
        {
            Called = true;
            await _fn(productionId);
        }
    }

    private sealed class FnScriptAgent : IScriptAgent
    {
        private readonly Func<string, ScriptDocument> _fn;
        public FnScriptAgent(Func<string, ScriptDocument> fn) => _fn = fn;
        public Task<ScriptDocument> GenerateScriptAsync(string productionId, string correlationId, CancellationToken ct = default)
            => Task.FromResult(_fn(productionId));
    }
}
