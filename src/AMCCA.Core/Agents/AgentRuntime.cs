using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using AMCCA.Core.Providers;
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

    /// <summary>
    /// Runs one agent to a final answer: LLM turn → parse envelope → (tool call → <see
    /// cref="ExecuteToolCallAsync"/> → feed result back) → repeat. Stops on a <c>{"final": …}</c>
    /// envelope, or fails on a forbidden tool (AMCCA-AI-004), an exhausted budget (AMCCA-BUD-002 /
    /// Cst002), a repeated protocol or output-schema failure, or the <paramref name="maxIterations"/>
    /// cap (AMCCA-AI-006). <c>contract.TimeoutSeconds</c> cancels the whole run — it surfaces as a raw
    /// <see cref="OperationCanceledException"/>, the same tested convention as a tool timeout (SPEC/05).
    /// </summary>
    public async Task<AgentRunResult> RunAgentAsync(
        AgentContract contract,
        string systemPrompt,
        ToolExecutionContext toolContext,
        IProviderGateway gateway,
        string modelId,
        AgentRunSession session,
        IReadOnlyDictionary<string, decimal>? toolCosts = null,
        int maxIterations = 12,
        double temperature = 0.2,
        int maxTokensPerTurn = 2048,
        CancellationToken ct = default)
    {
        using var linkedCts = contract.TimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        linkedCts?.CancelAfter(TimeSpan.FromSeconds(contract.TimeoutSeconds));
        var effectiveCt = linkedCts?.Token ?? ct;

        bool structuredFinal = !string.IsNullOrWhiteSpace(contract.OutputSchemaJson);
        var transcript = new List<AgentTurn>();
        var convo = new StringBuilder();
        convo.AppendLine(systemPrompt.Trim());
        convo.AppendLine();
        convo.AppendLine(AgentProtocol.Instructions(contract.AllowedTools, structuredFinal));

        int unparseableStreak = 0;
        int schemaFailStreak = 0;

        // Stamp the run's model-token totals onto whatever result we return, so a cost-accounting
        // caller (H1) gets the usage figures the gateway reported instead of them being discarded.
        AgentRunResult Tagged(AgentRunResult r) => r with
        {
            ModelInputTokens = session.ModelInputTokens,
            ModelOutputTokens = session.ModelOutputTokens,
        };

        for (int iteration = 1; iteration <= maxIterations; iteration++)
        {
            if (contract.MaxCost > 0 && session.AccumulatedCost >= contract.MaxCost)
            {
                return Tagged(AgentRunResult.Failed(AmccaErrors.Cst002,
                    $"Agent budget of {contract.MaxCost:F2} is exhausted before iteration {iteration}.",
                    iteration - 1, session.AccumulatedCost, transcript));
            }

            var resp = await gateway.GenerateTextAsync(
                new GatewayTextRequest(modelId, convo.ToString(), temperature, maxTokensPerTurn, toolContext.CorrelationId),
                effectiveCt);
            session.AddModelTokens(resp.InputTokens, resp.OutputTokens);

            var modelText = resp.Text ?? string.Empty;
            transcript.Add(new AgentTurn("assistant", modelText));
            convo.AppendLine();
            convo.AppendLine("ASSISTANT: " + modelText);

            var msg = AgentProtocol.Parse(modelText);

            if (msg.Kind == AgentMessageKind.Unparseable)
            {
                if (++unparseableStreak >= 2)
                {
                    return Tagged(AgentRunResult.Failed(AmccaErrors.Ai006,
                        "Agent did not produce a parseable tool-call or final envelope twice in a row.",
                        iteration, session.AccumulatedCost, transcript));
                }
                const string nudge = "Your message had no valid JSON envelope. Reply with exactly one {\"tool\":...} or {\"final\":...} envelope.";
                transcript.Add(new AgentTurn("tool", nudge));
                convo.AppendLine("TOOL_RESULT: " + nudge);
                continue;
            }
            unparseableStreak = 0;

            if (msg.Kind == AgentMessageKind.Final)
            {
                var finalOutput = msg.FinalJson ?? string.Empty;
                if (structuredFinal)
                {
                    try
                    {
                        ValidateAgentOutput(contract, finalOutput);
                    }
                    catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Ai003)
                    {
                        if (++schemaFailStreak >= 2)
                        {
                            return Tagged(AgentRunResult.Failed(AmccaErrors.Ai003,
                                $"Agent final answer failed its output schema twice: {ex.Message}",
                                iteration, session.AccumulatedCost, transcript));
                        }
                        var msgBack = "Your final answer failed the required output schema: " + ex.Message +
                                      " Fix it and send {\"final\": {...}} again.";
                        transcript.Add(new AgentTurn("tool", msgBack));
                        convo.AppendLine("TOOL_RESULT: " + msgBack);
                        continue;
                    }
                }
                return Tagged(AgentRunResult.Completed(finalOutput, iteration, session.AccumulatedCost, transcript));
            }

            // Tool call — enforcement, cost reservation and execution all live in ExecuteToolCallAsync.
            var toolId = msg.ToolId!;
            var toolCost = toolCosts is not null && toolCosts.TryGetValue(toolId, out var c) ? c : 0m;
            string toolResult;
            try
            {
                toolResult = await ExecuteToolCallAsync(
                    contract, toolId, msg.ToolInputJson ?? "{}", toolContext, toolCost, session, effectiveCt);
            }
            catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Ai004)
            {
                return Tagged(AgentRunResult.Failed(AmccaErrors.Ai004, ex.Message, iteration, session.AccumulatedCost, transcript));
            }
            catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Cst002)
            {
                return Tagged(AgentRunResult.Failed(AmccaErrors.Cst002, ex.Message, iteration, session.AccumulatedCost, transcript));
            }
            catch (AmccaException ex)
            {
                // Recoverable tool error: feed it back so the model can adapt rather than abort the run.
                toolResult = $"{{\"error\": {JsonSerializer.Serialize(ex.Message)}}}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                toolResult = $"{{\"error\": {JsonSerializer.Serialize(ex.Message)}}}";
            }

            transcript.Add(new AgentTurn("tool", toolResult));
            convo.AppendLine("TOOL_RESULT: " + toolResult);
        }

        return Tagged(AgentRunResult.Failed(AmccaErrors.Ai006,
            $"Agent reached the {maxIterations}-iteration limit without a final answer.",
            maxIterations, session.AccumulatedCost, transcript));
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
