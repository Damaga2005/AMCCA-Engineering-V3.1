using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.StateMachine;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class OrchestratorAndStateResumeRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly EventStore _eventStore;
    private readonly StateMachineRegistry _stateMachine;
    private readonly ProductionService _productionService;

    public OrchestratorAndStateResumeRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_ORCH_DEF008_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "orchestrator_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _eventStore = new EventStore(_factory);

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        var repoRoot = dir ?? throw new InvalidOperationException("Could not locate repo root");
        var stateMachineJson = File.ReadAllText(Path.Combine(repoRoot, "SCHEMAS", "state-machine.json"));
        _stateMachine = new StateMachineRegistry(stateMachineJson);

        _productionService = new ProductionService(_factory, _stateMachine, _eventStore);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task DEF008_AgentCannotBeActor_ForStateTransitions()
    {
        var prod = await _productionService.CreateProductionAsync("Title", "es", "AUTONOMOUS", "corr-1");

        // Attempting to advance transition with actorType = "AGENT" must be rejected
        var act = async () => await _productionService.TransitionAsync(
            prod.Id,
            toState: "RESEARCHING",
            actorType: "AGENT",
            correlationId: "corr-1");

        await act.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Ai004, "Agents may never mutate persistent state or act as transition authority (AGENTS.md, DEF-008)");
    }

    [Fact]
    public async Task DEF010_ResumeFromBlocked_ToOriginState_Succeeds()
    {
        var prod = await _productionService.CreateProductionAsync("Title", "es", "AUTONOMOUS", "corr-1");

        // Block from INIT (T-301)
        prod = await _productionService.TransitionAsync(prod.Id, "BLOCKED", "SYSTEM", "corr-1");
        prod.State.Should().Be("BLOCKED");
        prod.BlockedFrom.Should().Be("INIT");

        // Resume to original state INIT (T-401)
        var resumed = await _productionService.TransitionAsync(prod.Id, "INIT", "OPERATOR", "corr-1");
        resumed.State.Should().Be("INIT");
        resumed.BlockedFrom.Should().BeNull();
    }

    [Fact]
    public async Task DEF010_ResumeFromBlocked_ToDifferentState_ThrowsStm002()
    {
        var prod = await _productionService.CreateProductionAsync("Title", "es", "AUTONOMOUS", "corr-1");
        prod = await _productionService.TransitionAsync(prod.Id, "BLOCKED", "SYSTEM", "corr-1");

        // Attempting to resume to RESEARCHING (when blocked_from is INIT) must fail with Stm002
        var act = async () => await _productionService.TransitionAsync(prod.Id, "RESEARCHING", "OPERATOR", "corr-1");

        await act.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Stm002);
    }

    [Fact]
    public async Task DEF009_ResumeFromUnknownExternalState_RequiresReconciliation()
    {
        var prod = await _productionService.CreateProductionAsync("Title", "es", "AUTONOMOUS", "corr-1");
        // INIT -> RESEARCHING (T-001)
        prod = await _productionService.TransitionAsync(prod.Id, "RESEARCHING", "ORCHESTRATOR", "corr-1");

        // RESEARCHING -> UNKNOWN_EXTERNAL_STATE (T-501)
        prod = await _productionService.TransitionAsync(prod.Id, "UNKNOWN_EXTERNAL_STATE", "ORCHESTRATOR", "corr-1");
        prod.State.Should().Be("UNKNOWN_EXTERNAL_STATE");
        prod.UnknownFrom.Should().Be("RESEARCHING");

        // Attempting to resume without reconciliation evidence (causationId is null) must be rejected
        var unverifiedAct = async () => await _productionService.TransitionAsync(
            prod.Id, "RESEARCHING", "ReconciliationService", "corr-1", causationId: null);

        await unverifiedAct.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Stm001);

        // Resuming WITH reconciliation evidence (causationId) succeeds (T-601)
        var reconciled = await _productionService.TransitionAsync(
            prod.Id, "RESEARCHING", "ReconciliationService", "corr-1", causationId: "reconciliation-event-777");

        reconciled.State.Should().Be("RESEARCHING");
        reconciled.UnknownFrom.Should().BeNull();
    }
}
