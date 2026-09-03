using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Tools;

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, CancellationToken ct = default);
}

public class ToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new();

    public void RegisterTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Definition.ToolId))
        {
            throw new ArgumentException("Tool ID cannot be null or empty.", nameof(tool));
        }

        if (!_tools.TryAdd(tool.Definition.ToolId, tool))
        {
            throw new InvalidOperationException($"Tool with ID '{tool.Definition.ToolId}' is already registered.");
        }
    }

    public ITool? GetTool(string toolId)
    {
        _tools.TryGetValue(toolId, out var tool);
        return tool;
    }

    public bool HasTool(string toolId) => _tools.ContainsKey(toolId);
}
