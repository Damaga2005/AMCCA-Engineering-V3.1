using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// For the pure bookkeeping states that sit between producing stages — the <c>verified</c>-kind states
/// (RESEARCH_VERIFIED, SCRIPT_VERIFIED, …) and the concept gate. They carry no work of their own: the
/// prior producing stage's handler already did the verification, so entering the next producing stage
/// is the only thing left. In ASSISTED mode the engine still parks at <c>gate</c> states before this
/// runs; this only ever advances in AUTONOMOUS.
/// </summary>
public sealed class NoWorkAdvanceHandler : IStageHandler
{
    public Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
        => Task.FromResult(StageResult.Advance($"'{context.Production.State}' has no stage work; entering the next stage."));
}
