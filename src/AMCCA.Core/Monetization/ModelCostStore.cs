using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Agents;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Monetization;

/// <summary>
/// Writes one <c>cost_events</c> row of kind SETTLEMENT for an agent run's model spend (SPEC/20 profit
/// is <c>sum(cost_events where kind = SETTLEMENT)</c>; SPEC/21 actual usage is settled after execution).
/// </summary>
public sealed class ModelCostStore : IModelCostStore
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public ModelCostStore(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task RecordModelRunCostAsync(
        string productionId,
        string provider,
        string modelId,
        decimal amount,
        string currency,
        bool reconciled,
        string? pricingSnapshotId,
        string? providerRequestId,
        CancellationToken ct = default)
    {
        if (amount < 0m) amount = 0m; // a settlement is never negative (D-031); guard the input.
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO cost_events (
                id, production_id, job_id, kind, amount, currency, provider,
                occurred_at, created_at, schema_version,
                model_id, provider_request_id, pricing_snapshot_id, reconciliation_state
            ) VALUES (
                @Id, @ProductionId, NULL, 'SETTLEMENT', @Amount, @Currency, @Provider,
                @Now, @Now, '3.1.0',
                @ModelId, @ProviderRequestId, @PricingSnapshotId, @ReconciliationState
            );",
            new
            {
                Id = UlidGenerator.NewUlid(),
                ProductionId = productionId,
                Amount = Money.Format(amount),
                Currency = currency,
                Provider = provider,
                Now = now,
                ModelId = modelId,
                ProviderRequestId = providerRequestId,
                PricingSnapshotId = pricingSnapshotId,
                ReconciliationState = reconciled ? "RECONCILED" : "ESTIMATED_UNRECONCILED",
            }, cancellationToken: ct));
    }
}
