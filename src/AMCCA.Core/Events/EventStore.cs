using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Events;

public record EventRecord(
    string EventId,
    string EventType,
    string AggregateType,
    string AggregateId,
    long AggregateVersion,
    string CorrelationId,
    string? CausationId,
    string? TransitionId,
    string PayloadJson,
    string SchemaVersion,
    string OccurredAt,
    long Seq);

public interface IEventStore
{
    Task AppendEventAsync(EventRecord evt, CancellationToken ct = default);
    Task<IReadOnlyList<EventRecord>> GetEventsAsync(string aggregateType, string aggregateId, CancellationToken ct = default);
}

public class EventStore : IEventStore
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public EventStore(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task AppendEventAsync(EventRecord evt, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO events (
                event_id, event_type, aggregate_type, aggregate_id, aggregate_version,
                correlation_id, causation_id, transition_id, payload_json, schema_version,
                occurred_at, seq
            ) VALUES (
                @EventId, @EventType, @AggregateType, @AggregateId, @AggregateVersion,
                @CorrelationId, @CausationId, @TransitionId, @PayloadJson, @SchemaVersion,
                @OccurredAt, @Seq
            );
        ";
        await connection.ExecuteAsync(new CommandDefinition(sql, evt, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<EventRecord>> GetEventsAsync(string aggregateType, string aggregateId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                event_id AS EventId,
                event_type AS EventType,
                aggregate_type AS AggregateType,
                aggregate_id AS AggregateId,
                aggregate_version AS AggregateVersion,
                correlation_id AS CorrelationId,
                causation_id AS CausationId,
                transition_id AS TransitionId,
                payload_json AS PayloadJson,
                schema_version AS SchemaVersion,
                occurred_at AS OccurredAt,
                seq AS Seq
            FROM events
            WHERE aggregate_type = @AggregateType AND aggregate_id = @AggregateId
            ORDER BY aggregate_version ASC;
        ";
        var result = await connection.QueryAsync<EventRecord>(sql, new { AggregateType = aggregateType, AggregateId = aggregateId });
        return result.ToList();
    }
}
