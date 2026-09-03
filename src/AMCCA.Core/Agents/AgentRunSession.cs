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
}
