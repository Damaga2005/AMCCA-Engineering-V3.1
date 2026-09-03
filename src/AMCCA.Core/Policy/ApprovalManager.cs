using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Policy;

public class ApprovalManager
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public ApprovalManager(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<string> CreateApprovalRequestAsync(
        string productionId,
        string action,
        string scopeJson,
        TimeSpan validFor,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var expiresAt = DateTimeOffset.UtcNow.Add(validFor).ToString("O");

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO approvals (id, production_id, action, scope_json, state, single_use, expires_at, created_at)
            VALUES (@Id, @ProductionId, @Action, @ScopeJson, 'PENDING', 1, @ExpiresAt, @Now);
        ";
        await connection.ExecuteAsync(sql, new
        {
            Id = id,
            ProductionId = productionId,
            Action = action,
            ScopeJson = scopeJson,
            ExpiresAt = expiresAt,
            Now = now
        });

        return id;
    }

    public async Task ApproveRequestAsync(string approvalId, string decidedBy, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            UPDATE approvals
            SET state = 'APPROVED',
                decided_by = @DecidedBy,
                decided_at = @Now
            WHERE id = @Id AND state = 'PENDING';
        ";
        await connection.ExecuteAsync(sql, new { Id = approvalId, DecidedBy = decidedBy, Now = now });
    }

    public async Task<bool> ValidateAndConsumeApprovalAsync(
        string productionId,
        string action,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        // 1. Locate valid active non-expired approval (SPEC/09)
        const string querySql = @"
            SELECT id FROM approvals
            WHERE production_id = @ProductionId
              AND action = @Action
              AND state = 'APPROVED'
              AND expires_at > @Now
            ORDER BY created_at ASC
            LIMIT 1;
        ";
        var validApprovalId = await connection.QuerySingleOrDefaultAsync<string>(
            querySql, new { ProductionId = productionId, Action = action, Now = now }, transaction: tx);

        if (string.IsNullOrEmpty(validApprovalId))
        {
            tx.Rollback();
            throw new AmccaException(
                AmccaErrors.Pol004,
                ErrorCategory.Security,
                $"Protected action '{action}' on production '{productionId}' requires valid human approval before entering protected state (SPEC/09, D-009).");
        }

        // 2. Consume atomically with action (SPEC/09: "single-use by default, same transaction sets consumed_at")
        const string consumeSql = @"
            UPDATE approvals
            SET state = 'CONSUMED',
                consumed_at = @Now
            WHERE id = @Id AND state = 'APPROVED';
        ";
        var rows = await connection.ExecuteAsync(consumeSql, new { Id = validApprovalId, Now = now }, transaction: tx);
        if (rows == 0)
        {
            tx.Rollback();
            throw new AmccaException(
                AmccaErrors.Pol004,
                ErrorCategory.Security,
                "Approval already consumed or invalidated.");
        }

        tx.Commit();
        return true;
    }
}
