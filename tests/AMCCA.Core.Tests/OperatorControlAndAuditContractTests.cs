using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Operator;
using AMCCA.Core.Policy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class OperatorControlAndAuditContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly AuditStore _auditStore;
    private readonly BudgetManager _budgetManager;
    private readonly ApprovalManager _approvalManager;
    private readonly PolicyEngine _policyEngine;
    private readonly OperatorControlService _controlService;

    public OperatorControlAndAuditContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_OP_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "operator_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        // Run migrations for audit_log, approvals, budgets
        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        using (var conn = _factory.CreateOpenConnectionAsync().GetAwaiter().GetResult())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS approvals (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    scope_json TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('PENDING','APPROVED','REJECTED','EXPIRED','CONSUMED')),
                    single_use INTEGER NOT NULL DEFAULT 1,
                    decided_by TEXT NULL,
                    decided_at TEXT NULL,
                    consumed_at TEXT NULL,
                    expires_at TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS budgets (
                    id TEXT PRIMARY KEY,
                    window TEXT NOT NULL,
                    scope_id TEXT NOT NULL,
                    limit_amount REAL NOT NULL,
                    reserved REAL NOT NULL DEFAULT 0.0,
                    spent REAL NOT NULL DEFAULT 0.0,
                    currency TEXT NOT NULL DEFAULT 'EUR',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        _auditStore = new AuditStore(_factory);
        _budgetManager = new BudgetManager(_factory);
        _approvalManager = new ApprovalManager(_factory);
        _policyEngine = new PolicyEngine(_factory, _budgetManager, _approvalManager);

        _controlService = new OperatorControlService(
            _factory,
            _auditStore,
            _policyEngine,
            _approvalManager);
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
    public async Task KillSwitchToggle_FromOperatorUI_LeavesFullAuditTrail()
    {
        // Exit criterion: "An action taken from UI leaves a full audit trail" (SPEC/60, AGENTS.md)
        var corrId = "corr-op-kill-1";
        await _controlService.ToggleGlobalKillSwitchAsync(
            operatorId: "operator@amcca.local",
            active: true,
            reason: "Emergency halt due to provider degradation",
            correlationId: corrId);

        // Verify kill switch is active
        var decision = _policyEngine.EvaluateAction("prod-1", "publish");
        decision.Decision.Should().Be("BLOCK");

        // Verify audit log record
        var auditLogs = await _controlService.QueryAuditTrailAsync(correlationId: corrId);
        auditLogs.Should().ContainSingle();

        var log = auditLogs.First();
        log.Action.Should().Be("operator.global_kill_switch_toggled");
        log.ActorType.Should().Be("OPERATOR", "operator actions must have actor_type = OPERATOR");
        log.ActorId.Should().Be("operator@amcca.local");
        log.Outcome.Should().Be("COMMITTED");
        log.SubjectType.Should().Be("system_control");
    }

    [Fact]
    public async Task ApprovalDecision_FromOperatorUI_LeavesFullAuditTrail()
    {
        var approvalId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: "prod-ui-1",
            action: "publish",
            scopeJson: "{\"target\":\"youtube\"}",
            validFor: TimeSpan.FromHours(1));

        var corrId = "corr-op-appr-1";
        await _controlService.SubmitApprovalDecisionAsync(
            operatorId: "admin@amcca.local",
            approvalId: approvalId,
            approved: true,
            reason: "Reviewed script and verified claims",
            correlationId: corrId);

        var auditLogs = await _controlService.QueryAuditTrailAsync(correlationId: corrId);
        auditLogs.Should().ContainSingle();

        var log = auditLogs.First();
        log.Action.Should().Be("operator.approval_decided");
        log.ActorType.Should().Be("OPERATOR");
        log.ActorId.Should().Be("admin@amcca.local");
        log.Outcome.Should().Be("COMMITTED");
        log.SubjectType.Should().Be("approval");
        log.SubjectId.Should().Be(approvalId);
    }

    [Fact]
    public async Task SystemStatusSummary_AccuratelyReflectsSystemState()
    {
        _policyEngine.SetGlobalKillSwitch(active: false);

        var status = await _controlService.GetSystemStatusAsync();

        status.GlobalKillSwitchActive.Should().BeFalse();
        status.AutonomyMode.Should().Be("ASSISTED");
    }
}
