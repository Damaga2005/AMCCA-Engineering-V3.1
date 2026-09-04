using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>
/// SEC-06 — an agent tool call reserves budget only after authorization, tool existence, the
/// side-effect gate and the intent check have all passed, and rolls the reservation back if
/// execution throws or is cancelled. A rejected or failed call consumes no budget.
/// </summary>
public class AgentCostReservationOrderRegressionTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly AuditStore _audit;
    private readonly ToolRegistry _tools = new();
    private readonly AgentRuntime _runtime;

    public AgentCostReservationOrderRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AMCCA_SEC06_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_dir, "t.db"));
        new MigrationService(_factory, _dir).UpgradeAsync().GetAwaiter().GetResult();
        _audit = new AuditStore(_factory);
        _runtime = new AgentRuntime(_tools, _audit);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AgentContract Contract(params string[] allowed)
        => new("agent-sec06", "1.0", new HashSet<string>(allowed), new HashSet<string>(), MaxCost: 100m, TimeoutSeconds: 5);

    private sealed class FakeTool : ITool
    {
        private readonly Func<CancellationToken, Task<string>> _body;
        public ToolDefinition Definition { get; }
        public bool Ran { get; private set; }

        public FakeTool(string id, SideEffectClass cls, Func<CancellationToken, Task<string>>? body = null)
        {
            Definition = new ToolDefinition(id, "1.0", cls, Array.Empty<string>(), 30);
            _body = body ?? (_ => Task.FromResult("ok"));
        }

        public async Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, CancellationToken ct = default)
        {
            Ran = true;
            return await _body(ct);
        }
    }

    [Fact]
    public async Task UnauthorizedTool_ReservesNoCost()
    {
        _tools.RegisterTool(new FakeTool("registered.pure", SideEffectClass.PURE));
        var session = new AgentRunSession(Contract("registered.pure"));

        var act = async () => await _runtime.ExecuteToolCallAsync(
            Contract("registered.pure"), "not.allowed", "{}", new ToolExecutionContext("p", "c"),
            toolCost: 25m, session: session);

        await act.Should().ThrowAsync<AmccaException>().Where(e => e.ErrorCode == AmccaErrors.Ai004);
        session.AccumulatedCost.Should().Be(0m);
    }

    [Fact]
    public async Task NonexistentTool_ReservesNoCost()
    {
        var contract = Contract("ghost.tool");
        var session = new AgentRunSession(contract);

        var act = async () => await _runtime.ExecuteToolCallAsync(
            contract, "ghost.tool", "{}", new ToolExecutionContext("p", "c"), toolCost: 25m, session: session);

        await act.Should().ThrowAsync<AmccaException>();
        session.AccumulatedCost.Should().Be(0m);
    }

    [Fact]
    public async Task ExternalUnsafeWithoutIntent_ReservesNoCost()
    {
        var tool = new FakeTool("publish.clip", SideEffectClass.EXTERNAL_UNSAFE);
        _tools.RegisterTool(tool);
        var contract = Contract("publish.clip");
        var session = new AgentRunSession(contract);

        var act = async () => await _runtime.ExecuteToolCallAsync(
            contract, "publish.clip", "{}", new ToolExecutionContext("corr", null),
            toolCost: 25m, session: session);

        await act.Should().ThrowAsync<AmccaException>().Where(e => e.ErrorCode == AmccaErrors.Sec001);
        session.AccumulatedCost.Should().Be(0m);
        tool.Ran.Should().BeFalse();
    }

    [Fact]
    public async Task ValidCall_ReservesCostExactlyOnce()
    {
        _tools.RegisterTool(new FakeTool("calc.pure", SideEffectClass.PURE));
        var contract = Contract("calc.pure");
        var session = new AgentRunSession(contract);

        await _runtime.ExecuteToolCallAsync(
            contract, "calc.pure", "{}", new ToolExecutionContext("p", "c"), toolCost: 12m, session: session);

        session.AccumulatedCost.Should().Be(12m);
    }

    [Fact]
    public async Task ExecutionThrows_RollsBackReservation()
    {
        _tools.RegisterTool(new FakeTool("boom", SideEffectClass.PURE,
            body: _ => throw new InvalidOperationException("tool blew up")));
        var contract = Contract("boom");
        var session = new AgentRunSession(contract);
        session.TryReserveCost(30m); // pre-existing spend on the run

        var act = async () => await _runtime.ExecuteToolCallAsync(
            contract, "boom", "{}", new ToolExecutionContext("p", "c"), toolCost: 40m, session: session);

        await act.Should().ThrowAsync<InvalidOperationException>();
        session.AccumulatedCost.Should().Be(30m, "the failed call's reservation is returned");
    }

    [Fact]
    public async Task ExecutionCancelled_RollsBackReservation()
    {
        _tools.RegisterTool(new FakeTool("slow", SideEffectClass.PURE, body: async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return "done";
        }));
        var contract = Contract("slow");
        var session = new AgentRunSession(contract);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await _runtime.ExecuteToolCallAsync(
            contract, "slow", "{}", new ToolExecutionContext("p", "c"), toolCost: 20m, session: session, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.AccumulatedCost.Should().Be(0m);
    }

    [Fact]
    public async Task SuccessfulExecution_KeepsSettlement()
    {
        _tools.RegisterTool(new FakeTool("ok.tool", SideEffectClass.PURE));
        var contract = Contract("ok.tool");
        var session = new AgentRunSession(contract);

        await _runtime.ExecuteToolCallAsync(
            contract, "ok.tool", "{}", new ToolExecutionContext("p", "c"), toolCost: 7m, session: session);
        await _runtime.ExecuteToolCallAsync(
            contract, "ok.tool", "{}", new ToolExecutionContext("p", "c"), toolCost: 8m, session: session);

        session.AccumulatedCost.Should().Be(15m);
    }
}
