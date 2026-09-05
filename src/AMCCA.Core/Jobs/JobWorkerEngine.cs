using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Jobs;

public sealed record JobWorkerOptions(
    int MaxConcurrency,
    TimeSpan LeaseDuration,
    TimeSpan HeartbeatInterval,
    TimeSpan PollInterval,
    TimeSpan AgingWindow,
    TimeSpan ReaperInterval)
{
    // ponytail: hardcoded. Move to a typed config block (policy.jobs.*) when PolicyConfig is typed
    // instead of Dictionary<string, object>.
    public static JobWorkerOptions Default => new(
        MaxConcurrency: 4,
        LeaseDuration: TimeSpan.FromMinutes(2),
        HeartbeatInterval: TimeSpan.FromSeconds(40),
        PollInterval: TimeSpan.FromSeconds(2),
        AgingWindow: TimeSpan.FromMinutes(15),
        ReaperInterval: TimeSpan.FromSeconds(30));
}

public enum JobProcessingOutcome
{
    NothingAvailable,
    Completed,
    Failed,

    /// <summary>Handler threw; the job was failed (requeued, or dead-lettered once attempts are exhausted).</summary>
    HandlerThrew,

    /// <summary>The lease moved to another worker while this one was working; the job was left untouched (SPEC/14).</summary>
    LeaseLost,
}

/// <summary>
/// The reusable core of the job worker pool, hosting-agnostic and fully unit-testable.
/// <see cref="ProcessNextAsync"/> claims one job, runs its handler while a heartbeat keeps the lease
/// alive, and marks the job SUCCEEDED or FAILED. A BackgroundService (AMCCA.App) runs
/// <see cref="JobWorkerOptions.MaxConcurrency"/> copies of that loop plus a periodic
/// <see cref="ReclaimExpiredLeasesAsync"/> sweep.
/// </summary>
public sealed class JobWorkerEngine
{
    private readonly JobManager _jobs;
    private readonly JobHandlerRegistry _handlers;

    public JobWorkerOptions Options { get; }

    public JobWorkerEngine(JobManager jobs, JobHandlerRegistry handlers, JobWorkerOptions? options = null)
    {
        _jobs = jobs;
        _handlers = handlers;
        Options = options ?? JobWorkerOptions.Default;
    }

    public Task<int> ReclaimExpiredLeasesAsync(CancellationToken ct = default)
        => _jobs.ReclaimExpiredLeasesAsync(ct);

    public async Task<JobProcessingOutcome> ProcessNextAsync(string workerId, CancellationToken ct = default)
    {
        var claim = await _jobs.TryClaimNextJobAsync(workerId, Options.LeaseDuration, Options.AgingWindow, ct);
        if (claim is null)
        {
            return JobProcessingOutcome.NothingAvailable;
        }

        var job = await _jobs.GetJobAsync(claim.JobId, ct);
        if (job is null)
        {
            return JobProcessingOutcome.NothingAvailable;
        }

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var leaseLost = new CancellationTokenSource();
        using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(ct, leaseLost.Token);

        var heartbeat = RunHeartbeatAsync(claim.JobId, workerId, claim.FenceToken, leaseLost, heartbeatStop.Token);

        JobResult result;
        bool handlerThrew = false;
        try
        {
            result = await _handlers.Resolve(job.Type)
                .HandleAsync(new JobExecutionContext(job, claim.FenceToken, workerId), handlerCts.Token);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            await StopHeartbeatAsync(heartbeatStop, heartbeat);
            return JobProcessingOutcome.LeaseLost;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopHeartbeatAsync(heartbeatStop, heartbeat);
            throw;
        }
        catch (Exception ex)
        {
            result = JobResult.Failure($"Handler for job type '{job.Type}' threw: {ex.Message}");
            handlerThrew = true;
        }

        await StopHeartbeatAsync(heartbeatStop, heartbeat);
        if (leaseLost.IsCancellationRequested)
        {
            return JobProcessingOutcome.LeaseLost;
        }

        try
        {
            if (result.Kind == JobResultKind.Success)
            {
                await _jobs.CompleteJobOrThrowAsync(claim.JobId, workerId, claim.FenceToken, ct);
                return JobProcessingOutcome.Completed;
            }

            await _jobs.FailJobAsync(claim.JobId, workerId, claim.FenceToken, result.Detail ?? "job failed", ct);
            return handlerThrew ? JobProcessingOutcome.HandlerThrew : JobProcessingOutcome.Failed;
        }
        catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Job001)
        {
            // The lease expired and was re-claimed between our last heartbeat and now; whoever holds it
            // is entitled to finish it. Abandon quietly (SPEC/14, "work abandoned").
            return JobProcessingOutcome.LeaseLost;
        }
    }

    private async Task RunHeartbeatAsync(
        string jobId, string workerId, long fenceToken, CancellationTokenSource leaseLost, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(Options.HeartbeatInterval, stop);
                var ok = await _jobs.HeartbeatLeaseAsync(
                    jobId, workerId, fenceToken, Options.LeaseDuration, CancellationToken.None);
                if (!ok)
                {
                    leaseLost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
    }

    private static async Task StopHeartbeatAsync(CancellationTokenSource stop, Task heartbeat)
    {
        if (!stop.IsCancellationRequested)
        {
            stop.Cancel();
        }
        try { await heartbeat; } catch { /* already handled inside the loop */ }
    }
}
