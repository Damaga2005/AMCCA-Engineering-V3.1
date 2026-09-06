using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Agents;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Providers;
using AMCCA.Core.Scripts;
using Dapper;

namespace AMCCA.Core.Orchestration.Handlers;

public sealed record ScriptAgentOptions(string ModelId, decimal MaxCost, int TimeoutSeconds, int MaxIterations)
{
    // ponytail: see ResearchAgentOptions — ModelId is a constant until config carries one.
    public static ScriptAgentOptions Default => new("gpt-4o-mini", MaxCost: 2.00m, TimeoutSeconds: 300, MaxIterations: 6);
}

/// <summary>
/// The generative half of SCRIPTING (SPEC/32): asks the model for a script whose every material
/// factual line maps to a VERIFIED claim of the production, validates the JSON against a schema, stores
/// it as the CURRENT SCRIPT artifact, and returns it. <see cref="ScriptStageHandler"/> then runs
/// <see cref="ScriptValidator"/> over it.
/// </summary>
public sealed class AgentScriptAgent : IScriptAgent
{
    private const string OutputSchema = """
    {
      "type": "object", "required": ["lines"], "additionalProperties": false,
      "properties": {
        "estimated_spoken_duration_sec": { "type": "integer", "minimum": 1 },
        "lines": {
          "type": "array", "minItems": 1,
          "items": {
            "type": "object", "required": ["line_number", "text", "is_material_fact"], "additionalProperties": false,
            "properties": {
              "line_number": { "type": "integer" },
              "text": { "type": "string" },
              "claim_id": { "type": ["string", "null"] },
              "is_material_fact": { "type": "boolean" },
              "uncertainty_wording_present": { "type": "boolean" }
            }
          }
        }
      }
    }
    """;

    private readonly ProductionService _productions;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IAuditStore _auditStore;
    private readonly IProviderGateway _gateway;
    private readonly ArtifactStore _artifacts;
    private readonly ScriptAgentOptions _options;
    private readonly IModelPricing? _modelPricing;
    private readonly IModelCostStore? _modelCostStore;

    public AgentScriptAgent(
        ProductionService productions,
        DatabaseConnectionFactory connectionFactory,
        IAuditStore auditStore,
        IProviderGateway gateway,
        ArtifactStore artifacts,
        ScriptAgentOptions? options = null,
        IModelPricing? modelPricing = null,
        IModelCostStore? modelCostStore = null)
    {
        _productions = productions;
        _connectionFactory = connectionFactory;
        _auditStore = auditStore;
        _gateway = gateway;
        _artifacts = artifacts;
        _options = options ?? ScriptAgentOptions.Default;
        _modelPricing = modelPricing;
        _modelCostStore = modelCostStore;
    }

    public async Task<ScriptDocument> GenerateScriptAsync(string productionId, string correlationId, CancellationToken ct = default)
    {
        var prod = await _productions.GetProductionAsync(productionId, ct)
            ?? throw new AmccaException(AmccaErrors.Res001, ErrorCategory.Validation, $"Production '{productionId}' not found.");

        var claims = await LoadVerifiedClaimsAsync(productionId, ct);
        if (claims.Count == 0)
        {
            throw new AmccaException(AmccaErrors.Res001, ErrorCategory.Validation,
                $"Production '{productionId}' has no verified claims to build a script from (SPEC/32).");
        }

        var contract = new AgentContract(
            AgentId: "script-agent", AgentVersion: "1.0",
            AllowedTools: new HashSet<string>(), ForbiddenTools: new HashSet<string>(),
            MaxCost: _options.MaxCost, TimeoutSeconds: _options.TimeoutSeconds,
            OutputSchemaJson: OutputSchema);

        var runtime = new AgentRuntime(new Tools.ToolRegistry(), _auditStore, _modelPricing, _modelCostStore);
        var session = new AgentRunSession(contract);
        var toolContext = new Tools.ToolExecutionContext(correlationId, IntentId: null, ProductionId: productionId);

        var result = await runtime.RunAgentAsync(
            contract, BuildSystemPrompt(prod, claims), toolContext, _gateway, _options.ModelId, session,
            toolCosts: null, maxIterations: _options.MaxIterations, ct: ct);

        if (result.Status != AgentRunStatus.Completed || string.IsNullOrWhiteSpace(result.FinalOutput))
        {
            throw new AmccaException(AmccaErrors.Res001, ErrorCategory.Validation,
                $"Script agent did not produce a script ({result.ReasonCode}: {result.Detail}).");
        }

        var script = ScriptDocumentSerializer.Deserialize(productionId, result.FinalOutput);
        await _artifacts.PutTextVersionAsync(productionId, "SCRIPT", result.FinalOutput, generatorModelId: _options.ModelId, ct: ct);
        return script;
    }

    private async Task<IReadOnlyList<(string Id, string Text, string Status)>> LoadVerifiedClaimsAsync(string productionId, CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(string Id, string Text, string Status)>(new CommandDefinition(
            "SELECT id AS Id, text AS Text, status AS Status FROM claims WHERE production_id = @P AND materiality = 'MATERIAL' AND status = 'VERIFIED';",
            new { P = productionId }, cancellationToken: ct));
        return rows.ToList();
    }

    private static string BuildSystemPrompt(Production prod, IReadOnlyList<(string Id, string Text, string Status)> claims)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the script agent for an autonomous video production (SPEC/32).");
        sb.AppendLine($"Topic: {prod.Title ?? "(untitled)"}   Language: {prod.Language}");
        sb.AppendLine();
        sb.AppendLine("Verified claims you may assert (use the exact claim_id for any material factual line):");
        foreach (var c in claims)
        {
            sb.AppendLine($"  - {c.Id}: {c.Text}");
        }
        sb.AppendLine();
        sb.AppendLine("Write a short spoken script. Rules:");
        sb.AppendLine("- Every line that states a material fact MUST set is_material_fact=true and claim_id to one of the ids above.");
        sb.AppendLine("- Non-factual lines (hook, transitions, CTA) set is_material_fact=false and claim_id=null.");
        sb.AppendLine("- Do not assert any fact that is not in the list.");
        sb.AppendLine("- Finish with {\"final\": { ...the script object matching the schema... }}.");
        return sb.ToString();
    }
}
