using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class EventsAppendOnlyPhysicalEnforcementRegressionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;

    public EventsAppendOnlyPhysicalEnforcementRegressionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_DEF015_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "append_only.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();
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
    public async Task DEF015_Events_Insert_SucceedsNormally()
    {
        using var connection = await _factory.CreateOpenConnectionAsync();
        const string sql = @"
            INSERT INTO events (
                event_id, event_type, aggregate_type, aggregate_id, aggregate_version,
                correlation_id, causation_id, transition_id, payload_json, schema_version, occurred_at, seq
            ) VALUES (
                'evt-1', 'PROD_CREATED', 'PRODUCTION', 'prod-1', 1,
                'corr-1', 'caus-1', 'T-001', '{""title"":""Test""}', '1.0.0', '2026-09-03T12:00:00Z', 1
            );
        ";

        var rows = await connection.ExecuteAsync(sql);
        rows.Should().Be(1);

        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM events WHERE event_id = 'evt-1';");
        count.Should().Be(1);
    }

    [Fact]
    public async Task DEF015_Events_Update_IsPhysicallyBlockedByDatabaseTrigger()
    {
        using var connection = await _factory.CreateOpenConnectionAsync();
        const string insertSql = @"
            INSERT INTO events (
                event_id, event_type, aggregate_type, aggregate_id, aggregate_version,
                correlation_id, causation_id, transition_id, payload_json, schema_version, occurred_at, seq
            ) VALUES (
                'evt-update-test', 'PROD_CREATED', 'PRODUCTION', 'prod-1', 1,
                'corr-1', 'caus-1', 'T-001', '{}', '1.0.0', '2026-09-03T12:00:00Z', 1
            );
        ";
        await connection.ExecuteAsync(insertSql);

        // Attempt direct SQL UPDATE on events
        const string updateSql = "UPDATE events SET payload_json = '{\"tampered\":true}' WHERE event_id = 'evt-update-test';";
        var act = async () => await connection.ExecuteAsync(updateSql);

        var ex = await act.Should().ThrowAsync<SqliteException>("Direct UPDATE on events table must be physically blocked by SQLite trigger (D-001, DEF-015)");
        ex.Which.Message.Should().Contain("events table is strictly append-only; UPDATE is prohibited");
    }

    [Fact]
    public async Task DEF015_Events_Delete_IsPhysicallyBlockedByDatabaseTrigger()
    {
        using var connection = await _factory.CreateOpenConnectionAsync();
        const string insertSql = @"
            INSERT INTO events (
                event_id, event_type, aggregate_type, aggregate_id, aggregate_version,
                correlation_id, causation_id, transition_id, payload_json, schema_version, occurred_at, seq
            ) VALUES (
                'evt-delete-test', 'PROD_CREATED', 'PRODUCTION', 'prod-1', 1,
                'corr-1', 'caus-1', 'T-001', '{}', '1.0.0', '2026-09-03T12:00:00Z', 1
            );
        ";
        await connection.ExecuteAsync(insertSql);

        // Attempt direct SQL DELETE on events
        const string deleteSql = "DELETE FROM events WHERE event_id = 'evt-delete-test';";
        var act = async () => await connection.ExecuteAsync(deleteSql);

        var ex = await act.Should().ThrowAsync<SqliteException>("Direct DELETE on events table must be physically blocked by SQLite trigger (D-001, DEF-015)");
        ex.Which.Message.Should().Contain("events table is strictly append-only; DELETE is prohibited");
    }

    [Fact]
    public async Task DEF015_AuditLog_UpdateAndDelete_ArePhysicallyBlockedByDatabaseTriggers()
    {
        using var connection = await _factory.CreateOpenConnectionAsync();
        const string insertSql = @"
            INSERT INTO audit_log (
                audit_id, action, actor_type, actor_id, subject_type, subject_id,
                production_id, outcome, policy_decision_id, reason_code, correlation_id, schema_version, occurred_at
            ) VALUES (
                'aud-1', 'PUBLISH', 'OPERATOR', 'op-1', 'PRODUCTION', 'prod-1',
                'prod-1', 'SUCCESS', 'pol-1', 'ALLOWED', 'corr-1', '1.0.0', '2026-09-03T12:00:00Z'
            );
        ";
        await connection.ExecuteAsync(insertSql);

        // Attempt UPDATE on audit_log
        var actUpdate = async () => await connection.ExecuteAsync("UPDATE audit_log SET outcome = 'TAMPERED' WHERE audit_id = 'aud-1';");
        var exUpdate = await actUpdate.Should().ThrowAsync<SqliteException>();
        exUpdate.Which.Message.Should().Contain("audit_log table is strictly append-only; UPDATE is prohibited");

        // Attempt DELETE on audit_log
        var actDelete = async () => await connection.ExecuteAsync("DELETE FROM audit_log WHERE audit_id = 'aud-1';");
        var exDelete = await actDelete.Should().ThrowAsync<SqliteException>();
        exDelete.Which.Message.Should().Contain("audit_log table is strictly append-only; DELETE is prohibited");
    }

    [Fact]
    public async Task DEF015_Triggers_SurviveReconnection()
    {
        // First connection inserts event
        using (var conn1 = await _factory.CreateOpenConnectionAsync())
        {
            await conn1.ExecuteAsync(@"
                INSERT INTO events (
                    event_id, event_type, aggregate_type, aggregate_id, aggregate_version,
                    correlation_id, causation_id, transition_id, payload_json, schema_version, occurred_at, seq
                ) VALUES (
                    'evt-reconnect', 'PROD_CREATED', 'PRODUCTION', 'prod-1', 1,
                    'corr-1', 'caus-1', 'T-001', '{}', '1.0.0', '2026-09-03T12:00:00Z', 1
                );
            ");
        }

        // Fresh factory and connection
        SqliteConnection.ClearAllPools();
        var freshFactory = new DatabaseConnectionFactory(_dbPath);
        using var conn2 = await freshFactory.CreateOpenConnectionAsync();

        var actUpdate = async () => await conn2.ExecuteAsync("UPDATE events SET seq = 99 WHERE event_id = 'evt-reconnect';");
        (await actUpdate.Should().ThrowAsync<SqliteException>())
            .Which.Message.Should().Contain("events table is strictly append-only");

        var actDelete = async () => await conn2.ExecuteAsync("DELETE FROM events WHERE event_id = 'evt-reconnect';");
        (await actDelete.Should().ThrowAsync<SqliteException>())
            .Which.Message.Should().Contain("events table is strictly append-only");
    }
}
