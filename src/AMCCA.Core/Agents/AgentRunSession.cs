using System;

namespace AMCCA.Core.Agents;

public class AgentRunSession
{
    private readonly object _lock = new();

    public AgentContract Contract { get; }
    public decimal AccumulatedCost { get; private set; }

    /// <summary>
    /// Model token usage the gateway reported, summed across every turn of the run. Captured so cost
    /// accounting has a real input to work from (H1); the gateway returns these on every response and
    /// they were previously discarded. This is a raw fact, not a priced amount.
    /// </summary>
    public long ModelInputTokens { get; private set; }
    public long ModelOutputTokens { get; private set; }

    /// <summary>The priced portion of <see cref="AccumulatedCost"/> that came from model calls (H1),
    /// kept separate so a single settled cost_events row can be written for the run.</summary>
    public decimal ModelCost { get; private set; }

    /// <summary>True once at least one model turn could not be priced (no pricing_snapshot on file).
    /// The run still completes; its cost event is recorded ESTIMATED_UNRECONCILED (SPEC/21).</summary>
    public bool HasUnpricedModelUsage { get; private set; }

    public AgentRunSession(AgentContract contract)
    {
        Contract = contract;
    }

    /// <summary>Adds one model turn's priced cost to the running budget total. Enforced by the same
    /// <c>AccumulatedCost &gt;= Contract.MaxCost</c> check the loop already applies to tool costs.</summary>
    public void AddModelCost(decimal cost)
    {
        if (cost <= 0m) return;
        lock (_lock)
        {
            ModelCost += cost;
            AccumulatedCost += cost;
        }
    }

    /// <summary>Records that a model turn ran without a resolvable price.</summary>
    public void MarkUnpricedModelUsage()
    {
        lock (_lock) { HasUnpricedModelUsage = true; }
    }

    /// <summary>Adds one turn's reported token counts. Negative counts (a gateway that does not report
    /// usage returns 0, but guard anyway) are clamped to 0.</summary>
    public void AddModelTokens(long inputTokens, long outputTokens)
    {
        lock (_lock)
        {
            ModelInputTokens += inputTokens > 0 ? inputTokens : 0;
            ModelOutputTokens += outputTokens > 0 ? outputTokens : 0;
        }
    }

    public bool TryReserveCost(decimal cost)
    {
        if (cost < 0) return false;

        lock (_lock)
        {
            if (AccumulatedCost + cost > Contract.MaxCost)
            {
                return false;
            }

            AccumulatedCost += cost;
            return true;
        }
    }

    /// <summary>
    /// SEC-06: returns a reservation to the pool when the operation it was made for did not run
    /// (execution threw or was cancelled). A blocked or failed call must not consume budget.
    /// </summary>
    public void ReleaseCost(decimal cost)
    {
        if (cost <= 0) return;

        lock (_lock)
        {
            AccumulatedCost -= cost;
            if (AccumulatedCost < 0) AccumulatedCost = 0;
        }
    }
}
