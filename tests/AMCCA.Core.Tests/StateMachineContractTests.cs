using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.StateMachine;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class StateMachineContractTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _stateMachineJson;
    private readonly StateMachineRegistry _registry;
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;

    public StateMachineContractTests()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        _repoRoot = dir ?? throw new InvalidOperationException("Could not locate repo root");
        _stateMachineJson = File.ReadAllText(Path.Combine(_repoRoot, "SCHEMAS", "state-machine.json"));
        _registry = new StateMachineRegistry(_stateMachineJson);

        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_STM_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "stm_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);
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
    public void Registry_LoadsAll32StatesAnd198Transitions()
    {
        _registry.States.Should().HaveCount(32);
        _registry.Transitions.Should().HaveCount(198);
        _registry.TerminalStates.Should().BeEquivalentTo(new[] { "ARCHIVED", "FAILED", "CANCELLED" });
    }

    public static IEnumerable<object[]> GetAllCanonicalTransitions()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        var json = File.ReadAllText(Path.Combine(dir!, "SCHEMAS", "state-machine.json"));
        using var doc = JsonDocument.Parse(json);
        var transitions = doc.RootElement.GetProperty("transitions");

        foreach (var t in transitions.EnumerateArray())
        {
            yield return new object[]
            {
                t.GetProperty("id").GetString()!,
                t.GetProperty("from").GetString()!,
                t.GetProperty("to").GetString()!,
                t.GetProperty("trigger").GetString()!
            };
        }
    }

    [Theory]
    [MemberData(nameof(GetAllCanonicalTransitions))]
    public void EveryCanonicalTransitionInSpec13_IsValidAndResolves(string id, string from, string to, string trigger)
    {
        var transitionById = _registry.FindTransitionById(id);
        transitionById.Should().NotBeNull();
        transitionById!.From.Should().Be(from);
        transitionById.To.Should().Be(to);
        transitionById.Trigger.Should().Be(trigger);

        var transition = _registry.FindTransition(from, to, trigger);
        transition.Should().NotBeNull();
        transition!.Id.Should().Be(id);
    }

    [Theory]
    [InlineData("INIT", "PUBLISHED")]
    [InlineData("STORYBOARDING", "ARCHIVED")]
    [InlineData("ASSETS_READY", "SCRIPTING")]
    [InlineData("RESEARCHING", "FINAL_VERIFIED")]
    public void NonListedTransition_IsRejectedWithStm001(string from, string to)
    {
        var act = () => _registry.ValidateTransition(from, to, currentBlockedFrom: null);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Stm001);
    }

    [Theory]
    [InlineData("ARCHIVED", "INIT")]
    [InlineData("FAILED", "RESEARCHING")]
    [InlineData("CANCELLED", "SCRIPTING")]
    public void OutboundTransitionFromTerminalState_IsRejectedWithStm003(string from, string to)
    {
        var act = () => _registry.ValidateTransition(from, to, currentBlockedFrom: null);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Stm003);
    }

    [Fact]
    public void ResumingFromBlocked_ToDifferentState_IsRejectedWithStm002()
    {
        // Production was blocked while in SCRIPTING
        string from = "BLOCKED";
        string to = "AUDIO_GENERATION"; // not SCRIPTING
        string blockedFrom = "SCRIPTING";

        var act = () => _registry.ValidateTransition(from, to, currentBlockedFrom: blockedFrom);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Stm002);
    }

    [Fact]
    public void ResumingFromBlocked_ToBlockedFromState_Succeeds()
    {
        string from = "BLOCKED";
        string to = "SCRIPTING";
        string blockedFrom = "SCRIPTING";

        var transition = _registry.ValidateTransition(from, to, currentBlockedFrom: blockedFrom);

        transition.Should().NotBeNull();
        transition.From.Should().Be(from);
        transition.To.Should().Be(to);
    }

    [Fact]
    public async Task Orchestrator_CommitsStateTransition_EventsAndTransitionsInOneTransaction()
    {
        var migrator = new MigrationService(_factory, _testDir);
        await migrator.UpgradeAsync();

        // Add productions and state_transitions tables if not present in migration 001
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS productions (
                    id TEXT PRIMARY KEY,
                    state TEXT NOT NULL,
                    blocked_from TEXT NULL,
                    unknown_from TEXT NULL,
                    rework_attempts INTEGER NOT NULL DEFAULT 0,
                    aggregate_version INTEGER NOT NULL DEFAULT 0,
                    autonomy_mode TEXT NOT NULL,
                    title TEXT NULL,
                    language TEXT NOT NULL,
                    niche_id TEXT NULL,
                    opportunity_id TEXT NULL,
                    current_manifest_id TEXT NULL,
                    schema_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS state_transitions (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    transition_id TEXT NOT NULL,
                    from_state TEXT NOT NULL,
                    to_state TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    actor_type TEXT NOT NULL,
                    correlation_id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    UNIQUE(event_id)
                );
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        var eventStore = new EventStore(_factory);
        var productionService = new ProductionService(_factory, _registry, eventStore);

        // 1. Create production in INIT state
        var prod = await productionService.CreateProductionAsync("Test Video", "es-ES", "MANUAL", "corr-1");
        prod.State.Should().Be("INIT");
        prod.AggregateVersion.Should().Be(0);

        // 2. Transition INIT -> RESEARCHING (T-001)
        var updated = await productionService.TransitionAsync(
            prod.Id,
            toState: "RESEARCHING",
            actorType: "Orchestrator",
            correlationId: "corr-2");

        updated.State.Should().Be("RESEARCHING");
        updated.AggregateVersion.Should().Be(1);

        // 3. Verify event and transition persisted
        var events = await eventStore.GetEventsAsync("production", prod.Id);
        events.Should().HaveCount(2); // 1 create + 1 transition

        var transitions = await productionService.GetStateTransitionsAsync(prod.Id);
        transitions.Should().HaveCount(1);
        transitions.First().TransitionId.Should().Be("T-001");
        transitions.First().FromState.Should().Be("INIT");
        transitions.First().ToState.Should().Be("RESEARCHING");
    }
}
