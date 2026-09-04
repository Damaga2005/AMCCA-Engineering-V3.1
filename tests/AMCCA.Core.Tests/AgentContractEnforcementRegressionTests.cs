using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Agents;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Tools;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class AgentContractEnforcementRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly AuditStore _auditStore;
    private readonly ToolRegistry _toolRegistry;
    private readonly AgentRuntime _agentRuntime;

    public AgentContractEnforcementRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_AGENT_DEF004_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "agent_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _auditStore = new AuditStore(_factory);
        _toolRegistry = new ToolRegistry();
        _agentRuntime = new AgentRuntime(_toolRegistry, _auditStore);
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
    public async Task MaxCost_ExactBudget_Passes()
    {
        var tool = new MockTool("math.add", SideEffectClass.PURE);
        _toolRegistry.RegisterTool(tool);

        var contract = new AgentContract(
            AgentId: "agent-1",
            AgentVersion: "1.0",
            AllowedTools: new HashSet<string> { "math.add" },
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 10.0m,
            TimeoutSeconds: 5);

        var session = new AgentRunSession(contract);
        var ctx = new ToolExecutionContext("prod-1", "corr-1");

        var result = await _agentRuntime.ExecuteToolCallAsync(
            contract, "math.add", "{}", ctx, toolCost: 10.0m, session: session);

        result.Should().Be("executed: math.add");
        session.AccumulatedCost.Should().Be(10.0m);
    }

    [Fact]
    public async Task MaxCost_OverBudget_IsBlockedBeforeExecution()
    {
        bool toolRan = false;
        var tool = new MockTool("video.generate", SideEffectClass.LOCAL_WRITE, () => toolRan = true);
        _toolRegistry.RegisterTool(tool);

        var contract = new AgentContract(
            AgentId: "agent-over",
            AgentVersion: "1.0",
            AllowedTools: new HashSet<string> { "video.generate" },
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 15.0m,
            TimeoutSeconds: 5);

        var session = new AgentRunSession(contract);
        var ctx = new ToolExecutionContext("prod-1", "corr-1");

        // Requested 20.0m > MaxCost 15.0m
        var act = async () => await _agentRuntime.ExecuteToolCallAsync(
            contract, "video.generate", "{}", ctx, toolCost: 20.0m, session: session);

        await act.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cst002);

        toolRan.Should().BeFalse("tool must NOT execute when call exceeds agent contract MaxCost (DEF-004)");
    }

    [Fact]
    public async Task MaxCost_CumulativeBudget_BlocksSubsequentCallWhenExceeded()
    {
        var tool = new MockTool("query.item", SideEffectClass.READ);
        _toolRegistry.RegisterTool(tool);

        var contract = new AgentContract(
            AgentId: "agent-cum",
            AgentVersion: "1.0",
            AllowedTools: new HashSet<string> { "query.item" },
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 10.0m,
            TimeoutSeconds: 5);

        var session = new AgentRunSession(contract);
        var ctx = new ToolExecutionContext("prod-1", "corr-1");

        // First call: 6.0m (within 10.0m limit) -> PASS
        await _agentRuntime.ExecuteToolCallAsync(
            contract, "query.item", "{}", ctx, toolCost: 6.0m, session: session);

        // Second call: 5.0m (cumulative 11.0m > 10.0m) -> BLOCKED
        var secondCall = async () => await _agentRuntime.ExecuteToolCallAsync(
            contract, "query.item", "{}", ctx, toolCost: 5.0m, session: session);

        await secondCall.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cst002);
    }

    [Fact]
    public async Task MaxCost_ConcurrentCalls_CannotExceedBudgetInAggregate()
    {
        var tool = new MockTool("calc.parallel", SideEffectClass.PURE);
        _toolRegistry.RegisterTool(tool);

        var contract = new AgentContract(
            AgentId: "agent-parallel",
            AgentVersion: "1.0",
            AllowedTools: new HashSet<string> { "calc.parallel" },
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 25.0m,
            TimeoutSeconds: 10);

        var session = new AgentRunSession(contract);
        var ctx = new ToolExecutionContext("prod-1", "corr-1");

        int succeeded = 0;
        int blocked = 0;

        // 10 concurrent requests of 5.0m each (total 50.0m requested against 25.0m ceiling)
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                await _agentRuntime.ExecuteToolCallAsync(
                    contract, "calc.parallel", "{}", ctx, toolCost: 5.0m, session: session);
                Interlocked.Increment(ref succeeded);
            }
            catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Cst002)
            {
                Interlocked.Increment(ref blocked);
            }
        });

        await Task.WhenAll(tasks);

        succeeded.Should().Be(5, "exactly 5 calls of 5.0m reach the 25.0m ceiling");
        blocked.Should().Be(5, "all calls exceeding the ceiling must be blocked");
        session.AccumulatedCost.Should().Be(25.0m);
    }

    [Fact]
    public async Task TimeoutSeconds_CancelsExecutionWhenExceeded()
    {
        var slowTool = new SlowMockTool("slow.op", delay: TimeSpan.FromSeconds(3));
        _toolRegistry.RegisterTool(slowTool);

        // Contract with 1 second timeout
        var contract = new AgentContract(
            AgentId: "agent-slow",
            AgentVersion: "1.0",
            AllowedTools: new HashSet<string> { "slow.op" },
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 100.0m,
            TimeoutSeconds: 1);

        var ctx = new ToolExecutionContext("prod-1", "corr-1");

        var act = async () => await _agentRuntime.ExecuteToolCallAsync(
            contract, "slow.op", "{}", ctx);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private class MockTool : ITool
    {
        private readonly Action? _onExecute;
        public ToolDefinition Definition { get; }

        public MockTool(string id, SideEffectClass sideEffectClass, Action? onExecute = null)
        {
            _onExecute = onExecute;
            Definition = new ToolDefinition(id, "1.0", sideEffectClass, Array.Empty<string>(), 30);
        }

        public Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, CancellationToken ct = default)
        {
            _onExecute?.Invoke();
            return Task.FromResult("executed: " + Definition.ToolId);
        }
    }

    private class SlowMockTool : ITool
    {
        private readonly TimeSpan _delay;
        public ToolDefinition Definition { get; }

        public SlowMockTool(string id, TimeSpan delay)
        {
            _delay = delay;
            Definition = new ToolDefinition(id, "1.0", SideEffectClass.PURE, Array.Empty<string>(), 30);
        }

        public async Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, CancellationToken ct = default)
        {
            await Task.Delay(_delay, ct);
            return "finished";
        }
    }
}
