namespace AMCCA.Core.Research;

public class Source
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? PublishedAt { get; set; }
    public string RetrievedAt { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string TrustTier { get; set; } = "UNRATED"; // PRIMARY, SECONDARY, AGGREGATOR, UNRATED
    public bool RobotsAllowed { get; set; } = true;
    public string CreatedAt { get; set; } = string.Empty;
}

public class Claim
{
    public string Id { get; set; } = string.Empty;
    public string ProductionId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = "UNKNOWN"; // VERIFIED, DISPUTED, ESTIMATED, UNKNOWN
    public string Materiality { get; set; } = "MATERIAL"; // MATERIAL, CONTEXT, BACKGROUND
    public string SubjectClass { get; set; } = "GENERAL";
    public bool ContainsPersonalData { get; set; }
    public string SchemaVersion { get; set; } = "3.1.0";
    public string CreatedAt { get; set; } = string.Empty;
}

public class ClaimSource
{
    public string ClaimId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string Relation { get; set; } = "SUPPORTS"; // SUPPORTS, CONTRADICTS, CONTEXT
    public string? ExcerptHash { get; set; }
}
