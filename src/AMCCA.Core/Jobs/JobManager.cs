using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Jobs;

public class JobManager
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public JobManager(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<JobRecord> EnqueueJobAsync(
        string type,
        string idempotencyKey,
        string correlationId,
        string payloadJson,
        int priority = 3,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var job = new JobRecord
        {
            Id = id,
            Type = type,
            State = "QUEUED",
            Priority = priority,
            IdempotencyKey = idempotencyKey,
            Attempt = 0,
            MaxAttempts = maxAttempts,
            CorrelationId = correlationId,
            PayloadJson = payloadJson,
            CreatedAt = now,
            UpdatedAt = now
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO jobs (
                id, type, state, priority, idempotency_key, attempt,
                max_attempts, correlation_id, payload_json, created_at, updated_at
            ) VALUES (
                @Id, @Type, @State, @Priority, @IdempotencyKey, @Attempt,
                @MaxAttempts, @CorrelationId, @PayloadJson, @CreatedAt, @UpdatedAt
            );
        ";
        await connection.ExecuteAsync(sql, job);
        return job;
    }

    public async Task<JobClaim?> TryClaimNextJobAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        // 1. Find candidate queued job with highest priority (lowest integer)
        const string candidateSql = @"
            SELECT id FROM jobs
            WHERE state = 'QUEUED'
            ORDER BY priority ASC, created_at ASC
            LIMIT 1;
        ";
        var candidateId = await connection.QuerySingleOrDefaultAsync<string>(candidateSql, transaction: tx);
        if (string.IsNullOrEmpty(candidateId))
        {
            tx.Rollback();
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var leaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration).ToString("O");

        // 2. Single-statement atomic conditional claim (SPEC/14, D-010)
        const string claimSql = @"
            UPDATE jobs
            SET state = 'LEASED',
                attempt = attempt + 1,
                updated_at = @Now
            WHERE id = @Id AND state = 'QUEUED';
        ";
        var rowsAffected = await connection.ExecuteAsync(claimSql, new { Now = now, Id = candidateId }, transaction: tx);
        if (rowsAffected == 0)
        {
            // Another worker claimed it first
            tx.Rollback();
            return null;
        }

        // 3. Resolve monotonically increasing fence token
        const string currentFenceSql = "SELECT fence_token FROM leases WHERE job_id = @Id;";
        var currentToken = await connection.QuerySingleOrDefaultAsync<long?>(currentFenceSql, new { Id = candidateId }, transaction: tx);
        long nextFenceToken = (currentToken ?? 0) + 1;

        // 4. Record lease with fence token
        const string leaseSql = @"
            INSERT INTO leases (job_id, owner_id, acquired_at, lease_until, heartbeat_at, fence_token)
            VALUES (@JobId, @OwnerId, @Now, @LeaseUntil, @Now, @FenceToken)
            ON CONFLICT(job_id) DO UPDATE SET
                owner_id = @OwnerId,
                acquired_at = @Now,
                lease_until = @LeaseUntil,
                heartbeat_at = @Now,
                fence_token = @FenceToken;
        ";
        await connection.ExecuteAsync(leaseSql, new
        {
            JobId = candidateId,
            OwnerId = workerId,
            Now = now,
            LeaseUntil = leaseUntil,
            FenceToken = nextFenceToken
        }, transaction: tx);

        tx.Commit();

        return new JobClaim
        {
            JobId = candidateId,
            OwnerId = workerId,
            FenceToken = nextFenceToken,
            LeaseUntil = leaseUntil
        };
    }

    public async Task<bool> HeartbeatLeaseAsync(
        string jobId,
        string workerId,
        long expectedFenceToken,
        TimeSpan extension,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var newLeaseUntil = DateTimeOffset.UtcNow.Add(extension).ToString("O");

        // DEF-017: Must verify lease is still active (lease_until > now), owned by workerId, and fence token matches!
        const string sql = @"
            UPDATE leases
            SET lease_until = @NewLeaseUntil,
                heartbeat_at = @Now
            WHERE job_id = @JobId
              AND owner_id = @WorkerId
              AND fence_token = @ExpectedFenceToken
              AND lease_until > @Now;
        ";
        var rowsAffected = await connection.ExecuteAsync(sql, new
        {
            NewLeaseUntil = newLeaseUntil,
            Now = now,
            JobId = jobId,
            WorkerId = workerId,
            ExpectedFenceToken = expectedFenceToken
        });
        return rowsAffected > 0;
    }

    public async Task HeartbeatLeaseOrThrowAsync(
        string jobId,
        string workerId,
        long expectedFenceToken,
        TimeSpan extension,
        CancellationToken ct = default)
    {
        var success = await HeartbeatLeaseAsync(jobId, workerId, expectedFenceToken, extension, ct);
        if (!success)
        {
            throw new AmccaException(
                AmccaErrors.Job002,
                ErrorCategory.Transient,
                $"Heartbeat refused for job '{jobId}': lease is expired, fence token {expectedFenceToken} is stale, or lease owned by another worker (DEF-017).");
        }
    }

    public async Task<bool> CompleteJobAsync(
        string jobId,
        long expectedFenceToken,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        // Stale fence token check
        const string checkSql = "SELECT fence_token FROM leases WHERE job_id = @JobId;";
        var currentToken = await connection.QuerySingleOrDefaultAsync<long?>(checkSql, new { JobId = jobId }, transaction: tx);
        if (currentToken != expectedFenceToken)
        {
            tx.Rollback();
            return false; // Stale worker must abandon completion (SPEC/14)
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await connection.ExecuteAsync("UPDATE jobs SET state = 'COMPLETED', updated_at = @Now WHERE id = @JobId;",
            new { Now = now, JobId = jobId }, transaction: tx);
        await connection.ExecuteAsync("DELETE FROM leases WHERE job_id = @JobId;",
            new { JobId = jobId }, transaction: tx);

        tx.Commit();
        return true;
    }

    public async Task CompleteJobOrThrowAsync(
        string jobId,
        string workerId,
        long expectedFenceToken,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string checkSql = "SELECT owner_id AS OwnerId, fence_token AS FenceToken FROM leases WHERE job_id = @JobId;";
        var lease = await connection.QuerySingleOrDefaultAsync<dynamic>(checkSql, new { JobId = jobId }, transaction: tx);

        if (lease == null || (string)lease.OwnerId != workerId || (long)lease.FenceToken != expectedFenceToken)
        {
            tx.Rollback();
            throw new AmccaException(
                AmccaErrors.Job003,
                ErrorCategory.Security,
                $"CompleteJob refused for job '{jobId}': worker '{workerId}' has stale fence token {expectedFenceToken} or does not hold lease (DEF-016).");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await connection.ExecuteAsync("UPDATE jobs SET state = 'COMPLETED', updated_at = @Now WHERE id = @JobId;",
            new { Now = now, JobId = jobId }, transaction: tx);
        await connection.ExecuteAsync("DELETE FROM leases WHERE job_id = @JobId;",
            new { JobId = jobId }, transaction: tx);

        tx.Commit();
    }

    public async Task FailJobAsync(
        string jobId,
        string workerId,
        long expectedFenceToken,
        string reason,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        // DEF-016: Stale worker / fence token check: must hold current lease with matching fence token
        const string checkSql = "SELECT owner_id AS OwnerId, fence_token AS FenceToken FROM leases WHERE job_id = @JobId;";
        var lease = await connection.QuerySingleOrDefaultAsync<dynamic>(checkSql, new { JobId = jobId }, transaction: tx);

        if (lease == null || (string)lease.OwnerId != workerId || (long)lease.FenceToken != expectedFenceToken)
        {
            tx.Rollback();
            throw new AmccaException(
                AmccaErrors.Job003,
                ErrorCategory.Security,
                $"FailJob refused for job '{jobId}': worker '{workerId}' has stale fence token {expectedFenceToken} or does not hold active lease (DEF-016).");
        }

        var job = await connection.QuerySingleOrDefaultAsync<JobRecord>(
            "SELECT id, attempt, max_attempts AS MaxAttempts FROM jobs WHERE id = @Id;",
            new { Id = jobId }, transaction: tx);

        if (job == null)
        {
            tx.Rollback();
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        string newState = job.Attempt >= job.MaxAttempts ? "DEAD_LETTER" : "QUEUED";

        await connection.ExecuteAsync(
            "UPDATE jobs SET state = @State, updated_at = @Now WHERE id = @Id;",
            new { State = newState, Now = now, Id = jobId }, transaction: tx);

        await connection.ExecuteAsync("DELETE FROM leases WHERE job_id = @Id;", new { Id = jobId }, transaction: tx);

        tx.Commit();
    }

    public async Task FailJobAsync(string jobId, string reason, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        var job = await connection.QuerySingleOrDefaultAsync<JobRecord>(
            "SELECT id, attempt, max_attempts AS MaxAttempts FROM jobs WHERE id = @Id;",
            new { Id = jobId }, transaction: tx);

        if (job == null)
        {
            tx.Rollback();
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        string newState = job.Attempt >= job.MaxAttempts ? "DEAD_LETTER" : "QUEUED";

        await connection.ExecuteAsync(
            "UPDATE jobs SET state = @State, updated_at = @Now WHERE id = @Id;",
            new { State = newState, Now = now, Id = jobId }, transaction: tx);

        if (newState == "DEAD_LETTER")
        {
            await connection.ExecuteAsync("DELETE FROM leases WHERE job_id = @Id;", new { Id = jobId }, transaction: tx);
        }

        tx.Commit();
    }

    public async Task ExpireAndRequeueLeaseForTestingAsync(string jobId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        await connection.ExecuteAsync(
            "UPDATE jobs SET state = 'QUEUED' WHERE id = @Id; UPDATE leases SET lease_until = @Past WHERE job_id = @Id;",
            new { Id = jobId, Past = past });
    }

    public async Task<JobRecord?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                type AS Type,
                state AS State,
                priority AS Priority,
                idempotency_key AS IdempotencyKey,
                attempt AS Attempt,
                max_attempts AS MaxAttempts,
                correlation_id AS CorrelationId,
                payload_json AS PayloadJson,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM jobs
            WHERE id = @Id;
        ";
        return await connection.QuerySingleOrDefaultAsync<JobRecord>(sql, new { Id = jobId });
    }
}
