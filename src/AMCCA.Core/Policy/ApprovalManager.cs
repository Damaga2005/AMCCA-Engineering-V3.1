using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Policy;

public record PendingApproval(
    string Id,
    string ProductionId,
    string Action,
    string State,
    string CreatedAt,
    string ExpiresAt,
    string? Target,
    string? Subject,
    decimal? CostCeiling);

public class ApprovalManager
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public ApprovalManager(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<PendingApproval>> GetPendingApprovalsAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT id AS Id, production_id AS ProductionId, action AS Action, state AS State,
                   created_at AS CreatedAt, expires_at AS ExpiresAt, scope_json AS ScopeJson
            FROM approvals
            WHERE state = 'PENDING'
            ORDER BY created_at ASC;
        ";
        var rows = await connection.QueryAsync<(string Id, string ProductionId, string Action, string State,
            string CreatedAt, string ExpiresAt, string ScopeJson)>(sql);

        var result = new List<PendingApproval>();
        foreach (var r in rows)
        {
            // SPEC/60 obligation 5: every approval request must show the exact action, subject, cost
            // ceiling and expiry being approved -- an operator approving without seeing the scope is
            // approving blind. scope_json is tolerated empty/malformed here exactly as
            // ExecuteWithApprovalAsync tolerates it when consuming the approval: a legacy or
            // scope-less request still needs to be visible in the queue, just without those three
            // fields filled in.
            string? target = null, subject = null;
            decimal? costCeiling = null;
            if (!string.IsNullOrWhiteSpace(r.ScopeJson) && r.ScopeJson != "{}")
            {
                try
                {
                    var scope = JsonSerializer.Deserialize<ApprovalScope>(r.ScopeJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (scope != null)
                    {
                        target = scope.Target;
                        subject = scope.Subject;
                        costCeiling = scope.CostCeiling;
                    }
                }
                catch (JsonException)
                {
                    // malformed scope json -> surface the approval with blank scope fields rather
                    // than hiding it from the queue or throwing.
                }
            }

            result.Add(new PendingApproval(r.Id, r.ProductionId, r.Action, r.State, r.CreatedAt, r.ExpiresAt, target, subject, costCeiling));
        }
        return result;
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
        var rows = await connection.ExecuteAsync(sql, new { Id = approvalId, DecidedBy = decidedBy, Now = now });
        if (rows == 0)
        {
            throw new AmccaException(
                AmccaErrors.Pol004,
                ErrorCategory.UserActionRequired,
                $"Approval '{approvalId}' is not PENDING and cannot be approved (SPEC/09, DEF-002).");
        }
    }

    public async Task RejectRequestAsync(string approvalId, string decidedBy, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            UPDATE approvals
            SET state = 'REJECTED',
                decided_by = @DecidedBy,
                decided_at = @Now
            WHERE id = @Id AND state = 'PENDING';
        ";
        var rows = await connection.ExecuteAsync(sql, new { Id = approvalId, DecidedBy = decidedBy, Now = now });
        if (rows == 0)
        {
            throw new AmccaException(
                AmccaErrors.Pol004,
                ErrorCategory.UserActionRequired,
                $"Approval '{approvalId}' is not PENDING and cannot be rejected (SPEC/09, DEF-002).");
        }
    }

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public async Task ExecuteWithApprovalAsync(
        string productionId,
        string action,
        string target,
        string subject,
        decimal cost,
        Func<Task> protectedAction,
        CancellationToken ct = default)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
            using var tx = connection.BeginTransaction();

        // Query active, non-expired approved requests matching production and action
        const string querySql = @"
            SELECT id, scope_json AS ScopeJson FROM approvals
            WHERE production_id = @ProductionId
              AND action = @Action
              AND state = 'APPROVED'
              AND expires_at > @Now
            ORDER BY created_at ASC;
        ";
        var candidates = await connection.QueryAsync<(string Id, string ScopeJson)>(
            querySql, new { ProductionId = productionId, Action = action, Now = now }, transaction: tx);

        string? matchingApprovalId = null;

        foreach (var candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate.ScopeJson) && candidate.ScopeJson != "{}")
                {
                    var scope = JsonSerializer.Deserialize<ApprovalScope>(candidate.ScopeJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (scope != null)
                    {
                        if (!string.Equals(scope.Target, target, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.Equals(scope.Subject, subject, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (cost > scope.CostCeiling)
                            continue;
                    }
                }

                // Try atomic consumption
                const string consumeSql = @"
                    UPDATE approvals
                    SET state = 'CONSUMED',
                        consumed_at = @Now
                    WHERE id = @Id AND state = 'APPROVED';
                ";
                var rows = await connection.ExecuteAsync(consumeSql, new { Id = candidate.Id, Now = now }, transaction: tx);
                if (rows > 0)
                {
                    matchingApprovalId = candidate.Id;
                    break;
                }
            }
            catch (JsonException)
            {
                // malformed scope json -> skip candidate
            }
        }

        if (string.IsNullOrEmpty(matchingApprovalId))
        {
            tx.Rollback();
            throw new AmccaException(
                AmccaErrors.Pol004,
                ErrorCategory.UserActionRequired,
                $"Protected action '{action}' on production '{productionId}' with target '{target}', subject '{subject}', cost {cost:F2} requires valid approved scoped human approval (SPEC/09, DEF-002).");
        }

        try
        {
            // Execute protected action while holding the consumption in the transaction
            await protectedAction();
            tx.Commit();
        }
        catch
        {
            tx.Rollback(); // Reverts consumption so approval remains available for retry
            throw;
        }
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Whether an unexpired APPROVED approval exists for this production+action, without consuming it.
    /// The orchestrator uses this to decide whether a protected action is cleared to proceed;
    /// <see cref="ValidateAndConsumeApprovalAsync"/> still does the single-use consume at dispatch time.
    /// </summary>
    public async Task<bool> HasApprovedGateAsync(string productionId, string action, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var count = await connection.ExecuteScalarAsync<int>(new Dapper.CommandDefinition(
            @"SELECT COUNT(*) FROM approvals
              WHERE production_id = @ProductionId AND action = @Action
                AND state = 'APPROVED' AND expires_at > @Now;",
            new { ProductionId = productionId, Action = action, Now = now }, cancellationToken: ct));
        return count > 0;
    }

    public async Task<bool> ValidateAndConsumeApprovalAsync(
        string productionId,
        string action,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

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
                ErrorCategory.UserActionRequired,
                $"Protected action '{action}' on production '{productionId}' requires valid human approval before entering protected state (SPEC/09, D-009).");
        }

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
                ErrorCategory.UserActionRequired,
                "Approval already consumed or invalidated.");
        }

        tx.Commit();
        return true;
    }
}
