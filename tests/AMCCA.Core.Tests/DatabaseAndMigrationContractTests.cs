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
            ActorType: "OPERATOR", // Valid: OPERATOR or SYSTEM
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
}
