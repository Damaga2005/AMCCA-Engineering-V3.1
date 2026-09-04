using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Policy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class PolicyBudgetAndApprovalContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly BudgetManager _budgetManager;
    private readonly ApprovalManager _approvalManager;
    private readonly PolicyEngine _policyEngine;

    public PolicyBudgetAndApprovalContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_POL_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "policy_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _budgetManager = new BudgetManager(_factory);
        _approvalManager = new ApprovalManager(_factory);
        _policyEngine = new PolicyEngine(_factory, _budgetManager, _approvalManager);
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
    public async Task Production_ExceedingBudget_IsRefusedAndHaltsWithCst002()
    {
        // Exit criterion: "A production exceeding budget halts" (D-003, SPEC/20)
        var budgetId = "b-prod-1";
        await _budgetManager.CreateBudgetAsync(budgetId, "PRODUCTION", "prod-1", limitAmount: 10.00m);

        // First reservation of 8.00m succeeds
        var res1 = await _budgetManager.TryReserveBudgetAsync(budgetId, 8.00m, "corr-1");
        res1.Should().BeTrue();

        // Second reservation of 3.00m exceeds limit (8 + 3 = 11 > 10) -> must fail with AMCCA-CST-002
        var act = async () => await _budgetManager.ReserveBudgetOrThrowAsync(budgetId, 3.00m, "corr-2");

        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Cst002);
    }

    [Fact]
    public async Task UnapprovedProduction_AttemptingToPublish_CannotProceedWithPol004()
    {
        // Exit criterion: "an unapproved production cannot reach publishing" (D-009, SPEC/09)
        var act = async () => await _approvalManager.ValidateAndConsumeApprovalAsync(
            productionId: "prod-unapproved",
            action: "publish");

        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Pol004);
    }

    [Fact]
    public async Task ApprovedRequest_AuthorisesAction_AndIsSingleUseConsumed()
    {
        var approvalId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: "prod-approved",
            action: "publish",
            scopeJson: "{\"target\":\"youtube\"}",
            validFor: TimeSpan.FromMinutes(10));

        await _approvalManager.ApproveRequestAsync(approvalId, decidedBy: "operator@amcca.local");

        // First consumption succeeds
        var consumed = await _approvalManager.ValidateAndConsumeApprovalAsync("prod-approved", "publish");
        consumed.Should().BeTrue();

        // Second consumption fails because approval was single_use (SPEC/09)
        var actSecond = async () => await _approvalManager.ValidateAndConsumeApprovalAsync("prod-approved", "publish");
        (await actSecond.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Pol004);
    }

    [Fact]
    public async Task ExpiredApproval_CannotBeConsumed()
    {
        var approvalId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: "prod-expired",
            action: "publish",
            scopeJson: "{}",
            validFor: TimeSpan.FromSeconds(-10)); // Already expired

        await _approvalManager.ApproveRequestAsync(approvalId, decidedBy: "operator@amcca.local");

        var act = async () => await _approvalManager.ValidateAndConsumeApprovalAsync("prod-expired", "publish");

        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Pol004);
    }

    [Fact]
    public void ActiveKillSwitch_ImmediatelyBlocksOperationsWithPol003()
    {
        // SPEC/08, D-006: "Emergency stop -> Security -> Safety... Kill switch is active"
        _policyEngine.SetGlobalKillSwitch(active: true);

        var decision = _policyEngine.EvaluateAction(
            productionId: "prod-kill",
            action: "publish",
            platform: "youtube");

        decision.Decision.Should().Be("BLOCK");
        decision.ReasonCode.Should().Be(AmccaErrors.Pol003);
    }
}
