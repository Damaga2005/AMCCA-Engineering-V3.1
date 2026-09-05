using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Events;

public record AuditRecord(
    string AuditId,
    string Action,
    string ActorType,
    string ActorId,
    string? SubjectType,
    string? SubjectId,
    string? ProductionId,
    string Outcome,
    string? PolicyDecisionId,
    string? ReasonCode,
    string CorrelationId,
    string SchemaVersion,
    string OccurredAt);

public interface IAuditStore
{
    Task AppendAuditAsync(AuditRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<AuditRecord>> GetAuditLogsAsync(string? correlationId = null, string? action = null, CancellationToken ct = default);

    /// <summary>
    /// Most-recent-first audit rows for the operator's Audit Log screen. A non-empty
    /// <paramref name="query"/> is a substring match against action, subject_id and actor_id.
    /// </summary>
    Task<IReadOnlyList<AuditRecord>> SearchAuditLogsAsync(string? query, int limit = 100, CancellationToken ct = default);
}

public class AuditStore : IAuditStore
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public AuditStore(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task AppendAuditAsync(AuditRecord record, CancellationToken ct = default)
    {
        // AGENTS.md rule: "audit_log.actor_type deliberately has no AGENT value. An agent is never the authority for a protected action..."
        if (string.Equals(record.ActorType, "AGENT", StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                AmccaErrors.Sec001,
                ErrorCategory.Security,
                "audit_log.actor_type cannot be 'AGENT'. Agents have no authority for protected actions (AGENTS.md).");
        }

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO audit_log (
                audit_id, action, actor_type, actor_id, subject_type, subject_id,
                production_id, outcome, policy_decision_id, reason_code, correlation_id,
                schema_version, occurred_at
            ) VALUES (
                @AuditId, @Action, @ActorType, @ActorId, @SubjectType, @SubjectId,
                @ProductionId, @Outcome, @PolicyDecisionId, @ReasonCode, @CorrelationId,
                @SchemaVersion, @OccurredAt
            );
        ";
        await connection.ExecuteAsync(sql, record);
    }

    public async Task<IReadOnlyList<AuditRecord>> GetAuditLogsAsync(string? correlationId = null, string? action = null, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        string sql = @"
            SELECT
                audit_id AS AuditId,
                action AS Action,
                actor_type AS ActorType,
                actor_id AS ActorId,
                subject_type AS SubjectType,
                subject_id AS SubjectId,
                production_id AS ProductionId,
                outcome AS Outcome,
                policy_decision_id AS PolicyDecisionId,
                reason_code AS ReasonCode,
                correlation_id AS CorrelationId,
                schema_version AS SchemaVersion,
                occurred_at AS OccurredAt
            FROM audit_log
            WHERE 1=1
        ";

        if (!string.IsNullOrEmpty(correlationId))
        {
            sql += " AND correlation_id = @CorrelationId";
        }
        if (!string.IsNullOrEmpty(action))
        {
            sql += " AND action = @Action";
        }
        sql += " ORDER BY occurred_at ASC;";

        var result = await connection.QueryAsync<AuditRecord>(sql, new { CorrelationId = correlationId, Action = action });
        return result.ToList();
    }

    public async Task<IReadOnlyList<AuditRecord>> SearchAuditLogsAsync(string? query, int limit = 100, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string columns = @"
                audit_id AS AuditId, action AS Action, actor_type AS ActorType, actor_id AS ActorId,
                subject_type AS SubjectType, subject_id AS SubjectId, production_id AS ProductionId,
                outcome AS Outcome, policy_decision_id AS PolicyDecisionId, reason_code AS ReasonCode,
                correlation_id AS CorrelationId, schema_version AS SchemaVersion, occurred_at AS OccurredAt";

        var sql = string.IsNullOrWhiteSpace(query)
            ? $"SELECT {columns} FROM audit_log ORDER BY occurred_at DESC LIMIT @Limit;"
            : $@"SELECT {columns} FROM audit_log
                 WHERE action LIKE @Pattern OR subject_id LIKE @Pattern OR actor_id LIKE @Pattern
                 ORDER BY occurred_at DESC LIMIT @Limit;";

        var result = await connection.QueryAsync<AuditRecord>(
            new CommandDefinition(sql, new { Pattern = $"%{query}%", Limit = limit }, cancellationToken: ct));
        return result.ToList();
    }
}
