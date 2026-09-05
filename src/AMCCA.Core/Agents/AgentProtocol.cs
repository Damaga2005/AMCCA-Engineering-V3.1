using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AMCCA.Core.Agents;

internal enum AgentMessageKind { ToolCall, Final, Unparseable }

internal sealed record AgentMessage(
    AgentMessageKind Kind,
    string? ToolId = null,
    string? ToolInputJson = null,
    string? FinalJson = null);

/// <summary>
/// The wire protocol between <see cref="AgentRuntime.RunAgentAsync"/> and the model. The transport is
/// the plain text completion API that <see cref="Providers.IProviderGateway"/> already exposes — the
/// model is instructed to end each turn with a single JSON envelope:
///
///   {"tool": "&lt;tool_id&gt;", "input": { ... }}   to call a tool
///   {"final": &lt;value&gt;}                          to finish (value is a string, or an object when the
///                                                    contract declares an output schema)
///
/// ponytail: this text envelope is the stopgap while the gateway has no native tool-calling. When a
/// gateway that returns structured tool_calls / finish_reason is added, replace only <see cref="Parse"/>
/// and <see cref="Instructions"/>; the loop in RunAgentAsync stays the same.
/// </summary>
internal static class AgentProtocol
{
    public static string Instructions(IEnumerable<string> allowedTools, bool structuredFinal)
    {
        var tools = allowedTools.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("You work in a strict loop. End every message with exactly one JSON envelope and nothing after it.");
        sb.AppendLine("To call a tool: {\"tool\": \"<tool_id>\", \"input\": { <arguments> }}");
        sb.AppendLine(structuredFinal
            ? "To finish: {\"final\": { <object matching the required output schema> }}"
            : "To finish: {\"final\": \"<your answer as a string>\"}");
        sb.AppendLine(tools.Count > 0
            ? $"Allowed tools: {string.Join(", ", tools)}. Calling any other tool ends the run in failure."
            : "No tools are available; produce a final answer.");
        sb.AppendLine("A tool result is fed back to you as a message prefixed with TOOL_RESULT. React to it, then send the next envelope.");
        return sb.ToString();
    }

    public static AgentMessage Parse(string modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return new AgentMessage(AgentMessageKind.Unparseable);
        }

        var json = ExtractJsonEnvelope(modelText);
        if (json is null)
        {
            return new AgentMessage(AgentMessageKind.Unparseable);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AgentMessage(AgentMessageKind.Unparseable);
            }

            if (root.TryGetProperty("final", out var final))
            {
                var finalJson = final.ValueKind == JsonValueKind.String ? final.GetString() ?? "" : final.GetRawText();
                return new AgentMessage(AgentMessageKind.Final, FinalJson: finalJson);
            }

            if (root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
            {
                var toolId = tool.GetString();
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    return new AgentMessage(AgentMessageKind.Unparseable);
                }
                var input = root.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                return new AgentMessage(AgentMessageKind.ToolCall, ToolId: toolId, ToolInputJson: input);
            }

            return new AgentMessage(AgentMessageKind.Unparseable);
        }
        catch (JsonException)
        {
            return new AgentMessage(AgentMessageKind.Unparseable);
        }
    }

    /// <summary>
    /// The last JSON object in the text: prefer a ```json fenced block, else the last substring starting
    /// at a '{' that parses as JSON.
    /// </summary>
    private static string? ExtractJsonEnvelope(string text)
    {
        var fenceStart = text.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart >= 0)
        {
            var bodyStart = text.IndexOf('\n', fenceStart);
            var fenceEnd = bodyStart >= 0 ? text.IndexOf("```", bodyStart, StringComparison.Ordinal) : -1;
            if (bodyStart >= 0 && fenceEnd > bodyStart)
            {
                return text.Substring(bodyStart + 1, fenceEnd - bodyStart - 1).Trim();
            }
        }

        for (int i = text.LastIndexOf('{'); i >= 0; i = text.LastIndexOf('{', i - 1))
        {
            var candidate = text.Substring(i).Trim();
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException)
            {
                // keep scanning earlier '{'
            }
            if (i == 0) break;
        }
        return null;
    }
}
