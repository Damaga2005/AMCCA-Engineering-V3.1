using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Jobs;

public class IntentManager
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public IntentManager(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IntentRecord> CreateIntentAsync(
        string kind,
        string target,
        string idempotencyKey,
        string requestFingerprint,
        string? jobId,
        string? productionId,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var intent = new IntentRecord
        {
            Id = id,
            JobId = jobId,
            ProductionId = productionId,
            Kind = kind,
            Target = target,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            State = "CREATED",
            ExternalRequestId = null,
            AttemptCount = 0,
            DispatchedAt = null,
            ResolvedAt = null,
            CreatedAt = now,
            UpdatedAt = now
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO intents (
                id, job_id, production_id, kind, target, idempotency_key,
                request_fingerprint, state, external_request_id, attempt_count,
                dispatched_at, resolved_at, created_at, updated_at
            ) VALUES (
                @Id, @JobId, @ProductionId, @Kind, @Target, @IdempotencyKey,
                @RequestFingerprint, @State, @ExternalRequestId, @AttemptCount,
                @DispatchedAt, @ResolvedAt, @CreatedAt, @UpdatedAt
            );
        ";
        await connection.ExecuteAsync(sql, intent);
        return intent;
    }

    public async Task MarkDispatchedAsync(string intentId, string? externalRequestId = null, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("O");
        const string sql = @"
            UPDATE intents
            SET state = 'DISPATCHED',
                external_request_id = @ExternalRequestId,
                attempt_count = attempt_count + 1,
                dispatched_at = @Now,
                updated_at = @Now
            WHERE id = @Id;
        ";
        await connection.ExecuteAsync(sql, new { ExternalRequestId = externalRequestId, Now = now, Id = intentId });
    }

    public async Task MarkUnknownAsync(string intentId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("O");
        const string sql = @"
            UPDATE intents
            SET state = 'UNKNOWN',
                updated_at = @Now
            WHERE id = @Id;
        ";
        await connection.ExecuteAsync(sql, new { Now = now, Id = intentId });
    }

    public async Task ResolveIntentAsync(string intentId, string outcome, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("O");
        const string sql = @"
            UPDATE intents
            SET state = @Outcome,
                resolved_at = @Now,
                updated_at = @Now
            WHERE id = @Id;
        ";
        await connection.ExecuteAsync(sql, new { Outcome = outcome, Now = now, Id = intentId });
    }

    public async Task<IntentRecord?> GetIntentAsync(string intentId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                job_id AS JobId,
                production_id AS ProductionId,
                kind AS Kind,
                target AS Target,
                idempotency_key AS IdempotencyKey,
                request_fingerprint AS RequestFingerprint,
                state AS State,
                external_request_id AS ExternalRequestId,
                attempt_count AS AttemptCount,
                dispatched_at AS DispatchedAt,
                resolved_at AS ResolvedAt,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM intents
            WHERE id = @Id;
        ";
        return await connection.QuerySingleOrDefaultAsync<IntentRecord>(sql, new { Id = intentId });
    }
}
