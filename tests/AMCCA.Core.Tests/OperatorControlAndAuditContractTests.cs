using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
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
    private readonly JobManager _jobManager;
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

        _auditStore = new AuditStore(_factory);
        _budgetManager = new BudgetManager(_factory);
        _approvalManager = new ApprovalManager(_factory);
        _policyEngine = new PolicyEngine(_factory, _budgetManager, _approvalManager);
        _jobManager = new JobManager(_factory);

        _controlService = new OperatorControlService(
            _factory,
            _auditStore,
            _policyEngine,
            _approvalManager,
            _jobManager);
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

    [Fact]
    public async Task RejectionDecision_FromOperatorUI_TransitionsApprovalAndLeavesAuditTrail()
    {
        // DEF-002: rejection must go through the domain (ApprovalManager), not raw SQL from the UI.
        var approvalId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: "prod-ui-2",
            action: "publish",
            scopeJson: "{\"target\":\"youtube\"}",
            validFor: TimeSpan.FromHours(1));

        var corrId = "corr-op-appr-reject-1";
        await _controlService.SubmitApprovalDecisionAsync(
            operatorId: "admin@amcca.local",
            approvalId: approvalId,
            approved: false,
            reason: "Claims not verifiable",
            correlationId: corrId);

        var pending = await _controlService.GetPendingApprovalsAsync();
        pending.Should().NotContain(p => p.Id == approvalId);

        var auditLogs = await _controlService.QueryAuditTrailAsync(correlationId: corrId);
        auditLogs.Should().ContainSingle();

        var log = auditLogs.First();
        log.Action.Should().Be("operator.approval_decided");
        log.ReasonCode.Should().Be("REJECTED");
        log.SubjectId.Should().Be(approvalId);

        // A rejected (no longer PENDING) approval cannot be rejected again.
        var repeat = async () => await _controlService.SubmitApprovalDecisionAsync(
            operatorId: "admin@amcca.local",
            approvalId: approvalId,
            approved: false,
            reason: "duplicate submit",
            correlationId: "corr-op-appr-reject-2");

        await repeat.Should().ThrowAsync<AmccaException>();
    }

    [Fact]
    public async Task GetPendingApprovalsAsync_OnlyReturnsPendingApprovals()
    {
        var pendingId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: "prod-ui-3",
            action: "publish",
            scopeJson: "{}",
            validFor: TimeSpan.FromHours(1));

        var approvedId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: "prod-ui-4",
            action: "publish",
            scopeJson: "{}",
            validFor: TimeSpan.FromHours(1));
        await _approvalManager.ApproveRequestAsync(approvedId, "operator@amcca.local");

        var pending = await _controlService.GetPendingApprovalsAsync();

        pending.Should().Contain(p => p.Id == pendingId);
        pending.Should().NotContain(p => p.Id == approvedId);
    }

    /// <summary>
    /// SPEC/60 obligation 5: an approval must show the exact subject, cost ceiling and expiry being
    /// approved. This is the Core-level contract GetPendingApprovalsAsync must uphold regardless of
    /// which UI reads it -- ApprovalScope's fields, parsed out of scope_json, and expires_at.
    /// </summary>
    [Fact]
    public async Task GetPendingApprovalsAsync_ExposesScopeAndExpiryFromApprovalScope()
    {
        var scopeJson = System.Text.Json.JsonSerializer.Serialize(
            new ApprovalScope(Target: "youtube", Subject: "video-77", CostCeiling: 12.34m));

        var approvalId = await _approvalManager.CreateApprovalRequestAsync(
            productionId: "prod-scope-core-1",
            action: "publish",
            scopeJson: scopeJson,
            validFor: TimeSpan.FromHours(2));

        var pending = await _controlService.GetPendingApprovalsAsync();
        var found = pending.Should().ContainSingle(p => p.Id == approvalId).Subject;

        found.Target.Should().Be("youtube");
        found.Subject.Should().Be("video-77");
        found.CostCeiling.Should().Be(12.34m);
        found.ExpiresAt.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Drives a job to DEAD_LETTER the way production does: one attempt allowed, claimed once, failed once.
    /// </summary>
    private async Task<string> CreateDeadLetteredJobAsync(string idempotencyKey)
    {
        var job = await _jobManager.EnqueueJobAsync(
            type: "RENDER",
            idempotencyKey: idempotencyKey,
            correlationId: "corr-job-dl",
            payloadJson: "{}",
            priority: 3,
            maxAttempts: 1);

        // AcquireLeaseAsync targets this specific job; TryClaimNextJobAsync would take the oldest queued
        // job instead, which is not necessarily the one just enqueued.
        var lease = await _jobManager.AcquireLeaseAsync(job.Id, "worker-1", TimeSpan.FromMinutes(5));
        lease.Should().NotBeNull();

        await _jobManager.FailJobAsync(job.Id, "worker-1", lease!.FenceToken, "render failed");

        var failed = await _jobManager.GetJobAsync(job.Id);
        failed!.State.Should().Be("DEAD_LETTER", "attempt has reached max_attempts (SPEC/14)");

        return job.Id;
    }

    [Fact]
    public async Task RequeueDeadLetterJob_FromOperatorUI_RequeuesAndLeavesFullAuditTrail()
    {
        var jobId = await CreateDeadLetteredJobAsync("idem-dl-1");
        var corrId = "corr-op-job-requeue-1";

        await _controlService.RequeueDeadLetterJobAsync(
            operatorId: "admin@amcca.local",
            jobId: jobId,
            reason: "Provider outage resolved",
            correlationId: corrId);

        var requeued = await _jobManager.GetJobAsync(jobId);
        requeued!.State.Should().Be("QUEUED");
        requeued.Attempt.Should().Be(1,
            "SPEC/14 bounds retries by max_attempts; an operator requeue grants one more attempt, it does not erase the history");

        var auditLogs = await _controlService.QueryAuditTrailAsync(correlationId: corrId);
        auditLogs.Should().ContainSingle();

        var log = auditLogs.First();
        log.Action.Should().Be("operator.job_requeued");
        log.ActorType.Should().Be("OPERATOR");
        log.ActorId.Should().Be("admin@amcca.local");
        log.SubjectType.Should().Be("job");
        log.SubjectId.Should().Be(jobId);
        log.ReasonCode.Should().Be(AmccaErrors.Job003);

        // The requeued job must be claimable again -- a stale lease would make it un-dispatchable.
        var reclaim = await _jobManager.TryClaimNextJobAsync("worker-2", TimeSpan.FromMinutes(5));
        reclaim.Should().NotBeNull();
        reclaim!.JobId.Should().Be(jobId);
    }

    [Fact]
    public async Task RequeueDeadLetterJob_OnJobThatIsNotDeadLettered_IsRefusedAndWritesNoAudit()
    {
        var job = await _jobManager.EnqueueJobAsync(
            type: "RENDER",
            idempotencyKey: "idem-queued-1",
            correlationId: "corr-job-queued",
            payloadJson: "{}");

        var corrId = "corr-op-job-requeue-refused";
        var act = async () => await _controlService.RequeueDeadLetterJobAsync(
            operatorId: "admin@amcca.local",
            jobId: job.Id,
            reason: "should not be possible",
            correlationId: corrId);

        (await act.Should().ThrowAsync<AmccaException>())
            .Which.ErrorCode.Should().Be(AmccaErrors.Job003);

        var stillQueued = await _jobManager.GetJobAsync(job.Id);
        stillQueued!.State.Should().Be("QUEUED");

        // No false success: a refused operator action must not leave an audit record claiming it happened.
        var auditLogs = await _controlService.QueryAuditTrailAsync(correlationId: corrId);
        auditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task JobQueueListing_IsPagedAndFilterableByState()
    {
        for (int i = 0; i < 5; i++)
        {
            await _jobManager.EnqueueJobAsync(
                type: "RENDER",
                idempotencyKey: $"idem-page-{i}",
                correlationId: "corr-page",
                payloadJson: "{}");
        }
        var deadLetteredId = await CreateDeadLetteredJobAsync("idem-page-dl");

        var total = await _controlService.CountJobsAsync();
        total.Should().Be(6);

        var firstPage = await _controlService.ListJobsAsync(stateFilter: null, limit: 4, offset: 0);
        firstPage.Should().HaveCount(4);

        var secondPage = await _controlService.ListJobsAsync(stateFilter: null, limit: 4, offset: 4);
        secondPage.Should().HaveCount(2);
        secondPage.Select(j => j.Id).Should().NotIntersectWith(firstPage.Select(j => j.Id));

        var deadLettered = await _controlService.ListJobsAsync(stateFilter: "DEAD_LETTER", limit: 50, offset: 0);
        deadLettered.Should().ContainSingle();
        deadLettered[0].Id.Should().Be(deadLetteredId);
        deadLettered[0].IsDeadLettered.Should().BeTrue();

        (await _controlService.CountJobsAsync("DEAD_LETTER")).Should().Be(1);

        var states = await _controlService.ListDistinctJobStatesAsync();
        states.Should().Contain("QUEUED").And.Contain("DEAD_LETTER");
    }

    [Fact]
    public async Task JobQueueListing_ExposesLeaseOwnerAndFenceTokenForLeasedJobs()
    {
        var job = await _jobManager.EnqueueJobAsync(
            type: "PUBLISH",
            idempotencyKey: "idem-leased-1",
            correlationId: "corr-leased",
            payloadJson: "{}");

        var claim = await _jobManager.TryClaimNextJobAsync("worker-lease-1", TimeSpan.FromMinutes(5));
        claim.Should().NotBeNull();

        var leased = await _controlService.ListJobsAsync(stateFilter: "LEASED", limit: 50, offset: 0);

        leased.Should().ContainSingle();
        leased[0].Id.Should().Be(job.Id);
        leased[0].LeaseOwnerId.Should().Be("worker-lease-1");
        leased[0].FenceToken.Should().Be(claim!.FenceToken);
        leased[0].LeaseUntil.Should().NotBeNullOrEmpty();
    }
}
