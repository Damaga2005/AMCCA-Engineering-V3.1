using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Policy;
using Dapper;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// The CONCEPT_SELECTED gate (SPEC/12 kind=gate, SPEC/13 T-003/T-004, SPEC/29). Replaces the silent
/// <see cref="NoWorkAdvanceHandler"/> for this one state (D-035): a decision gate must actually take a
/// decision, not wave the production through.
///
/// <para>Behaviour (select + BLOCK):</para>
/// <list type="bullet">
///   <item>An operator-selected opportunity (<c>productions.opportunity_id</c> already set): validate it,
///     commit the scripting budget reservation, persist the decision, advance.</item>
///   <item>AUTONOMOUS with none selected: pick the eligible <c>SCORED</c> opportunity with the highest
///     pre-computed score (never re-derived here — SPEC/29), reserve, persist, advance.</item>
///   <item>No selectable opportunity, or the budget reservation is refused: <c>BLOCKED</c> with a
///     SPEC/05 reason code for an operator. Never a silent advance.</item>
/// </list>
/// </summary>
public sealed class ConceptSelectionStageHandler : IStageHandler
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly BudgetManager _budgets;
    private readonly IAuditStore _auditStore;

    public ConceptSelectionStageHandler(
        DatabaseConnectionFactory connectionFactory,
        BudgetManager budgets,
        IAuditStore auditStore)
    {
        _connectionFactory = connectionFactory;
        _budgets = budgets;
        _auditStore = auditStore;
    }

    private sealed record OpportunityRow(
        string Id, string State, string Score, string ExpectedRevenue, string ExpectedCost,
        string Currency, string ScoreBreakdownJson);

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        var prod = context.Production;
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        OpportunityRow? opp;
        if (!string.IsNullOrWhiteSpace(prod.OpportunityId))
        {
            opp = await LoadByIdAsync(connection, prod.OpportunityId!, ct);
            if (opp is null)
            {
                return StageResult.Blocked(AmccaErrors.Pol001,
                    $"Production references opportunity '{prod.OpportunityId}', which does not exist.");
            }
            if (opp.State is not ("SCORED" or "SELECTED"))
            {
                return StageResult.Blocked(AmccaErrors.Pol001,
                    $"Opportunity '{opp.Id}' is {opp.State}; only a SCORED or SELECTED concept can be locked (SPEC/29).");
            }
        }
        else if (string.Equals(prod.AutonomyMode, "AUTONOMOUS", StringComparison.OrdinalIgnoreCase))
        {
            opp = await SelectHighestScoredAsync(connection, prod.NicheId, ct);
            if (opp is null)
            {
                return StageResult.Blocked(AmccaErrors.Pol001,
                    "No eligible SCORED opportunity is available to select as the concept (SPEC/29). "
                    + "Concept discovery/scoring must produce one before the production can proceed.");
            }
        }
        else
        {
            return StageResult.Blocked(AmccaErrors.Pol004,
                $"Concept selection in {prod.AutonomyMode} mode requires an operator to select an opportunity first (SPEC/29).");
        }

        // T-004: commit the scripting budget reservation against the production budget. No production
        // budget, or an exhausted one, blocks the gate rather than letting scripting spend uncapped.
        if (!Money.TryParse(opp.ExpectedCost, out var expectedCost))
        {
            return StageResult.Blocked(AmccaErrors.Pol001,
                $"Opportunity '{opp.Id}' has an unparseable expected_cost '{opp.ExpectedCost}'.");
        }
        try
        {
            await _budgets.ReserveAsync("PRODUCTION", prod.Id, expectedCost, context.CorrelationId, ct);
        }
        catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Bud002)
        {
            return StageResult.Blocked(AmccaErrors.Bud002,
                $"Scripting budget reservation of {expectedCost:F2} for concept '{opp.Id}' was refused: {ex.Message}");
        }

        // T-003: persist the strategy decision and its expected-value snapshot. The chosen opportunity
        // row is itself the immutable EV snapshot (score, expected_revenue, expected_cost, breakdown);
        // linking it and flipping it to SELECTED records the decision. The orchestrator, not this
        // handler, commits the CONCEPT_SELECTED -> SCRIPTING state transition (DEF-008).
        using (var tx = connection.BeginTransaction())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await connection.ExecuteAsync(new CommandDefinition(@"
                UPDATE productions SET opportunity_id = @Opp, updated_at = @Now
                WHERE id = @Prod AND opportunity_id IS NULL;",
                new { Opp = opp.Id, Now = now, Prod = prod.Id }, transaction: tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition(@"
                UPDATE opportunities SET state = 'SELECTED', updated_at = @Now WHERE id = @Opp;",
                new { Now = now, Opp = opp.Id }, transaction: tx, cancellationToken: ct));

            tx.Commit();
        }

        await _auditStore.AppendAuditAsync(new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "CONCEPT_SELECTED",
            ActorType: "ORCHESTRATOR",
            ActorId: "concept_selection_gate",
            SubjectType: "OPPORTUNITY",
            SubjectId: opp.Id,
            ProductionId: prod.Id,
            Outcome: "ALLOWED",
            PolicyDecisionId: null,
            ReasonCode: null,
            CorrelationId: context.CorrelationId,
            SchemaVersion: "3.1.0",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O")), ct);

        return StageResult.Advance(
            $"Concept '{opp.Id}' selected (score {opp.Score}, expected_revenue {opp.ExpectedRevenue}, "
            + $"expected_cost {opp.ExpectedCost} {opp.Currency}); scripting budget reserved.");
    }

    private static async Task<OpportunityRow?> LoadByIdAsync(System.Data.Common.DbConnection c, string id, CancellationToken ct)
        => await c.QuerySingleOrDefaultAsync<OpportunityRow>(new CommandDefinition(
            SelectColumns + " WHERE id = @Id;", new { Id = id }, cancellationToken: ct));

    private static async Task<OpportunityRow?> SelectHighestScoredAsync(
        System.Data.Common.DbConnection c, string? nicheId, CancellationToken ct)
    {
        // Highest pre-computed score wins (SPEC/29: the score is never re-derived here). Prefer the
        // production's own niche when it has one, but fall back to any scored opportunity.
        if (!string.IsNullOrWhiteSpace(nicheId))
        {
            var scoped = await c.QuerySingleOrDefaultAsync<OpportunityRow>(new CommandDefinition(
                SelectColumns + " WHERE state = 'SCORED' AND niche_id = @Niche ORDER BY score DESC, id ASC LIMIT 1;",
                new { Niche = nicheId }, cancellationToken: ct));
            if (scoped is not null) return scoped;
        }
        return await c.QuerySingleOrDefaultAsync<OpportunityRow>(new CommandDefinition(
            SelectColumns + " WHERE state = 'SCORED' ORDER BY score DESC, id ASC LIMIT 1;",
            cancellationToken: ct));
    }

    private const string SelectColumns = @"
        SELECT id AS Id, state AS State, CAST(score AS TEXT) AS Score,
               expected_revenue AS ExpectedRevenue, expected_cost AS ExpectedCost,
               currency AS Currency, score_breakdown_json AS ScoreBreakdownJson
        FROM opportunities";
}
