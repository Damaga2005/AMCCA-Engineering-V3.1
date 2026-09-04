namespace AMCCA.Core.Domain;

public class Production
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? BlockedFrom { get; set; }
    public string? UnknownFrom { get; set; }
    public long ReworkAttempts { get; set; }
    public long AggregateVersion { get; set; }
    public string AutonomyMode { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string Language { get; set; } = string.Empty;
    public string? NicheId { get; set; }
    public string? OpportunityId { get; set; }
    public string? CurrentManifestId { get; set; }
    public string SchemaVersion { get; set; } = "3.1.0";
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public class StateTransitionRecord
{
    public string Id { get; set; } = string.Empty;
    public string ProductionId { get; set; } = string.Empty;
    public string TransitionId { get; set; } = string.Empty;
    public string FromState { get; set; } = string.Empty;
    public string ToState { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string OccurredAt { get; set; } = string.Empty;
}
