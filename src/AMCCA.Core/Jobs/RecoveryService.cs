using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Jobs;

public class RecoveryService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly JobManager _jobManager;
    private readonly IntentManager _intentManager;
    private readonly IReconciler? _reconciler;

    public RecoveryService(
        DatabaseConnectionFactory connectionFactory,
        JobManager jobManager,
        IntentManager intentManager,
        IReconciler? reconciler = null)
    {
        _connectionFactory = connectionFactory;
        _jobManager = jobManager;
        _intentManager = intentManager;
        _reconciler = reconciler;
    }

    public async Task<RecoveryReport> RunStartupRecoveryPassAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("O");

        // 1. Scan for jobs with expired leases (SPEC/16)
        const string expiredLeasesSql = @"
            SELECT l.job_id, j.attempt, j.max_attempts AS MaxAttempts
            FROM leases l
            JOIN jobs j ON l.job_id = j.id
            WHERE l.lease_until < @Now;
        ";
        var expiredLeases = (await connection.QueryAsync<(string JobId, long Attempt, long MaxAttempts)>(
            expiredLeasesSql, new { Now = now })).ToList();

        int recoveredLeasesCount = 0;
        foreach (var (jobId, attempt, maxAttempts) in expiredLeases)
        {
            using var tx = connection.BeginTransaction();
            string targetState = attempt >= maxAttempts ? "DEAD_LETTER" : "QUEUED";

            await connection.ExecuteAsync(
                "UPDATE jobs SET state = @State, updated_at = @Now WHERE id = @JobId;",
                new { State = targetState, Now = now, JobId = jobId }, transaction: tx);

            await connection.ExecuteAsync(
                "DELETE FROM leases WHERE job_id = @JobId;",
                new { JobId = jobId }, transaction: tx);

            tx.Commit();
            recoveredLeasesCount++;
        }

        // 2. Scan for intents in DISPATCHED or UNKNOWN (SPEC/16)
        const string unknownIntentsSql = @"
            SELECT id FROM intents
            WHERE state IN ('DISPATCHED', 'UNKNOWN');
        ";
        var unknownIntents = (await connection.QueryAsync<string>(unknownIntentsSql)).ToList();

        int processedIntentsCount = 0;

        // No reconciler wired: an unknown intent is left exactly as it is. It is never resolved on a
        // guess (SPEC/16: "Reconciliation resolves it first"), and no evidence is fabricated.
        if (_reconciler is not null)
        {
            const string attemptSql = @"
                INSERT INTO reconciliation_attempts (id, intent_id, attempt_no, method, outcome, evidence_ref, occurred_at)
                VALUES (@Id, @IntentId, @AttemptNo, @Method, @Outcome, @EvidenceRef, @OccurredAt);";
            const string nextAttemptSql = "SELECT COALESCE(MAX(attempt_no), 0) FROM reconciliation_attempts WHERE intent_id = @Id;";

            foreach (var intentId in unknownIntents)
            {
                var rec = await _reconciler.ReconcileIntentAsync(intentId, ct);

                var attemptNo = (await connection.ExecuteScalarAsync<long>(nextAttemptSql, new { Id = intentId })) + 1;
                await connection.ExecuteAsync(attemptSql, new
                {
                    Id = UlidGenerator.NewUlid(),
                    IntentId = intentId,
                    AttemptNo = attemptNo,
                    Method = rec.Method,
                    Outcome = rec.Outcome switch
                    {
                        IntentReconciliationOutcome.Executed => "CONFIRMED",
                        IntentReconciliationOutcome.NotExecuted => "REFUTED",
                        IntentReconciliationOutcome.Failed => "REFUTED",
                        _ => "INCONCLUSIVE",
                    },
                    EvidenceRef = rec.EvidenceRef,
                    OccurredAt = now,
                });

                var resolvedState = rec.Outcome switch
                {
                    IntentReconciliationOutcome.Executed => "CONFIRMED",
                    IntentReconciliationOutcome.NotExecuted => "REFUTED",
                    IntentReconciliationOutcome.Failed => "ABANDONED",
                    _ => (string?)null,
                };
                if (resolvedState is not null)
                {
                    await _intentManager.ResolveIntentAsync(intentId, resolvedState, ct);
                    processedIntentsCount++;
                }
            }
        }

        var intentNote = _reconciler is null
            ? $"{unknownIntents.Count} unknown intent(s) left for a reconciler"
            : $"{processedIntentsCount} intent(s) reconciled";
        return new RecoveryReport(
            recoveredLeasesCount,
            processedIntentsCount,
            $"Recovery complete: {recoveredLeasesCount} expired lease(s) recovered, {intentNote}.");
    }
}
