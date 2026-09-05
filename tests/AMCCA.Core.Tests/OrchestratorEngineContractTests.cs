using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
using AMCCA.Core.Operator;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Policy;
using AMCCA.Core.StateMachine;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class OrchestratorEngineContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ProductionService _productions;
    private readonly OperatorControlService _operatorControl;
    private readonly StateMachineRegistry _registry;

    public OrchestratorEngineContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_ORC_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "orc.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();

        var repoRoot = FindRepoRoot();
        _registry = new StateMachineRegistry(File.ReadAllText(Path.Combine(repoRoot, "SCHEMAS", "state-machine.json")));
        _productions = new ProductionService(_factory, _registry, new EventStore(_factory));

        var jobManager = new JobManager(_factory);
        var approvalManager = new ApprovalManager(_factory);
        var budgetManager = new BudgetManager(_factory);
        _operatorControl = new OperatorControlService(
            _factory, new AuditStore(_factory),
            new PolicyEngine(_factory, budgetManager, approvalManager),
            approvalManager, jobManager);
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
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private OrchestratorEngine Engine(StageHandlerRegistry handlers)
        => new(_registry, _productions, handlers, _operatorControl);

    private async Task<string> CreateProductionAsync(string autonomy)
        => (await _productions.CreateProductionAsync("t", "en", autonomy, "corr-test")).Id;

    private sealed class FnHandler : IStageHandler
    {
        private readonly Func<StageContext, StageResult> _fn;
        public FnHandler(Func<StageContext, StageResult> fn) => _fn = fn;
        public Task<StageResult> HandleAsync(StageContext c, CancellationToken ct = default) => Task.FromResult(_fn(c));
    }

    private static StageHandlerRegistry Advancing(params string[] states)
    {
        var r = new StageHandlerRegistry();
        foreach (var s in states) r.Register(s, new FnHandler(_ => StageResult.Advance()));
        return r;
    }

    [Fact]
    public async Task Autonomous_DrivesInitToResearching_ThenBlocksAtTheFirstUnhandledState()
    {
        var id = await CreateProductionAsync("AUTONOMOUS");
        var handlers = new StageHandlerRegistry().Register("INIT", new InitStageHandler());
        var engine = Engine(handlers);

        var t1 = await engine.RunTickAsync();
        t1.Actions.Should().ContainSingle(a => a.ProductionId == id && a.FromState == "INIT" && a.ToState == "RESEARCHING");
        (await _productions.GetProductionAsync(id))!.State.Should().Be("RESEARCHING");

        var t2 = await engine.RunTickAsync();
        t2.Actions.Should().ContainSingle(a =>
            a.ProductionId == id && a.ToState == "BLOCKED" && a.ReasonCode == AmccaErrors.Orc001);

        var prod = await _productions.GetProductionAsync(id);
        prod!.State.Should().Be("BLOCKED");
        prod.BlockedFrom.Should().Be("RESEARCHING", "the operator resumes to where it was blocked");
    }

    [Fact]
    public async Task Manual_ProductionIsNeverDriven()
    {
        var id = await CreateProductionAsync("MANUAL");
        var engine = Engine(new StageHandlerRegistry().Register("INIT", new InitStageHandler()));

        var report = await engine.RunTickAsync();

        report.Skipped.Should().Be(1);
        report.Actions.Should().BeEmpty();
        (await _productions.GetProductionAsync(id))!.State.Should().Be("INIT");
    }

    [Fact]
    public async Task KillSwitchEngaged_HaltsTheTick()
    {
        var id = await CreateProductionAsync("AUTONOMOUS");
        await _operatorControl.ToggleGlobalKillSwitchAsync("operator", active: true, "test", "corr-k");
        var engine = Engine(new StageHandlerRegistry().Register("INIT", new InitStageHandler()));

        var report = await engine.RunTickAsync();

        report.KillSwitchEngaged.Should().BeTrue();
        report.Actions.Should().BeEmpty();
        (await _productions.GetProductionAsync(id))!.State.Should().Be("INIT");
    }

    [Fact]
    public async Task Assisted_ParksAtTheFirstGateState_ForOperatorSignOff()
    {
        var id = await CreateProductionAsync("ASSISTED");
        // Every state advances; ASSISTED must still stop at the first `gate` (CONCEPT_SELECTED).
        var handlers = Advancing("INIT", "RESEARCHING", "RESEARCH_VERIFIED", "CONCEPT_SELECTED", "SCRIPTING");
        handlers.Register("INIT", new InitStageHandler());
        var engine = Engine(handlers);

        for (int i = 0; i < 6; i++) await engine.RunTickAsync();

        (await _productions.GetProductionAsync(id))!.State.Should().Be("CONCEPT_SELECTED");
        (await engine.RunTickAsync()).AwaitingApproval.Should().Be(1);
    }

    [Fact]
    public async Task StageHandlerReturningDefect_RoutesToRework()
    {
        var id = await CreateProductionAsync("AUTONOMOUS");
        var handlers = Advancing("INIT", "RESEARCHING", "RESEARCH_VERIFIED", "CONCEPT_SELECTED");
        handlers.Register("SCRIPTING", new FnHandler(_ => StageResult.Defect(AmccaErrors.Qa001, "bad script")));
        var engine = Engine(handlers);

        for (int i = 0; i < 5; i++) await engine.RunTickAsync();

        (await _productions.GetProductionAsync(id))!.State.Should().Be("REWORK");
    }

    [Fact]
    public async Task StageHandlerThatThrows_BlocksWithOrc002()
    {
        var id = await CreateProductionAsync("AUTONOMOUS");
        var handlers = new StageHandlerRegistry()
            .Register("INIT", new InitStageHandler())
            .Register("RESEARCHING", new FnHandler(_ => throw new InvalidOperationException("boom")));
        var engine = Engine(handlers);

        await engine.RunTickAsync(); // INIT -> RESEARCHING
        var t2 = await engine.RunTickAsync(); // handler throws

        t2.Actions.Should().ContainSingle(a =>
            a.ProductionId == id && a.ToState == "BLOCKED" && a.ReasonCode == AmccaErrors.Orc002);
        (await _productions.GetProductionAsync(id))!.State.Should().Be("BLOCKED");
    }
}
