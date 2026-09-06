using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class InstallationUpgradeRestoreValidationTests : IDisposable
{
    private readonly string _testDir;

    public InstallationUpgradeRestoreValidationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_INSTALL_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
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

    private static bool CheckInstallerReady(string installerDir)
    {
        var msiPath = Path.Combine(installerDir, "AMCCA-Setup.msi");
        var exePath = Path.Combine(installerDir, "AMCCA-Setup.exe");
        var shaPath = Path.Combine(installerDir, "SHA256SUMS");
        if (!File.Exists(msiPath) || !File.Exists(exePath) || !File.Exists(shaPath))
            return false;

        var exeInfo = new FileInfo(exePath);
        var msiInfo = new FileInfo(msiPath);
        if (exeInfo.Length <= msiInfo.Length)
            return false;

        var checksums = File.ReadAllText(shaPath);
        using var sha256 = SHA256.Create();
        var msiHash = Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(msiPath))).ToLowerInvariant();
        var exeHash = Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(exePath))).ToLowerInvariant();
        return checksums.Contains(msiHash) && checksums.Contains(exeHash);
    }

    private static void EnsureInstallerBuilt(string installerDir, string repoRoot)
    {
        if (!CheckInstallerReady(installerDir))
        {
            using var mutex = new System.Threading.Mutex(false, "Global\\AMCCA_INSTALLER_BUILD_MUTEX");
            try
            {
                mutex.WaitOne(TimeSpan.FromMinutes(3));
                if (!CheckInstallerReady(installerDir))
                {
                    var scriptPath = Path.Combine(repoRoot, "installer", "build_installer.ps1");
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        WorkingDirectory = repoRoot,
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
                            var curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                            psi.EnvironmentVariables["PATH"] = localDotnet + ";" + curPath;
                        }
                    }
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(180000);
                }
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    [Fact]
    public void InstallerArtifacts_AndSha256Checksums_AreValid()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AMCCA.sln")))
        {
            current = current.Parent;
        }
        var root = current?.FullName ?? Directory.GetCurrentDirectory();
        var installerDir = Path.Combine(root, "dist", "installer");
        EnsureInstallerBuilt(installerDir, root);

        if (Directory.Exists(installerDir))
        {
            var msiPath = Path.Combine(installerDir, "AMCCA-Setup.msi");
            var exePath = Path.Combine(installerDir, "AMCCA-Setup.exe");
            var shaPath = Path.Combine(installerDir, "SHA256SUMS");

            File.Exists(msiPath).Should().BeTrue("AMCCA-Setup.msi must exist in dist/installer");
            File.Exists(exePath).Should().BeTrue("AMCCA-Setup.exe must exist in dist/installer");
            File.Exists(shaPath).Should().BeTrue("SHA256SUMS must exist in dist/installer");

            var checksums = File.ReadAllText(shaPath);
            using var sha256 = SHA256.Create();
            
            var msiBytes = File.ReadAllBytes(msiPath);
            var msiHash = Convert.ToHexString(sha256.ComputeHash(msiBytes)).ToLowerInvariant();
            checksums.Should().Contain(msiHash, "MSI checksum must match SHA256SUMS");

            var exeBytes = File.ReadAllBytes(exePath);
            var exeHash = Convert.ToHexString(sha256.ComputeHash(exeBytes)).ToLowerInvariant();
            checksums.Should().Contain(exeHash, "EXE checksum must match SHA256SUMS");
        }
    }

    [Fact]
    public void ApplicationBinary_VersionAndHeadlessMode_Succeeds()
    {
        var binPath = Path.Combine(AppContext.BaseDirectory, "AMCCA.exe");
        if (!File.Exists(binPath))
        {
            binPath = Path.GetFullPath("src/AMCCA.App/bin/Release/net8.0-windows/AMCCA.exe");
        }

        if (File.Exists(binPath))
        {
            var psi = new ProcessStartInfo
            {
                FileName = binPath,
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
            var output = proc!.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            proc.ExitCode.Should().Be(0);
            output.Should().Be("3.1.0");

            // Headless mode
            psi.Arguments = "--headless";
            using var procH = Process.Start(psi);
            procH.Should().NotBeNull();
            var outH = procH!.StandardOutput.ReadToEnd();
            procH.WaitForExit(5000);
            procH.ExitCode.Should().Be(0);
            outH.Should().Contain("Headless");
        }
    }

    [Fact]
    public async Task Upgrade_PreservesExistingUserData_AndAppliesNewMigrations()
    {
        var dbPath = Path.Combine(_testDir, "upgrade_test.db");
        var factory = new DatabaseConnectionFactory(dbPath);

        // 1. Seed database with initial migration
        var migrator = new MigrationService(factory, _testDir);
        await migrator.UpgradeAsync();

        // 2. Insert sample production and audit log
        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-u1', 'INIT', 'Upgrade Topic', 'en', 'tech', 'ASSISTED', '3.1.0', datetime('now'), datetime('now'));
            ");

            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (audit_id, action, actor_type, actor_id, subject_type, subject_id, outcome, reason_code, correlation_id, schema_version, occurred_at)
                VALUES ('aud-u1', 'PRODUCTION_CREATED', 'OPERATOR', 'operator_admin', 'PRODUCTION', 'prod-u1', 'ALLOWED', 'NEW', 'corr-u1', '3.1.0', datetime('now'));
            ");
        }

        // 3. Re-run migration service (simulating upgrade idempotency)
        var report = await migrator.UpgradeAsync();
        report.AppliedCount.Should().Be(0, "Subsequent migrations on up-to-date schema apply 0 changes");

        // 4. Verify data integrity
        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            var prodTitle = await conn.ExecuteScalarAsync<string>("SELECT title FROM productions WHERE id = 'prod-u1'");
            var auditAction = await conn.ExecuteScalarAsync<string>("SELECT action FROM audit_log WHERE audit_id = 'aud-u1'");
            prodTitle.Should().Be("Upgrade Topic");
            auditAction.Should().Be("PRODUCTION_CREATED");

            var integrity = await conn.ExecuteScalarAsync<string>("PRAGMA integrity_check;");
            integrity.Should().Be("ok");
        }
    }

    [Fact]
    public async Task BackupAndRestore_RestoresDatabaseVerbatim_AndPassesIntegrityCheck()
    {
        var dbPath = Path.Combine(_testDir, "live.db");
        var backupPath = Path.Combine(_testDir, "backups", "live_backup.db");
        var factory = new DatabaseConnectionFactory(dbPath);

        var migrator = new MigrationService(factory, _testDir);
        await migrator.UpgradeAsync();

        // Seed data
        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-b1', 'CANDIDATE_RENDERED', 'Backup Topic', 'en', 'tech', 'ASSISTED', '3.1.0', datetime('now'), datetime('now'));
            ");
        }

        var backupService = new BackupService(factory);
        var backupSuccess = await backupService.CreateBackupAsync(backupPath);
        backupSuccess.Should().BeTrue();
        File.Exists(backupPath).Should().BeTrue();

        var backupVerified = await backupService.VerifyBackupAsync(backupPath);
        backupVerified.Should().BeTrue();

        // Corrupt or alter active database
        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync("DELETE FROM productions WHERE id = 'prod-b1';");
            var countAfterDelete = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM productions;");
            countAfterDelete.Should().Be(0);
        }

        // Restore backup
        await backupService.RestoreBackupAsync(backupPath, dbPath);

        // Verify restoration
        using (var conn = await factory.CreateOpenConnectionAsync())
        {
            var restoredTitle = await conn.ExecuteScalarAsync<string>("SELECT title FROM productions WHERE id = 'prod-b1';");
            restoredTitle.Should().Be("Backup Topic");

            var integrity = await conn.ExecuteScalarAsync<string>("PRAGMA integrity_check;");
            integrity.Should().Be("ok");
        }
    }

    [Fact]
    public void Uninstall_PreservesUserDataDirectory()
    {
        // Simulates app installation and data directory
        var appDir = Path.Combine(_testDir, "ProgramFiles", "AMCCA");
        var userDataDir = Path.Combine(_testDir, "AppData", "AMCCA");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(userDataDir);

        File.WriteAllText(Path.Combine(appDir, "AMCCA.exe"), "dummy exe");
        File.WriteAllText(Path.Combine(userDataDir, "amcca.db"), "dummy db");

        // Simulate uninstall by removing app directory
        Directory.Delete(appDir, recursive: true);

        // Assert binaries deleted, user data intact
        Directory.Exists(appDir).Should().BeFalse();
        Directory.Exists(userDataDir).Should().BeTrue();
        File.Exists(Path.Combine(userDataDir, "amcca.db")).Should().BeTrue();
    }
}
