using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Agents;
using AMCCA.Core.Contracts;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Providers;
using AMCCA.Core.Research;
using AMCCA.Core.Tools;

namespace AMCCA.Core.Orchestration.Handlers;

public sealed record ResearchAgentOptions(string ModelId, decimal MaxCost, int TimeoutSeconds, int MaxIterations)
{
    // ponytail: ModelId is a constant until config.providers.gateway carries a default_model_id (or the
    // research capability is resolved from model_registry).
    public static ResearchAgentOptions Default => new("gpt-4o-mini", MaxCost: 2.00m, TimeoutSeconds: 300, MaxIterations: 20);
}

/// <summary>
/// The generative half of RESEARCHING: runs <see cref="AgentRuntime.RunAgentAsync"/> with the research
/// tools (fetch_source / record_claim / evaluate_claims) so the model gathers evidence and records
/// claims. It does not decide the stage outcome — <see cref="ResearchStageHandler"/> re-checks the DB
/// after this returns.
/// </summary>
public sealed class AgentResearchAgent : IResearchAgent
{
    private readonly ProductionService _productions;
    private readonly ResearchService _research;
    private readonly IAuditStore _auditStore;
    private readonly IProviderGateway _gateway;
    private readonly ResearchAgentOptions _options;
    private readonly IModelPricing? _modelPricing;
    private readonly IModelCostStore? _modelCostStore;

    public AgentResearchAgent(
        ProductionService productions,
        ResearchService research,
        IAuditStore auditStore,
        IProviderGateway gateway,
        ResearchAgentOptions? options = null,
        IModelPricing? modelPricing = null,
        IModelCostStore? modelCostStore = null)
    {
        _productions = productions;
        _research = research;
        _auditStore = auditStore;
        _gateway = gateway;
        _options = options ?? ResearchAgentOptions.Default;
        _modelPricing = modelPricing;
        _modelCostStore = modelCostStore;
    }

    public async Task PerformResearchAsync(string productionId, string correlationId, CancellationToken ct = default)
    {
        var prod = await _productions.GetProductionAsync(productionId, ct)
            ?? throw new AmccaException(AmccaErrors.Res002, ErrorCategory.Validation, $"Production '{productionId}' not found.");

        var tools = new ToolRegistry();
        tools.RegisterTool(new FetchSourceTool(_research));
        tools.RegisterTool(new RecordClaimTool(_research));
        tools.RegisterTool(new EvaluateClaimsTool(_research));

        var contract = new AgentContract(
            AgentId: "research-agent",
            AgentVersion: "1.0",
            AllowedTools: new HashSet<string> { "fetch_source", "record_claim", "evaluate_claims" },
            ForbiddenTools: new HashSet<string>(),
            MaxCost: _options.MaxCost,
            TimeoutSeconds: _options.TimeoutSeconds);

        var runtime = new AgentRuntime(tools, _auditStore, _modelPricing, _modelCostStore);
        var session = new AgentRunSession(contract);
        var toolContext = new ToolExecutionContext(correlationId, IntentId: null, ProductionId: productionId);

        // The result is intentionally not acted on here: ResearchStageHandler re-checks the DB state
        // (SPEC/26) and decides advance / rework / block. A run that fails on budget or protocol still
        // leaves whatever verified claims it managed to record.
        await runtime.RunAgentAsync(
            contract, BuildSystemPrompt(prod), toolContext, _gateway, _options.ModelId, session,
            toolCosts: null, maxIterations: _options.MaxIterations, ct: ct);
    }

    private static string BuildSystemPrompt(Production prod) => $@"
You are the research agent for an autonomous video production.
Topic: {prod.Title ?? "(untitled)"}
Language: {prod.Language}
Niche: {prod.NicheId ?? "general"}

Goal: establish the factual claims this video will make, each backed by evidence (SPEC/26).

You do NOT answer the topic yourself. Your only output that counts is what you write to the database
through the tools. A final answer is worthless unless you have already recorded and verified claims.

Required sequence, every run:
1. fetch_source at least TWICE, for INDEPENDENT authoritative sources (distinct publishers), passing a
   real https URL. It stores and content-hashes the page and returns a source id.
2. record_claim for each MATERIAL factual claim, with the claim text and the ids of >= 2 independent
   sources that support it. Never state a claim's verification status yourself.
3. evaluate_claims so the system scores what you recorded.
4. If evaluate_claims reports material claims that are not verified, fetch more independent sources,
   record_claim again (or link more sources), and evaluate_claims again.
5. Only then finish: {{""final"": ""<short summary of the verified claims>""}}.

Do not send a final envelope before step 3 has run at least once. If you do, you will be told to go
back and use your tools.".Trim();
}
