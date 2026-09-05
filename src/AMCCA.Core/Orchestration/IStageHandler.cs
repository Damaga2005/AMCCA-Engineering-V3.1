using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Orchestration;

/// <summary>
/// Does one production state's work and reports where the production should go next. Registered per
/// state name in <see cref="StageHandlerRegistry"/>. The orchestrator, never the handler, commits the
/// state transition (DEF-008: the orchestrator is the sole state committer).
/// </summary>
public interface IStageHandler
{
    Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default);
}
