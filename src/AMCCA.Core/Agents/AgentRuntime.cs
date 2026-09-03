using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Tools;
using Json.Schema;

namespace AMCCA.Core.Agents;

public class AgentRuntime
{
    private readonly ToolRegistry _toolRegistry;
    private readonly IAuditStore _auditStore;

    public AgentRuntime(ToolRegistry toolRegistry, IAuditStore auditStore)
    {
        _toolRegistry = toolRegistry;
        _auditStore = auditStore;
    }

    public async Task<string> ExecuteToolCallAsync(
        AgentContract contract,
        string toolId,
        string inputJson,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        // 1. Enforce agent contract permissions (AGENTS.md, SPEC/06, SPEC/07)
        bool isForbidden = contract.ForbiddenTools.Contains(toolId);
        bool isAllowed = contract.AllowedTools.Contains(toolId);

        if (isForbidden || !isAllowed)
        {
            // Block and audit the violation (Exit criterion for Phase 6)
            var audit = new AuditRecord(
                AuditId: UlidGenerator.NewUlid(),
                Action: "agent.tool_call_blocked",
                ActorType: "SYSTEM",
                ActorId: "AMCCA.AgentRuntime",
                SubjectType: "agent",
                SubjectId: contract.AgentId,
                ProductionId: context.ProductionId,
                Outcome: "BLOCKED",
                PolicyDecisionId: null,
                ReasonCode: AmccaErrors.Ai004,
                CorrelationId: context.CorrelationId,
                SchemaVersion: "3.1.0",
                OccurredAt: DateTimeOffset.UtcNow.ToString("O"));

            await _auditStore.AppendAuditAsync(audit, ct);

            throw new AmccaException(
                AmccaErrors.Ai004,
                ErrorCategory.Security,
                $"Agent '{contract.AgentId}' attempted to call tool '{toolId}' which is not in its allowed_tools set. Call blocked and audited (AGENTS.md, SPEC/06).");
        }

        // 2. Resolve tool from registry
        var tool = _toolRegistry.GetTool(toolId)
            ?? throw new AmccaException(
                AmccaErrors.Ai004,
                ErrorCategory.Configuration,
                $"Tool '{toolId}' is not registered in ToolRegistry.");

        // 3. Structural invariant: EXTERNAL_UNSAFE requires committed intent before call (SPEC/07, SPEC/15)
        if (tool.Definition.SideEffectClass == SideEffectClass.EXTERNAL_UNSAFE && string.IsNullOrEmpty(context.IntentId))
        {
            throw new AmccaException(
                AmccaErrors.Sec001,
                ErrorCategory.Security,
                $"EXTERNAL_UNSAFE tool '{toolId}' cannot be executed without a committed intent (SPEC/07, SPEC/15).");
        }

        // 4. Execute tool
        return await tool.ExecuteAsync(inputJson, context, ct);
    }

    public void ValidateAgentOutput(AgentContract contract, string outputJson)
    {
        if (string.IsNullOrWhiteSpace(contract.OutputSchemaJson)) return;

        var schema = JsonSchema.FromText(contract.OutputSchemaJson);
        var jsonNode = JsonNode.Parse(outputJson);

        var result = schema.Evaluate(jsonNode, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!result.IsValid)
        {
            var errors = string.Join("; ", result.Details.Where(d => !d.IsValid && d.Errors != null)
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));

            throw new AmccaException(
                AmccaErrors.Ai003,
                ErrorCategory.Validation,
                $"Agent '{contract.AgentId}' output failed schema validation: {errors}. Validation failed (AGENTS.md, SPEC/06).");
        }
    }
}
