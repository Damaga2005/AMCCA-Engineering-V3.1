using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Orchestration;

/// <summary>
/// INIT has no work of its own — starting a production just means entering RESEARCHING. This is the one
/// state where "advance with no work" is genuinely correct; every other producing/QA state needs a real
/// handler (research, scripting, render, QA, …), which is why <see cref="UnhandledStageHandler"/> blocks
/// rather than advances.
/// </summary>
public sealed class InitStageHandler : IStageHandler
{
    public Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
        => Task.FromResult(StageResult.Advance("Production started; entering RESEARCHING."));
}
