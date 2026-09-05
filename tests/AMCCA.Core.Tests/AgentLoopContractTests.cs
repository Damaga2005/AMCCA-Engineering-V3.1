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
using AMCCA.Core.Providers;
using AMCCA.Core.Tools;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class AgentLoopContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ToolRegistry _tools = new();
    private readonly AgentRuntime _runtime;

    public AgentLoopContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_AGLOOP_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "agloop.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _runtime = new AgentRuntime(_tools, new AuditStore(_factory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    // ---- test doubles -------------------------------------------------------

    private sealed class ScriptedGateway : IProviderGateway
    {
        private readonly Queue<string> _responses;
        private readonly TimeSpan _delay;
        public List<string> PromptsSeen { get; } = new();
        public string ProviderId => "scripted";

        public ScriptedGateway(IEnumerable<string> responses, TimeSpan? delay = null)
        {
            _responses = new Queue<string>(responses);
            _delay = delay ?? TimeSpan.Zero;
        }

        public Task<ProviderProbeResult> ProbeCapabilityAsync(string p, string m, string c, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, 1));

        public async Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);
            PromptsSeen.Add(request.Prompt);
            var text = _responses.Count > 0 ? _responses.Dequeue() : "{\"final\": \"ran out of script\"}";
            return new GatewayTextResponse(text, "req-" + PromptsSeen.Count, 10, 5);
        }
    }

    private sealed class EchoTool : ITool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition { get; } =
            new("echo", "1.0", SideEffectClass.PURE, Array.Empty<string>(), 30);
        public Task<string> ExecuteAsync(string inputJson, ToolExecutionContext c, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult($"{{\"echoed\": {inputJson}}}");
        }
    }

    private static AgentContract Contract(
        IEnumerable<string>? allowed = null, decimal maxCost = 1m, int timeoutSeconds = 30, string? outputSchema = null)
        => new("agent-loop", "1.0",
            new HashSet<string>(allowed ?? Array.Empty<string>()),
            new HashSet<string>(),
            maxCost, timeoutSeconds, null, outputSchema);

    private static ToolExecutionContext Ctx() => new("corr-loop", IntentId: null, ProductionId: "prod-loop");

    // ---- tests ------------------------------------------------------------

    [Fact]
    public async Task ModelReturnsFinalImmediately_Completes()
    {
        var gw = new ScriptedGateway(new[] { "Here you go. {\"final\": \"the answer is 42\"}" });

        var result = await _runtime.RunAgentAsync(
            Contract(), "You are a helper.", Ctx(), gw, "m1", new AgentRunSession(Contract()));

        result.Status.Should().Be(AgentRunStatus.Completed);
        result.FinalOutput.Should().Be("the answer is 42");
        result.Iterations.Should().Be(1);
    }

    [Fact]
    public async Task ModelCallsToolThenFinal_ExecutesToolAndFeedsResultBack()
    {
        var echo = new EchoTool();
        _tools.RegisterTool(echo);
        var contract = Contract(allowed: new[] { "echo" });
        var gw = new ScriptedGateway(new[]
        {
            "{\"tool\": \"echo\", \"input\": {\"x\": 7}}",
            "{\"final\": \"done\"}",
        });

        var result = await _runtime.RunAgentAsync(
            contract, "sys", Ctx(), gw, "m1", new AgentRunSession(contract));

        result.Status.Should().Be(AgentRunStatus.Completed);
        echo.Calls.Should().Be(1);
        result.Transcript.Should().Contain(t => t.Role == "tool" && t.Content.Contains("echoed"));
        gw.PromptsSeen[1].Should().Contain("\"echoed\"", "the tool result is fed into the next prompt");
    }

    [Fact]
    public async Task ForbiddenTool_FailsWithAi004()
    {
        _tools.RegisterTool(new EchoTool());
        var contract = Contract(allowed: Array.Empty<string>()); // echo not allowed
        var gw = new ScriptedGateway(new[] { "{\"tool\": \"echo\", \"input\": {}}" });

        var result = await _runtime.RunAgentAsync(contract, "sys", Ctx(), gw, "m1", new AgentRunSession(contract));

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.ReasonCode.Should().Be(AmccaErrors.Ai004);
    }

    [Fact]
    public async Task BudgetExhausted_FailsWithCst002()
    {
        var echo = new EchoTool();
        _tools.RegisterTool(echo);
        var contract = Contract(allowed: new[] { "echo" }, maxCost: 0.01m);
        var session = new AgentRunSession(contract);
        var gw = new ScriptedGateway(new[] { "{\"tool\": \"echo\", \"input\": {}}", "{\"final\": \"x\"}" });

        var result = await _runtime.RunAgentAsync(
            contract, "sys", Ctx(), gw, "m1", session,
            toolCosts: new Dictionary<string, decimal> { ["echo"] = 0.05m });

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.ReasonCode.Should().Be(AmccaErrors.Cst002);
        echo.Calls.Should().Be(0, "the reservation is refused before the tool runs (SEC-06)");
    }

    [Fact]
    public async Task ExceedsMaxIterations_FailsWithAi006()
    {
        var echo = new EchoTool();
        _tools.RegisterTool(echo);
        var contract = Contract(allowed: new[] { "echo" });
        var gw = new ScriptedGateway(Enumerable.Repeat("{\"tool\": \"echo\", \"input\": {}}", 20));

        var result = await _runtime.RunAgentAsync(
            contract, "sys", Ctx(), gw, "m1", new AgentRunSession(contract), maxIterations: 3);

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.ReasonCode.Should().Be(AmccaErrors.Ai006);
        result.Iterations.Should().Be(3);
    }

    [Fact]
    public async Task UnparseableTwiceInARow_FailsWithAi006()
    {
        var contract = Contract();
        var gw = new ScriptedGateway(new[] { "I am thinking about it...", "still no idea" });

        var result = await _runtime.RunAgentAsync(contract, "sys", Ctx(), gw, "m1", new AgentRunSession(contract));

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.ReasonCode.Should().Be(AmccaErrors.Ai006);
    }

    [Fact]
    public async Task StructuredFinal_FailingItsOutputSchemaTwice_FailsWithAi003()
    {
        const string schema = @"{""type"":""object"",""required"":[""answer""],""properties"":{""answer"":{""type"":""string""}},""additionalProperties"":false}";
        var contract = Contract(outputSchema: schema);
        var gw = new ScriptedGateway(new[]
        {
            "{\"final\": {\"wrong\": 1}}",
            "{\"final\": {\"still\": \"wrong\"}}",
        });

        var result = await _runtime.RunAgentAsync(contract, "sys", Ctx(), gw, "m1", new AgentRunSession(contract));

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.ReasonCode.Should().Be(AmccaErrors.Ai003);
    }

    [Fact]
    public async Task StructuredFinal_ThatMatchesTheSchema_Completes()
    {
        const string schema = @"{""type"":""object"",""required"":[""answer""],""properties"":{""answer"":{""type"":""string""}},""additionalProperties"":false}";
        var contract = Contract(outputSchema: schema);
        var gw = new ScriptedGateway(new[] { "{\"final\": {\"answer\": \"hello\"}}" });

        var result = await _runtime.RunAgentAsync(contract, "sys", Ctx(), gw, "m1", new AgentRunSession(contract));

        result.Status.Should().Be(AgentRunStatus.Completed);
        result.FinalOutput.Should().Contain("\"answer\"").And.Contain("hello");
    }

    [Fact]
    public async Task ContractTimeout_SurfacesAsOperationCanceled()
    {
        var contract = Contract(timeoutSeconds: 1);
        var gw = new ScriptedGateway(new[] { "{\"final\": \"too late\"}" }, delay: TimeSpan.FromSeconds(3));

        var act = async () => await _runtime.RunAgentAsync(
            contract, "sys", Ctx(), gw, "m1", new AgentRunSession(contract));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
