using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Configuration;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Jobs;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AMCCA.App.Orchestration;

/// <summary>
/// SPEC/16 / SPEC/44: on an interval, recovers expired leases and reconciles DISPATCHED / UNKNOWN
/// intents (via <see cref="IReconciler"/>), and — when a reconciler confirms a side effect did not run
/// — resumes a production out of UNKNOWN_EXTERNAL_STATE back to where it was. With no reconciler wired
/// it does neither: it logs how much is stuck waiting for one rather than guessing.
/// </summary>
public sealed class ReconciliationHostedService : BackgroundService
{
    private readonly RecoveryService _recovery;
    private readonly ProductionService _productions;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IReconciler? _reconciler;
    private readonly TimeSpan _interval;
    private readonly ILogger<ReconciliationHostedService> _logger;

    public ReconciliationHostedService(
        RecoveryService recovery, ProductionService productions, DatabaseConnectionFactory connectionFactory,
        AmccaConfig config, ILogger<ReconciliationHostedService> logger, IReconciler? reconciler = null)
    {
        _recovery = recovery;
        _productions = productions;
        _connectionFactory = connectionFactory;
        _reconciler = reconciler;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(config.Policy?.Reconcile?.IntervalSeconds ?? 120);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reconciliation service started; interval {IntervalSeconds}s; reconciler {Reconciler}.",
            _interval.TotalSeconds, _reconciler is null ? "not configured" : "configured");

        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                var report = await _recovery.RunStartupRecoveryPassAsync(stoppingToken);
                if (report.ExpiredLeasesRecovered > 0 || report.UnknownIntentsProcessed > 0)
                {
                    _logger.LogInformation("Reconciliation pass: {Message}", report.Message);
                }

                await ResumeStuckProductionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation pass failed; retrying next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        _logger.LogInformation("Reconciliation service stopped.");
    }

    private async Task ResumeStuckProductionsAsync(CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var stuck = await connection.QueryAsync<(string Id, string? UnknownFrom)>(new CommandDefinition(
            "SELECT id AS Id, unknown_from AS UnknownFrom FROM productions WHERE state = 'UNKNOWN_EXTERNAL_STATE';",
            cancellationToken: ct));

        foreach (var (productionId, unknownFrom) in stuck)
        {
            if (_reconciler is null || string.IsNullOrEmpty(unknownFrom))
            {
                _logger.LogWarning("Production {ProductionId} is in UNKNOWN_EXTERNAL_STATE and cannot be resumed automatically (reconciler {R}).",
                    productionId, _reconciler is null ? "not configured" : "configured");
                continue;
            }

            var intentId = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT id FROM intents WHERE production_id = @P AND state IN ('DISPATCHED','UNKNOWN') ORDER BY created_at DESC LIMIT 1;",
                new { P = productionId }, cancellationToken: ct));
            if (intentId is null)
            {
                _logger.LogWarning("Production {ProductionId} is UNKNOWN_EXTERNAL_STATE but has no unresolved intent to reconcile.", productionId);
                continue;
            }

            var rec = await _reconciler.ReconcileIntentAsync(intentId, ct);
            if (rec.Outcome == IntentReconciliationOutcome.NotExecuted)
            {
                await _productions.TransitionAsync(
                    productionId, unknownFrom, actorType: "ReconciliationService",
                    correlationId: $"reconcile-{Guid.NewGuid():N}",
                    causationId: rec.EvidenceRef ?? $"reconciled:{intentId}", ct: ct);
                _logger.LogInformation("Production {ProductionId} reconciled (side effect did not run); resumed to {State}.",
                    productionId, unknownFrom);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
