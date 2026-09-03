using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class CanonicalMigrationSchemaTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly MigrationService _migrator;

    public CanonicalMigrationSchemaTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_MIGRATIONS_DEF018_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "canonical_schema.db");
        _factory = new DatabaseConnectionFactory(_dbPath);
        _migrator = new MigrationService(_factory, _testDir);
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
        }
    }

    [Fact]
    public async Task DEF018_CleanDatabase_RunsAllMigrations_AndContainsAllCanonicalTablesFromTablesJson()
    {
        // 1. Upgrade from clean slate
        var applied = await _migrator.UpgradeAsync();
        applied.AppliedCount.Should().Be(3, "all 3 canonical migration scripts must be executed on a clean database");

        // 2. Query all user tables in SQLite
        using var connection = await _factory.CreateOpenConnectionAsync();
        const string queryTablesSql = @"
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name ASC;
        ";
        var existingTables = (await connection.QueryAsync<string>(queryTablesSql)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 3. Load expected canonical tables from SCHEMAS/tables.json
        // Look up workspace path relative to current domain
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var tablesJsonPath = Path.Combine(repoRoot, "SCHEMAS", "tables.json");

        if (File.Exists(tablesJsonPath))
        {
            var json = await File.ReadAllTextAsync(tablesJsonPath);
            using var doc = JsonDocument.Parse(json);
            var expectedTables = doc.RootElement.GetProperty("tables")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();

            foreach (var expectedTable in expectedTables)
            {
                existingTables.Should().Contain(expectedTable,
                    $"Canonical table '{expectedTable}' from SCHEMAS/tables.json must be created by migrations (DEF-018)");
            }
        }
        else
        {
            // Fallback assertion if path differs: verify key canonical tables exist
            existingTables.Should().Contain("productions");
            existingTables.Should().Contain("production_versions");
            existingTables.Should().Contain("artifacts");
            existingTables.Should().Contain("artifact_versions");
            existingTables.Should().Contain("jobs");
            existingTables.Should().Contain("leases");
            existingTables.Should().Contain("intents");
            existingTables.Should().Contain("events");
            existingTables.Should().Contain("audit_log");
            existingTables.Should().Contain("agent_runs");
            existingTables.Should().Contain("tool_runs");
            existingTables.Should().Contain("budgets");
            existingTables.Should().Contain("cost_events");
            existingTables.Should().Contain("revenue_events");
            existingTables.Should().Contain("qa_reports");
            existingTables.Should().Contain("qa_findings");
            existingTables.Should().Contain("rights_records");
        }

        // 4. Verify triggers exist
        const string queryTriggersSql = @"
            SELECT name FROM sqlite_master
            WHERE type = 'trigger'
            ORDER BY name ASC;
        ";
        var triggers = (await connection.QueryAsync<string>(queryTriggersSql)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        triggers.Should().Contain("trg_events_prevent_update");
        triggers.Should().Contain("trg_events_prevent_delete");
        triggers.Should().Contain("trg_audit_log_prevent_update");
        triggers.Should().Contain("trg_audit_log_prevent_delete");
    }

    [Fact]
    public async Task DEF018_DowngradeAndReUpgrade_ExecutesCleanly()
    {
        // 1. Initial Upgrade
        await _migrator.UpgradeAsync();

        // 2. Rollback migration 3
        await _migrator.RollbackAsync(targetVersion: 2);

        using (var connection = await _factory.CreateOpenConnectionAsync())
        {
            var tablesAfterDowngrade = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Migration 3 tables should be dropped
            tablesAfterDowngrade.Should().NotContain("production_versions");
            tablesAfterDowngrade.Should().NotContain("artifact_versions");

            // Migration 2 tables must still exist
            tablesAfterDowngrade.Should().Contain("productions");
            tablesAfterDowngrade.Should().Contain("budgets");
        }

        // 3. Re-upgrade to latest
        var reApplied = await _migrator.UpgradeAsync();
        reApplied.AppliedCount.Should().Be(1, "only migration 3 should be re-applied");

        using (var connection = await _factory.CreateOpenConnectionAsync())
        {
            var tablesReUpgraded = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet(StringComparer.OrdinalIgnoreCase);

            tablesReUpgraded.Should().Contain("production_versions");
            tablesReUpgraded.Should().Contain("artifact_versions");
        }
    }
}
