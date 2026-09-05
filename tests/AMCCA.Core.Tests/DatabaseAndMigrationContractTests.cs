using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
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
}
