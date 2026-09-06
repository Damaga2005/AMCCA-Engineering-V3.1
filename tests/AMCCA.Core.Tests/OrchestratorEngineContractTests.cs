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
using Dapper;
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
    private readonly PolicyGate _policyGate;
    private readonly ApprovalManager _approvals;
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
        _approvals = new ApprovalManager(_factory);
        var budgetManager = new BudgetManager(_factory);
        var auditStore = new AuditStore(_factory);
        var policyEngine = new PolicyEngine(_factory, budgetManager, _approvals);
        _operatorControl = new OperatorControlService(
            _factory, auditStore, policyEngine, _approvals, jobManager);
        _policyGate = new PolicyGate(_factory, policyEngine, auditStore);
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
        => new(_registry, _productions, handlers, _operatorControl, _policyGate, _approvals);

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

    private static readonly string[] StagesToPublish =
    {
        "INIT", "RESEARCHING", "RESEARCH_VERIFIED", "CONCEPT_SELECTED", "SCRIPTING", "SCRIPT_VERIFIED",
        "STORYBOARDING", "STORYBOARD_VERIFIED", "ASSET_GENERATION", "ASSETS_READY", "AUDIO_GENERATION",
        "AUDIO_READY", "EDITING", "CANDIDATE_RENDERED", "TECHNICAL_QA", "VISUAL_QA", "AUDIO_QA",
        "CONTENT_QA", "RETENTION_QA", "COMPLIANCE_QA", "SCORING", "FINAL_VERIFIED",
    };

    private async Task DriveToAsync(OrchestratorEngine engine, string id, string target, int maxTicks = 40)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            if ((await _productions.GetProductionAsync(id))!.State == target) return;
            await engine.RunTickAsync();
        }
    }

    [Fact]
    public async Task Autonomous_AtPublishBoundary_RecordsAPolicyDecision_AndBlocksWithoutAnApproval()
    {
        var id = await CreateProductionAsync("AUTONOMOUS");
        var engine = Engine(Advancing(StagesToPublish));

        await DriveToAsync(engine, id, "READY_TO_PUBLISH");
        (await _productions.GetProductionAsync(id))!.State.Should().Be("READY_TO_PUBLISH");

        await engine.RunTickAsync(); // hits the publish boundary

        var prod = await _productions.GetProductionAsync(id);
        prod!.State.Should().Be("BLOCKED");
        prod.BlockedFrom.Should().Be("READY_TO_PUBLISH");

        using var conn = await _factory.CreateOpenConnectionAsync();
        var decision = await conn.QuerySingleAsync<(string Decision, string RuleKey, string VersionId)>(
            @"SELECT decision AS Decision, rule_key AS RuleKey, policy_version_id AS VersionId
              FROM policy_decisions WHERE production_id = @Id AND action = 'publication.dispatch'
              ORDER BY decided_at DESC LIMIT 1;", new { Id = id });
        decision.Decision.Should().Be("REQUIRE_APPROVAL");
        decision.VersionId.Should().NotBeNullOrEmpty();

        var audit = await conn.QuerySingleAsync<(string ReasonCode, string PolicyDecisionId)>(
            @"SELECT reason_code AS ReasonCode, policy_decision_id AS PolicyDecisionId
              FROM audit_log WHERE subject_id = @Id AND outcome IN ('BLOCKED','DENIED')
              ORDER BY occurred_at DESC LIMIT 1;", new { Id = id });
        audit.ReasonCode.Should().Be(AmccaErrors.Pol004);
        audit.PolicyDecisionId.Should().NotBeNullOrEmpty("SPEC/60 obligation 4: the Inspector resolves this id");
    }

    [Fact]
    public async Task Autonomous_AtPublishBoundary_WithAnApprovedGate_IsAllowed_AndDrivesPublishing()
    {
        var id = await CreateProductionAsync("AUTONOMOUS");
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO approvals (id, production_id, action, scope_json, state, single_use, expires_at, created_at)
                  VALUES (@Aid, @Id, 'publication.dispatch', '{}', 'APPROVED', 1, @Exp, @Now);",
                new
                {
                    Aid = "app-pub-1", Id = id,
                    Exp = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
                    Now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var handlers = Advancing(StagesToPublish);
        handlers.Register("READY_TO_PUBLISH", new FnHandler(_ => StageResult.Advance()));
        var engine = Engine(handlers);

        await DriveToAsync(engine, id, "PUBLISHING", maxTicks: 45);

        (await _productions.GetProductionAsync(id))!.State.Should().Be("PUBLISHING");

        using var conn2 = await _factory.CreateOpenConnectionAsync();
        var lastDecision = await conn2.QuerySingleAsync<string>(
            @"SELECT decision FROM policy_decisions WHERE production_id = @Id AND action = 'publication.dispatch'
              ORDER BY decided_at DESC LIMIT 1;", new { Id = id });
        lastDecision.Should().Be("ALLOW", "an approved gate lets the protected action proceed");
    }

    [Fact]
    public async Task Autonomous_WithVerifiedResearch_DrivesThroughToScripting_ThenBlocksWithoutAScriptAgent()
    {
        var id = await CreateProductionAsync("AUTONOMOUS");
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            for (int i = 0; i < 2; i++)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO claims (id, production_id, text, status, materiality, subject_class, contains_personal_data, schema_version, created_at)
                      VALUES (@Id, @Pid, 'fact', 'VERIFIED', 'MATERIAL', 'GENERAL', 0, '3.1.0', @Now);",
                    new { Id = AMCCA.Core.Database.UlidGenerator.NewUlid(), Pid = id, Now = DateTimeOffset.UtcNow.ToString("O") });
            }
        }

        var advance = new AMCCA.Core.Orchestration.Handlers.NoWorkAdvanceHandler();
        var handlers = new StageHandlerRegistry()
            .Register("INIT", new InitStageHandler())
            .Register("RESEARCHING", new AMCCA.Core.Orchestration.Handlers.ResearchStageHandler(_factory))
            .Register("RESEARCH_VERIFIED", advance)
            .Register("CONCEPT_SELECTED", advance)
            .Register("SCRIPTING", new AMCCA.Core.Orchestration.Handlers.ScriptStageHandler(_factory));
        var engine = Engine(handlers);

        for (int i = 0; i < 8; i++) await engine.RunTickAsync();

        var prod = await _productions.GetProductionAsync(id);
        prod!.State.Should().Be("BLOCKED");
        prod.BlockedFrom.Should().Be("SCRIPTING", "research verified and advanced; scripting needs an agent");

        using var conn2 = await _factory.CreateOpenConnectionAsync();
        var transitions = await conn2.QueryAsync<string>(
            "SELECT to_state FROM state_transitions WHERE production_id = @Id ORDER BY occurred_at ASC;", new { Id = id });
        transitions.Should().ContainInOrder("RESEARCHING", "RESEARCH_VERIFIED", "CONCEPT_SELECTED", "SCRIPTING", "BLOCKED");
    }

    [Fact]
    public async Task PolicyGate_SeedsExactlyOneBuiltInPolicyVersion_EvenAcrossCalls()
    {
        var v1 = await _policyGate.EnsureBuiltInPolicyVersionAsync();
        var v2 = await _policyGate.EnsureBuiltInPolicyVersionAsync();
        v1.Should().Be(v2);

        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM policy_versions;")).Should().Be(1);
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM policies;")).Should().Be(1);
        (await conn.ExecuteScalarAsync<string>("SELECT body_sha256 FROM policy_versions;"))
            .Should().MatchRegex("^[0-9a-f]{64}$");
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
