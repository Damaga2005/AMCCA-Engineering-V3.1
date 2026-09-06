using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Jobs;
using AMCCA.Core.Operator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AMCCA.App.Orchestration;

/// <summary>
/// Logs a structured health snapshot on an interval (kill switch, autonomy, active productions,
/// pending approvals, job queue depth, dead-letter count). The headless host has no HTTP surface, so
/// this is how "is it healthy" is answered when it runs unattended; a DEAD_LETTER backlog is escalated
/// to a warning.
/// </summary>
public sealed class SystemHealthReporter : BackgroundService
{
    private readonly OperatorControlService _operatorControl;
    private readonly JobManager _jobs;
    private readonly ILogger<SystemHealthReporter> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public SystemHealthReporter(OperatorControlService operatorControl, JobManager jobs, ILogger<SystemHealthReporter> logger)
    {
        _operatorControl = operatorControl;
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var status = await _operatorControl.GetSystemStatusAsync(stoppingToken);
                var queued = await _jobs.CountJobsAsync("QUEUED", stoppingToken);
                var leased = await _jobs.CountJobsAsync("LEASED", stoppingToken);
                var deadLetter = await _jobs.CountJobsAsync("DEAD_LETTER", stoppingToken);

                _logger.LogInformation(
                    "Health: killSwitch={KillSwitch} autonomy={Autonomy} activeProductions={Active} pendingApprovals={Approvals} jobsQueued={Queued} jobsLeased={Leased} jobsDeadLetter={DeadLetter}",
                    status.GlobalKillSwitchActive, status.AutonomyMode, status.ActiveProductionsCount,
                    status.PendingApprovalsCount, queued, leased, deadLetter);

                if (deadLetter > 0)
                {
                    _logger.LogWarning("Health: {DeadLetter} job(s) in DEAD_LETTER are waiting for an operator (SPEC/14).", deadLetter);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health snapshot failed; retrying next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
