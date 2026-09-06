using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
using AMCCA.Core.Policy;
using Dapper;

namespace AMCCA.Core.Operator;

public class OperatorControlService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IAuditStore _auditStore;
    private readonly PolicyEngine _policyEngine;
    private readonly ApprovalManager _approvalManager;
    private readonly JobManager _jobManager;

    private volatile string _autonomyMode = "ASSISTED";

    public OperatorControlService(
        DatabaseConnectionFactory connectionFactory,
        IAuditStore auditStore,
        PolicyEngine policyEngine,
        ApprovalManager approvalManager,
        JobManager jobManager)
    {
        _connectionFactory = connectionFactory;
        _auditStore = auditStore;
        _policyEngine = policyEngine;
        _approvalManager = approvalManager;
        _jobManager = jobManager;
    }

    public async Task ToggleGlobalKillSwitchAsync(
        string operatorId,
        bool active,
        string reason,
        string correlationId,
        CancellationToken ct = default)
    {
        _policyEngine.SetGlobalKillSwitch(active);

        // Persist so SPEC/49 preflight gate 10 (kill_switch_state) sees this across restarts, not just
        // the in-memory PolicyEngine flag reset on every process start.
        using (var connection = await _connectionFactory.CreateOpenConnectionAsync(ct))
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            if (active)
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO kill_switch_state (id, mode, engaged_at, engaged_by, reason)
                    VALUES (1, 'EMERGENCY_STOP', @Now, @OperatorId, @Reason)
                    ON CONFLICT(id) DO UPDATE SET
                        mode = 'EMERGENCY_STOP', engaged_at = @Now, engaged_by = @OperatorId, reason = @Reason,
                        cleared_at = NULL, cleared_by = NULL;
                ", new { Now = now, OperatorId = operatorId, Reason = reason });
            }
            else
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO kill_switch_state (id, mode, cleared_at, cleared_by)
                    VALUES (1, 'NORMAL', @Now, @OperatorId)
                    ON CONFLICT(id) DO UPDATE SET mode = 'NORMAL', cleared_at = @Now, cleared_by = @OperatorId;
                ", new { Now = now, OperatorId = operatorId });
            }
        }

        // SPEC/60, AGENTS.md: Every action taken from UI leaves a full audit trail
        var audit = new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "operator.global_kill_switch_toggled",
            ActorType: "OPERATOR", // Never AGENT!
            ActorId: operatorId,
            SubjectType: "system_control",
            SubjectId: "global_kill_switch",
            ProductionId: null,
            Outcome: "APPROVED",
            PolicyDecisionId: null,
            ReasonCode: active ? AmccaErrors.Pol003 : null,
            CorrelationId: correlationId,
            SchemaVersion: "3.1.0",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"));

        await _auditStore.AppendAuditAsync(audit, ct);
    }

    public async Task SubmitApprovalDecisionAsync(
        string operatorId,
        string approvalId,
        bool approved,
        string reason,
        string correlationId,
        CancellationToken ct = default)
    {
        if (approved)
        {
            await _approvalManager.ApproveRequestAsync(approvalId, operatorId, ct);
        }
        else
        {
            await _approvalManager.RejectRequestAsync(approvalId, operatorId, ct);
        }

        var audit = new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "operator.approval_decided",
            ActorType: "OPERATOR",
            ActorId: operatorId,
            SubjectType: "approval",
            SubjectId: approvalId,
            ProductionId: null,
            Outcome: "APPROVED",
            PolicyDecisionId: null,
            ReasonCode: approved ? "APPROVED" : "REJECTED",
            CorrelationId: correlationId,
            SchemaVersion: "3.1.0",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"));

        await _auditStore.AppendAuditAsync(audit, ct);
    }

    public Task<IReadOnlyList<PendingApproval>> GetPendingApprovalsAsync(CancellationToken ct = default)
    {
        return _approvalManager.GetPendingApprovalsAsync(ct);
    }

    public Task<IReadOnlyList<JobQueueEntry>> ListJobsAsync(
        string? stateFilter = null,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        return _jobManager.ListJobsAsync(stateFilter, limit, offset, ct);
    }

    public Task<int> CountJobsAsync(string? stateFilter = null, CancellationToken ct = default)
    {
        return _jobManager.CountJobsAsync(stateFilter, ct);
    }

    public Task<IReadOnlyList<string>> ListDistinctJobStatesAsync(CancellationToken ct = default)
    {
        return _jobManager.ListDistinctJobStatesAsync(ct);
    }

    /// <summary>
    /// SPEC/14: a dead-lettered job waits for an operator. The requeue itself is refused by JobManager
    /// unless the job really is in DEAD_LETTER, so the audit record below is only ever written for a
    /// requeue that actually happened.
    /// </summary>
    public async Task RequeueDeadLetterJobAsync(
        string operatorId,
        string jobId,
        string reason,
        string correlationId,
        CancellationToken ct = default)
    {
        await _jobManager.RequeueDeadLetterJobAsync(jobId, ct);

        var audit = new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "operator.job_requeued",
            ActorType: "OPERATOR",
            ActorId: operatorId,
            SubjectType: "job",
            SubjectId: jobId,
            ProductionId: null,
            Outcome: "APPROVED",
            PolicyDecisionId: null,
            ReasonCode: AmccaErrors.Job003,
            CorrelationId: correlationId,
            SchemaVersion: "3.1.0",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"));

        await _auditStore.AppendAuditAsync(audit, ct);
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryAuditTrailAsync(
        string? correlationId = null,
        string? action = null,
        CancellationToken ct = default)
    {
        return await _auditStore.GetAuditLogsAsync(correlationId, action, ct);
    }

    /// <summary>
    /// Whether the global kill switch is engaged, read from the persisted <c>kill_switch_state</c> (the
    /// same source SPEC/49 preflight gate 10 uses). Lean check for hot paths like the orchestrator tick,
    /// which runs in a different process from the console and so cannot trust an in-memory flag.
    /// </summary>
    public async Task<bool> IsGlobalKillSwitchEngagedAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var mode = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition("SELECT mode FROM kill_switch_state WHERE id = 1;", cancellationToken: ct));
        return string.Equals(mode, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SystemStatusSummary> GetSystemStatusAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        const string pendingApprovalsSql = "SELECT COUNT(*) FROM approvals WHERE state = 'PENDING';";
        int pendingCount = await connection.ExecuteScalarAsync<int>(pendingApprovalsSql);

        // DEF-005: read from the persisted kill_switch_state (SPEC/49 gate 10's own source of truth)
        // rather than an in-memory flag that resets on every process start.
        var mode = await connection.ExecuteScalarAsync<string?>("SELECT mode FROM kill_switch_state WHERE id = 1;");
        var killSwitchActive = string.Equals(mode, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase);

        // DEF-005: count productions not in a terminal state (SPEC/13 terminal_states), instead of the
        // hardcoded 0 this used to return.
        const string activeProductionsSql =
            "SELECT COUNT(*) FROM productions WHERE state NOT IN ('CANCELLED', 'ARCHIVED', 'FAILED');";
        int activeProductionsCount = await connection.ExecuteScalarAsync<int>(activeProductionsSql);

        // Was computed with its own SQL inside DashboardViewModel -- the last direct query left in that
        // screen. Folded in here so the dashboard reads every number from one place.
        int verifiedPublicationsCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM publications WHERE state = 'VERIFIED';");

        return new SystemStatusSummary(
            GlobalKillSwitchActive: killSwitchActive,
            AutonomyMode: _autonomyMode,
            PendingApprovalsCount: pendingCount,
            ActiveProductionsCount: activeProductionsCount,
            VerifiedPublicationsCount: verifiedPublicationsCount);
    }
}
