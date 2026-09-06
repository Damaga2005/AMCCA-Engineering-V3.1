using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ReconciliationSeamContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly IntentManager _intents;
    private readonly JobManager _jobs;

    public ReconciliationSeamContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_RECON_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "recon.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _intents = new IntentManager(_factory);
        _jobs = new JobManager(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private sealed class FnReconciler : IReconciler
    {
        private readonly IntentReconciliation _r;
        public FnReconciler(IntentReconciliation r) => _r = r;
        public Task<IntentReconciliation> ReconcileIntentAsync(string id, CancellationToken ct = default) => Task.FromResult(_r);
    }

    private async Task<string> SeedUnknownIntentAsync()
    {
        var key = IntentKeyGenerator.GenerateKey("charge", $"c-{Guid.NewGuid():N}", 1);
        var intent = await _intents.CreateIntentAsync("EXTERNAL_UNSAFE", "gateway", key, "fp", null, null);
        await _intents.MarkDispatchedAsync(intent.Id, "ext");
        await _intents.MarkUnknownAsync(intent.Id);
        return intent.Id;
    }

    private async Task<string?> IntentStateAsync(string id)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string>("SELECT state FROM intents WHERE id = @Id;", new { Id = id });
    }

    [Fact]
    public async Task NoReconciler_LeavesUnknownIntentsUntouched_AndFabricatesNoEvidence()
    {
        var intentId = await SeedUnknownIntentAsync();
        var recovery = new RecoveryService(_factory, _jobs, _intents, reconciler: null);

        var report = await recovery.RunStartupRecoveryPassAsync();

        report.UnknownIntentsProcessed.Should().Be(0);
        (await IntentStateAsync(intentId)).Should().Be("UNKNOWN");
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM reconciliation_attempts WHERE intent_id = @Id;", new { Id = intentId }))
            .Should().Be(0, "no fabricated STARTUP_STATUS_PROBE evidence");
    }

    [Fact]
    public async Task Reconciler_Executed_ResolvesConfirmed_WithARealAttemptRow()
    {
        var intentId = await SeedUnknownIntentAsync();
        var recovery = new RecoveryService(_factory, _jobs, _intents,
            new FnReconciler(new IntentReconciliation(IntentReconciliationOutcome.Executed, "PLATFORM_STATUS_API", "evidence://plat/xyz", "found")));

        await recovery.RunStartupRecoveryPassAsync();

        (await IntentStateAsync(intentId)).Should().Be("CONFIRMED");
        using var conn = await _factory.CreateOpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(string Method, string Outcome, string Evidence)>(
            "SELECT method AS Method, outcome AS Outcome, evidence_ref AS Evidence FROM reconciliation_attempts WHERE intent_id = @Id;", new { Id = intentId });
        row.Method.Should().Be("PLATFORM_STATUS_API");
        row.Outcome.Should().Be("CONFIRMED");
        row.Evidence.Should().Be("evidence://plat/xyz");
    }

    [Fact]
    public async Task Reconciler_NotExecuted_ResolvesRefuted()
    {
        var intentId = await SeedUnknownIntentAsync();
        var recovery = new RecoveryService(_factory, _jobs, _intents,
            new FnReconciler(new IntentReconciliation(IntentReconciliationOutcome.NotExecuted, "PLATFORM_STATUS_API", null, "not found")));

        await recovery.RunStartupRecoveryPassAsync();

        (await IntentStateAsync(intentId)).Should().Be("REFUTED");
    }

    [Fact]
    public async Task Reconciler_StillUnknown_LeavesIntentUnknown_ButRecordsAnInconclusiveAttempt()
    {
        var intentId = await SeedUnknownIntentAsync();
        var recovery = new RecoveryService(_factory, _jobs, _intents,
            new FnReconciler(new IntentReconciliation(IntentReconciliationOutcome.StillUnknown, "PLATFORM_STATUS_API", null, "no answer")));

        await recovery.RunStartupRecoveryPassAsync();

        (await IntentStateAsync(intentId)).Should().Be("UNKNOWN");
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<string>("SELECT outcome FROM reconciliation_attempts WHERE intent_id = @Id;", new { Id = intentId }))
            .Should().Be("INCONCLUSIVE");
    }
}
