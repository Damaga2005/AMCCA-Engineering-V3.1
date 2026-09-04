using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Policy;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class PolicyEngineFailClosedRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly BudgetManager _budgetManager;
    private readonly ApprovalManager _approvalManager;
    private readonly PolicyEngine _policyEngine;

    public PolicyEngineFailClosedRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_POL_DEF001_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "policy_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        // Run migrations for base schema
        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _budgetManager = new BudgetManager(_factory);
        _approvalManager = new ApprovalManager(_factory);
        _policyEngine = new PolicyEngine(_factory, _budgetManager, _approvalManager);
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
    public void DEF001_DefaultEvaluation_MustBeFailClosed_NeverDefaultAllow()
    {
        // DEF-001: PolicyEngine must NEVER return ALLOW by default.
        // If an action has no explicit allow rule or is unknown, it MUST return BLOCK / DENY.
        var context = new PolicyEvaluationContext(
            ProductionId: "prod-unknown",
            Action: "unknown.action",
            AutonomyMode: "ASSISTED");

        var result = _policyEngine.EvaluateAction(context);

        result.Decision.Should().Be("BLOCK", "PolicyEngine must be fail-closed by default (SPEC/08, DEF-001)");
        result.Decision.Should().NotBe("ALLOW");
    }

    [Fact]
    public void DEF001_EvaluationOrder_EmergencyStopFirst()
    {
        _policyEngine.SetGlobalKillSwitch(true);

        var context = new PolicyEvaluationContext(
            ProductionId: "prod-1",
            Action: "media.render",
            AutonomyMode: "AUTONOMOUS");

        var result = _policyEngine.EvaluateAction(context);

        result.Decision.Should().Be("BLOCK");
        result.RuleKey.Should().Be("emergency_stop.global_kill_switch");
        result.ReasonCode.Should().Be(AmccaErrors.Pol003);
    }

    [Fact]
    public void DEF001_SecurityAndSafetyDenial_MustBlockImmediately()
    {
        var contextWithSecurityThreat = new PolicyEvaluationContext(
            ProductionId: "prod-sec",
            Action: "source.fetch",
            AutonomyMode: "ASSISTED",
            SecurityFlags: new[] { "SSRF_RISK" });

        var result = _policyEngine.EvaluateAction(contextWithSecurityThreat);

        result.Decision.Should().Be("BLOCK");
        result.RuleKey.Should().StartWith("security.");
    }

    [Fact]
    public void DEF001_MissingRequiredPolicyData_MustBeFailClosed()
    {
        // When required context/data is missing, evaluate must fail-closed (BLOCK)
        var emptyContext = new PolicyEvaluationContext(
            ProductionId: "",
            Action: "",
            AutonomyMode: "UNKNOWN");

        var result = _policyEngine.EvaluateAction(emptyContext);

        result.Decision.Should().Be("BLOCK");
    }

    [Fact]
    public void DEF001_AutonomousPublishing_RequiresExplicitHumanApprovalRuleWhenAssisted()
    {
        var publishContext = new PolicyEvaluationContext(
            ProductionId: "prod-pub",
            Action: "publication.dispatch",
            AutonomyMode: "ASSISTED",
            HasApprovedHumanGate: false);

        var result = _policyEngine.EvaluateAction(publishContext);

        result.Decision.Should().Be("REQUIRE_APPROVAL");
        result.RuleKey.Should().Be("approval.human_gate_required");
    }
}
