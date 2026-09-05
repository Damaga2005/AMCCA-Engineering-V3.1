using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class DatabaseAndMigrationContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;

    public DatabaseAndMigrationContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_DB_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "amcca.db");
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
        catch
        {
            // Ignore cleanup failure in temp dir
        }
    }

    [Fact]
    public async Task DatabaseConnection_AssertsWalAndForeignKeys_Succeeds()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        using var connection = await factory.CreateOpenConnectionAsync();

        var journalMode = await factory.GetJournalModeAsync(connection);
        var foreignKeys = await factory.GetForeignKeysEnabledAsync(connection);

        journalMode.Should().BeEquivalentTo("wal");
        foreignKeys.Should().BeTrue();
    }

    [Fact]
    public async Task MigrationService_AppliesMigrationsInSequence_AndRecordsChecksums()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);

        var report = await migrationService.UpgradeAsync();

        report.AppliedCount.Should().BeGreaterThan(0);

        var applied = await migrationService.GetAppliedMigrationsAsync();
        applied.Should().NotBeEmpty();
        applied.First().Version.Should().Be(1);
        applied.First().Checksum.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MigrationService_WhenChecksumDiffersFromRecorded_AbortsWithDb002()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        // Corrupt the recorded migration checksum in the database
        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE schema_migrations SET checksum = 'tampered_hash_12345' WHERE version = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        // Running upgrade again with tampered recorded checksum must abort with AMCCA-DB-002
        var act = async () => await migrationService.UpgradeAsync();

        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Db002);
    }

    [Fact]
    public async Task MigrationService_Rollback_RevertsToTargetVersionSuccessfully()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        var rollbackReport = await migrationService.RollbackAsync(targetVersion: 0);

        rollbackReport.RolledBackCount.Should().BeGreaterThan(0);

        var appliedAfterRollback = await migrationService.GetAppliedMigrationsAsync();
        appliedAfterRollback.Should().BeEmpty();
    }

    [Fact]
    public async Task BackupService_CreatesAndVerifiesBackup_AndRestoreRecoversState()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        var backupService = new BackupService(factory);
        var backupPath = Path.Combine(_testDir, "pre_migration.bak");

        // 1. Create and verify backup
        var backupCreated = await backupService.CreateBackupAsync(backupPath);
        backupCreated.Should().BeTrue();

        var isVerified = await backupService.VerifyBackupAsync(backupPath);
        isVerified.Should().BeTrue();

        // 2. Corrupt or delete primary DB
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        File.Exists(_dbPath).Should().BeFalse();

        // 3. Restore backup
        await backupService.RestoreBackupAsync(backupPath, _dbPath);
        File.Exists(_dbPath).Should().BeTrue();

        // 4. Verify restored DB opens cleanly
        using var restoredConn = await factory.CreateOpenConnectionAsync();
        var integrity = await factory.CheckIntegrityAsync(restoredConn);
        integrity.Should().BeTrue();
    }

    [Fact]
    public async Task EventStore_AppendsEvents_AndEnforcesOptimisticConcurrency()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        var eventStore = new EventStore(factory);

        var event1 = new EventRecord(
            EventId: UlidGenerator.NewUlid(),
            EventType: "production.created",
            AggregateType: "production",
            AggregateId: "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            AggregateVersion: 1,
            CorrelationId: "corr-1",
            CausationId: null,
            TransitionId: "T-01",
            PayloadJson: "{\"title\":\"Test\"}",
            SchemaVersion: "3.1.0",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Seq: 1);

        await eventStore.AppendEventAsync(event1);

        // Attempting duplicate aggregate_version on same aggregate must violate UNIQUE constraint
        var duplicateVersionEvent = event1 with
        {
            EventId = UlidGenerator.NewUlid(),
            Seq = 2
        };

        var act = async () => await eventStore.AppendEventAsync(duplicateVersionEvent);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task AuditStore_AppendsAuditRecord_AndRejectsAgentActorType()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        var auditStore = new AuditStore(factory);

        var validAudit = new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "production.approve",
            ActorType: "OPERATOR", // Valid: OPERATOR, SCHEDULER, ORCHESTRATOR, RECONCILER or SYSTEM (audit.schema.json)
            ActorId: "operator-1",
            SubjectType: "production",
            SubjectId: "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            ProductionId: "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            Outcome: "ALLOWED",
            PolicyDecisionId: null,
            ReasonCode: null,
            CorrelationId: "corr-1",
            SchemaVersion: "3.1.0",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"));

        await auditStore.AppendAuditAsync(validAudit);

        // AGENTS.md: "audit_log.actor_type deliberately has no AGENT value. An agent is never the authority..."
        var agentAudit = validAudit with
        {
            AuditId = UlidGenerator.NewUlid(),
            ActorType = "AGENT"
        };

        var act = async () => await auditStore.AppendAuditAsync(agentAudit);

        (await act.Should().ThrowAsync<AmccaException>())
            .Where(e => e.ErrorCode == AmccaErrors.Sec001);
    }

    /// <summary>
    /// I-09 / SPEC/55: audit.schema.json enumerates OPERATOR, SCHEDULER, ORCHESTRATOR, RECONCILER and
    /// SYSTEM as legitimate actors -- everything except AGENT. Migration 4 widened the audit_log CHECK
    /// to match; before it, this table only accepted OPERATOR and SYSTEM, so an orchestrator or
    /// reconciler audit record failed at the database layer without ever reaching the AGENT check above.
    /// </summary>
    [Theory]
    [InlineData("OPERATOR")]
    [InlineData("SCHEDULER")]
    [InlineData("ORCHESTRATOR")]
    [InlineData("RECONCILER")]
    [InlineData("SYSTEM")]
    public async Task AuditStore_AcceptsEveryNonAgentActorTypeInTheContract(string actorType)
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        var auditStore = new AuditStore(factory);

        var audit = new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "system.event",
            ActorType: actorType,
            ActorId: "actor-1",
            SubjectType: null,
            SubjectId: null,
            ProductionId: null,
            Outcome: "ALLOWED",
            PolicyDecisionId: null,
            ReasonCode: null,
            CorrelationId: "corr-actor-types",
            SchemaVersion: "3.1.0",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"));

        var act = async () => await auditStore.AppendAuditAsync(audit);
        await act.Should().NotThrowAsync();

        var logs = await auditStore.GetAuditLogsAsync(correlationId: "corr-actor-types");
        logs.Should().ContainSingle(a => a.ActorType == actorType);
    }

    /// <summary>
    /// SPEC/07 / tool-run.schema.json bound side_effect_class to five values, but the DDL left the column
    /// unconstrained: a mistyped value evaded 'EXTERNAL_UNSAFE requires an intent_id' instead of being
    /// rejected by it, the structural defence failing open. Migration 4 closed the domain.
    /// </summary>
    [Fact]
    public async Task Migration004_RejectsInvalidToolRunSideEffectClass()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        using var connection = await factory.CreateOpenConnectionAsync();
        var insert = () => connection.ExecuteAsync(@"
            INSERT INTO tool_runs (
                run_id, tool_id, tool_version, side_effect_class, state,
                input_hash, correlation_id, schema_version, started_at
            ) VALUES (
                @RunId, 'tool-1', 'v1', 'external_unsafe', 'STARTED',
                'hash', 'corr-1', '3.1.0', @Now
            );
        ", new { RunId = UlidGenerator.NewUlid(), Now = DateTimeOffset.UtcNow.ToString("O") });

        // Lowercase mirrors the exact enum value with the wrong case -- the case this defence must catch,
        // since a value this close to correct is the one a hand-written call site is most likely to send.
        var act = async () => await insert();
        await act.Should().ThrowAsync<SqliteException>();
    }

    /// <summary>
    /// DEF-001/DEF-003: an installation that had the kill switch engaged through the old
    /// settings['kill_switch.global'] path must not silently lose that fact when it upgrades to the
    /// version that reads kill_switch_state instead. Migration 5 is that carry-over.
    ///
    /// The scenario is built with only the public API: apply every migration, roll back just the last
    /// one (settings and kill_switch_state both predate it, from migration 1, so they survive), insert
    /// the legacy row the old SettingsViewModel code used to write, then upgrade again so migration 5
    /// runs against a database that genuinely has that row -- not a hand-constructed shortcut.
    /// </summary>
    [Theory]
    [InlineData(true, "EMERGENCY_STOP")]
    [InlineData(false, "NORMAL")]
    public async Task Migration005_CarriesLegacySettingsKillSwitchIntoKillSwitchState(bool legacyActive, string expectedMode)
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);

        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 4);

        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync(@"
                INSERT INTO settings (key, value_json, schema_version, updated_by, updated_at)
                VALUES ('kill_switch.global', @ValueJson, '3.1.0', 'legacy-operator', '2026-01-01T00:00:00Z');
            ", new { ValueJson = legacyActive ? "{\"active\":true}" : "{\"active\":false}" });
        }

        await migrationService.UpgradeAsync();

        using var verifyConnection = await factory.CreateOpenConnectionAsync();
        var mode = await verifyConnection.ExecuteScalarAsync<string>(
            "SELECT mode FROM kill_switch_state WHERE id = 1;");
        mode.Should().Be(expectedMode);

        var remainingLegacyKeys = await verifyConnection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM settings WHERE key = 'kill_switch.global';");
        remainingLegacyKeys.Should().Be(0, "the old key must not linger once its value has been carried over");
    }

    [Fact]
    public async Task Migration005_OnFreshInstallWithNoLegacyKey_CreatesNoKillSwitchStateRow()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);

        await migrationService.UpgradeAsync();

        using var connection = await factory.CreateOpenConnectionAsync();
        var rowCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM kill_switch_state;");
        rowCount.Should().Be(0);
    }

    [Fact]
    public async Task Migration005_NeverOverwritesAKillSwitchStateRowThatAlreadyExists()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);

        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 4);

        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync(@"
                INSERT INTO settings (key, value_json, schema_version, updated_by, updated_at)
                VALUES ('kill_switch.global', '{""active"":true}', '3.1.0', 'legacy-operator', '2026-01-01T00:00:00Z');
            ");
            // Simulates a row already written through the new path before migration 5 ever runs.
            await connection.ExecuteAsync(@"
                INSERT INTO kill_switch_state (id, mode, cleared_at, cleared_by)
                VALUES (1, 'NORMAL', '2026-02-01T00:00:00Z', 'new-operator');
            ");
        }

        await migrationService.UpgradeAsync();

        using var verifyConnection = await factory.CreateOpenConnectionAsync();
        var row = await verifyConnection.QuerySingleAsync<(string Mode, string? ClearedBy)>(
            "SELECT mode AS Mode, cleared_by AS ClearedBy FROM kill_switch_state WHERE id = 1;");
        row.Mode.Should().Be("NORMAL");
        row.ClearedBy.Should().Be("new-operator", "a decision already made through the new path must never be overwritten by the legacy carry-over");
    }

    /// <summary>
    /// job.schema.json has always enumerated SUCCEEDED as the terminal success state; JobManager wrote
    /// COMPLETED instead (fourth audit, section 2.3). Migration 6 renames the value on rows written
    /// before the code was corrected, so a job that already finished successfully does not read as if
    /// it silently reverted to never having completed.
    /// </summary>
    [Fact]
    public async Task Migration006_RenamesLegacyCompletedJobsToSucceeded()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);

        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 5);

        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync(@"
                INSERT INTO jobs (id, type, state, priority, idempotency_key, attempt, max_attempts, payload_json, created_at, updated_at)
                VALUES ('job-legacy-completed', 'RENDER', 'COMPLETED', 3, 'idem-legacy-1', 1, 3, '{}', datetime('now'), datetime('now'));
            ");
            // A job in some other state must be left alone by a migration scoped to exactly one value.
            await connection.ExecuteAsync(@"
                INSERT INTO jobs (id, type, state, priority, idempotency_key, attempt, max_attempts, payload_json, created_at, updated_at)
                VALUES ('job-still-queued', 'RENDER', 'QUEUED', 3, 'idem-legacy-2', 0, 3, '{}', datetime('now'), datetime('now'));
            ");
        }

        await migrationService.UpgradeAsync();

        using var verifyConnection = await factory.CreateOpenConnectionAsync();
        var renamedState = await verifyConnection.ExecuteScalarAsync<string>(
            "SELECT state FROM jobs WHERE id = 'job-legacy-completed';");
        renamedState.Should().Be("SUCCEEDED");

        var untouchedState = await verifyConnection.ExecuteScalarAsync<string>(
            "SELECT state FROM jobs WHERE id = 'job-still-queued';");
        untouchedState.Should().Be("QUEUED");
    }

    /// <summary>
    /// analytics.schema.json has always declared the optional source_account_id, but the column never
    /// existed (fourth audit section 2.2, and the contracts.fields_have_columns gate check it inspired).
    /// Migration 7 adds it, FK-checked against platform_accounts. No code writes analytics_snapshots
    /// rows yet, so this asserts the column round-trips and the FK actually rejects a bogus id --
    /// exercised via a raw connection like the rest of this contract, the same way the existing
    /// analytics_snapshots tests in MemoryGenomeExperimentContractTests already do for its other columns.
    /// </summary>
    [Fact]
    public async Task Migration007_AddsAnalyticsSourceAccountColumn_FkEnforced()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        using var connection = await factory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(@"
            INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
            VALUES ('prod-src-acct', 'PUBLISHED', 0, 1, 'FULL_AUTONOMY', 'en', '3.1.0', datetime('now'), datetime('now'));
            INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, schema_version, created_at, updated_at)
            VALUES ('pub-src-acct', 'prod-src-acct', 'youtube', 'acc-x', 'ver-x', 'PUBLISHED', 'idem-src-acct', '3.1.0', datetime('now'), datetime('now'));
            INSERT INTO platform_accounts (id, platform, account_handle, credential_secret_ref, state, created_at, updated_at)
            VALUES ('platacct-1', 'youtube', '@handle', 'secret://vault/x', 'CONNECTED', datetime('now'), datetime('now'));
        ");

        await connection.ExecuteAsync(@"
            INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at, source_account_id)
            VALUES ('snap-with-source', 'prod-src-acct', 'pub-src-acct', 'views', 100, 'API_MEASURED', '3.1.0', datetime('now'), 'platacct-1');
        ");

        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT source_account_id FROM analytics_snapshots WHERE id = 'snap-with-source';");
        stored.Should().Be("platacct-1");

        // A snapshot whose provenance doesn't trace to a real platform account is a bug, not a valid row.
        var act = async () => await connection.ExecuteAsync(@"
            INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at, source_account_id)
            VALUES ('snap-bad-source', 'prod-src-acct', 'pub-src-acct', 'views', 50, 'API_MEASURED', '3.1.0', datetime('now'), 'no-such-account');
        ");
        await act.Should().ThrowAsync<SqliteException>("source_account_id is FK-checked against platform_accounts");

        // The field is optional (analytics.schema.json: oneOf string/null) -- a snapshot with no known
        // source account must still be insertable.
        await connection.ExecuteAsync(@"
            INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at)
            VALUES ('snap-no-source', 'prod-src-acct', 'pub-src-acct', 'views', 75, 'API_MEASURED', '3.1.0', datetime('now'));
        ");
        var nullStored = await connection.ExecuteScalarAsync<string?>(
            "SELECT source_account_id FROM analytics_snapshots WHERE id = 'snap-no-source';");
        nullStored.Should().BeNull();
    }

    /// <summary>
    /// Rolling migration 7 back must leave the table in exactly its pre-migration shape -- no orphaned
    /// column, and (implicitly, since the ADD COLUMN target no longer exists) no way to write the field
    /// this migration introduced.
    /// </summary>
    [Fact]
    public async Task Migration007_RollbackRemovesTheSourceAccountColumn()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 6);

        using var connection = await factory.CreateOpenConnectionAsync();
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('analytics_snapshots');")).ToList();
        columns.Should().NotContain("source_account_id");
    }
}
