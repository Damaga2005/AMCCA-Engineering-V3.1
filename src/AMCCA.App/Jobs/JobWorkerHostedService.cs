using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Diagnostics;
using AMCCA.Core.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AMCCA.App.Jobs;

/// <summary>
/// The job worker pool. Runs <see cref="JobWorkerOptions.MaxConcurrency"/> loops of
/// <see cref="JobWorkerEngine.ProcessNextAsync"/> plus a periodic sweep that reclaims leases left
/// behind by crashed workers (SPEC/14, SPEC/16). One bad job never stops the pool.
/// </summary>
public sealed class JobWorkerHostedService : BackgroundService
{
    private readonly JobWorkerEngine _engine;
    private readonly ILogger<JobWorkerHostedService> _logger;

    public JobWorkerHostedService(JobWorkerEngine engine, ILogger<JobWorkerHostedService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = _engine.Options;
        _logger.LogInformation(
            "Job worker pool starting: {Workers} workers, lease {LeaseSeconds}s, heartbeat {HeartbeatSeconds}s, aging window {AgingMinutes}m.",
            o.MaxConcurrency, o.LeaseDuration.TotalSeconds, o.HeartbeatInterval.TotalSeconds, o.AgingWindow.TotalMinutes);

        var loops = Enumerable.Range(0, o.MaxConcurrency)
            .Select(i => RunWorkerAsync($"worker-{Environment.MachineName}-{i}", stoppingToken))
            .Append(RunReaperAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
        _logger.LogInformation("Job worker pool stopped.");
    }

    private async Task RunWorkerAsync(string workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var outcome = await _engine.ProcessNextAsync(workerId, ct);
                if (outcome == JobProcessingOutcome.NothingAvailable)
                {
                    await Task.Delay(_engine.Options.PollInterval, ct);
                }
                else
                {
                    AmccaMetrics.CountJob(outcome.ToString());
                    _logger.Log(
                        outcome == JobProcessingOutcome.Completed ? LogLevel.Information : LogLevel.Warning,
                        "{Worker}: {Outcome}.", workerId, outcome);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Worker}: loop error; backing off.", workerId);
                try { await Task.Delay(_engine.Options.PollInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunReaperAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_engine.Options.ReaperInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                {
                    break;
                }
                var reclaimed = await _engine.ReclaimExpiredLeasesAsync(ct);
                if (reclaimed > 0)
                {
                    _logger.LogWarning("Reaper reclaimed {Count} expired lease(s).", reclaimed);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reaper cycle failed; retrying next interval.");
            }
        }
    }
}
