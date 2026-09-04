using System;
using System.IO;
using System.Text.Json;
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
        decimal toolCost = 0m,
        AgentRunSession? session = null,
        CancellationToken ct = default)
    {
        // SEC-06: cost is reserved only after every check that can reject the call (authorization,
        // tool existence, side-effect gate, intent). A blocked operation must consume no budget.

        // 1. Enforce TimeoutSeconds (DEF-005)
        using var linkedCts = contract.TimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if (linkedCts != null)
        {
            linkedCts.CancelAfter(TimeSpan.FromSeconds(contract.TimeoutSeconds));
        }

        var effectiveCt = linkedCts?.Token ?? ct;

        // 2. Enforce agent contract permissions (AGENTS.md, SPEC/06, SPEC/07)
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

            await _auditStore.AppendAuditAsync(audit, effectiveCt);

            throw new AmccaException(
                AmccaErrors.Ai004,
                ErrorCategory.Security,
                $"Agent '{contract.AgentId}' attempted to call tool '{toolId}' which is not in its allowed_tools set. Call blocked and audited (AGENTS.md, SPEC/06).");
        }

        // 3. Resolve tool from registry
        var tool = _toolRegistry.GetTool(toolId)
            ?? throw new AmccaException(
                AmccaErrors.Ai004,
                ErrorCategory.Configuration,
                $"Tool '{toolId}' is not registered in ToolRegistry.");

        // 4. Structural invariant: EXTERNAL_UNSAFE requires committed intent before call (SPEC/07, SPEC/15)
        if (tool.Definition.SideEffectClass == SideEffectClass.EXTERNAL_UNSAFE && string.IsNullOrEmpty(context.IntentId))
        {
            throw new AmccaException(
                AmccaErrors.Sec001,
                ErrorCategory.Security,
                $"EXTERNAL_UNSAFE tool '{toolId}' cannot be executed without a committed intent (SPEC/07, SPEC/15).");
        }

        // 5. Reserve cost — every rejecting check has now passed (SEC-06, DEF-004)
        bool costReserved = false;
        if (session != null)
        {
            if (!session.TryReserveCost(toolCost))
            {
                throw new AmccaException(
                    AmccaErrors.Cst002,
                    ErrorCategory.Validation,
                    $"Agent '{contract.AgentId}' call cost {toolCost:F2} exceeds remaining budget of {contract.MaxCost - session.AccumulatedCost:F2} (DEF-004).");
            }
            costReserved = true;
        }
        else if (toolCost > contract.MaxCost)
        {
            throw new AmccaException(
                AmccaErrors.Cst002,
                ErrorCategory.Validation,
                $"Agent '{contract.AgentId}' call cost {toolCost:F2} exceeds contract MaxCost {contract.MaxCost:F2} (DEF-004).");
        }

        // 6. Execute tool; roll back the reservation if it does not run to completion
        try
        {
            return await tool.ExecuteAsync(inputJson, context, effectiveCt);
        }
        catch
        {
            if (costReserved)
            {
                session!.ReleaseCost(toolCost);
            }
            throw;
        }
    }

    // SEC-07: defensive bounds so a hostile or malformed agent output cannot exhaust memory
    // during parsing/validation. Constants are internal policy, never agent-controlled.
    private const int MaxOutputChars = 512 * 1024;
    private const int MaxJsonDepth = 64;
    private const int MaxTotalProperties = 10_000;
    private const int MaxStringLength = 100_000;
    private const int MaxArrayLength = 10_000;

    public void ValidateAgentOutput(AgentContract contract, string outputJson)
    {
        EnforceOutputResourceLimits(contract.AgentId, outputJson);

        if (string.IsNullOrWhiteSpace(contract.OutputSchemaJson)) return;

        var schema = JsonSchema.FromText(contract.OutputSchemaJson);

        JsonNode? jsonNode;
        try
        {
            jsonNode = JsonNode.Parse(outputJson);
        }
        catch (JsonException ex)
        {
            throw new AmccaException(
                AmccaErrors.Ai003,
                ErrorCategory.Validation,
                $"Agent '{contract.AgentId}' output is not valid JSON: {ex.Message} (AGENTS.md, SPEC/06).");
        }

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

    private static void EnforceOutputResourceLimits(string agentId, string outputJson)
    {
        if (outputJson is null)
        {
            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation,
                $"Agent '{agentId}' produced a null output.");
        }

        if (outputJson.Length > MaxOutputChars)
        {
            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation,
                $"Agent '{agentId}' output is {outputJson.Length} characters, exceeding the {MaxOutputChars} limit (SEC-07).");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(outputJson, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException ex)
        {
            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation,
                $"Agent '{agentId}' output could not be parsed within safe limits (SEC-07): {ex.Message}");
        }

        using (doc)
        {
            var stack = new Stack<JsonElement>();
            stack.Push(doc.RootElement);
            int totalProperties = 0;

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            if (++totalProperties > MaxTotalProperties)
                            {
                                throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation,
                                    $"Agent '{agentId}' output has more than {MaxTotalProperties} properties (SEC-07).");
                            }
                            stack.Push(property.Value);
                        }
                        break;

                    case JsonValueKind.Array:
                        var length = element.GetArrayLength();
                        if (length > MaxArrayLength)
                        {
                            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation,
                                $"Agent '{agentId}' output contains an array of {length} elements, exceeding {MaxArrayLength} (SEC-07).");
                        }
                        foreach (var item in element.EnumerateArray())
                        {
                            stack.Push(item);
                        }
                        break;

                    case JsonValueKind.String:
                        var value = element.GetString();
                        if (value != null && value.Length > MaxStringLength)
                        {
                            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation,
                                $"Agent '{agentId}' output contains a string of {value.Length} characters, exceeding {MaxStringLength} (SEC-07).");
                        }
                        break;
                }
            }
        }
    }
}
