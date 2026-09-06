using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Scripts;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// The generative half of the RESEARCHING stage: proposes claims, finds and ingests sources, and
/// persists them (via ResearchService) so <see cref="ResearchStageHandler"/> can verify the result.
/// A real implementation runs an agent (AgentRuntime + a model provider + search/fetch tools); until
/// one is wired the handler has no agent and blocks the production for an operator.
/// </summary>
public interface IResearchAgent
{
    Task PerformResearchAsync(string productionId, string correlationId, CancellationToken ct = default);
}

/// <summary>
/// The generative half of the SCRIPTING stage: produces a <see cref="ScriptDocument"/> for the
/// production from its verified claims. <see cref="ScriptStageHandler"/> then validates it with
/// <see cref="ScriptValidator"/>. Wired later, same as <see cref="IResearchAgent"/>.
/// </summary>
public interface IScriptAgent
{
    Task<ScriptDocument> GenerateScriptAsync(string productionId, string correlationId, CancellationToken ct = default);
}
