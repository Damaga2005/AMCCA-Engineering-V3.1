using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Preflight;
using AMCCA.Core.Security;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class PreflightContractTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _schemaJson;
    private readonly string _exampleYaml;
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly MigrationService _migrationService;

    public PreflightContractTests()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        _repoRoot = dir ?? throw new InvalidOperationException("Could not locate repo root");
        _schemaJson = File.ReadAllText(Path.Combine(_repoRoot, "SCHEMAS", "config.schema.json"));
        _exampleYaml = File.ReadAllText(Path.Combine(_repoRoot, "CONFIG", "config.example.yaml"));

        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_PREFLIGHT_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "preflight_test.db");
        _connectionFactory = new DatabaseConnectionFactory(_dbPath);
        _migrationService = new MigrationService(_connectionFactory, _testDir);
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

    private AmccaConfig LoadValidConfig()
    {
        var configService = new ConfigService(_schemaJson);
        var config = configService.LoadFromYaml(_exampleYaml);
        config.DataRoot = Path.Combine(_testDir, "data_root");
        return config;
    }

    [Fact]
    public async Task SystemPreflight_WithValidConfigAndReachableSecretStore_PermitsStartup()
    {
        var config = LoadValidConfig();
        var secretStore = new InMemorySecretStore();
        var preflightService = new PreflightService(_connectionFactory, _migrationService);

        var report = await preflightService.RunSystemStartupPreflightAsync(config, secretStore);

        // FFmpeg presence and disk headroom are environment-dependent (SPEC/49 gates 7-8 degrade
        // rather than abort), so the only universal guarantee is that a valid config with a reachable
        // secret store and clean database is allowed to start.
        report.IsStartupPermitted.Should().BeTrue();
        report.Status.Should().BeOneOf(PreflightStatus.Pass, PreflightStatus.Degraded);

        // Migrations must have actually been applied as part of gates 4/5.
        var applied = await _migrationService.GetAppliedMigrationsAsync();
        applied.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SystemPreflight_WithUnreachableSecretStore_AbortsStartup()
    {
        var config = LoadValidConfig();
        var unreachableSecretStore = new UnreachableSecretStoreFake();
        var preflightService = new PreflightService(_connectionFactory, _migrationService);

        var report = await preflightService.RunSystemStartupPreflightAsync(config, unreachableSecretStore);

        report.Status.Should().Be(PreflightStatus.Abort);
        report.IsStartupPermitted.Should().BeFalse();
        report.FailureDetails.Should().Contain(d => d.Contains("Secret store unreachable"));
    }

    [Fact]
    public async Task SystemPreflight_WithInconsistentBudgetWindow_AbortsBeforeTouchingDatabase()
    {
        var config = LoadValidConfig();
        config.Budgets.PerProduction = "999.000000"; // per_production > daily violates SPEC/03
        var secretStore = new InMemorySecretStore();
        var preflightService = new PreflightService(_connectionFactory, _migrationService);

        var report = await preflightService.RunSystemStartupPreflightAsync(config, secretStore);

        report.Status.Should().Be(PreflightStatus.Abort);
        report.IsStartupPermitted.Should().BeFalse();
        report.FailureDetails.Should().Contain(d => d.Contains(AmccaErrors.Cfg004));

        // Aborting on gate 3 must short-circuit before gate 4/5 ever touch the database.
        var applied = await _migrationService.GetAppliedMigrationsAsync();
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task SystemPreflight_WithEmergencyStopEngaged_HaltsStartup()
    {
        var config = LoadValidConfig();
        var secretStore = new InMemorySecretStore();
        await _migrationService.UpgradeAsync();

        using (var connection = await _connectionFactory.CreateOpenConnectionAsync())
        {
            await connection.ExecuteAsync(@"
                INSERT INTO kill_switch_state (id, mode, engaged_at, engaged_by, reason)
                VALUES (1, 'EMERGENCY_STOP', @Now, 'operator@amcca.local', 'Provider outage')
                ON CONFLICT(id) DO UPDATE SET mode = 'EMERGENCY_STOP', engaged_at = @Now, engaged_by = 'operator@amcca.local';
            ", new { Now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var preflightService = new PreflightService(_connectionFactory, _migrationService);
        var report = await preflightService.RunSystemStartupPreflightAsync(config, secretStore);

        report.Status.Should().Be(PreflightStatus.Halted);
        report.IsStartupPermitted.Should().BeFalse();
        report.FailureDetails.Should().Contain(d => d.Contains("EMERGENCY_STOP"));
    }

    [Fact]
    public async Task SystemPreflight_WithMissingDataRoot_DegradesRatherThanAborts()
    {
        var config = LoadValidConfig();
        config.DataRoot = string.Empty;
        var secretStore = new InMemorySecretStore();
        var preflightService = new PreflightService(_connectionFactory, _migrationService);

        var report = await preflightService.RunSystemStartupPreflightAsync(config, secretStore);

        report.IsStartupPermitted.Should().BeTrue();
        report.Warnings.Should().Contain(w => w.Contains("Data root is not configured"));
    }

    private class UnreachableSecretStoreFake : ISecretStore
    {
        public Task<string?> GetSecretAsync(SecretReference secretRef, System.Threading.CancellationToken ct = default) =>
            throw new InvalidOperationException("Store unreachable");

        public Task SetSecretAsync(SecretReference secretRef, string value, System.Threading.CancellationToken ct = default) =>
            throw new InvalidOperationException("Store unreachable");

        public Task<bool> IsReachableAsync(System.Threading.CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
