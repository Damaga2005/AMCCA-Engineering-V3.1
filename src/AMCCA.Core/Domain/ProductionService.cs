using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.StateMachine;
using Dapper;

namespace AMCCA.Core.Domain;

public class ProductionService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly StateMachineRegistry _stateMachine;
    private readonly IEventStore _eventStore;

    public ProductionService(
        DatabaseConnectionFactory connectionFactory,
        StateMachineRegistry stateMachine,
        IEventStore eventStore)
    {
        _connectionFactory = connectionFactory;
        _stateMachine = stateMachine;
        _eventStore = eventStore;
    }

    public async Task<Production> CreateProductionAsync(
        string? title,
        string language,
        string autonomyMode,
        string correlationId,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var prod = new Production
        {
            Id = id,
            State = _stateMachine.InitialState,
            BlockedFrom = null,
            UnknownFrom = null,
            ReworkAttempts = 0,
            AggregateVersion = 0,
            AutonomyMode = autonomyMode,
            Title = title,
            Language = language,
            NicheId = null,
            OpportunityId = null,
            CurrentManifestId = null,
            SchemaVersion = "3.1.0",
            CreatedAt = now,
            UpdatedAt = now
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string insertSql = @"
            INSERT INTO productions (
                id, state, blocked_from, unknown_from, rework_attempts,
                aggregate_version, autonomy_mode, title, language, niche_id,
                opportunity_id, current_manifest_id, schema_version, created_at, updated_at
            ) VALUES (
                @Id, @State, @BlockedFrom, @UnknownFrom, @ReworkAttempts,
                @AggregateVersion, @AutonomyMode, @Title, @Language, @NicheId,
                @OpportunityId, @CurrentManifestId, @SchemaVersion, @CreatedAt, @UpdatedAt
            );
        ";
        await connection.ExecuteAsync(insertSql, prod, transaction: tx);

        // Record creation event in event store
        var createEvent = new EventRecord(
            EventId: UlidGenerator.NewUlid(),
            EventType: "production.created",
            AggregateType: "production",
            AggregateId: id,
            AggregateVersion: 0,
            CorrelationId: correlationId,
            CausationId: null,
            TransitionId: null,
            PayloadJson: JsonSerializer.Serialize(new { title, language, autonomyMode }),
            SchemaVersion: "3.1.0",
            OccurredAt: now,
            Seq: 1);

        const string eventSql = @"
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
        await connection.ExecuteAsync(eventSql, createEvent, transaction: tx);

        tx.Commit();
        return prod;
    }

    public async Task<Production> TransitionAsync(
        string productionId,
        string toState,
        string actorType,
        string correlationId,
        string? causationId = null,
        CancellationToken ct = default)
    {
        var prod = await GetProductionAsync(productionId, ct)
            ?? throw new InvalidOperationException($"Production '{productionId}' not found.");

        // DEF-008: An agent is never the authority for state transitions
        if (string.Equals(actorType, "AGENT", StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                AmccaErrors.Ai004,
                ErrorCategory.Security,
                "An agent cannot be the actor for state transitions. The orchestrator is the sole state committer (AGENTS.md, DEF-008).");
        }

        // DEF-009: UNKNOWN_EXTERNAL_STATE requires reconciliation evidence (causationId) to transition
        if (string.Equals(prod.State, "UNKNOWN_EXTERNAL_STATE", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(causationId))
        {
            throw new AmccaException(
                AmccaErrors.Stm001,
                ErrorCategory.Validation,
                "Resuming from UNKNOWN_EXTERNAL_STATE requires reconciliation evidence / causationId (SPEC/44, DEF-009).");
        }

        // 1. Validate transition against canonical state machine matrix (SPEC/12, SPEC/13)
        var transitionDef = _stateMachine.ValidateTransition(prod.State, toState, prod.BlockedFrom);

        var now = DateTimeOffset.UtcNow.ToString("O");
        var nextVersion = prod.AggregateVersion + 1;
        var eventId = UlidGenerator.NewUlid();
        var transitionRecordId = UlidGenerator.NewUlid();

        // 2. Resolve blocked_from and unknown_from bookkeeping
        string? newBlockedFrom = prod.BlockedFrom;
        if (string.Equals(toState, "BLOCKED", StringComparison.OrdinalIgnoreCase))
        {
            newBlockedFrom = prod.State;
        }
        else if (string.Equals(prod.State, "BLOCKED", StringComparison.OrdinalIgnoreCase))
        {
            newBlockedFrom = null; // Cleared on successful resume
        }

        string? newUnknownFrom = prod.UnknownFrom;
        if (string.Equals(toState, "UNKNOWN_EXTERNAL_STATE", StringComparison.OrdinalIgnoreCase))
        {
            newUnknownFrom = prod.State;
        }
        else if (string.Equals(prod.State, "UNKNOWN_EXTERNAL_STATE", StringComparison.OrdinalIgnoreCase))
        {
            newUnknownFrom = null;
        }

        long newReworkAttempts = prod.ReworkAttempts;
        if (string.Equals(toState, "REWORK", StringComparison.OrdinalIgnoreCase))
        {
            newReworkAttempts++;
        }

        // 3. Atomically commit production state, transition record, and event row (Rule 2, SPEC/12)
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string updateSql = @"
            UPDATE productions
            SET state = @ToState,
                blocked_from = @BlockedFrom,
                unknown_from = @UnknownFrom,
                rework_attempts = @ReworkAttempts,
                aggregate_version = @NextVersion,
                updated_at = @UpdatedAt
            WHERE id = @Id AND aggregate_version = @ExpectedVersion;
        ";

        var rowsAffected = await connection.ExecuteAsync(updateSql, new
        {
            ToState = toState,
            BlockedFrom = newBlockedFrom,
            UnknownFrom = newUnknownFrom,
            ReworkAttempts = newReworkAttempts,
            NextVersion = nextVersion,
            UpdatedAt = now,
            Id = prod.Id,
            ExpectedVersion = prod.AggregateVersion
        }, transaction: tx);

        if (rowsAffected == 0)
        {
            tx.Rollback();
            throw new InvalidOperationException($"Optimistic concurrency violation on production '{productionId}'. Expected version {prod.AggregateVersion}.");
        }

        const string transitionSql = @"
            INSERT INTO state_transitions (
                id, production_id, transition_id, from_state, to_state,
                event_id, actor_type, correlation_id, occurred_at
            ) VALUES (
                @Id, @ProductionId, @TransitionId, @FromState, @ToState,
                @EventId, @ActorType, @CorrelationId, @OccurredAt
            );
        ";

        await connection.ExecuteAsync(transitionSql, new
        {
            Id = transitionRecordId,
            ProductionId = prod.Id,
            TransitionId = transitionDef.Id,
            FromState = prod.State,
            ToState = toState,
            EventId = eventId,
            ActorType = actorType,
            CorrelationId = correlationId,
            OccurredAt = now
        }, transaction: tx);

        const string eventSql = @"
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

        await connection.ExecuteAsync(eventSql, new
        {
            EventId = eventId,
            EventType = "production.state_changed",
            AggregateType = "production",
            AggregateId = prod.Id,
            AggregateVersion = nextVersion,
            CorrelationId = correlationId,
            CausationId = causationId,
            TransitionId = transitionDef.Id,
            PayloadJson = JsonSerializer.Serialize(new { from = prod.State, to = toState, transition_id = transitionDef.Id }),
            SchemaVersion = "3.1.0",
            OccurredAt = now,
            Seq = nextVersion + 1
        }, transaction: tx);

        tx.Commit();

        prod.State = toState;
        prod.BlockedFrom = newBlockedFrom;
        prod.UnknownFrom = newUnknownFrom;
        prod.ReworkAttempts = newReworkAttempts;
        prod.AggregateVersion = nextVersion;
        prod.UpdatedAt = now;

        return prod;
    }

    public async Task<Production?> GetProductionAsync(string productionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                state AS State,
                blocked_from AS BlockedFrom,
                unknown_from AS UnknownFrom,
                rework_attempts AS ReworkAttempts,
                aggregate_version AS AggregateVersion,
                autonomy_mode AS AutonomyMode,
                title AS Title,
                language AS Language,
                niche_id AS NicheId,
                opportunity_id AS OpportunityId,
                current_manifest_id AS CurrentManifestId,
                schema_version AS SchemaVersion,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM productions
            WHERE id = @Id;
        ";
        return await connection.QuerySingleOrDefaultAsync<Production>(sql, new { Id = productionId });
    }

    public async Task<IReadOnlyList<StateTransitionRecord>> GetStateTransitionsAsync(string productionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                production_id AS ProductionId,
                transition_id AS TransitionId,
                from_state AS FromState,
                to_state AS ToState,
                event_id AS EventId,
                actor_type AS ActorType,
                correlation_id AS CorrelationId,
                occurred_at AS OccurredAt
            FROM state_transitions
            WHERE production_id = @ProductionId
            ORDER BY occurred_at ASC;
        ";
        var result = await connection.QueryAsync<StateTransitionRecord>(sql, new { ProductionId = productionId });
        return result.ToList();
    }
}
