using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Research;

namespace AMCCA.Core.Tools;

/// <summary>
/// <c>fetch_source</c>: fetches a URL (real HTTP, SSRF-validated) and ingests it into <c>sources</c>.
/// Input: {"url": "...", "publisher": "...", "trust_tier": "PRIMARY|SECONDARY|AGGREGATOR|UNRATED"}.
/// Output: {"source_id": "...", "content_hash": "..."}.
/// </summary>
public sealed class FetchSourceTool : ITool
{
    private readonly ResearchService _research;
    public ToolDefinition Definition { get; } =
        new("fetch_source", "1.0", SideEffectClass.READ, Array.Empty<string>(), 30);

    public FetchSourceTool(ResearchService research) => _research = research;

    public async Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(inputJson);
        var root = doc.RootElement;
        var url = GetString(root, "url") ?? throw Bad("fetch_source requires 'url'.");
        var publisher = GetString(root, "publisher") ?? new Uri(url).Host;
        var trustTier = GetString(root, "trust_tier") ?? "UNRATED";

        var source = await _research.FetchAndIngestSourceAsync(url, publisher, trustTier, robotsAllowed: true, ct);
        return JsonSerializer.Serialize(new { source_id = source.Id, content_hash = source.ContentHash });
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static AmccaException Bad(string msg)
        => new(AmccaErrors.Ai003, ErrorCategory.Validation, msg);
}

/// <summary>
/// <c>record_claim</c>: records a claim for the current production, linked to one or more sources.
/// Status is left UNKNOWN — the agent calls <c>evaluate_claims</c> to have <see cref="ClaimValidator"/>
/// decide it (SPEC/26: the agent never sets a claim's verification status directly).
/// Input: {"text": "...", "materiality": "MATERIAL|INCIDENTAL", "subject_class": "...",
///         "sources": [{"source_id": "...", "relation": "SUPPORTS|CONTRADICTS|CONTEXT"}]}.
/// Output: {"claim_id": "..."}.
/// </summary>
public sealed class RecordClaimTool : ITool
{
    private readonly ResearchService _research;
    public ToolDefinition Definition { get; } =
        new("record_claim", "1.0", SideEffectClass.LOCAL_WRITE, Array.Empty<string>(), 15);

    public RecordClaimTool(ResearchService research) => _research = research;

    public async Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.ProductionId))
        {
            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation, "record_claim needs a production context.");
        }

        using var doc = JsonDocument.Parse(inputJson);
        var root = doc.RootElement;
        var claim = new Claim
        {
            ProductionId = context.ProductionId,
            Text = Req(root, "text"),
            Status = "UNKNOWN",
            Materiality = Opt(root, "materiality", "MATERIAL"),
            SubjectClass = Opt(root, "subject_class", "GENERAL"),
        };

        if (!root.TryGetProperty("sources", out var srcs) || srcs.ValueKind != JsonValueKind.Array || srcs.GetArrayLength() == 0)
        {
            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation, "record_claim requires a non-empty 'sources' array.");
        }

        bool first = true;
        foreach (var s in srcs.EnumerateArray())
        {
            var sourceId = Req(s, "source_id");
            var relation = Opt(s, "relation", "SUPPORTS");
            if (first)
            {
                await _research.InsertClaimWithSourceAsync(claim, sourceId, relation, ct: ct);
                first = false;
            }
            else
            {
                await _research.LinkClaimSourceAsync(claim.Id, sourceId, relation, ct: ct);
            }
        }

        return JsonSerializer.Serialize(new { claim_id = claim.Id });
    }

    private static string Req(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation, $"record_claim requires '{name}'.");

    private static string Opt(JsonElement e, string name, string fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : fallback;
}

/// <summary>
/// <c>evaluate_claims</c>: re-runs <see cref="ClaimValidator"/> over the production's claims and writes
/// back their status (SPEC/26). Input: {} (production comes from context). Output: the status breakdown.
/// </summary>
public sealed class EvaluateClaimsTool : ITool
{
    private readonly ResearchService _research;
    public ToolDefinition Definition { get; } =
        new("evaluate_claims", "1.0", SideEffectClass.LOCAL_WRITE, Array.Empty<string>(), 20);

    public EvaluateClaimsTool(ResearchService research) => _research = research;

    public async Task<string> ExecuteAsync(string inputJson, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.ProductionId))
        {
            throw new AmccaException(AmccaErrors.Ai003, ErrorCategory.Validation, "evaluate_claims needs a production context.");
        }

        var s = await _research.EvaluateAllClaimsAsync(context.ProductionId, ct);
        return JsonSerializer.Serialize(new
        {
            verified = s.Verified, estimated = s.Estimated, disputed = s.Disputed, unknown = s.Unknown, total = s.Total,
        });
    }
}
