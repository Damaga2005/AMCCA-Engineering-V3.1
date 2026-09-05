using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class JobsAndLeasesContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;

    public JobsAndLeasesContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_JOB_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "jobs_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();
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
    public async Task EnqueueJob_WithDuplicateIdempotencyKey_IsPreventedByUniqueConstraint()
    {
        var jobManager = new JobManager(_factory);
        var key = IntentKeyGenerator.GenerateKey("render", "prod-1", 1);

        var job1 = await jobManager.EnqueueJobAsync("render", key, "corr-1", "{}", priority: 2);
        job1.Should().NotBeNull();
        job1.State.Should().Be("QUEUED");
        job1.SchemaVersion.Should().Be("3.1.0", "D-004: every persisted contract object carries schema_version");

        // Enqueueing identical logical intent key fails with unique constraint
        var act = async () => await jobManager.EnqueueJobAsync("render", key, "corr-2", "{}", priority: 2);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ConcurrentWorkers_ClaimingSameJob_OnlyOneSucceedsWithoutDuplication()
    {
        var jobManager = new JobManager(_factory);
        var key = IntentKeyGenerator.GenerateKey("transcode", "prod-2", 1);
        await jobManager.EnqueueJobAsync("transcode", key, "corr-1", "{}", priority: 1);

        // Two workers attempt to claim the job concurrently
        var task1 = Task.Run(() => jobManager.TryClaimNextJobAsync("worker-A", TimeSpan.FromSeconds(30)));
        var task2 = Task.Run(() => jobManager.TryClaimNextJobAsync("worker-B", TimeSpan.FromSeconds(30)));

        var results = await Task.WhenAll(task1, task2);
        var successfulClaims = results.Where(r => r != null).ToList();

        successfulClaims.Should().HaveCount(1, "exactly one worker must successfully claim the job");
        successfulClaims[0]!.FenceToken.Should().Be(1);
    }

    [Fact]
    public async Task FenceToken_IncrementsMonotonicallyOnReclaim()
    {
        var jobManager = new JobManager(_factory);
        var key = IntentKeyGenerator.GenerateKey("audio", "prod-3", 1);
        var enqueued = await jobManager.EnqueueJobAsync("audio", key, "corr-1", "{}");

        // Worker 1 claims
        var claim1 = await jobManager.TryClaimNextJobAsync("worker-1", TimeSpan.FromSeconds(30));
        claim1.Should().NotBeNull();
        claim1!.FenceToken.Should().Be(1);

        // Simulate lease expiration by resetting state to QUEUED and updating lease expiration in past
        await jobManager.ExpireAndRequeueLeaseForTestingAsync(enqueued.Id);

        // Worker 2 reclaims
        var claim2 = await jobManager.TryClaimNextJobAsync("worker-2", TimeSpan.FromSeconds(30));
        claim2.Should().NotBeNull();
        claim2!.FenceToken.Should().Be(2, "fence token must increment monotonically on each acquisition");
    }

    [Fact]
    public async Task StaleWorkerFenceToken_IsRejectedOnHeartbeatAndCompletion()
    {
        var jobManager = new JobManager(_factory);
        var key = IntentKeyGenerator.GenerateKey("script", "prod-4", 1);
        var enqueued = await jobManager.EnqueueJobAsync("script", key, "corr-1", "{}");

        var claim1 = await jobManager.TryClaimNextJobAsync("worker-1", TimeSpan.FromSeconds(30));
        claim1.Should().NotBeNull();

        // Expire and let worker 2 take over with fence token 2
        await jobManager.ExpireAndRequeueLeaseForTestingAsync(enqueued.Id);
        var claim2 = await jobManager.TryClaimNextJobAsync("worker-2", TimeSpan.FromSeconds(30));
        claim2!.FenceToken.Should().Be(2);

        // Stale worker 1 attempts to heartbeat with old fence token 1 -> must fail
        var heartbeatOk = await jobManager.HeartbeatLeaseAsync(enqueued.Id, "worker-1", expectedFenceToken: 1, TimeSpan.FromSeconds(30));
        heartbeatOk.Should().BeFalse();

        // Stale worker 1 attempts to complete job -> must fail
        var completeOk = await jobManager.CompleteJobAsync(enqueued.Id, expectedFenceToken: 1);
        completeOk.Should().BeFalse();
    }

    [Fact]
    public async Task ExceedingMaxAttempts_MovesJobToDeadLetter()
    {
        var jobManager = new JobManager(_factory);
        var key = IntentKeyGenerator.GenerateKey("qa", "prod-5", 1);
        var enqueued = await jobManager.EnqueueJobAsync("qa", key, "corr-1", "{}", maxAttempts: 2);

        // Attempt 1 fails
        var claim1 = await jobManager.TryClaimNextJobAsync("worker-1", TimeSpan.FromSeconds(30));
        await jobManager.FailJobAsync(enqueued.Id, "Temporary timeout");

        // Attempt 2 fails -> reaches max_attempts
        var claim2 = await jobManager.TryClaimNextJobAsync("worker-1", TimeSpan.FromSeconds(30));
        await jobManager.FailJobAsync(enqueued.Id, "Crash occurred");

        var job = await jobManager.GetJobAsync(enqueued.Id);
        job.Should().NotBeNull();
        job!.State.Should().Be("DEAD_LETTER", "job must be moved to DEAD_LETTER on attempt exhaustion (SPEC/14)");
        job.SchemaVersion.Should().Be("3.1.0", "GetJobAsync must round-trip schema_version, not just EnqueueJobAsync's in-memory copy");
    }

    [Fact]
    public async Task IntentManager_RecordsIntentBeforeExternalUnsafeCall_AndHandlesUnknownResult()
    {
        var intentManager = new IntentManager(_factory);
        var idempotencyKey = IntentKeyGenerator.GenerateKey("publish", "pub-123", 1);
        var fingerprint = IntentKeyGenerator.ComputeFingerprint("https://api.platform.example/upload", "{\"video\":\"content\"}");

        // 1. Insert intent with state CREATED before external call
        var intent = await intentManager.CreateIntentAsync(
            kind: "EXTERNAL_UNSAFE_PUBLISH",
            target: "https://api.platform.example/upload",
            idempotencyKey: idempotencyKey,
            requestFingerprint: fingerprint,
            jobId: null,
            productionId: "prod-100");

        intent.State.Should().Be("CREATED");

        // 2. Mark DISPATCHED when sending
        await intentManager.MarkDispatchedAsync(intent.Id, externalRequestId: "req-abc");
        var dispatched = await intentManager.GetIntentAsync(intent.Id);
        dispatched!.State.Should().Be("DISPATCHED");

        // 3. On connection timeout, mark UNKNOWN (never assume failure or retry blind)
        await intentManager.MarkUnknownAsync(intent.Id);
        var unknown = await intentManager.GetIntentAsync(intent.Id);
        unknown!.State.Should().Be("UNKNOWN");
    }

    [Fact]
    public async Task RecoveryService_ReclaimsExpiredLeases_AndResolvesUnknownIntents()
    {
        var jobManager = new JobManager(_factory);
        var intentManager = new IntentManager(_factory);
        var recoveryService = new RecoveryService(_factory, jobManager, intentManager);

        // Setup 1: an expired lease
        var key = IntentKeyGenerator.GenerateKey("render", "prod-6", 1);
        var enqueued = await jobManager.EnqueueJobAsync("render", key, "corr-1", "{}");
        var claim = await jobManager.TryClaimNextJobAsync("worker-1", TimeSpan.FromSeconds(30));
        // Force lease expiration
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE leases SET lease_until = '2020-01-01T00:00:00Z' WHERE job_id = @id;";
            cmd.Parameters.AddWithValue("@id", enqueued.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        // Setup 2: an intent in UNKNOWN state
        var intentKey = IntentKeyGenerator.GenerateKey("charge", "cost-1", 1);
        var intent = await intentManager.CreateIntentAsync("EXTERNAL_UNSAFE", "gateway", intentKey, "fp-1", null, null);
        await intentManager.MarkDispatchedAsync(intent.Id, "ext-1");
        await intentManager.MarkUnknownAsync(intent.Id);

        // Run recovery pass
        var report = await recoveryService.RunStartupRecoveryPassAsync();

        report.ExpiredLeasesRecovered.Should().BeGreaterThan(0);
        report.UnknownIntentsProcessed.Should().BeGreaterThan(0);

        // Job should be back in QUEUED state
        var recoveredJob = await jobManager.GetJobAsync(enqueued.Id);
        recoveredJob!.State.Should().Be("QUEUED");
    }
}
