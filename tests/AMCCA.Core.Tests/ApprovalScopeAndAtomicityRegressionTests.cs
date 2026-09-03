using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Policy;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class ApprovalScopeAndAtomicityRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ApprovalManager _approvalManager;

    public ApprovalScopeAndAtomicityRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_APPR_DEF002_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "approvals_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        // Run migrations for base schema
        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _approvalManager = new ApprovalManager(_factory);
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
    public async Task SameTarget_SameAction_ValidCost_AllowsAndConsumes()
    {
        var prodId = "prod-100";
        var scope = new ApprovalScope(Target: "youtube", Subject: "video-render-1", CostCeiling: 50.0m);
        var scopeJson = JsonSerializer.Serialize(scope);

        var appId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: prodId,
            action: "publication.dispatch",
            scopeJson: scopeJson,
            validFor: TimeSpan.FromHours(1));

        await _approvalManager.ApproveRequestAsync(appId, "operator@amcca.local");

        bool actionRan = false;
        var act = async () => await _approvalManager.ExecuteWithApprovalAsync(
            productionId: prodId,
            action: "publication.dispatch",
            target: "youtube",
            subject: "video-render-1",
            cost: 30.0m,
            protectedAction: () =>
            {
                actionRan = true;
                return Task.CompletedTask;
            });

        await act.Should().NotThrowAsync();
        actionRan.Should().BeTrue();

        // Attempting to reuse the consumed approval must be denied
        var secondRun = async () => await _approvalManager.ExecuteWithApprovalAsync(
            productionId: prodId,
            action: "publication.dispatch",
            target: "youtube",
            subject: "video-render-1",
            cost: 30.0m,
            protectedAction: () => Task.CompletedTask);

        await secondRun.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Pol004);
    }

    [Fact]
    public async Task DifferentTarget_OrDifferentSubject_OrDifferentAction_MustDeny()
    {
        var prodId = "prod-101";
        var scope = new ApprovalScope(Target: "youtube", Subject: "video-1", CostCeiling: 50.0m);
        var scopeJson = JsonSerializer.Serialize(scope);

        var appId = await _approvalManager.CreateApprovalRequestAsync(
            prodId, "publication.dispatch", scopeJson, TimeSpan.FromHours(1));
        await _approvalManager.ApproveRequestAsync(appId, "operator@amcca.local");

        // Different target
        var diffTarget = async () => await _approvalManager.ExecuteWithApprovalAsync(
            prodId, "publication.dispatch", target: "tiktok", subject: "video-1", cost: 10m,
            protectedAction: () => Task.CompletedTask);
        await diffTarget.Should().ThrowAsync<AmccaException>().Where(e => e.ErrorCode == AmccaErrors.Pol004);

        // Different subject
        var diffSubject = async () => await _approvalManager.ExecuteWithApprovalAsync(
            prodId, "publication.dispatch", target: "youtube", subject: "other-video", cost: 10m,
            protectedAction: () => Task.CompletedTask);
        await diffSubject.Should().ThrowAsync<AmccaException>().Where(e => e.ErrorCode == AmccaErrors.Pol004);

        // Different action
        var diffAction = async () => await _approvalManager.ExecuteWithApprovalAsync(
            prodId, "money.spend", target: "youtube", subject: "video-1", cost: 10m,
            protectedAction: () => Task.CompletedTask);
        await diffAction.Should().ThrowAsync<AmccaException>().Where(e => e.ErrorCode == AmccaErrors.Pol004);
    }

    [Fact]
    public async Task CostExceedingApprovedCeiling_MustDeny()
    {
        var prodId = "prod-102";
        var scope = new ApprovalScope(Target: "youtube", Subject: "video-1", CostCeiling: 25.0m);
        var scopeJson = JsonSerializer.Serialize(scope);

        var appId = await _approvalManager.CreateApprovalRequestAsync(
            prodId, "publication.dispatch", scopeJson, TimeSpan.FromHours(1));
        await _approvalManager.ApproveRequestAsync(appId, "operator@amcca.local");

        // Requested cost 30.0m > ceiling 25.0m
        var act = async () => await _approvalManager.ExecuteWithApprovalAsync(
            prodId, "publication.dispatch", target: "youtube", subject: "video-1", cost: 30.0m,
            protectedAction: () => Task.CompletedTask);

        await act.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Pol004);
    }

    [Fact]
    public async Task FailedProtectedAction_RollsBack_LeavingApprovalAvailable()
    {
        var prodId = "prod-103";
        var scope = new ApprovalScope(Target: "youtube", Subject: "video-1", CostCeiling: 100.0m);
        var scopeJson = JsonSerializer.Serialize(scope);

        var appId = await _approvalManager.CreateApprovalRequestAsync(
            prodId, "publication.dispatch", scopeJson, TimeSpan.FromHours(1));
        await _approvalManager.ApproveRequestAsync(appId, "operator@amcca.local");

        // Protected action fails
        var failingRun = async () => await _approvalManager.ExecuteWithApprovalAsync(
            prodId, "publication.dispatch", target: "youtube", subject: "video-1", cost: 10.0m,
            protectedAction: () => throw new InvalidOperationException("External API network drop"));

        await failingRun.Should().ThrowAsync<InvalidOperationException>();

        // Because action failed and rolled back, approval should remain available for retry
        bool retryRan = false;
        var retryRun = async () => await _approvalManager.ExecuteWithApprovalAsync(
            prodId, "publication.dispatch", target: "youtube", subject: "video-1", cost: 10.0m,
            protectedAction: () =>
            {
                retryRan = true;
                return Task.CompletedTask;
            });

        await retryRun.Should().NotThrowAsync();
        retryRan.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentConsumption_ExactlyOneSucceeds()
    {
        var prodId = "prod-104";
        var scope = new ApprovalScope(Target: "youtube", Subject: "video-1", CostCeiling: 50.0m);
        var scopeJson = JsonSerializer.Serialize(scope);

        var appId = await _approvalManager.CreateApprovalRequestAsync(
            prodId, "publication.dispatch", scopeJson, TimeSpan.FromHours(1));
        await _approvalManager.ApproveRequestAsync(appId, "operator@amcca.local");

        int successCount = 0;
        int failureCount = 0;

        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                await _approvalManager.ExecuteWithApprovalAsync(
                    prodId, "publication.dispatch", target: "youtube", subject: "video-1", cost: 10.0m,
                    protectedAction: async () =>
                    {
                        await Task.Delay(10);
                    });
                System.Threading.Interlocked.Increment(ref successCount);
            }
            catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Pol004)
            {
                System.Threading.Interlocked.Increment(ref failureCount);
            }
        });

        await Task.WhenAll(tasks);

        successCount.Should().Be(1, "exactly one concurrent execution must claim and consume the approval");
        failureCount.Should().Be(4, "all other concurrent attempts must be rejected");
    }
}
