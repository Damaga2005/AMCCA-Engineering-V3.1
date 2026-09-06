using System.Collections.Generic;

namespace AMCCA.Core.Agents;

public enum AgentRunStatus
{
    /// <summary>The agent produced a final answer (schema-valid if the contract declares an output schema).</summary>
    Completed,

    /// <summary>The agent stopped without a usable answer — forbidden tool, budget exhausted, protocol failure, or max iterations.</summary>
    Failed,
}

/// <summary>One turn in the agent transcript. Role is "assistant" (the model) or "tool" (a tool result fed back).</summary>
public sealed record AgentTurn(string Role, string Content);

public sealed record AgentRunResult(
    AgentRunStatus Status,
    string? FinalOutput,
    string? ReasonCode,
    string? Detail,
    int Iterations,
    decimal CostAccrued,
    IReadOnlyList<AgentTurn> Transcript)
{
    /// <summary>Total model tokens the gateway reported across the run. Additive, defaulted so the
    /// factories below and existing callers are unaffected; populated by RunAgentAsync from the run
    /// session so a cost-accounting caller (H1) has the usage figures instead of them being lost.</summary>
    public long ModelInputTokens { get; init; }
    public long ModelOutputTokens { get; init; }

    /// <summary>The priced model cost accrued during the run, and whether every turn could be priced.
    /// When <see cref="ModelPricingComplete"/> is false the run still succeeded, but at least one turn
    /// had no pricing_snapshot and its cost is recorded ESTIMATED_UNRECONCILED (SPEC/21).</summary>
    public decimal ModelCost { get; init; }
    public bool ModelPricingComplete { get; init; } = true;

    public static AgentRunResult Completed(string finalOutput, int iterations, decimal cost, IReadOnlyList<AgentTurn> transcript)
        => new(AgentRunStatus.Completed, finalOutput, null, null, iterations, cost, transcript);

    public static AgentRunResult Failed(string reasonCode, string detail, int iterations, decimal cost, IReadOnlyList<AgentTurn> transcript)
        => new(AgentRunStatus.Failed, null, reasonCode, detail, iterations, cost, transcript);
}
