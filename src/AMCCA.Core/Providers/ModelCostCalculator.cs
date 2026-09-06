using System;

namespace AMCCA.Core.Providers;

/// <summary>
/// Turns a token count + a per-1M-token price into a settled <c>decimal</c> cost. Pure, no I/O.
/// SPEC/21: the estimate that drives a reservation is rounded up rather than down, and money is never
/// a float (D-023) — every operation here is <c>decimal</c> and the result is rounded to six fractional
/// digits away from zero.
/// </summary>
public static class ModelCostCalculator
{
    private const decimal PerMillion = 1_000_000m;

    /// <summary>
    /// Cost of one model call: <c>inputTokens/1e6 * inputPer1M + outputTokens/1e6 * outputPer1M</c>,
    /// rounded up to 6 dp. Negative token counts are clamped to 0 (a gateway that does not report usage
    /// returns 0; a negative is a bug, not a credit).
    /// </summary>
    public static decimal Compute(long inputTokens, long outputTokens, decimal inputPer1MTokens, decimal outputPer1MTokens)
    {
        if (inputTokens < 0) inputTokens = 0;
        if (outputTokens < 0) outputTokens = 0;
        if (inputPer1MTokens < 0m) inputPer1MTokens = 0m;
        if (outputPer1MTokens < 0m) outputPer1MTokens = 0m;

        decimal raw = (inputTokens / PerMillion * inputPer1MTokens)
                    + (outputTokens / PerMillion * outputPer1MTokens);

        return Math.Round(raw, 6, MidpointRounding.ToPositiveInfinity);
    }

    public static decimal Compute(long inputTokens, long outputTokens, ModelPrice price)
        => Compute(inputTokens, outputTokens, price.InputPer1MTokens, price.OutputPer1MTokens);
}
