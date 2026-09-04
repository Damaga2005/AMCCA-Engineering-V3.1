using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Policy;
using Dapper;

namespace AMCCA.Core.Operator;

public class OperatorControlService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IAuditStore _auditStore;
    private readonly PolicyEngine _policyEngine;
    private readonly ApprovalManager _approvalManager;

    private volatile bool _killSwitchActive;
    private volatile string _autonomyMode = "ASSISTED";

    public OperatorControlService(
        DatabaseConnectionFactory connectionFactory,
        IAuditStore auditStore,
        PolicyEngine policyEngine,
        ApprovalManager approvalManager)
    {
        _connectionFactory = connectionFactory;
        _auditStore = auditStore;
        _policyEngine = policyEngine;
        _approvalManager = approvalManager;
    }

    public async Task ToggleGlobalKillSwitchAsync(
        string operatorId,
        bool active,
        string reason,
        string correlationId,
        CancellationToken ct = default)
    {
        _killSwitchActive = active;
        _policyEngine.SetGlobalKillSwitch(active);

        // SPEC/60, AGENTS.md: Every action taken from UI leaves a full audit trail
        var audit = new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "operator.global_kill_switch_toggled",
            ActorType: "OPERATOR", // Never AGENT!
            ActorId: operatorId,
            SubjectType: "system_control",
            SubjectId: "global_kill_switch",
            ProductionId: null,
            Outcome: "COMMITTED",
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

        var audit = new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "operator.approval_decided",
            ActorType: "OPERATOR",
            ActorId: operatorId,
            SubjectType: "approval",
            SubjectId: approvalId,
            ProductionId: null,
            Outcome: "COMMITTED",
            PolicyDecisionId: null,
            ReasonCode: approved ? "APPROVED" : "REJECTED",
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

    public async Task<SystemStatusSummary> GetSystemStatusAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        const string pendingApprovalsSql = "SELECT COUNT(*) FROM approvals WHERE state = 'PENDING';";
        int pendingCount = await connection.ExecuteScalarAsync<int>(pendingApprovalsSql);

        return new SystemStatusSummary(
            GlobalKillSwitchActive: _killSwitchActive,
            AutonomyMode: _autonomyMode,
            PendingApprovalsCount: pendingCount,
            ActiveProductionsCount: 0);
    }
}
