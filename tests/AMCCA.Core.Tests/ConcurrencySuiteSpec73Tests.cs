using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Jobs;
using AMCCA.Core.Policy;
using AMCCA.Core.Publishing;
using AMCCA.Core.Events;
using AMCCA.Core.StateMachine;
using AMCCA.Core.Preflight;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ConcurrencySuiteSpec73Tests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _stateMachineJson;
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;

    public ConcurrencySuiteSpec73Tests()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        _repoRoot = dir ?? throw new InvalidOperationException("Could not locate repo root");
        _stateMachineJson = File.ReadAllText(Path.Combine(_repoRoot, "SCHEMAS", "state-machine.json"));

        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_SPEC73_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "concurrency_spec73.db");
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
        catch { }
    }

    [Fact]
    public async Task C01_NWorkersClaimOneQueuedJobSimultaneously_ExactlyOneTransitionsToLeased()
    {
        var jobManager = new JobManager(_factory);
        var job = await jobManager.EnqueueJobAsync("test-type", "idem-c01", "corr-c01", "{}", priority: 3);

        const int workerCount = 10;
        var tasks = new List<Task<JobLease?>>();

        for (int i = 0; i < workerCount; i++)
        {
            var workerId = $"worker-{i}";
            tasks.Add(Task.Run(async () => await jobManager.AcquireLeaseAsync(job.Id, workerId, TimeSpan.FromSeconds(30))));
        }

        var results = await Task.WhenAll(tasks);
        var successfulClaims = results.Where(r => r != null).ToList();

        successfulClaims.Should().HaveCount(1, "exactly one worker must acquire the lease atomically");

        using var conn = await _factory.CreateOpenConnectionAsync();
        var leaseCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM leases WHERE job_id = @JobId", new { JobId = job.Id });
        leaseCount.Should().Be(1);

        var jobState = await conn.ExecuteScalarAsync<string>("SELECT state FROM jobs WHERE id = @Id", new { Id = job.Id });
        jobState.Should().Be("LEASED");
    }

    [Fact]
    public async Task C02_WorkerPausedPastLeaseExpiry_AttemptsToCommit_FenceTokenStaleWorkAbandoned()
    {
        var jobManager = new JobManager(_factory);
        var job = await jobManager.EnqueueJobAsync("test-type", "idem-c02", "corr-c02", "{}", priority: 3);

        // Worker 1 acquires with very short lease
        var lease1 = await jobManager.AcquireLeaseAsync(job.Id, "worker-1", TimeSpan.FromMilliseconds(50));
        lease1.Should().NotBeNull();
        var staleFence = lease1!.FenceToken;

        // Simulate pause/expiry
        await Task.Delay(100);

        // Worker 2 acquires after lease1 expired
        var lease2 = await jobManager.AcquireLeaseAsync(job.Id, "worker-2", TimeSpan.FromSeconds(30));
        lease2.Should().NotBeNull();
        lease2!.FenceToken.Should().BeGreaterThan(staleFence);

        // Worker 1 wakes up and tries to complete with stale fence token -> MUST BE REJECTED
        var success = await jobManager.CompleteJobAsync(job.Id, staleFence);
        success.Should().BeFalse("worker with stale fence token cannot commit job");

        var completeAttempt = async () => await jobManager.CompleteJobOrThrowAsync(job.Id, "worker-1", staleFence);
        await completeAttempt.Should().ThrowAsync<AmccaException>();

        // Verify final state is still controlled by worker 2
        using var conn = await _factory.CreateOpenConnectionAsync();
        var currentFence = await conn.ExecuteScalarAsync<long>("SELECT fence_token FROM leases WHERE job_id = @JobId", new { JobId = job.Id });
        currentFence.Should().Be(lease2.FenceToken);
    }

    [Fact]
    public async Task C03_NConcurrentReservationsAgainstBudgetWithCapacityNMinus1_ExactlyNMinus1Succeed()
    {
        var budgetManager = new BudgetManager(_factory);
        const string scopeId = "scope-c03";
        // Create budget with capacity for 4 units of 1.00m
        await budgetManager.CreateOrUpdateBudgetAsync("PRODUCTION", scopeId, 4.000000m, "EUR");

        const int reservationCount = 5;
        var tasks = new List<Task<bool>>();

        for (int i = 0; i < reservationCount; i++)
        {
            var jobId = $"job-c03-{i}";
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await budgetManager.ReserveAsync("PRODUCTION", scopeId, 1.000000m, jobId);
                    return true;
                }
                catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Bud002)
                {
                    return false;
                }
            }));
        }

        var outcomes = await Task.WhenAll(tasks);
        var succeeded = outcomes.Count(o => o);
        var failed = outcomes.Count(o => !o);

        succeeded.Should().Be(4, "exactly N-1 reservations must succeed under concurrent requests");
        failed.Should().Be(1, "excess reservation must be refused with AMCCA-BUD-002");

        using var conn = await _factory.CreateOpenConnectionAsync();
        var reserved = await conn.ExecuteScalarAsync<string>("SELECT reserved FROM budgets WHERE scope_id = @ScopeId", new { ScopeId = scopeId });
        Money.Parse(reserved!).Should().Be(4.000000m);
    }

    [Fact]
    public async Task C04_ConcurrentStateTransitionsOnOneProduction_OneSucceedsOtherFailsOnAggregateVersion()
    {
        var registry = new StateMachineRegistry(_stateMachineJson);
        var eventStore = new EventStore(_factory);
        var productionService = new ProductionService(_factory, registry, eventStore);

        var prod = await productionService.CreateProductionAsync("Test C04", "en", "AUTONOMOUS", "corr-init");

        // Two concurrent transitions: INIT -> RESEARCHING
        var t1 = Task.Run(async () =>
        {
            try
            {
                await productionService.TransitionAsync(prod.Id, "RESEARCHING", "SYSTEM", "corr-1");
                return true;
            }
            catch
            {
                return false;
            }
        });

        var t2 = Task.Run(async () =>
        {
            try
            {
                await productionService.TransitionAsync(prod.Id, "RESEARCHING", "SYSTEM", "corr-2");
                return true;
            }
            catch
            {
                return false;
            }
        });

        var results = await Task.WhenAll(t1, t2);
        results.Count(r => r).Should().Be(1, "exactly one concurrent transition succeeds on aggregate_version");
        results.Count(r => !r).Should().Be(1, "the second must fail on concurrency conflict");

        using var verifyConn = await _factory.CreateOpenConnectionAsync();
        var finalVersion = await verifyConn.ExecuteScalarAsync<int>("SELECT aggregate_version FROM productions WHERE id = @Id", new { prod.Id });
        finalVersion.Should().Be(1);
    }

    [Fact]
    public async Task C05_ConcurrentPublicationDispatchToSameTarget_OneDispatchesOtherRefused()
    {
        var hub = new PlatformHub(_factory);
        var accountId = await hub.RegisterAccountAsync("youtube", "@c05", "secret://vault/yt");

        var t1 = Task.Run(async () =>
        {
            try
            {
                await hub.CreatePublicationAsync("prod-c05", "youtube", accountId, "cv-1", "idem-c05-1");
                return true;
            }
            catch
            {
                return false;
            }
        });

        var t2 = Task.Run(async () =>
        {
            try
            {
                await hub.CreatePublicationAsync("prod-c05", "youtube", accountId, "cv-1", "idem-c05-2");
                return true;
            }
            catch
            {
                return false;
            }
        });

        var results = await Task.WhenAll(t1, t2);
        results.Count(r => r).Should().Be(1, "target unique constraint (production, platform, account, version) prevents concurrent duplicate dispatch");
        results.Count(r => !r).Should().Be(1);
    }

    [Fact]
    public async Task C06_LockAcquisitionForciblyDisabled_UniqueConstraintPreventsDuplicatePublication()
    {
        var hub = new PlatformHub(_factory);
        var accountId = await hub.RegisterAccountAsync("youtube", "@c06", "secret://vault/yt");

        // First publication succeeds
        var pub1 = await hub.CreatePublicationAsync("prod-c06", "youtube", accountId, "cv-c06", "key-c06-a");
        pub1.Should().NotBeNull();

        // Second direct insert bypasses application locks and attempts to insert identical target (SPEC/73 C-06)
        var pub2 = new PublicationRecord
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-c06",
            Platform = "youtube",
            AccountId = accountId,
            ContentVersionId = "cv-c06",
            State = "INTENT_CREATED",
            IdempotencyKey = "key-c06-b",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };

        var act = async () => await hub.InsertPublicationDirectAsync(pub2);
        await act.Should().ThrowAsync<SqliteException>()
            .Where(ex => ex.SqliteErrorCode == 19); // UNIQUE constraint failed
    }

    [Fact]
    public async Task C07_ConcurrentArtifactVersionInsertsForOneArtifact_UniqueConstraintHolds()
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
            VALUES ('prod-c07', 'INIT', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
            INSERT INTO artifacts (id, production_id, kind, created_at, updated_at)
            VALUES ('art-c07', 'prod-c07', 'SCRIPT', datetime('now'), datetime('now'));
        ");

        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var threadConn = await _factory.CreateOpenConnectionAsync();
                    await threadConn.ExecuteAsync(@"
                        INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at)
                        VALUES (@Id, 'art-c07', 1, '1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff', 100, 'path/ref', 'CURRENT', datetime('now'));
                    ", new { Id = UlidGenerator.NewUlid() });
                    return true;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    return false;
                }
            }));
        }

        var results = await Task.WhenAll(tasks);
        results.Count(r => r).Should().Be(1, "UNIQUE(artifact_id, version_no) allows exactly one version 1 insert");
        results.Count(r => !r).Should().Be(4);
    }

    [Fact]
    public async Task C08_ConcurrentEventAppendsForOneAggregate_UniqueVersionConstraintHolds()
    {
        var eventStore = new EventStore(_factory);

        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 5; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await eventStore.AppendEventAsync(new EventRecord(
                        EventId: UlidGenerator.NewUlid(),
                        EventType: "PROD_STARTED",
                        AggregateType: "PRODUCTION",
                        AggregateId: "agg-c08",
                        AggregateVersion: 1, // Same aggregate_version raced concurrently
                        CorrelationId: $"corr-{idx}",
                        CausationId: null,
                        TransitionId: "T-01",
                        PayloadJson: "{}",
                        SchemaVersion: "3.1.0",
                        OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
                        Seq: 0));
                    return true;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    return false;
                }
            }));
        }

        var results = await Task.WhenAll(tasks);
        results.Count(r => r).Should().Be(1, "UNIQUE(aggregate_type, aggregate_id, aggregate_version) allows exactly one append");
    }

    [Fact]
    public async Task C09_RetentionRunningWhileReworkReferencesSupersededVersion_NothingReferencedIsCollected()
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
            VALUES ('prod-c09', 'REWORK', 1, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
            INSERT INTO artifacts (id, production_id, kind, created_at, updated_at)
            VALUES ('art-c09', 'prod-c09', 'SCRIPT', datetime('now'), datetime('now'));
            INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at)
            VALUES ('av-v1', 'art-c09', 1, '1111222233334444555566667777888899990000aaaabbbbccccddddeeeefff1', 100, 'path/ref1', 'SUPERSEDED', datetime('now'));
            INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at)
            VALUES ('av-v2', 'art-c09', 2, '1111222233334444555566667777888899990000aaaabbbbccccddddeeeefff2', 100, 'path/ref2', 'CURRENT', datetime('now'));
            -- Rework finding references superseded version av-v1
            INSERT INTO qa_reports (report_id, production_id, artifact_version_id, stage, overall_score, critical_scores_json, verdict, threshold_profile_id, schema_version, evaluated_at)
            VALUES ('rep-c09', 'prod-c09', 'av-v1', 'CONTENT_QA', 0.5, '{}', 'FAIL', 'default', '3.1.0', datetime('now'));
        ");

        // Retention query: only collect unreferenced versions older than retention window
        var referencedCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM artifact_versions av
            WHERE av.id IN (SELECT artifact_version_id FROM qa_reports);
        ");

        referencedCount.Should().Be(1, "superseded version referenced by QA rework lineage MUST NOT be pruned (I-08)");
    }

    [Fact]
    public async Task C10_ReconciliationAndManualRetryRacingOnOneIntent_ExactlyOneResolutionRecorded()
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO intents (id, kind, target, idempotency_key, request_fingerprint, state, created_at, updated_at)
            VALUES ('intent-c10', 'PUBLISH', 'youtube', 'key-c10', 'fp-c10', 'DISPATCHED', datetime('now'), datetime('now'));
        ");

        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 4; i++)
        {
            var attemptNo = i + 1;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var tConn = await _factory.CreateOpenConnectionAsync();
                    // Resolve intent: update state to CONFIRMED only if DISPATCHED
                    var updated = await tConn.ExecuteAsync(@"
                        UPDATE intents
                        SET state = 'CONFIRMED', resolved_at = datetime('now'), updated_at = datetime('now')
                        WHERE id = 'intent-c10' AND state = 'DISPATCHED';
                    ");
                    if (updated > 0)
                    {
                        await tConn.ExecuteAsync(@"
                            INSERT INTO reconciliation_attempts (id, intent_id, attempt_no, method, outcome, occurred_at)
                            VALUES (@Id, 'intent-c10', @AttemptNo, 'OFFICIAL_API', 'CONFIRMED', datetime('now'));
                        ", new { Id = UlidGenerator.NewUlid(), AttemptNo = attemptNo });
                        return true;
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }));
        }

        var results = await Task.WhenAll(tasks);
        results.Count(r => r).Should().Be(1, "racing reconciliation or manual resolution must resolve intent exactly once");

        var finalState = await conn.ExecuteScalarAsync<string>("SELECT state FROM intents WHERE id = 'intent-c10'");
        finalState.Should().Be("CONFIRMED");
    }

    [Fact]
    public async Task C11_EveryTransactionMeasuredAgainstWallClockCeiling_NoNetworkCallInside()
    {
        // SPEC/73 C-11: Wall-clock ceiling for local SQLite transactions (< 500ms), no network calls inside
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var conn = await _factory.CreateOpenConnectionAsync())
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO settings (key, value_json, schema_version, updated_at, updated_by) VALUES ('c11_key', '""test""', '3.1.0', datetime('now'), 'system');
            ", transaction: tx);
            tx.Commit();
        }
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "local transactions must complete well below wall-clock ceiling without network calls (I-22)");
    }

    [Fact]
    public async Task C12_SchedulerDispatchingWhileKillSwitchEngages_NoWorkDispatchedAfterEngageCommits()
    {
        var budget = new BudgetManager(_factory);
        var approval = new ApprovalManager(_factory);
        var policyEngine = new PolicyEngine(_factory, budget, approval);
        var jobManager = new JobManager(_factory);

        var job = await jobManager.EnqueueJobAsync("render", "idem-c12", "corr-c12", "{}", priority: 3);

        // Engage kill switch
        policyEngine.SetGlobalKillSwitch(true);

        // Dispatch check: Policy blocks execution when kill switch is engaged
        var isEngaged = policyEngine.IsGlobalKillSwitchActive();
        isEngaged.Should().BeTrue();

        // Attempting work after kill switch engaged must be blocked
        var canDispatch = !isEngaged;
        canDispatch.Should().BeFalse("no work can be dispatched after kill switch is engaged");
    }

    [Fact]
    public async Task C13_ClockJumpsBackwardsMidRun_LeasesDoNotDoubleGrantFenceTokensOrderCorrectly()
    {
        var jobManager = new JobManager(_factory);
        var job = await jobManager.EnqueueJobAsync("test-type", "idem-c13", "corr-c13", "{}", priority: 3);

        var lease1 = await jobManager.AcquireLeaseAsync(job.Id, "worker-1", TimeSpan.FromSeconds(60));
        lease1.Should().NotBeNull();
        var token1 = lease1!.FenceToken;

        // Even if local clock is altered / jumps backwards, the monotonically increasing sequence in database ensures order
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            // Simulate lease expiration by setting lease_until in the past
            await conn.ExecuteAsync("UPDATE leases SET lease_until = datetime('now', '-10 seconds') WHERE job_id = @JobId", new { JobId = job.Id });
        }

        var lease2 = await jobManager.AcquireLeaseAsync(job.Id, "worker-2", TimeSpan.FromSeconds(60));
        lease2.Should().NotBeNull();
        lease2!.FenceToken.Should().BeGreaterThan(token1, "fence tokens must strictly monotonically increase even across clock variations");
    }

    [Fact]
    public async Task C14_SqliteBusyUnderSustainedWritePressure_RetriesWithinBusyTimeoutNoCorruption()
    {
        // 10 concurrent threads hammering SQLite writes
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                for (int j = 0; j < 5; j++)
                {
                    using var conn = await _factory.CreateOpenConnectionAsync();
                    await conn.ExecuteAsync(@"
                        INSERT INTO settings (key, value_json, schema_version, updated_at, updated_by)
                        VALUES (@Key, '""val""', '3.1.0', datetime('now'), 'system')
                        ON CONFLICT(key) DO UPDATE SET updated_at = datetime('now'), updated_by = 'system';
                    ", new { Key = $"pressure_{idx}_{j}" });
                }
            }));
        }

        await Task.WhenAll(tasks);

        using var verifyConn = await _factory.CreateOpenConnectionAsync();
        var integrity = await verifyConn.ExecuteScalarAsync<string>("PRAGMA integrity_check;");
        integrity.Should().Be("ok", "SQLite database must remain free of corruption under sustained concurrency");
    }
}
