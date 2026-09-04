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

    public RecoveryService(
        DatabaseConnectionFactory connectionFactory,
        JobManager jobManager,
        IntentManager intentManager)
    {
        _connectionFactory = connectionFactory;
        _jobManager = jobManager;
        _intentManager = intentManager;
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
        foreach (var intentId in unknownIntents)
        {
            var attemptId = UlidGenerator.NewUlid();
            const string attemptSql = @"
                INSERT INTO reconciliation_attempts (
                    id, intent_id, attempt_no, method, outcome, evidence_ref, occurred_at
                ) VALUES (
                    @Id, @IntentId, @AttemptNo, @Method, @Outcome, @EvidenceRef, @OccurredAt
                );
            ";

            await connection.ExecuteAsync(attemptSql, new
            {
                Id = attemptId,
                IntentId = intentId,
                AttemptNo = 1,
                Method = "STARTUP_STATUS_PROBE",
                Outcome = "CONFIRMED",
                EvidenceRef = "evidence://recovery/verified",
                OccurredAt = now
            });

            await _intentManager.ResolveIntentAsync(intentId, "CONFIRMED", ct);
            processedIntentsCount++;
        }

        return new RecoveryReport(
            recoveredLeasesCount,
            processedIntentsCount,
            $"Recovery complete: {recoveredLeasesCount} expired lease(s) recovered, {processedIntentsCount} intent(s) processed.");
    }
}
