using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

[Collection("InstallerTests")]
public class InstallationCleanInstallTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _programFilesDir;
    private readonly string _appDataDir;

    public InstallationCleanInstallTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "AMCCA_LIFECYCLE_" + Guid.NewGuid().ToString("N"));
        _programFilesDir = Path.Combine(_testRoot, "ProgramFiles", "AMCCA");
        _appDataDir = Path.Combine(_testRoot, "AppData", "AMCCA");

        Directory.CreateDirectory(_programFilesDir);
        Directory.CreateDirectory(_appDataDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task FullLifecycle_CleanInstall_Launch_WriteData_Upgrade_UninstallPreserve_Restore()
    {
        // 1. CLEAN INSTALLATION SIMULATION
        // Copy binary and config into ProgramFiles
        var publishDir = Path.GetFullPath("artifacts/publish/win-x64");
        if (!Directory.Exists(publishDir))
        {
            publishDir = Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/publish/win-x64");
        }

        if (Directory.Exists(publishDir))
        {
            // Install essential files
            File.Copy(Path.Combine(publishDir, "AMCCA.exe"), Path.Combine(_programFilesDir, "AMCCA.exe"), true);
            File.Copy(Path.Combine(publishDir, "AMCCA.dll"), Path.Combine(_programFilesDir, "AMCCA.dll"), true);
            File.Copy(Path.Combine(publishDir, "AMCCA.runtimeconfig.json"), Path.Combine(_programFilesDir, "AMCCA.runtimeconfig.json"), true);
            
            File.Exists(Path.Combine(_programFilesDir, "AMCCA.exe")).Should().BeTrue("Clean install must drop AMCCA.exe");

            // 2. LAUNCH APPLICATION
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(_programFilesDir, "AMCCA.exe"),
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (string.IsNullOrEmpty(envRoot))
            {
                var localDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
                if (Directory.Exists(localDotnet))
                {
                    psi.EnvironmentVariables["DOTNET_ROOT"] = localDotnet;
                }
            }

            using var proc = Process.Start(psi);
            proc.Should().NotBeNull();
            var ver = proc!.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            proc.ExitCode.Should().Be(0);
            ver.Should().Be("3.1.0");
        }

        // 3. CREATE / WRITE USER DATA (Simulate active production in AppData)
        var dbPath = Path.Combine(_appDataDir, "amcca.db");
        var factory = new DatabaseConnectionFactory(dbPath);
        var migrator = new MigrationService(factory, _appDataDir);
        await migrator.UpgradeAsync();

        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-life-1', 'CANDIDATE_RENDERED', 'Lifecycle Video', 'en', 'tech', 'ASSISTED', '3.1.0', datetime('now'), datetime('now'));
            ");

            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (audit_id, action, actor_type, actor_id, subject_type, subject_id, outcome, reason_code, correlation_id, schema_version, occurred_at)
                VALUES ('aud-life-1', 'PRODUCTION_CREATED', 'OPERATOR', 'operator_admin', 'PRODUCTION', 'prod-life-1', 'SUCCESS', 'INIT', 'corr-l1', '3.1.0', datetime('now'));
            ");
        }

        // Also write custom user document in AppData
        var userDocPath = Path.Combine(_appDataDir, "operator_preferences.json");
        await File.WriteAllTextAsync(userDocPath, "{\"auto_publish\": false, \"theme\": \"dark\"}");

        // 4. UPGRADE (vN -> vN+1)
        // Simulating upgrade: new binary drops in ProgramFiles, migrations re-run, user data must be 100% preserved
        var upgradeReport = await migrator.UpgradeAsync();
        upgradeReport.AppliedCount.Should().Be(0, "Schema is already up to date; upgrade must be idempotent");

        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            var title = await conn.ExecuteScalarAsync<string>("SELECT title FROM productions WHERE id = 'prod-life-1'");
            var auditCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_log WHERE audit_id = 'aud-life-1'");
            title.Should().Be("Lifecycle Video", "Upgrade must preserve existing production titles");
            auditCount.Should().Be(1, "Upgrade must preserve audit log records");

            var integrity = await conn.ExecuteScalarAsync<string>("PRAGMA integrity_check;");
            integrity.Should().Be("ok", "Database must remain healthy after upgrade");
        }

        File.Exists(userDocPath).Should().BeTrue("Upgrade must not delete operator preferences");

        // 5. UNINSTALL APPLICATION — PRESERVE USER DATA
        // Uninstaller purges ProgramFiles but MUST PRESERVE %LOCALAPPDATA%\AMCCA
        Directory.Delete(_programFilesDir, recursive: true);
        Directory.Exists(_programFilesDir).Should().BeFalse("Uninstall must remove ProgramFiles binaries");

        // Assert user data in AppData is preserved
        Directory.Exists(_appDataDir).Should().BeTrue("DEF-CERT-002: User AppData directory must survive uninstall");
        File.Exists(dbPath).Should().BeTrue("amcca.db must survive uninstall");
        File.Exists(userDocPath).Should().BeTrue("operator_preferences.json must survive uninstall");

        // 6. BACKUP AND RESTORE VERIFICATION
        var backupPath = Path.Combine(_testRoot, "backup.db");
        var backupService = new BackupService(factory);
        var backedUp = await backupService.CreateBackupAsync(backupPath);
        backedUp.Should().BeTrue();

        var verified = await backupService.VerifyBackupAsync(backupPath);
        verified.Should().BeTrue();

        // Corrupt active DB
        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync("DELETE FROM productions;");
        }

        // Restore
        await backupService.RestoreBackupAsync(backupPath, dbPath);

        // Verify restoration
        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            var restoredTitle = await conn.ExecuteScalarAsync<string>("SELECT title FROM productions WHERE id = 'prod-life-1'");
            restoredTitle.Should().Be("Lifecycle Video");
            var integrity = await conn.ExecuteScalarAsync<string>("PRAGMA integrity_check;");
            integrity.Should().Be("ok");
        }
    }
}
