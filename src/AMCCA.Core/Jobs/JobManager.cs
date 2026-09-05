using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;
using Microsoft.Data.Sqlite;

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
        string? productionId = null,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var job = new JobRecord
        {
            Id = id,
            ProductionId = productionId,
            Type = type,
            State = "QUEUED",
            Priority = priority,
            IdempotencyKey = idempotencyKey,
            Attempt = 0,
            MaxAttempts = maxAttempts,
            CorrelationId = correlationId,
            PayloadJson = payloadJson,
            CreatedAt = now,
            UpdatedAt = now,
            SchemaVersion = "3.1.0"
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO jobs (
                id, production_id, type, state, priority, idempotency_key, attempt,
                max_attempts, correlation_id, payload_json, created_at, updated_at, schema_version
            ) VALUES (
                @Id, @ProductionId, @Type, @State, @Priority, @IdempotencyKey, @Attempt,
                @MaxAttempts, @CorrelationId, @PayloadJson, @CreatedAt, @UpdatedAt, @SchemaVersion
            );
        ";
        try
        {
            await connection.ExecuteAsync(sql, job);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == SqliteConstraintErrorCode &&
            ex.Message.Contains("jobs.idempotency_key", StringComparison.Ordinal))
        {
            // SPEC/15: duplicate enqueue is caught by the DB's UNIQUE(idempotency_key), never a
            // check-then-act pre-check (unsound under concurrency). Wrap the raw engine error so the
            // caller gets an actionable code instead of a bare SqliteException (AMCCA-JOB-002, SPEC/05).
            throw new AmccaException(
                AmccaErrors.Job002,
                ErrorCategory.Internal,
                $"A job with idempotency key '{idempotencyKey}' is already enqueued. The key is a pure " +
                "function of operation + entity + intent version (SPEC/15), so a collision means the same " +
                "logical intent was submitted twice; act on the existing job rather than enqueuing again.",
                retryable: false,
                innerException: ex);
        }
        return job;
    }

    // SQLITE_CONSTRAINT. Matches the literal the concurrency suite already asserts on.
    private const int SqliteConstraintErrorCode = 19;

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

    public async Task<JobLease?> AcquireLeaseAsync(
        string jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToString("O");
        var leaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration).ToString("O");

        const string claimSql = @"
            UPDATE jobs
            SET state = 'LEASED',
                attempt = attempt + 1,
                updated_at = @Now
            WHERE id = @Id AND (state = 'QUEUED' OR id IN (SELECT job_id FROM leases WHERE lease_until <= @Now));
        ";
        var rows = await connection.ExecuteAsync(claimSql, new { Now = now, Id = jobId }, transaction: tx);
        if (rows == 0)
        {
            tx.Rollback();
            return null;
        }

        const string currentFenceSql = "SELECT fence_token FROM leases WHERE job_id = @Id;";
        var currentToken = await connection.QuerySingleOrDefaultAsync<long?>(currentFenceSql, new { Id = jobId }, transaction: tx);
        long nextFenceToken = (currentToken ?? 0) + 1;

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
            JobId = jobId,
            OwnerId = workerId,
            Now = now,
            LeaseUntil = leaseUntil,
            FenceToken = nextFenceToken
        }, transaction: tx);

        tx.Commit();

        return new JobLease
        {
            JobId = jobId,
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
            // Same condition SPEC/14 describes for AMCCA-JOB-001: the fence token no longer matches the
            // current lease, so this worker has already lost the job -- a heartbeat is just another kind
            // of write that a stale owner must abandon. Previously miscoded as AMCCA-JOB-002, whose
            // catalogued meaning (SPEC/05) is an unrelated condition -- a duplicate idempotency key --
            // that this method has nothing to do with.
            throw new AmccaException(
                AmccaErrors.Job001,
                ErrorCategory.Transient,
                $"Heartbeat refused for job '{jobId}': lease is expired, fence token {expectedFenceToken} is stale, or lease owned by another worker (DEF-017, SPEC/14).");
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
        await connection.ExecuteAsync("UPDATE jobs SET state = 'SUCCEEDED', updated_at = @Now WHERE id = @JobId;",
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
        var lease = await connection.QuerySingleOrDefaultAsync<LeaseCheckRecord>(checkSql, new { JobId = jobId }, transaction: tx);

        if (lease == null || lease.OwnerId != workerId || lease.FenceToken != expectedFenceToken)
        {
            tx.Rollback();
            // SPEC/14, AMCCA-JOB-001: the lease already moved on (expired and was re-claimed, or was
            // never this worker's), so this worker's write is stale, not forbidden -- whoever holds the
            // lease now is entitled to complete it. TRANSIENT/retryable, not USER_ACTION_REQUIRED: no
            // operator needs to act, the caller simply abandons and stops (SPEC/14 "work abandoned").
            throw new AmccaException(
                AmccaErrors.Job001,
                ErrorCategory.Transient,
                $"CompleteJob refused for job '{jobId}': worker '{workerId}' has stale fence token {expectedFenceToken} or does not hold lease (DEF-016, SPEC/14).");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await connection.ExecuteAsync("UPDATE jobs SET state = 'SUCCEEDED', updated_at = @Now WHERE id = @JobId;",
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
        var lease = await connection.QuerySingleOrDefaultAsync<LeaseCheckRecord>(checkSql, new { JobId = jobId }, transaction: tx);

        if (lease == null || lease.OwnerId != workerId || lease.FenceToken != expectedFenceToken)
        {
            tx.Rollback();
            // Same stale-lease condition as CompleteJobOrThrowAsync above: AMCCA-JOB-001, not JOB-003 --
            // this worker no longer holds the job, it did not fail an operator-facing precondition.
            throw new AmccaException(
                AmccaErrors.Job001,
                ErrorCategory.Transient,
                $"FailJob refused for job '{jobId}': worker '{workerId}' has stale fence token {expectedFenceToken} or does not hold active lease (DEF-016, SPEC/14).");
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

    /// <summary>
    /// Operator-facing queue listing (SPEC/62 requires lists to be paged; this system accumulates
    /// hundreds of thousands of rows, so the UI never asks for the whole table).
    /// </summary>
    public async Task<IReadOnlyList<JobQueueEntry>> ListJobsAsync(
        string? stateFilter = null,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var sql = @"
            SELECT
                j.id AS Id,
                j.production_id AS ProductionId,
                j.type AS Type,
                j.state AS State,
                j.priority AS Priority,
                j.attempt AS Attempt,
                j.max_attempts AS MaxAttempts,
                j.correlation_id AS CorrelationId,
                j.created_at AS CreatedAt,
                j.updated_at AS UpdatedAt,
                l.owner_id AS LeaseOwnerId,
                l.lease_until AS LeaseUntil,
                l.heartbeat_at AS HeartbeatAt,
                l.fence_token AS FenceToken
            FROM jobs j
            LEFT JOIN leases l ON l.job_id = j.id
        ";

        if (!string.IsNullOrWhiteSpace(stateFilter))
        {
            sql += " WHERE j.state = @StateFilter";
        }

        // Priority ascending mirrors the dispatch order in TryClaimNextJobAsync (SPEC/14: 0 is highest).
        sql += " ORDER BY j.priority ASC, j.created_at DESC LIMIT @Limit OFFSET @Offset;";

        var rows = await connection.QueryAsync<JobQueueEntry>(
            sql, new { StateFilter = stateFilter, Limit = limit, Offset = offset });
        return rows.ToList();
    }

    /// <summary>
    /// The job states actually present in this database. The queue filter is built from this rather than
    /// a hardcoded list so it stays honest even if the set of states in use ever drifts from
    /// `job.schema.json`'s enum again, the way COMPLETED vs SUCCEEDED once did.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListDistinctJobStatesAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<string>(
            "SELECT DISTINCT state FROM jobs ORDER BY state ASC;");
        return rows.ToList();
    }

    public async Task<int> CountJobsAsync(string? stateFilter = null, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var sql = "SELECT COUNT(*) FROM jobs";
        if (!string.IsNullOrWhiteSpace(stateFilter))
        {
            sql += " WHERE state = @StateFilter";
        }
        sql += ";";
        return await connection.ExecuteScalarAsync<int>(sql, new { StateFilter = stateFilter });
    }

    /// <summary>
    /// SPEC/14: "A dead-lettered job is never silently dropped and never automatically retried; it waits
    /// for an operator." This is that operator action, and the only legal way out of DEAD_LETTER.
    ///
    /// The attempt counter is deliberately NOT reset (SPEC/14, "Retries and dead-lettering"): zeroing it
    /// would erase both the max_attempts bound and the attempt history, letting an operator loop a
    /// poisoned job indefinitely with no record. Preserving it grants exactly one further attempt, after
    /// which the job returns to DEAD_LETTER for the operator to look at again.
    /// </summary>
    public async Task RequeueDeadLetterJobAsync(string jobId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToString("O");

        // Single conditional statement, as with every other job state change (SPEC/14, D-010).
        const string requeueSql = @"
            UPDATE jobs
            SET state = 'QUEUED',
                updated_at = @Now
            WHERE id = @Id AND state = 'DEAD_LETTER';
        ";
        var rows = await connection.ExecuteAsync(requeueSql, new { Id = jobId, Now = now }, transaction: tx);

        if (rows == 0)
        {
            tx.Rollback();
            throw new AmccaException(
                AmccaErrors.Job003,
                ErrorCategory.UserActionRequired,
                $"Job '{jobId}' cannot be requeued: only a DEAD_LETTER job waits for an operator (SPEC/14).");
        }

        // A requeued job must not carry a stale lease into its next claim; FailJobAsync already removes it
        // on the dead-letter path, so this only defends against a lease row that outlived its job.
        await connection.ExecuteAsync("DELETE FROM leases WHERE job_id = @Id;", new { Id = jobId }, transaction: tx);

        tx.Commit();
    }

    public async Task<JobRecord?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                production_id AS ProductionId,
                type AS Type,
                state AS State,
                priority AS Priority,
                idempotency_key AS IdempotencyKey,
                attempt AS Attempt,
                max_attempts AS MaxAttempts,
                correlation_id AS CorrelationId,
                payload_json AS PayloadJson,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt,
                schema_version AS SchemaVersion
            FROM jobs
            WHERE id = @Id;
        ";
        return await connection.QuerySingleOrDefaultAsync<JobRecord>(sql, new { Id = jobId });
    }

    private record LeaseCheckRecord(string? OwnerId, long FenceToken);
}
