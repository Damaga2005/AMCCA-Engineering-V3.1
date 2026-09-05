using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class JobLeaseFenceAndHeartbeatRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly JobManager _jobManager;

    public JobLeaseFenceAndHeartbeatRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_JOBS_DEF016_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "jobs_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _jobManager = new JobManager(_factory);
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
        }
    }

    [Fact]
    public async Task DEF016_DEF017_ZombieWorkerWithExpiredLease_CannotHeartbeat_Fail_Or_CompleteJob()
    {
        // 1. Enqueue job
        var job = await _jobManager.EnqueueJobAsync("render", "idemp-job-1", "corr-1", "{}", priority: 1, maxAttempts: 3);

        // 2. Worker A acquires lease -> fence_token = 1
        var claimA = await _jobManager.TryClaimNextJobAsync("worker-A", TimeSpan.FromMinutes(5));
        claimA.Should().NotBeNull();
        claimA!.OwnerId.Should().Be("worker-A");
        claimA.FenceToken.Should().Be(1);

        // 3. Simulate lease expiration: set lease_until to past and re-queue job (recovery)
        using (var connection = await _factory.CreateOpenConnectionAsync())
        {
            var past = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
            await connection.ExecuteAsync(
                "UPDATE leases SET lease_until = @Past WHERE job_id = @JobId; UPDATE jobs SET state = 'QUEUED' WHERE id = @JobId;",
                new { Past = past, JobId = job.Id });
        }

        // 4. Worker B claims the recovered job -> gets fence_token = 2
        var claimB = await _jobManager.TryClaimNextJobAsync("worker-B", TimeSpan.FromMinutes(5));
        claimB.Should().NotBeNull();
        claimB!.OwnerId.Should().Be("worker-B");
        claimB.FenceToken.Should().Be(2);

        // 5. DEF-017: Worker A attempts Heartbeat on expired / lost lease -> must fail
        var heartbeatA = await _jobManager.HeartbeatLeaseAsync(job.Id, "worker-A", 1, TimeSpan.FromMinutes(5));
        heartbeatA.Should().BeFalse("Worker A has lost the lease and cannot renew it (DEF-017)");

        // AMCCA-JOB-001, not JOB-002: a heartbeat is a write like any other, and this worker's fence
        // token is stale for the same reason CompleteJobOrThrowAsync/FailJobAsync would reject it
        // (SPEC/14). JOB-002 is reserved for a duplicate idempotency key on enqueue, an unrelated
        // condition this method has nothing to do with.
        var actHeartbeatOrThrow = () => _jobManager.HeartbeatLeaseOrThrowAsync(job.Id, "worker-A", 1, TimeSpan.FromMinutes(5));
        await actHeartbeatOrThrow.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Job001, "Heartbeat on expired lease must throw AMCCA-JOB-001");

        // 6. DEF-016: Stale Worker A attempts FailJobAsync with fence_token=1 -> must throw AMCCA-JOB-001
        // (SPEC/14: a stale fence token means the lease already moved on; this is a transient, retryable
        // race outcome for the zombie worker to abandon, not an operator-facing dead-letter condition).
        var actFailA = () => _jobManager.FailJobAsync(job.Id, "worker-A", 1, "Zombie worker error");
        await actFailA.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Job001, "Zombie worker cannot fail a job held by another worker (DEF-016)");

        // 7. Stale Worker A attempts CompleteJobOrThrowAsync with fence_token=1 -> must throw AMCCA-JOB-001
        var actCompleteA = () => _jobManager.CompleteJobOrThrowAsync(job.Id, "worker-A", 1);
        await actCompleteA.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Job001, "Zombie worker cannot complete a job held by another worker");

        // 8. Active Worker B executes Heartbeat -> SUCCEEDS
        var heartbeatB = await _jobManager.HeartbeatLeaseAsync(job.Id, "worker-B", 2, TimeSpan.FromMinutes(5));
        heartbeatB.Should().BeTrue("Active owner with valid fence token must be able to heartbeat");

        // 9. Active Worker B completes job with fence_token=2 -> SUCCEEDS
        await _jobManager.CompleteJobOrThrowAsync(job.Id, "worker-B", 2);

        var finalJob = await _jobManager.GetJobAsync(job.Id);
        finalJob.Should().NotBeNull();
        finalJob!.State.Should().Be("SUCCEEDED"); // job.schema.json's terminal success state (migration 006)
    }
}
