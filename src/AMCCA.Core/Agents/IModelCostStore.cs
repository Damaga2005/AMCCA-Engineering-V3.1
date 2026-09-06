using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Agents;

/// <summary>
/// Persists the settled cost of one agent run's model usage as a <c>cost_events</c> row (H1).
/// Injected as a port so AgentRuntime stays free of a database dependency, exactly like
/// <c>IAuditStore</c>. A null implementation is the no-op default.
/// </summary>
public interface IModelCostStore
{
    /// <param name="reconciled">
    /// true  → a pricing snapshot was resolved for every turn: kind SETTLEMENT, state RECONCILED.
    /// false → at least one turn had no price: kind SETTLEMENT, state ESTIMATED_UNRECONCILED, and the
    ///         amount is whatever could be priced (possibly 0). SPEC/21: a known unknown on the books.
    /// </param>
    Task RecordModelRunCostAsync(
        string productionId,
        string provider,
        string modelId,
        decimal amount,
        string currency,
        bool reconciled,
        string? pricingSnapshotId,
        string? providerRequestId,
        CancellationToken ct = default);
}

/// <summary>Default: records nothing. Used wherever no cost store is wired (unit tests, tools).</summary>
public sealed class NullModelCostStore : IModelCostStore
{
    public static readonly NullModelCostStore Instance = new();
    public Task RecordModelRunCostAsync(
        string productionId, string provider, string modelId, decimal amount, string currency,
        bool reconciled, string? pricingSnapshotId, string? providerRequestId, CancellationToken ct = default)
        => Task.CompletedTask;
}
