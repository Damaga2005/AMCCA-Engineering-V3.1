using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AMCCA.Core.Database;

public class BackupService
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public BackupService(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> CreateBackupAsync(string destinationPath, CancellationToken ct = default)
    {
        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        using var sourceConn = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var destBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        using var destConn = new SqliteConnection(destBuilder.ToString());
        await destConn.OpenAsync(ct);

        sourceConn.BackupDatabase(destConn);
        return File.Exists(destinationPath);
    }

    public async Task<bool> VerifyBackupAsync(string backupPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath)) return false;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly
        };

        try
        {
            using var conn = new SqliteConnection(builder.ToString());
            await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = await cmd.ExecuteScalarAsync(ct);
            return string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public Task RestoreBackupAsync(string backupPath, string targetDatabasePath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup file not found.", backupPath);
        }

        SqliteConnection.ClearAllPools();
        var targetDir = Path.GetDirectoryName(targetDatabasePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        File.Copy(backupPath, targetDatabasePath, overwrite: true);
        return Task.CompletedTask;
    }
}
