using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AMCCA.Core.Database;

public class MigrationService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly string _workingDirectory;

    public MigrationService(DatabaseConnectionFactory connectionFactory, string? workingDirectory = null)
    {
        _connectionFactory = connectionFactory;
        _workingDirectory = workingDirectory ?? AppContext.BaseDirectory;
    }

    private static readonly List<(int Version, string Name, string UpSql, string DownSql)> BuiltInMigrations = new()
    {
        (
            1,
            "001_initial_core_schema",
            @"
                -- Settings and Kill Switch
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value_json TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    is_secret_ref INTEGER NOT NULL DEFAULT 0 CHECK(is_secret_ref IN (0,1)),
                    updated_at TEXT NOT NULL,
                    updated_by TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS kill_switch_state (
                    id INTEGER PRIMARY KEY CHECK(id=1),
                    mode TEXT NOT NULL CHECK(mode IN ('NORMAL','PAUSED','PUBLISHING_DISABLED','EMERGENCY_STOP')),
                    engaged_at TEXT NULL,
                    engaged_by TEXT NULL,
                    reason TEXT NULL,
                    cleared_at TEXT NULL,
                    cleared_by TEXT NULL
                );

                -- Events and Audit Log
                CREATE TABLE IF NOT EXISTS events (
                    event_id TEXT PRIMARY KEY,
                    event_type TEXT NOT NULL,
                    aggregate_type TEXT NOT NULL,
                    aggregate_id TEXT NOT NULL,
                    aggregate_version INTEGER NOT NULL,
                    correlation_id TEXT NOT NULL,
                    causation_id TEXT NULL,
                    transition_id TEXT NULL,
                    payload_json TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    seq INTEGER NOT NULL,
                    UNIQUE(aggregate_type, aggregate_id, aggregate_version)
                );

                CREATE TABLE IF NOT EXISTS audit_log (
                    audit_id TEXT PRIMARY KEY,
                    action TEXT NOT NULL,
                    actor_type TEXT NOT NULL CHECK(actor_type IN ('OPERATOR','SYSTEM')),
                    actor_id TEXT NOT NULL,
                    subject_type TEXT NULL,
                    subject_id TEXT NULL,
                    production_id TEXT NULL,
                    outcome TEXT NOT NULL,
                    policy_decision_id TEXT NULL,
                    reason_code TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );

                -- Distribution & Synthetic Declarations (from SCHEMAS/schema.sql)
                CREATE TABLE IF NOT EXISTS synthetic_declarations (
                    id TEXT PRIMARY KEY
                );

                CREATE TABLE IF NOT EXISTS publications (
                    id TEXT PRIMARY KEY,
                    state TEXT NOT NULL,
                    external_id TEXT NULL,
                    evidence_source TEXT NULL,
                    evidence_retrieved_at TEXT NULL,
                    synthetic_declaration_id TEXT NULL,
                    platform_label_required INTEGER NOT NULL DEFAULT 0,
                    synthetic_label_applied INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (synthetic_declaration_id) REFERENCES synthetic_declarations(id),
                    CHECK (state IN ('INTENT_CREATED', 'UPLOAD_REQUESTED', 'UPLOADED', 'PROCESSING', 'PUBLISHED', 'VERIFIED', 'REJECTED', 'FAILED', 'UNKNOWN_EXTERNAL_STATE', 'CANCELLED')),
                    CHECK (platform_label_required = 0 OR synthetic_declaration_id IS NOT NULL),
                    CHECK (state <> 'VERIFIED' OR (external_id IS NOT NULL
                           AND evidence_source IN ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OPERATOR_CONFIRMATION')
                           AND evidence_retrieved_at IS NOT NULL)),
                    CHECK (state <> 'VERIFIED' OR platform_label_required = 0 OR synthetic_label_applied = 1)
                );

                CREATE TABLE IF NOT EXISTS platform_capabilities (
                    platform TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    capability TEXT NOT NULL,
                    status TEXT NOT NULL,
                    evidence_source TEXT NOT NULL,
                    verified_at TEXT NOT NULL,
                    expires_at TEXT NULL,
                    PRIMARY KEY (platform, account_id, capability),
                    CHECK (status IN ('DISCOVERED', 'VERIFIED', 'UNVERIFIED', 'DISABLED', 'UNSUPPORTED')),
                    CHECK (status <> 'VERIFIED' OR evidence_source IN
                      ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OFFICIAL_DOCUMENTATION', 'DIRECT_PLATFORM_PROBE', 'OPERATOR_CONFIRMATION'))
                );
            ",
            @"
                DROP TABLE IF EXISTS platform_capabilities;
                DROP TABLE IF EXISTS publications;
                DROP TABLE IF EXISTS synthetic_declarations;
                DROP TABLE IF EXISTS audit_log;
                DROP TABLE IF EXISTS events;
                DROP TABLE IF EXISTS kill_switch_state;
                DROP TABLE IF EXISTS settings;
            "
        ),
        (
            2,
            "002_domain_policy_approvals_and_triggers",
            @"
                -- DEF-015: Events table append-only triggers
                CREATE TRIGGER IF NOT EXISTS trg_events_prevent_update
                BEFORE UPDATE ON events
                BEGIN
                    SELECT RAISE(ABORT, 'events table is strictly append-only; UPDATE is prohibited (D-001, DEF-015)');
                END;

                CREATE TRIGGER IF NOT EXISTS trg_events_prevent_delete
                BEFORE DELETE ON events
                BEGIN
                    SELECT RAISE(ABORT, 'events table is strictly append-only; DELETE is prohibited (D-001, DEF-015)');
                END;

                -- DEF-018: Full schema tables
                CREATE TABLE IF NOT EXISTS approvals (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    scope_json TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('PENDING','APPROVED','REJECTED','EXPIRED','CONSUMED')),
                    single_use INTEGER NOT NULL DEFAULT 1,
                    decided_by TEXT NULL,
                    decided_at TEXT NULL,
                    consumed_at TEXT NULL,
                    expires_at TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS budgets (
                    id TEXT PRIMARY KEY,
                    window TEXT NOT NULL,
                    scope_id TEXT NOT NULL,
                    limit_amount REAL NOT NULL,
                    reserved REAL NOT NULL DEFAULT 0.0,
                    spent REAL NOT NULL DEFAULT 0.0,
                    currency TEXT NOT NULL DEFAULT 'EUR',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS policy_decisions (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    decision TEXT NOT NULL CHECK(decision IN ('ALLOW','REQUIRE_APPROVAL','BLOCK')),
                    rule_key TEXT NOT NULL,
                    policy_version_id TEXT NOT NULL,
                    inputs_hash TEXT NOT NULL,
                    correlation_id TEXT NOT NULL,
                    decided_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS productions (
                    id TEXT PRIMARY KEY,
                    state TEXT NOT NULL,
                    blocked_from TEXT NULL,
                    unknown_from TEXT NULL,
                    rework_attempts INTEGER NOT NULL DEFAULT 0,
                    aggregate_version INTEGER NOT NULL DEFAULT 0,
                    autonomy_mode TEXT NOT NULL,
                    title TEXT NULL,
                    language TEXT NOT NULL,
                    niche_id TEXT NULL,
                    opportunity_id TEXT NULL,
                    current_manifest_id TEXT NULL,
                    schema_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS state_transitions (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    transition_id TEXT NOT NULL,
                    from_state TEXT NOT NULL,
                    to_state TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    actor_type TEXT NOT NULL,
                    correlation_id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    job_type TEXT NOT NULL,
                    state TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    priority INTEGER NOT NULL DEFAULT 0,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    max_attempts INTEGER NOT NULL DEFAULT 3,
                    fence_token INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS leases (
                    id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    owner TEXT NOT NULL,
                    fence_token INTEGER NOT NULL,
                    expires_at TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS platform_accounts (
                    id TEXT PRIMARY KEY,
                    platform TEXT NOT NULL,
                    account_handle TEXT NOT NULL,
                    credential_secret_ref TEXT NOT NULL CHECK(credential_secret_ref LIKE 'secret://%'),
                    state TEXT NOT NULL CHECK(state IN ('DISCONNECTED','CONNECTED','REAUTH_REQUIRED','SUSPENDED','DISABLED')),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS revenue_events (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    publication_id TEXT NULL,
                    program_id TEXT NULL,
                    state TEXT NOT NULL CHECK(state IN ('PENDING','CONFIRMED','DISPUTED','REVERSED')),
                    provenance TEXT NOT NULL CHECK(provenance IN ('OFFICIAL_API','STATEMENT_IMPORT','MANUAL_CONFIRMED')),
                    gross_amount REAL NOT NULL,
                    fee_amount REAL NOT NULL DEFAULT 0.0,
                    net_amount REAL NOT NULL,
                    currency TEXT NOT NULL,
                    statement_ref TEXT NULL,
                    occurred_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    CHECK(provenance <> 'ESTIMATED')
                );

                CREATE TABLE IF NOT EXISTS cost_events (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    job_id TEXT NULL,
                    kind TEXT NOT NULL CHECK(kind IN ('RESERVATION','SETTLEMENT','REFUND','ADJUSTMENT')),
                    amount REAL NOT NULL,
                    currency TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
            ",
            @"
                DROP TABLE IF EXISTS cost_events;
                DROP TABLE IF EXISTS revenue_events;
                DROP TABLE IF EXISTS platform_accounts;
                DROP TABLE IF EXISTS leases;
                DROP TABLE IF EXISTS jobs;
                DROP TABLE IF EXISTS state_transitions;
                DROP TABLE IF EXISTS productions;
                DROP TABLE IF EXISTS policy_decisions;
                DROP TABLE IF EXISTS budgets;
                DROP TABLE IF EXISTS approvals;
                DROP TRIGGER IF EXISTS trg_events_prevent_delete;
                DROP TRIGGER IF EXISTS trg_events_prevent_update;
            "
        )
    };

    public static string ComputeSha256Checksum(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Trim();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<MigrationRecord>> GetAppliedMigrationsAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureMigrationTableExistsAsync(connection, ct);

        var list = await connection.QueryAsync<MigrationRecord>(
            "SELECT version, name, checksum, applied_at AS AppliedAt, applied_by AS AppliedBy, rollback_sql_ref AS RollbackSqlRef FROM schema_migrations ORDER BY version ASC;");

        return list.ToList();
    }

    public async Task<MigrationReport> UpgradeAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureMigrationTableExistsAsync(connection, ct);

        var applied = (await GetAppliedMigrationsAsync(ct)).ToDictionary(m => m.Version);

        // Verify checksums of already-applied migrations
        foreach (var m in BuiltInMigrations)
        {
            if (applied.TryGetValue((long)m.Version, out var existing))
            {
                var currentChecksum = ComputeSha256Checksum(m.UpSql);
                if (!string.Equals(existing.Checksum, currentChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AmccaException(
                        AmccaErrors.Db002,
                        ErrorCategory.Internal,
                        $"Migration checksum mismatch on version {m.Version} ('{m.Name}'). Recorded: {existing.Checksum}, Shipped: {currentChecksum}. Startup aborted.");
                }
            }
        }

        int appliedCount = 0;
        foreach (var m in BuiltInMigrations.OrderBy(m => m.Version))
        {
            if (applied.ContainsKey((long)m.Version)) continue;

            using var tx = connection.BeginTransaction();
            try
            {
                await connection.ExecuteAsync(m.UpSql, transaction: tx);

                var checksum = ComputeSha256Checksum(m.UpSql);
                var now = DateTimeOffset.UtcNow.ToString("O");

                await connection.ExecuteAsync(@"
                    INSERT INTO schema_migrations (version, name, checksum, applied_at, applied_by, rollback_sql_ref)
                    VALUES (@Version, @Name, @Checksum, @AppliedAt, @AppliedBy, @RollbackSqlRef);
                ", new
                {
                    m.Version,
                    m.Name,
                    Checksum = checksum,
                    AppliedAt = now,
                    AppliedBy = "AMCCA.Migrator",
                    RollbackSqlRef = m.Name + "_rollback"
                }, transaction: tx);

                tx.Commit();
                appliedCount++;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        return new MigrationReport(appliedCount, $"Applied {appliedCount} migration(s).");
    }

    public async Task<RollbackReport> RollbackAsync(int targetVersion, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureMigrationTableExistsAsync(connection, ct);

        var applied = await GetAppliedMigrationsAsync(ct);
        var toRollback = applied
            .Where(m => m.Version > targetVersion)
            .OrderByDescending(m => m.Version)
            .ToList();

        int rolledBackCount = 0;
        foreach (var rec in toRollback)
        {
            var migrationDef = BuiltInMigrations.FirstOrDefault(m => m.Version == rec.Version);
            if (migrationDef == default)
            {
                throw new InvalidOperationException($"No rollback definition for version {rec.Version}.");
            }

            using var tx = connection.BeginTransaction();
            try
            {
                await connection.ExecuteAsync(migrationDef.DownSql, transaction: tx);
                await connection.ExecuteAsync(
                    "DELETE FROM schema_migrations WHERE version = @Version;",
                    new { rec.Version },
                    transaction: tx);

                tx.Commit();
                rolledBackCount++;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        return new RollbackReport(rolledBackCount, $"Rolled back {rolledBackCount} migration(s).");
    }

    private static async Task EnsureMigrationTableExistsAsync(SqliteConnection connection, CancellationToken ct)
    {
        const string ddl = @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                checksum TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                applied_by TEXT NOT NULL,
                rollback_sql_ref TEXT NULL
            );
        ";
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
