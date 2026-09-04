using System;

namespace AMCCA.Core.Agents;

public class AgentRunSession
{
    private readonly object _lock = new();

    public AgentContract Contract { get; }
    public decimal AccumulatedCost { get; private set; }

    public AgentRunSession(AgentContract contract)
    {
        Contract = contract;
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
