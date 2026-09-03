using System.Collections.Generic;

namespace AMCCA.Core.Agents;

public record AgentContract(
    string AgentId,
    string AgentVersion,
    IReadOnlySet<string> AllowedTools,
    IReadOnlySet<string> ForbiddenTools,
    decimal MaxCost,
    int TimeoutSeconds,
    string? InputSchemaJson = null,
    string? OutputSchemaJson = null);
