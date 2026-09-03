using System.Collections.Generic;

namespace AMCCA.Core.Tools;

public enum SideEffectClass
{
    PURE,
    READ,
    LOCAL_WRITE,
    EXTERNAL_IDEMPOTENT,
    EXTERNAL_UNSAFE
}

public record ToolDefinition(
    string ToolId,
    string ToolVersion,
    SideEffectClass SideEffectClass,
    IReadOnlyList<string> RequiredPermissions,
    int TimeoutSeconds);

public record ToolExecutionContext(
    string CorrelationId,
    string? IntentId,
    string? ProductionId = null);
