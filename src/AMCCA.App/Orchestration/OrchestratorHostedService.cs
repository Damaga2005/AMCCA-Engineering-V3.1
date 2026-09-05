using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AMCCA.App.Orchestration;

/// <summary>
/// Loops <see cref="OrchestratorEngine.RunTickAsync"/> on a fixed interval. A failed tick is logged and
/// retried next interval — one bad production must not stop the pipeline. Stops cleanly on host
/// shutdown.
/// </summary>
public sealed class OrchestratorHostedService : BackgroundService
{
    private readonly OrchestratorEngine _engine;
    private readonly ILogger<OrchestratorHostedService> _logger;

    // ponytail: fixed 5s tick. Move to config (e.g. policy.orchestrator.tick_seconds) once that config
    // block is typed instead of Dictionary<string, object>.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public OrchestratorHostedService(OrchestratorEngine engine, ILogger<OrchestratorHostedService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Orchestrator started; tick interval {IntervalSeconds}s.", Interval.TotalSeconds);
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                LogReport(await _engine.RunTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orchestrator tick failed; retrying next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        _logger.LogInformation("Orchestrator stopped.");
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private void LogReport(OrchestratorTickReport r)
    {
        if (r.KillSwitchEngaged)
        {
            _logger.LogWarning("Kill switch engaged — orchestrator idle this tick.");
            return;
        }

        foreach (var a in r.Actions)
        {
            _logger.LogInformation("Production {ProductionId}: {From} -> {To} ({Outcome}{Reason}).",
                a.ProductionId, a.FromState, a.ToState, a.Outcome,
                a.ReasonCode is null ? "" : $" {a.ReasonCode}");
        }

        foreach (var e in r.Errors)
        {
            _logger.LogError("Production {ProductionId} in {State}: {Message}", e.ProductionId, e.State, e.Message);
        }

        if (r.Considered > 0)
        {
            _logger.LogInformation(
                "Tick: considered {Considered}, committed {Committed}, awaiting-approval {Awaiting}, noop {Noop}, skipped {Skipped}, errors {Errors}.",
                r.Considered, r.TransitionsCommitted, r.AwaitingApproval, r.Noop, r.Skipped, r.Errors.Count);
        }
    }
}
