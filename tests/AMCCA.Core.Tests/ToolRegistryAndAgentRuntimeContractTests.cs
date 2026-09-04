using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.Core.Agents;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Tools;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ToolRegistryAndAgentRuntimeContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly AuditStore _auditStore;
    private readonly ToolRegistry _toolRegistry;
    private readonly AgentRuntime _runtime;

    public ToolRegistryAndAgentRuntimeContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_AGENT_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "agents_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        // Run migrations to ensure audit_log table exists
        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _auditStore = new AuditStore(_factory);
        _toolRegistry = new ToolRegistry();
        _runtime = new AgentRuntime(_toolRegistry, _auditStore);

        // Register a safe tool and an external unsafe tool
        _toolRegistry.RegisterTool(new FakeTool("search_evidence", SideEffectClass.READ));
        _toolRegistry.RegisterTool(new FakeTool("publish_clip", SideEffectClass.EXTERNAL_UNSAFE));
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
    public async Task Agent_CallingAllowedTool_ExecutesSuccessfully()
    {
        var contract = new AgentContract(
            AgentId: "ResearchAgent",
            AgentVersion: "1.0.0",
            AllowedTools: new HashSet<string> { "search_evidence" },
            ForbiddenTools: new HashSet<string> { "publish_clip" },
            MaxCost: 0.50m,
            TimeoutSeconds: 30);

        var context = new ToolExecutionContext(CorrelationId: "corr-10", IntentId: null);
        var result = await _runtime.ExecuteToolCallAsync(contract, "search_evidence", "{\"query\":\"AI Act\"}", context);

        result.Should().Be("Executed: search_evidence");
    }

    [Fact]
    public async Task Agent_CallingForbiddenTool_IsBlockedAndAuditedWithAi004()
    {
        // Exit criterion: "An agent calling a forbidden tool is blocked and audited"
        var contract = new AgentContract(
            AgentId: "ResearchAgent",
            AgentVersion: "1.0.0",
            AllowedTools: new HashSet<string> { "search_evidence" },
            ForbiddenTools: new HashSet<string> { "publish_clip" },
            MaxCost: 0.50m,
            TimeoutSeconds: 30);

        var context = new ToolExecutionContext(CorrelationId: "corr-audit-check", IntentId: null);

        var act = async () => await _runtime.ExecuteToolCallAsync(contract, "publish_clip", "{}", context);

        // 1. Must throw AMCCA-AI-004
        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Ai004);

        // 2. Must audit the blocked attempt in audit_log
        var logs = await _auditStore.GetAuditLogsAsync(correlationId: "corr-audit-check");
        logs.Should().ContainSingle();
        var log = logs.First();
        log.Action.Should().Be("agent.tool_call_blocked");
        log.ActorType.Should().Be("SYSTEM");
        log.Outcome.Should().Be("BLOCKED");
        log.ReasonCode.Should().Be(AmccaErrors.Ai004);
    }

    [Fact]
    public async Task Agent_CallingUngrantedTool_IsBlockedAndAuditedWithAi004()
    {
        var contract = new AgentContract(
            AgentId: "ScriptAgent",
            AgentVersion: "1.0.0",
            AllowedTools: new HashSet<string>(), // no tools granted
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 0.50m,
            TimeoutSeconds: 30);

        var context = new ToolExecutionContext(CorrelationId: "corr-ungranted", IntentId: null);

        var act = async () => await _runtime.ExecuteToolCallAsync(contract, "search_evidence", "{}", context);

        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Ai004);

        var logs = await _auditStore.GetAuditLogsAsync(correlationId: "corr-ungranted");
        logs.Should().ContainSingle();
        logs.First().Outcome.Should().Be("BLOCKED");
    }

    [Fact]
    public async Task ExternalUnsafeTool_WithoutCommittedIntent_IsRejected()
    {
        var contract = new AgentContract(
            AgentId: "PublishAgent",
            AgentVersion: "1.0.0",
            AllowedTools: new HashSet<string> { "publish_clip" },
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 1.00m,
            TimeoutSeconds: 60);

        // IntentId is null for an EXTERNAL_UNSAFE tool
        var context = new ToolExecutionContext(CorrelationId: "corr-unsafe-no-intent", IntentId: null);

        var act = async () => await _runtime.ExecuteToolCallAsync(contract, "publish_clip", "{}", context);

        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Sec001);
    }

    [Fact]
    public void AgentOutput_FailingSchemaValidation_FailsWithAi003()
    {
        var contract = new AgentContract(
            AgentId: "HookAgent",
            AgentVersion: "1.0.0",
            AllowedTools: new HashSet<string>(),
            ForbiddenTools: new HashSet<string>(),
            MaxCost: 0.10m,
            TimeoutSeconds: 15,
            OutputSchemaJson: @"{
                ""type"": ""object"",
                ""required"": [""hook_text""],
                ""properties"": {
                    ""hook_text"": { ""type"": ""string"", ""minLength"": 5 }
                }
            }");

        // Output violates schema (missing 'hook_text')
        var invalidOutputJson = "{\"invalid_field\":\"hello\"}";

        var act = () => _runtime.ValidateAgentOutput(contract, invalidOutputJson);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Ai003);
    }

    private class FakeTool : ITool
    {
        public ToolDefinition Definition { get; }

        public FakeTool(string toolId, SideEffectClass sideEffectClass)
        {
            Definition = new ToolDefinition(toolId, "1.0.0", sideEffectClass, Array.Empty<string>(), 30);
        }

        public Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult($"Executed: {Definition.ToolId}");
        }
    }
}
