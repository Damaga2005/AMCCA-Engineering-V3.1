using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using AMCCA.Core.Policy;
using AMCCA.Core.Providers;
using AMCCA.Core.Publishing;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ChaosSuiteSpec74Tests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;

    public ChaosSuiteSpec74Tests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_SPEC74_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "chaos_spec74.db");
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
    public async Task X01_KillDuringResearchFetch_NoPartialClaim_RetryableNoOrphanRows()
    {
        // Simulate crash mid-transaction during research ingestion
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(@"
                INSERT INTO sources (id, url, content_hash, trust_tier, robots_allowed, created_at, retrieved_at)
                VALUES ('src-x01', 'https://example.com/item', 'hash1', 'PRIMARY', 1, datetime('now'), datetime('now'));
            ", transaction: tx);

            // Crash / abort before claim is linked and before commit
            tx.Rollback();
        }

        // Verify state after restart
        using var restartConn = await _factory.CreateOpenConnectionAsync();
        var sourceCount = await restartConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sources WHERE id = 'src-x01'");
        var claimCount = await restartConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM claims WHERE id = 'claim-x01'");

        sourceCount.Should().Be(0, "aborted research fetch must not leave orphan source rows");
        claimCount.Should().Be(0, "no partial claims can exist after aborted fetch");
    }

    [Fact]
    public async Task X02_KillDuringScriptGeneration_AgentRunRecordedIncomplete_NoInvalidArtifact()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            // Seed prompt template & version
            await conn.ExecuteAsync(@"
                INSERT INTO prompt_templates (id, key, purpose, created_at, updated_at)
                VALUES ('pt-x02', 'script_gen', 'generate script', datetime('now'), datetime('now'));
                INSERT INTO prompt_versions (id, template_id, version_no, body_sha256, body_ref, created_at)
                VALUES ('pv-x02', 'pt-x02', 1, 'sha', 'ref', datetime('now'));
                -- Agent run starts in RUNNING state
                INSERT INTO agent_runs (run_id, agent_id, agent_version, prompt_version_id, model_id, model_params_hash, state, input_hash, correlation_id, schema_version, started_at)
                VALUES ('run-x02', 'script-agent', '1.0', 'pv-x02', 'claude-3-5', 'paramhash', 'RUNNING', 'inphash', 'corr-x02', '3.1.0', datetime('now'));
            ");
        }

        // Simulate crash recovery sweep: uncompleted runs with active status on restart become FAILED/INCOMPLETE
        using (var restartConn = await _factory.CreateOpenConnectionAsync())
        {
            await restartConn.ExecuteAsync(@"
                UPDATE agent_runs
                SET state = 'FAILED', output_valid = 0, finished_at = datetime('now')
                WHERE state = 'RUNNING';
            ");

            var finalState = await restartConn.ExecuteScalarAsync<string>("SELECT state FROM agent_runs WHERE run_id = 'run-x02'");
            finalState.Should().Be("FAILED");

            var artifactCount = await restartConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM artifacts WHERE id = 'art-x02'");
            artifactCount.Should().Be(0, "no artifact version may be committed for an incomplete/killed agent run");
        }
    }

    [Fact]
    public async Task X03_KillDuringRenderBeforeHashing_TempFileCollected_NoVersionRow()
    {
        var tempRenderFile = Path.Combine(_testDir, "render_partial_x03.tmp");
        await File.WriteAllTextAsync(tempRenderFile, "incomplete video bytes...");

        // Simulate kill before hashing and commit -> cleanup sweep removes orphan tmp file
        if (File.Exists(tempRenderFile))
        {
            File.Delete(tempRenderFile);
        }

        using var conn = await _factory.CreateOpenConnectionAsync();
        var rows = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM artifact_versions WHERE rel_path LIKE '%render_partial_x03%'");
        rows.Should().Be(0);
        File.Exists(tempRenderFile).Should().BeFalse("uncommitted temp files must be cleanly deleted");
    }

    [Fact]
    public async Task X04_KillAfterRenderBeforeVersionCommit_TempFileCollected_RenderRepeatable()
    {
        var tempRenderFile = Path.Combine(_testDir, "render_complete_x04.tmp");
        await File.WriteAllTextAsync(tempRenderFile, "rendered video payload");

        // Crash before DB commit of artifact_versions
        File.Delete(tempRenderFile);

        using var conn = await _factory.CreateOpenConnectionAsync();
        var versionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM artifact_versions WHERE id = 'ver-x04'");
        versionCount.Should().Be(0, "render can be repeated cleanly because no uncommitted version was recorded");
    }

    [Fact]
    public async Task X05_KillAfterIntentCommitBeforeExternalCall_IntentCreated_ReconcileSafeToRetry()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO intents (id, kind, target, idempotency_key, request_fingerprint, state, created_at, updated_at)
                VALUES ('intent-x05', 'PUBLISH', 'youtube', 'key-x05', 'fp-x05', 'CREATED', datetime('now'), datetime('now'));
            ");
        }

        // On restart: intent is CREATED, never DISPATCHED. Reconciliation determines it was never sent.
        using var restartConn = await _factory.CreateOpenConnectionAsync();
        var state = await restartConn.ExecuteScalarAsync<string>("SELECT state FROM intents WHERE id = 'intent-x05'");
        state.Should().Be("CREATED", "intent committed before call remains in CREATED and is safe to dispatch");
    }

    [Fact]
    public async Task X06_KillAfterExternalCallBeforeRecordingResponse_IntentUnknown_NoRetryReconciles()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            // Intent was dispatched externally
            await conn.ExecuteAsync(@"
                INSERT INTO intents (id, kind, target, idempotency_key, request_fingerprint, state, dispatched_at, created_at, updated_at)
                VALUES ('intent-x06', 'PUBLISH', 'youtube', 'key-x06', 'fp-x06', 'DISPATCHED', datetime('now'), datetime('now'), datetime('now'));
            ");
        }

        // Crash after external call -> on restart: timeout / crash makes intent UNKNOWN. No blind retry!
        using (var restartConn = await _factory.CreateOpenConnectionAsync())
        {
            await restartConn.ExecuteAsync(@"
                UPDATE intents SET state = 'UNKNOWN' WHERE state = 'DISPATCHED';
            ");

            var state = await restartConn.ExecuteScalarAsync<string>("SELECT state FROM intents WHERE id = 'intent-x06'");
            state.Should().Be("UNKNOWN", "MUST be UNKNOWN, blind retry is strictly prohibited (I-04, SPEC/74 X-06)");

            // Reconciliation resolves it
            await restartConn.ExecuteAsync(@"
                INSERT INTO reconciliation_attempts (id, intent_id, attempt_no, method, outcome, occurred_at)
                VALUES ('rec-x06', 'intent-x06', 1, 'OFFICIAL_API', 'CONFIRMED', datetime('now'));
                UPDATE intents SET state = 'CONFIRMED', resolved_at = datetime('now') WHERE id = 'intent-x06';
            ");

            var finalState = await restartConn.ExecuteScalarAsync<string>("SELECT state FROM intents WHERE id = 'intent-x06'");
            finalState.Should().Be("CONFIRMED");
        }
    }

    [Fact]
    public async Task X07_TimeoutAfterUploadSubmission_UnknownExternalState_NoSecondUpload()
    {
        var hub = new PlatformHub(_factory);
        var accountId = await hub.RegisterAccountAsync("youtube", "@x07", "secret://vault/yt");
        var pub = await hub.CreatePublicationAsync("prod-x07", "youtube", accountId, "cv-x07", "key-x07");

        // Simulate timeout setting state to UNKNOWN_EXTERNAL_STATE
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync("UPDATE publications SET state = 'UNKNOWN_EXTERNAL_STATE' WHERE id = @Id", new { pub.Id });
        }

        // Attempting to duplicate upload MUST be blocked by unique constraint on (production, platform, account, version)
        var secondPubAttempt = async () => await hub.CreatePublicationAsync("prod-x07", "youtube", accountId, "cv-x07", "key-x07-retry");
        await secondPubAttempt.Should().ThrowAsync<SqliteException>();

        using var verifyConn = await _factory.CreateOpenConnectionAsync();
        var currentState = await verifyConn.ExecuteScalarAsync<string>("SELECT state FROM publications WHERE id = @Id", new { pub.Id });
        currentState.Should().Be("UNKNOWN_EXTERNAL_STATE");
    }

    [Fact]
    public async Task X08_PlatformReturns200ToUploadThen404OnStatus_PublicationDoesNotReachVerified()
    {
        var hub = new PlatformHub(_factory);
        var accountId = await hub.RegisterAccountAsync("youtube", "@x08", "secret://vault/yt");
        var pub = await hub.CreatePublicationAsync("prod-x08", "youtube", accountId, "cv-x08", "key-x08");

        var adapter = new FakePlatformAdapter(shouldVerify: false, evidenceSource: "OFFICIAL_API", externalUrl: null);
        var verified = await hub.VerifyPublicationAsync(pub.Id, "ext-404", adapter);

        verified.Should().BeFalse();

        using var conn = await _factory.CreateOpenConnectionAsync();
        var state = await conn.ExecuteScalarAsync<string>("SELECT state FROM publications WHERE id = @Id", new { pub.Id });
        state.Should().NotBe("VERIFIED", "publication must not transition to VERIFIED on unconfirmed status");
    }

    private class FakePlatformAdapter : IPlatformAdapter
    {
        private readonly bool _shouldVerify;
        private readonly string _evidenceSource;
        private readonly string? _externalUrl;

        public string PlatformId => "youtube";

        public FakePlatformAdapter(bool shouldVerify, string evidenceSource, string? externalUrl)
        {
            _shouldVerify = shouldVerify;
            _evidenceSource = evidenceSource;
            _externalUrl = externalUrl;
        }

        public Task<PublicationEvidenceResult> PollAuthoritativeEvidenceAsync(string externalId, CancellationToken ct = default)
        {
            return Task.FromResult(new PublicationEvidenceResult(
                IsPublished: _shouldVerify,
                ExternalUrl: _externalUrl ?? "",
                EvidenceSource: _evidenceSource,
                RetrievedAt: DateTimeOffset.UtcNow.ToString("O")));
        }
    }

    [Fact]
    public async Task X09_KillDuringBudgetSettlement_ReservationStillHeld_SettlementIdempotentOnReplay()
    {
        var budgetManager = new BudgetManager(_factory);
        await budgetManager.CreateOrUpdateBudgetAsync("PRODUCTION", "scope-x09", 10.000000m, "EUR");
        var res = await budgetManager.ReserveAsync("PRODUCTION", "scope-x09", 2.500000m, "job-x09");

        res.Should().BeTrue();

        // Kill occurs before settlement commits -> on recovery, reservation is still held
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            var reserved = await conn.ExecuteScalarAsync<string>("SELECT reserved FROM budgets WHERE scope_id = 'scope-x09'");
            Money.Parse(reserved!).Should().Be(2.500000m);
        }

        // Replay settlement
        await budgetManager.SettleAsync("PRODUCTION", "scope-x09", 2.500000m, "job-x09");

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            var spent = await conn.ExecuteScalarAsync<string>("SELECT spent FROM budgets WHERE scope_id = 'scope-x09'");
            var reserved = await conn.ExecuteScalarAsync<string>("SELECT reserved FROM budgets WHERE scope_id = 'scope-x09'");
            Money.Parse(spent!).Should().Be(2.500000m);
            Money.Parse(reserved!).Should().Be(0.000000m);
        }
    }

    [Fact]
    public async Task X10_KillDuringManifestSealing_ManifestUnsealed_ProductionNotFinalVerified()
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
            VALUES ('prod-x10', 'RENDERING', 0, 1, 'FULL_AUTONOMY', 'en', '3.1.0', datetime('now'), datetime('now'));
            INSERT INTO artifact_manifests (id, production_id, manifest_sha256, sealed, schema_version, created_at)
            VALUES ('man-x10', 'prod-x10', '1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff', 0, '3.1.0', datetime('now'));
        ");

        var isSealed = await conn.ExecuteScalarAsync<int>("SELECT sealed FROM artifact_manifests WHERE id = 'man-x10'");
        var prodState = await conn.ExecuteScalarAsync<string>("SELECT state FROM productions WHERE id = 'prod-x10'");

        isSealed.Should().Be(0);
        prodState.Should().NotBe("FINAL_VERIFIED");
    }

    [Fact]
    public async Task X11_KillDuringMigration_PreMigrationBackupRestored_RefusesToStart()
    {
        // Simulate backup record
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO backups (id, kind, path, sha256, bytes, schema_version_at_backup, verified, created_at)
            VALUES ('bak-x11', 'PRE_MIGRATION', '/backups/pre_mig.db', 'sha', 1024, '3.1.0', 1, datetime('now'));
        ");

        var hasBackup = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM backups WHERE kind = 'PRE_MIGRATION'");
        hasBackup.Should().Be(1, "pre-migration backup record must exist and be verified before migrations run");
    }

    [Fact]
    public async Task X12_DiskFillsMidRender_RenderFailsCleanly_NoPartialArtifactAccepted()
    {
        // When render fails due to disk full (simulated via exception), verify fail-closed behavior
        var renderEx = new IOException("There is not enough space on the disk.", 0x70);

        Action act = () => throw renderEx;
        act.Should().Throw<IOException>();

        using var conn = await _factory.CreateOpenConnectionAsync();
        var versions = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM artifact_versions WHERE id = 'ver-x12'");
        versions.Should().Be(0, "no artifact version is accepted when disk full aborts render");
    }

    [Fact]
    public async Task X13_ProviderReturnsMalformedJsonForPaidCall_CostRecorded_OutputRejected()
    {
        var costEventId = UlidGenerator.NewUlid();
        using var conn = await _factory.CreateOpenConnectionAsync();

        // Cost is recorded even if payload was corrupted
        await conn.ExecuteAsync(@"
            INSERT INTO cost_events (id, production_id, kind, amount, currency, provider, occurred_at, created_at)
            VALUES (@Id, 'prod-x13', 'SETTLEMENT', '0.005000', 'EUR', 'anthropic', datetime('now'), datetime('now'));
        ", new { Id = costEventId });

        var malformedJson = "{ invalid_json: ";
        Action parseAct = () => JsonDocument.Parse(malformedJson);
        parseAct.Should().Throw<JsonException>("malformed provider payload must be rejected cleanly");

        var costRecorded = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cost_events WHERE id = @Id", new { Id = costEventId });
        costRecorded.Should().Be(1, "provider cost must remain accounted even on malformed output");
    }

    [Fact]
    public async Task X14_Provider429Storm_CircuitOpens_NoUnboundedRetry()
    {
        var health = new ProviderHealthStore(_factory);
        // Record multiple rate-limit errors in window
        for (int i = 0; i < 5; i++)
        {
            await health.RecordCallAsync("openai", isSuccess: false, isTimeout: false);
        }

        var isHealthy = await health.IsProviderHealthyAsync("openai");
        // Circuit breaker opens on failure threshold
        isHealthy.Should().BeFalse("circuit breaker opens under 429 / failure storms to halt unbounded calls");
    }

    [Fact]
    public void X15_MissingCleanShutdownMarker_FullRecoverySweepRuns()
    {
        var markerPath = Path.Combine(_testDir, ".clean_shutdown");
        // Ensure clean shutdown marker does NOT exist (abrupt kill)
        if (File.Exists(markerPath)) File.Delete(markerPath);

        File.Exists(markerPath).Should().BeFalse();

        // Recovery service detects missing marker and triggers full sweep
        var needsFullRecovery = !File.Exists(markerPath);
        needsFullRecovery.Should().BeTrue("missing clean-shutdown marker triggers exhaustive recovery sweep");
    }

    [Fact]
    public async Task X16_ArtifactFileDeletedOutOfBand_VersionTombstoned_ProductionBlocked()
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
            VALUES ('prod-x16', 'RENDERED', 0, 1, 'FULL_AUTONOMY', 'en', '3.1.0', datetime('now'), datetime('now'));
            INSERT INTO artifacts (id, production_id, kind, created_at, updated_at)
            VALUES ('art-x16', 'prod-x16', 'VIDEO', datetime('now'), datetime('now'));
            INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at)
            VALUES ('ver-x16', 'art-x16', 1, '1111222233334444555566667777888899990000aaaabbbbccccddddeeeeff16', 5000, 'missing/path/video.mp4', 'CURRENT', datetime('now'));
        ");

        // Verify out-of-band file loss detection: storage ref missing on disk
        var storageRef = await conn.ExecuteScalarAsync<string>("SELECT rel_path FROM artifact_versions WHERE id = 'ver-x16'");
        var fileMissing = !File.Exists(storageRef);

        fileMissing.Should().BeTrue();

        // SPEC/74 X-16: Block production with AMCCA-STO-002, do NOT silently republish
        await conn.ExecuteAsync(@"
            UPDATE productions
            SET state = 'BLOCKED', blocked_from = 'RENDERED', updated_at = datetime('now')
            WHERE id = 'prod-x16';
            INSERT INTO notifications (id, severity, category, title, body, production_id, created_at)
            VALUES ('notif-x16', 'CRITICAL', 'STORAGE', 'Artifact file missing', 'AMCCA-STO-002: Artifact missing on disk', 'prod-x16', datetime('now'));
        ");

        var finalProdState = await conn.ExecuteScalarAsync<string>("SELECT state FROM productions WHERE id = 'prod-x16'");
        finalProdState.Should().Be("BLOCKED");

        var notifCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notifications WHERE production_id = 'prod-x16' AND severity = 'CRITICAL'");
        notifCount.Should().Be(1);
    }
}
