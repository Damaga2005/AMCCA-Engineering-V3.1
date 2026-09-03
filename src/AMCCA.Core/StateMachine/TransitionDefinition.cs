using System.Text.Json.Serialization;

namespace AMCCA.Core.StateMachine;

public record StateDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("description")] string Description);

public record TransitionDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("guard")] string Guard,
    [property: JsonPropertyName("actor")] string Actor);
