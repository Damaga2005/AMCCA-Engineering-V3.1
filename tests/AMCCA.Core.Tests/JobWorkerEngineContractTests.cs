using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class JobWorkerEngineContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly JobManager _jobs;

    // The heartbeat fires ~20x per lease, so only a ~2s scheduling stall could let the lease lapse
    // between beats. A fully deterministic version waits on B2 (inject TimeProvider).
    private static readonly JobWorkerOptions FastOptions = new(
        MaxConcurrency: 2,
        LeaseDuration: TimeSpan.FromMilliseconds(2000),
        HeartbeatInterval: TimeSpan.FromMilliseconds(100),
        PollInterval: TimeSpan.FromMilliseconds(50),
        AgingWindow: TimeSpan.FromSeconds(2),
        ReaperInterval: TimeSpan.FromMilliseconds(100));

    public JobWorkerEngineContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_JOBW_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "jobw.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _jobs = new JobManager(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private JobWorkerEngine Engine(JobHandlerRegistry handlers, JobWorkerOptions? opts = null)
        => new(_jobs, handlers, opts ?? FastOptions);

    private sealed class FnJobHandler : IJobHandler
    {
        private readonly Func<JobExecutionContext, CancellationToken, Task<JobResult>> _fn;
        public FnJobHandler(Func<JobExecutionContext, CancellationToken, Task<JobResult>> fn) => _fn = fn;
        public Task<JobResult> HandleAsync(JobExecutionContext c, CancellationToken ct = default) => _fn(c, ct);
    }

    private Task<JobRecord> EnqueueAsync(string type, string key, int priority = 3, int maxAttempts = 3)
        => _jobs.EnqueueJobAsync(type, key, "corr-jw", "{}", priority, maxAttempts);

    [Fact]
    public async Task ProcessNext_WithNoJobs_ReportsNothingAvailable()
        => (await Engine(new JobHandlerRegistry()).ProcessNextAsync("w1"))
            .Should().Be(JobProcessingOutcome.NothingAvailable);

    [Fact]
    public async Task ProcessNext_RunsHandler_AndMarksJobSucceeded()
    {
        var job = await EnqueueAsync("RENDER", "k-ok");
        var handlers = new JobHandlerRegistry().Register("RENDER",
            new FnJobHandler((_, _) => Task.FromResult(JobResult.Success())));

        var outcome = await Engine(handlers).ProcessNextAsync("w1");

        outcome.Should().Be(JobProcessingOutcome.Completed);
        (await _jobs.GetJobAsync(job.Id))!.State.Should().Be("SUCCEEDED");
    }

    [Fact]
    public async Task ProcessNext_HandlerFailure_RequeuesThenDeadLettersAtMaxAttempts()
    {
        var job = await EnqueueAsync("RENDER", "k-fail", maxAttempts: 2);
        var engine = Engine(new JobHandlerRegistry().Register("RENDER",
            new FnJobHandler((_, _) => Task.FromResult(JobResult.Failure("nope")))));

        (await engine.ProcessNextAsync("w1")).Should().Be(JobProcessingOutcome.Failed);
        (await _jobs.GetJobAsync(job.Id))!.State.Should().Be("QUEUED", "attempt 1 of 2 — requeue");

        (await engine.ProcessNextAsync("w1")).Should().Be(JobProcessingOutcome.Failed);
        (await _jobs.GetJobAsync(job.Id))!.State.Should().Be("DEAD_LETTER", "attempts exhausted (SPEC/14)");
    }

    [Fact]
    public async Task ProcessNext_UnknownJobType_DeadLettersForAnOperator()
    {
        var job = await EnqueueAsync("NO_SUCH_TYPE", "k-unknown", maxAttempts: 1);

        var outcome = await Engine(new JobHandlerRegistry()).ProcessNextAsync("w1");

        outcome.Should().Be(JobProcessingOutcome.Failed);
        (await _jobs.GetJobAsync(job.Id))!.State.Should().Be("DEAD_LETTER");
    }

    [Fact]
    public async Task ProcessNext_HandlerThrows_ReportsHandlerThrew_AndFailsTheJob()
    {
        var job = await EnqueueAsync("RENDER", "k-throw", maxAttempts: 1);
        var engine = Engine(new JobHandlerRegistry().Register("RENDER",
            new FnJobHandler((_, _) => throw new InvalidOperationException("boom"))));

        (await engine.ProcessNextAsync("w1")).Should().Be(JobProcessingOutcome.HandlerThrew);
        (await _jobs.GetJobAsync(job.Id))!.State.Should().Be("DEAD_LETTER");
    }

    [Fact]
    public async Task ReclaimExpiredLeases_MovesAStaleLeasedJobBackToQueued_PreservingAttempt()
    {
        var job = await EnqueueAsync("RENDER", "k-stale");
        var claim = await _jobs.TryClaimNextJobAsync("dead-worker", TimeSpan.FromMinutes(5));
        claim.Should().NotBeNull();

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync("UPDATE leases SET lease_until = @Past WHERE job_id = @Id;",
                new { Past = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"), Id = job.Id });
        }

        (await _jobs.ReclaimExpiredLeasesAsync()).Should().Be(1);
        var reclaimed = await _jobs.GetJobAsync(job.Id);
        reclaimed!.State.Should().Be("QUEUED");
        reclaimed.Attempt.Should().Be(1, "the attempt made by the crashed worker still counts");
    }

    [Fact]
    public async Task Heartbeat_KeepsALongRunningJobsLeaseAlive_SoAReaperDoesNotStealIt()
    {
        var job = await EnqueueAsync("RENDER", "k-long");
        int reclaimedMidFlight = -1;

        var handlers = new JobHandlerRegistry().Register("RENDER", new FnJobHandler(async (_, ct) =>
        {
            await Task.Delay(2800, ct);          // well past the 2000ms lease — only the heartbeat keeps it alive
            reclaimedMidFlight = await _jobs.ReclaimExpiredLeasesAsync();
            return JobResult.Success();
        }));

        var outcome = await Engine(handlers).ProcessNextAsync("w1");

        outcome.Should().Be(JobProcessingOutcome.Completed);
        reclaimedMidFlight.Should().Be(0, "the heartbeat extended the lease past the handler's runtime");
        (await _jobs.GetJobAsync(job.Id))!.State.Should().Be("SUCCEEDED");
    }

    [Fact]
    public async Task Aging_LetsAnOldLowPriorityJobOutrankAFreshHighPriorityOne()
    {
        var fresh = await EnqueueAsync("RENDER", "k-fresh", priority: 2);
        var old = await EnqueueAsync("DISCOVERY", "k-old", priority: 5);

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync("UPDATE jobs SET created_at = @Old WHERE id = @Id;",
                new { Old = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O"), Id = old.Id });
        }

        // 30s old / 2s window = 15 levels of boost, capped at the job's own priority (5) -> effective 0.
        var claim = await _jobs.TryClaimNextJobAsync("w1", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2));

        claim!.JobId.Should().Be(old.Id, "aging must not let P5 starve behind a fresh P2 (SPEC/17)");
    }

    [Fact]
    public async Task WithoutAging_TheFreshHighPriorityJobWinsAsBefore()
    {
        var fresh = await EnqueueAsync("RENDER", "k-fresh2", priority: 2);
        var old = await EnqueueAsync("DISCOVERY", "k-old2", priority: 5);
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync("UPDATE jobs SET created_at = @Old WHERE id = @Id;",
                new { Old = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O"), Id = old.Id });
        }

        var claim = await _jobs.TryClaimNextJobAsync("w1", TimeSpan.FromMinutes(1));

        claim!.JobId.Should().Be(fresh.Id, "with aging off, priority is absolute");
    }
}
