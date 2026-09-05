namespace AMCCA.Core.Prompts;

public class PromptTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string? CurrentVersionId { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public class PromptVersion
{
    public string Id { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public long VersionNo { get; set; }
    public string BodySha256 { get; set; } = string.Empty;
    public string BodyRef { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class AgentRunRecord
{
    public string Id { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string PromptVersionId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelParamsHash { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public bool OutputValid { get; set; }
    public string State { get; set; } = "STARTED";
    public string Cost { get; set; } = "0.00";
    public string StartedAt { get; set; } = string.Empty;
    public string? CompletedAt { get; set; }
}
