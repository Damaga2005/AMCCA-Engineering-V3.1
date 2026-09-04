using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace AMCCA.Core.Database;

public class DatabaseConnectionFactory
{
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public DatabaseConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        };
        _connectionString = builder.ToString();
    }

    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Configure connection pragmas per SPEC/10
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                PRAGMA temp_store = MEMORY;
            ";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Assert WAL and foreign_keys = ON (SPEC/10)
        var journalMode = await GetJournalModeAsync(connection, ct);
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            connection.Dispose();
            throw new AmccaException(
                AmccaErrors.Db001,
                ErrorCategory.Internal,
                $"Database journal_mode must be WAL, but reported '{journalMode}'.");
        }

        var foreignKeys = await GetForeignKeysEnabledAsync(connection, ct);
        if (!foreignKeys)
        {
            connection.Dispose();
            throw new AmccaException(
                AmccaErrors.Db001,
                ErrorCategory.Internal,
                "Database foreign_keys must be ON, but reported OFF.");
        }

        return connection;
    }

    public async Task<string> GetJournalModeAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? string.Empty;
    }

    public async Task<bool> GetForeignKeysEnabledAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) == 1;
    }

    public async Task<bool> CheckIntegrityAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
    }
}
