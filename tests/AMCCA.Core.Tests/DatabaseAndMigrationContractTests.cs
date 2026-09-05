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
            VALUES ('prod-src-acct', 'PUBLICATION_VERIFIED', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
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

    /// <summary>
    /// D-004: "Every persisted contract object carries schema_version." job.schema.json and
    /// cost-event.schema.json both require it, and generate_artifacts.py's own SPEC/11 model for both
    /// tables already listed it, but neither table's real DDL ever had the column (fourth audit section
    /// 2.2, and 2.4's contracts.fields_have_columns gate check). Unlike migration 7's tables, jobs and
    /// cost_events have real writers, so this proves the DEFAULT '3.1.0' actually backfills a row written
    /// before the column existed -- not just that a fresh row can set it.
    /// </summary>
    [Fact]
    public async Task Migration008_BackfillsSchemaVersionOnPreExistingJobsAndCostEventsRows()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);

        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 7);

        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync(@"
                INSERT INTO jobs (id, type, state, priority, idempotency_key, attempt, max_attempts, payload_json, created_at, updated_at)
                VALUES ('job-pre-mig8', 'RENDER', 'QUEUED', 3, 'idem-pre-mig8', 0, 3, '{}', datetime('now'), datetime('now'));
                INSERT INTO cost_events (id, production_id, job_id, kind, amount, currency, provider, occurred_at, created_at)
                VALUES ('cost-pre-mig8', 'prod-pre-mig8', 'job-pre-mig8', 'RESERVATION', '1.000000', 'EUR', 'test-provider', datetime('now'), datetime('now'));
            ");
        }

        await migrationService.UpgradeAsync();

        using var verifyConnection = await factory.CreateOpenConnectionAsync();
        var jobSchemaVersion = await verifyConnection.ExecuteScalarAsync<string>(
            "SELECT schema_version FROM jobs WHERE id = 'job-pre-mig8';");
        jobSchemaVersion.Should().Be("3.1.0", "a row written before this migration must be backfilled, not left NULL");

        var costSchemaVersion = await verifyConnection.ExecuteScalarAsync<string>(
            "SELECT schema_version FROM cost_events WHERE id = 'cost-pre-mig8';");
        costSchemaVersion.Should().Be("3.1.0");
    }

    [Fact]
    public async Task Migration008_RollbackRemovesSchemaVersionFromJobsAndCostEvents()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 7);

        using var connection = await factory.CreateOpenConnectionAsync();
        var jobColumns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('jobs');")).ToList();
        jobColumns.Should().NotContain("schema_version");

        var costColumns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('cost_events');")).ToList();
        costColumns.Should().NotContain("schema_version");
    }

    /// <summary>
    /// Closes the last 13 contract-fields-with-no-column entries from the fourth audit (section 2.2):
    /// cost_events gained agent_run_id, model_id, provider_request_id, budget_id, pricing_snapshot_id and
    /// reconciliation_state; jobs gained causation_id, currency, deadline_at, estimated_cost,
    /// reserved_cost, last_error_code and scheduled_at. This proves every column exists, the
    /// reconciliation_state CHECK and DEFAULT both hold, and the money/FK constraints on the new columns
    /// actually reject bad data rather than merely existing.
    /// </summary>
    [Fact]
    public async Task Migration009_AddsRemainingCostEventAndJobColumns_WithWorkingChecksAndFks()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 8);

        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync(@"
                INSERT INTO jobs (id, type, state, priority, idempotency_key, attempt, max_attempts, payload_json, created_at, updated_at, schema_version)
                VALUES ('job-pre-mig9', 'RENDER', 'QUEUED', 3, 'idem-pre-mig9', 0, 3, '{}', datetime('now'), datetime('now'), '3.1.0');
                INSERT INTO cost_events (id, production_id, job_id, kind, amount, currency, provider, occurred_at, created_at, schema_version)
                VALUES ('cost-pre-mig9', 'prod-pre-mig9', 'job-pre-mig9', 'RESERVATION', '1.000000', 'EUR', 'test-provider', datetime('now'), datetime('now'), '3.1.0');
            ");
        }

        await migrationService.UpgradeAsync();

        using var connection2 = await factory.CreateOpenConnectionAsync();

        // A row written before migration 9 must be backfilled to ESTIMATED, never left NULL.
        var backfilledState = await connection2.ExecuteScalarAsync<string>(
            "SELECT reconciliation_state FROM cost_events WHERE id = 'cost-pre-mig9';");
        backfilledState.Should().Be("ESTIMATED");

        // The CHECK constraint on reconciliation_state actually rejects an invalid value.
        var actBadReconciliation = async () => await connection2.ExecuteAsync(@"
            INSERT INTO cost_events (id, production_id, job_id, kind, amount, currency, provider, occurred_at, created_at, schema_version, reconciliation_state)
            VALUES ('cost-bad-reconciliation', 'prod-x', NULL, 'RESERVATION', '1.000000', 'EUR', 'prov', datetime('now'), datetime('now'), '3.1.0', 'BOGUS');
        ");
        await actBadReconciliation.Should().ThrowAsync<SqliteException>();

        // estimated_cost/reserved_cost keep the same non-negative CHECK the rest of this codebase uses for money.
        var actNegativeCost = async () => await connection2.ExecuteAsync(@"
            INSERT INTO jobs (id, type, state, priority, idempotency_key, attempt, max_attempts, payload_json, created_at, updated_at, schema_version, estimated_cost)
            VALUES ('job-negative-cost', 'RENDER', 'QUEUED', 3, 'idem-negative-cost', 0, 3, '{}', datetime('now'), datetime('now'), '3.1.0', '-1.000000');
        ");
        await actNegativeCost.Should().ThrowAsync<SqliteException>();

        // agent_run_id is FK-checked against agent_runs.run_id (not the usual `id`), and rejects a bogus reference.
        var actBadAgentRun = async () => await connection2.ExecuteAsync(@"
            INSERT INTO cost_events (id, production_id, job_id, kind, amount, currency, provider, occurred_at, created_at, schema_version, agent_run_id)
            VALUES ('cost-bad-agent-run', 'prod-x', NULL, 'RESERVATION', '1.000000', 'EUR', 'prov', datetime('now'), datetime('now'), '3.1.0', 'no-such-run');
        ");
        await actBadAgentRun.Should().ThrowAsync<SqliteException>();

        // pricing_snapshot_id is deliberately nullable (not the NOT NULL the contract declares): no
        // pipeline populates pricing_snapshots yet, so a row with none must still be insertable.
        await connection2.ExecuteAsync(@"
            INSERT INTO cost_events (id, production_id, job_id, kind, amount, currency, provider, occurred_at, created_at, schema_version)
            VALUES ('cost-no-pricing-snapshot', 'prod-x', NULL, 'RESERVATION', '1.000000', 'EUR', 'prov', datetime('now'), datetime('now'), '3.1.0');
        ");
        var nullPricingSnapshot = await connection2.ExecuteScalarAsync<string?>(
            "SELECT pricing_snapshot_id FROM cost_events WHERE id = 'cost-no-pricing-snapshot';");
        nullPricingSnapshot.Should().BeNull();
    }

    [Fact]
    public async Task Migration009_RollbackRemovesAllThirteenColumns()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 8);

        using var connection = await factory.CreateOpenConnectionAsync();
        var costColumns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('cost_events');")).ToList();
        foreach (var col in new[] { "agent_run_id", "model_id", "provider_request_id", "budget_id", "pricing_snapshot_id", "reconciliation_state" })
        {
            costColumns.Should().NotContain(col);
        }

        var jobColumns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('jobs');")).ToList();
        foreach (var col in new[] { "causation_id", "currency", "deadline_at", "estimated_cost", "reserved_cost", "last_error_code", "scheduled_at" })
        {
            jobColumns.Should().NotContain(col);
        }
    }

    /// <summary>
    /// contracts.enum_matches_ddl_check (fourth audit): 17 columns across 13 tables had a JSON-contract
    /// enum with no matching DDL CHECK, so a bug anywhere upstream of an INSERT could silently write a
    /// value the contract itself forbids (D-026). Five of those tables -- agent_runs, jobs, productions,
    /// publications, qa_reports -- are FK targets from other live tables, so this migration cannot use
    /// the ordinary DROP+CREATE-under-original-name rebuild: it runs with FK enforcement off for its
    /// duration (MigrationService.MigrationsRequiringForeignKeysOff), the one pattern verified against
    /// real SQLite not to hit a phantom deferred-FK COMMIT failure. This test seeds one row per touched
    /// table with data that predates the migration, upgrades through it, and checks both that every row
    /// survived (including through the FK web productions/agent_runs/publications/qa_reports sit at the
    /// center of) and that each new CHECK actually rejects a value the contract disallows.
    /// </summary>
    [Fact]
    public async Task Migration010_AddsEnumCheckConstraints_DataSurvivesAndChecksReject()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();
        await migrationService.RollbackAsync(targetVersion: 9);

        var sha = new string('a', 64);
        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync($@"
                INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
                VALUES ('prod-mig10', 'INIT', 0, 0, 'MANUAL', 'en', '3.1.0', datetime('now'), datetime('now'));

                INSERT INTO prompt_templates (id, key, purpose, created_at, updated_at)
                VALUES ('pt-mig10', 'key-mig10', 'purpose-mig10', datetime('now'), datetime('now'));
                INSERT INTO prompt_versions (id, template_id, version_no, body_sha256, body_ref, created_at)
                VALUES ('pv-mig10', 'pt-mig10', 1, '{sha}', 'ref-mig10', datetime('now'));
                INSERT INTO agent_runs (run_id, production_id, agent_id, agent_version, prompt_version_id, model_id, model_params_hash, state, input_hash, correlation_id, schema_version, started_at)
                VALUES ('run-mig10', 'prod-mig10', 'agent-1', '1.0', 'pv-mig10', 'model-1', 'h1', 'STARTED', 'ih1', 'corr-mig10', '3.1.0', datetime('now'));

                INSERT INTO jobs (id, production_id, type, state, payload_json, created_at, updated_at, schema_version)
                VALUES ('job-mig10', 'prod-mig10', 'RENDER', 'QUEUED', '{{}}', datetime('now'), datetime('now'), '3.1.0');

                INSERT INTO platform_accounts (id, platform, account_handle, credential_secret_ref, state, created_at, updated_at)
                VALUES ('acct-mig10', 'youtube', '@h', 'secret://vault/x', 'CONNECTED', datetime('now'), datetime('now'));
                INSERT INTO synthetic_declarations (id) VALUES ('sd-mig10');
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, synthetic_declaration_id, platform_label_required, state, idempotency_key, schema_version, created_at, updated_at)
                VALUES ('pub-mig10', 'prod-mig10', 'youtube', 'acct-mig10', 'cv-mig10', 'sd-mig10', 1, 'INTENT_CREATED', 'idem-mig10', '3.1.0', datetime('now'), datetime('now'));

                INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at)
                VALUES ('as-mig10', 'prod-mig10', 'pub-mig10', 'views', 1, 'API_MEASURED', '3.1.0', datetime('now'));

                INSERT INTO audit_log (audit_id, action, actor_type, actor_id, outcome, correlation_id, schema_version, occurred_at)
                VALUES ('al-mig10', 'some_action', 'OPERATOR', 'op-1', 'ALLOWED', 'corr-mig10', '3.1.0', datetime('now'));

                INSERT INTO claims (id, production_id, text, status, materiality, subject_class, schema_version, created_at)
                VALUES ('cl-mig10', 'prod-mig10', 'claim text', 'VERIFIED', 'MATERIAL', 'GENERAL', '3.1.0', datetime('now'));

                INSERT INTO cost_events (id, production_id, kind, amount, currency, provider, occurred_at, created_at, schema_version, reconciliation_state)
                VALUES ('ce-mig10', 'prod-mig10', 'SETTLEMENT', '10.00', 'EUR', 'openai', datetime('now'), datetime('now'), '3.1.0', 'ESTIMATED');

                INSERT INTO events (event_id, event_type, aggregate_type, aggregate_id, aggregate_version, correlation_id, payload_json, schema_version, occurred_at, seq)
                VALUES ('ev-mig10', 'created', 'production', 'prod-mig10', 1, 'corr-mig10', '{{}}', '3.1.0', datetime('now'), 1);

                INSERT INTO artifacts (id, production_id, kind, created_at, updated_at) VALUES ('art-mig10', 'prod-mig10', 'video', datetime('now'), datetime('now'));
                INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at)
                VALUES ('av-mig10', 'art-mig10', 1, '{sha}', 10, 'p.mp4', 'CURRENT', datetime('now'));
                INSERT INTO qa_reports (report_id, production_id, artifact_version_id, stage, overall_score, critical_scores_json, verdict, threshold_profile_id, schema_version, evaluated_at)
                VALUES ('qr-mig10', 'prod-mig10', 'av-mig10', 'TECHNICAL_QA', 0.9, '{{}}', 'PASS', 'default', '3.1.0', datetime('now'));

                INSERT INTO referral_programs (id, brand, program, state, disclosure_required, created_at, updated_at)
                VALUES ('rp-mig10', 'brand', 'program', 'ACTIVE', 1, datetime('now'), datetime('now'));
                INSERT INTO referral_links (id, program_id, production_id, state, validation_method, validated_at, geo_json, platform_json, schema_version, created_at, updated_at)
                VALUES ('rl-mig10', 'rp-mig10', 'prod-mig10', 'ACTIVE', 'OFFICIAL_API', datetime('now'), '{{}}', '{{}}', '3.1.0', datetime('now'), datetime('now'));

                INSERT INTO rights_records (id, production_id, asset_hash, status, license, provenance, commercial_use, modification, attribution_required, restrictions_json, schema_version, evaluated_at)
                VALUES ('rr-mig10', 'prod-mig10', '{sha}', 'GREEN', 'CC0', 'GENERATED', 'ALLOWED', 'ALLOWED', 0, '{{}}', '3.1.0', datetime('now'));

                INSERT INTO tool_runs (run_id, production_id, job_id, agent_run_id, tool_id, tool_version, side_effect_class, state, input_hash, correlation_id, schema_version, started_at)
                VALUES ('tr-mig10', 'prod-mig10', 'job-mig10', 'run-mig10', 'tool-1', '1.0', 'PURE', 'STARTED', 'ih1', 'corr-mig10', '3.1.0', datetime('now'));
            ");
        }

        await migrationService.UpgradeAsync();

        using var connection2 = await factory.CreateOpenConnectionAsync();

        // Every row seeded on the pre-migration-10 (unconstrained) schema survives the rebuild --
        // including the ones sitting inside the productions/agent_runs/publications/qa_reports FK web.
        var survivalChecks = new (string Table, string Column, string Id)[]
        {
            ("productions", "id", "prod-mig10"),
            ("agent_runs", "run_id", "run-mig10"),
            ("jobs", "id", "job-mig10"),
            ("publications", "id", "pub-mig10"),
            ("qa_reports", "report_id", "qr-mig10"),
            ("analytics_snapshots", "id", "as-mig10"),
            ("audit_log", "audit_id", "al-mig10"),
            ("claims", "id", "cl-mig10"),
            ("cost_events", "id", "ce-mig10"),
            ("events", "event_id", "ev-mig10"),
            ("referral_links", "id", "rl-mig10"),
            ("rights_records", "id", "rr-mig10"),
            ("tool_runs", "run_id", "tr-mig10"),
        };
        foreach (var (table, column, id) in survivalChecks)
        {
            var count = await connection2.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table} WHERE {column} = @Id;", new { Id = id });
            count.Should().Be(1, $"row {id} in {table} must survive migration 10's rebuild");
        }

        var fkViolations = (await connection2.QueryAsync("PRAGMA foreign_key_check;")).AsList();
        fkViolations.Should().BeEmpty("migration 10 must leave the database with zero foreign key violations");

        var foreignKeysOn = await connection2.ExecuteScalarAsync<long>("PRAGMA foreign_keys;");
        foreignKeysOn.Should().Be(1, "migration 10 must restore foreign_keys = ON before returning control");

        // The two real DIVERGES the fourth audit found: cost_events.kind's DDL allowed the legacy
        // REFUND value the contract never had, and publications.state's DDL allowed several legacy
        // values (including QUEUED, which PlatformHub used to write) never in the contract's 10-value
        // domain. Both writes pre-date this test's seed rows, so this proves the CHECK is real, not
        // just present in the schema text.
        var actBadCostKind = async () => await connection2.ExecuteAsync(
            "UPDATE cost_events SET kind = 'REFUND' WHERE id = 'ce-mig10';");
        await actBadCostKind.Should().ThrowAsync<SqliteException>();

        var actBadPublicationState = async () => await connection2.ExecuteAsync(
            "UPDATE publications SET state = 'QUEUED' WHERE id = 'pub-mig10';");
        await actBadPublicationState.Should().ThrowAsync<SqliteException>();

        // A representative sample of the other 15 new CHECKs, one per remaining table, confirms this
        // isn't limited to the two DIVERGES cases above.
        var actBadAgentRunState = async () => await connection2.ExecuteAsync(
            "UPDATE agent_runs SET state = 'RUNNING' WHERE run_id = 'run-mig10';");
        await actBadAgentRunState.Should().ThrowAsync<SqliteException>("RUNNING was never in agent-run.schema.json's enum -- STARTED is");

        var actBadQaStage = async () => await connection2.ExecuteAsync(
            "UPDATE qa_reports SET stage = 'SCRIPT_QA' WHERE report_id = 'qr-mig10';");
        await actBadQaStage.Should().ThrowAsync<SqliteException>();

        var actBadAuditOutcome = async () => await connection2.ExecuteAsync(
            "UPDATE audit_log SET outcome = 'COMMITTED' WHERE audit_id = 'al-mig10';");
        await actBadAuditOutcome.Should().ThrowAsync<SqliteException>("COMMITTED was never in audit.schema.json's enum -- APPROVED is");

        var actBadClaimSubjectClass = async () => await connection2.ExecuteAsync(
            "UPDATE claims SET subject_class = 'HISTORY' WHERE id = 'cl-mig10';");
        await actBadClaimSubjectClass.Should().ThrowAsync<SqliteException>();

        var actBadProductionState = async () => await connection2.ExecuteAsync(
            "UPDATE productions SET state = 'BOGUS_STATE' WHERE id = 'prod-mig10';");
        await actBadProductionState.Should().ThrowAsync<SqliteException>();

        var actBadAutonomyMode = async () => await connection2.ExecuteAsync(
            "UPDATE productions SET autonomy_mode = 'FULL_AUTONOMY' WHERE id = 'prod-mig10';");
        await actBadAutonomyMode.Should().ThrowAsync<SqliteException>();

        var actBadJobState = async () => await connection2.ExecuteAsync(
            "UPDATE jobs SET state = 'BOGUS_STATE' WHERE id = 'job-mig10';");
        await actBadJobState.Should().ThrowAsync<SqliteException>();

        var actBadRightsProvenance = async () => await connection2.ExecuteAsync(
            "UPDATE rights_records SET provenance = 'BOGUS' WHERE id = 'rr-mig10';");
        await actBadRightsProvenance.Should().ThrowAsync<SqliteException>();

        var actBadReferralMethod = async () => await connection2.ExecuteAsync(
            "UPDATE referral_links SET validation_method = 'BOGUS' WHERE id = 'rl-mig10';");
        await actBadReferralMethod.Should().ThrowAsync<SqliteException>();

        var actBadToolRunState = async () => await connection2.ExecuteAsync(
            "UPDATE tool_runs SET state = 'BOGUS' WHERE run_id = 'tr-mig10';");
        await actBadToolRunState.Should().ThrowAsync<SqliteException>();

        var actBadAnalyticsProvenance = async () => await connection2.ExecuteAsync(
            "UPDATE analytics_snapshots SET provenance = 'BOGUS' WHERE id = 'as-mig10';");
        await actBadAnalyticsProvenance.Should().ThrowAsync<SqliteException>();

        var actBadEventsAggregateType = async () => await connection2.ExecuteAsync(
            "UPDATE events SET aggregate_type = 'BOGUS' WHERE event_id = 'ev-mig10';");
        await actBadEventsAggregateType.Should().ThrowAsync<SqliteException>();

        // The append-only triggers on audit_log/events (dropped and recreated as part of this
        // migration's rebuild of those two tables) must still be armed afterward.
        var actAuditUpdate = async () => await connection2.ExecuteAsync(
            "UPDATE audit_log SET action = 'x' WHERE audit_id = 'al-mig10';");
        (await actAuditUpdate.Should().ThrowAsync<SqliteException>()).Which.Message.Should().Contain("append-only");

        var actEventsDelete = async () => await connection2.ExecuteAsync(
            "DELETE FROM events WHERE event_id = 'ev-mig10';");
        (await actEventsDelete.Should().ThrowAsync<SqliteException>()).Which.Message.Should().Contain("append-only");
    }

    /// <summary>
    /// Rolling migration 10 back must restore every touched column to its pre-migration (unconstrained,
    /// or legacy-domain for the two real DIVERGES) shape, with all data intact -- proving the DownSql,
    /// which runs through the same FK-enforcement-off path as the UpSql, is not a one-way door.
    /// </summary>
    [Fact]
    public async Task Migration010_RollbackRestoresUnconstrainedColumnsAndLegacyDomains()
    {
        var factory = new DatabaseConnectionFactory(_dbPath);
        var migrationService = new MigrationService(factory, _testDir);
        await migrationService.UpgradeAsync();

        var sha = new string('a', 64);
        using (var connection = await factory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync($@"
                INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
                VALUES ('prod-mig10-rb', 'INIT', 0, 0, 'MANUAL', 'en', '3.1.0', datetime('now'), datetime('now'));
                INSERT INTO cost_events (id, production_id, kind, amount, currency, provider, occurred_at, created_at, schema_version, reconciliation_state)
                VALUES ('ce-mig10-rb', 'prod-mig10-rb', 'SETTLEMENT', '10.00', 'EUR', 'openai', datetime('now'), datetime('now'), '3.1.0', 'ESTIMATED');
            ");
        }

        await migrationService.RollbackAsync(targetVersion: 9);

        using var connection2 = await factory.CreateOpenConnectionAsync();

        var survivingProduction = await connection2.ExecuteScalarAsync<string>(
            "SELECT id FROM productions WHERE id = 'prod-mig10-rb';");
        survivingProduction.Should().Be("prod-mig10-rb");

        var survivingCostEvent = await connection2.ExecuteScalarAsync<string>(
            "SELECT id FROM cost_events WHERE id = 'ce-mig10-rb';");
        survivingCostEvent.Should().Be("ce-mig10-rb");

        var fkViolations = (await connection2.QueryAsync("PRAGMA foreign_key_check;")).AsList();
        fkViolations.Should().BeEmpty("rolling migration 10 back must also leave zero foreign key violations");

        var foreignKeysOn = await connection2.ExecuteScalarAsync<long>("PRAGMA foreign_keys;");
        foreignKeysOn.Should().Be(1, "migration 10's rollback must restore foreign_keys = ON before returning control");

        // The legacy value cost_events.kind's CHECK forbade after migration 10 must be accepted again.
        await connection2.ExecuteAsync("UPDATE cost_events SET kind = 'REFUND' WHERE id = 'ce-mig10-rb';");
        var revertedKind = await connection2.ExecuteScalarAsync<string>(
            "SELECT kind FROM cost_events WHERE id = 'ce-mig10-rb';");
        revertedKind.Should().Be("REFUND");

        // A previously-CHECKed column (agent_runs.state) must go back to accepting anything.
        await connection2.ExecuteAsync(@"
            INSERT INTO prompt_templates (id, key, purpose, created_at, updated_at)
            VALUES ('pt-mig10-rb', 'key-rb', 'purpose-rb', datetime('now'), datetime('now'));
            INSERT INTO prompt_versions (id, template_id, version_no, body_sha256, body_ref, created_at)
            VALUES ('pv-mig10-rb', 'pt-mig10-rb', 1, @Sha, 'ref-rb', datetime('now'));
        ", new { Sha = sha });
        await connection2.ExecuteAsync(@"
            INSERT INTO agent_runs (run_id, production_id, agent_id, agent_version, prompt_version_id, model_id, model_params_hash, state, input_hash, correlation_id, schema_version, started_at)
            VALUES ('run-mig10-rb', 'prod-mig10-rb', 'agent-1', '1.0', 'pv-mig10-rb', 'model-1', 'h1', 'ANY_LEGACY_VALUE', 'ih1', 'corr-rb', '3.1.0', datetime('now'));
        ");
        var revertedAgentRunState = await connection2.ExecuteScalarAsync<string>(
            "SELECT state FROM agent_runs WHERE run_id = 'run-mig10-rb';");
        revertedAgentRunState.Should().Be("ANY_LEGACY_VALUE");
    }
}
