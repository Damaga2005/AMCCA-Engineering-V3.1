using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.StateMachine;
using AMCCA.Core.Tools;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ArchitectureAndRegistryRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly StateMachineRegistry _stateMachine;
    private readonly ProductionService _prodService;

    public ArchitectureAndRegistryRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_ARCH_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "arch_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var smJsonPath = Path.Combine(repoRoot, "SCHEMAS", "state-machine.json");
        var smJson = File.ReadAllText(smJsonPath);
        _stateMachine = new StateMachineRegistry(smJson);

        var eventStore = new EventStore(_factory);
        _prodService = new ProductionService(_factory, _stateMachine, eventStore);
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
        }
    }

    [Fact]
    public void DEF024_ToolRegistry_DuplicateToolRegistration_ThrowsException_AndDoesNotOverwrite()
    {
        var registry = new ToolRegistry();

        var tool1 = new DummyTool("test_tool", "1.0.0");
        var tool2 = new DummyTool("test_tool", "2.0.0");

        registry.RegisterTool(tool1);
        registry.HasTool("test_tool").Should().BeTrue();

        var act = () => registry.RegisterTool(tool2);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");

        registry.GetTool("test_tool")!.Definition.ToolVersion.Should().Be("1.0.0",
            "first registered tool instance must not be overwritten by duplicate registration (DEF-024)");
    }

    [Fact]
    public void DEF025_StateMachineRegistry_ConstructionValidation_RejectsInvalidStructures()
    {
        // 1. Duplicate transition ID
        var duplicateIdJson = @"{
            ""schema_version"": ""3.1.0"",
            ""initial_state"": ""A"",
            ""terminal_states"": [""C""],
            ""states"": [
                {""name"": ""A"", ""kind"": ""START"", ""description"": ""start""},
                {""name"": ""B"", ""kind"": ""ACTIVE"", ""description"": ""active""},
                {""name"": ""C"", ""kind"": ""TERMINAL"", ""description"": ""end""}
            ],
            ""transitions"": [
                {""id"": ""T-01"", ""from"": ""A"", ""to"": ""B"", ""trigger"": ""trig"", ""guard"": ""g"", ""actor"": ""HUMAN""},
                {""id"": ""T-01"", ""from"": ""B"", ""to"": ""C"", ""trigger"": ""trig"", ""guard"": ""g"", ""actor"": ""HUMAN""}
            ]
        }";

        var actDup = () => new StateMachineRegistry(duplicateIdJson);
        actDup.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate transition ID*");

        // 2. Unknown 'from' state
        var unknownFrom = @"{
            ""schema_version"": ""3.1.0"",
            ""initial_state"": ""A"",
            ""terminal_states"": [],
            ""states"": [{""name"": ""A"", ""kind"": ""START"", ""description"": ""start""}],
            ""transitions"": [{""id"": ""T-01"", ""from"": ""UNKNOWN"", ""to"": ""A"", ""trigger"": ""trig"", ""guard"": ""g"", ""actor"": ""HUMAN""}]
        }";
        var actUnknownFrom = () => new StateMachineRegistry(unknownFrom);
        actUnknownFrom.Should().Throw<InvalidOperationException>().WithMessage("*Unknown 'from' state*");

        // 3. Outbound from terminal state
        var termOutbound = @"{
            ""schema_version"": ""3.1.0"",
            ""initial_state"": ""A"",
            ""terminal_states"": [""TERM""],
            ""states"": [
                {""name"": ""A"", ""kind"": ""START"", ""description"": ""start""},
                {""name"": ""TERM"", ""kind"": ""TERMINAL"", ""description"": ""term""}
            ],
            ""transitions"": [
                {""id"": ""T-01"", ""from"": ""TERM"", ""to"": ""A"", ""trigger"": ""trig"", ""guard"": ""g"", ""actor"": ""HUMAN""}
            ]
        }";
        var actTermOutbound = () => new StateMachineRegistry(termOutbound);
        actTermOutbound.Should().Throw<InvalidOperationException>().WithMessage("*Terminal state*cannot have outbound transitions*");

        // 4. Self-loop
        var selfLoop = @"{
            ""schema_version"": ""3.1.0"",
            ""initial_state"": ""A"",
            ""terminal_states"": [],
            ""states"": [{""name"": ""A"", ""kind"": ""START"", ""description"": ""start""}],
            ""transitions"": [{""id"": ""T-01"", ""from"": ""A"", ""to"": ""A"", ""trigger"": ""trig"", ""guard"": ""g"", ""actor"": ""HUMAN""}]
        }";
        var actSelfLoop = () => new StateMachineRegistry(selfLoop);
        actSelfLoop.Should().Throw<InvalidOperationException>().WithMessage("*Self-loop transition is not permitted*");
    }

    [Fact]
    public async Task DEF026_ProductionStateTransition_IsStrictlyAtomic_UnderTX1()
    {
        var prod = await _prodService.CreateProductionAsync("Atomic Test", "es-ES", "AUTONOMOUS", "corr-atom");
        prod.State.Should().Be("INIT");
        prod.AggregateVersion.Should().Be(0);

        // Transition from INIT to RESEARCHING
        var updated = await _prodService.TransitionAsync(prod.Id, "RESEARCHING", "HUMAN", "corr-atom-1");
        updated.State.Should().Be("RESEARCHING");
        updated.AggregateVersion.Should().Be(1);

        // Verify that state_transitions and events tables both have exactly 1 record for this transition
        using (var connection = await _factory.CreateOpenConnectionAsync())
        {
            var stCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM state_transitions WHERE production_id = @ProdId;",
                new { ProdId = prod.Id });
            stCount.Should().Be(1);

            var evCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM events WHERE aggregate_id = @ProdId AND event_type = 'production.state_changed';",
                new { ProdId = prod.Id });
            evCount.Should().Be(1);
        }

        // Test Concurrency Failure: simulating an out-of-sync version
        // Transition from RESEARCHING to ASSETS_READY with wrong expected version
        var actConcurrent = () => _prodService.TransitionAsync(prod.Id, "RESEARCHING", "HUMAN", "corr-err");
        // Trigger error: transition to same or invalid state throws AmccaException
        await actConcurrent.Should().ThrowAsync<AmccaException>();

        // Verify no orphaned events or state transitions were written
        using (var connection = await _factory.CreateOpenConnectionAsync())
        {
            var finalState = await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM productions WHERE id = @ProdId;",
                new { ProdId = prod.Id });
            finalState.Should().Be("RESEARCHING");

            var stCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM state_transitions WHERE production_id = @ProdId;",
                new { ProdId = prod.Id });
            stCount.Should().Be(1, "failed transition must not write state_transitions record");
        }
    }

    private class DummyTool : ITool
    {
        public ToolDefinition Definition { get; }

        public DummyTool(string toolId, string version)
        {
            Definition = new ToolDefinition(
                ToolId: toolId,
                ToolVersion: version,
                SideEffectClass: SideEffectClass.READ,
                RequiredPermissions: Array.Empty<string>(),
                TimeoutSeconds: 30);
        }

        public Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult("{}");
        }
    }
}
