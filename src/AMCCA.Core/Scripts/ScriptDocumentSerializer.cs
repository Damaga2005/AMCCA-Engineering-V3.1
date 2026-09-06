using System.Collections.Generic;
using System.Text.Json;

namespace AMCCA.Core.Scripts;

/// <summary>JSON round-trip for a <see cref="ScriptDocument"/> as stored in the SCRIPT artifact.</summary>
public static class ScriptDocumentSerializer
{
    public static ScriptDocument Deserialize(string productionId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var lines = new List<ScriptLine>();
        foreach (var l in root.GetProperty("lines").EnumerateArray())
        {
            lines.Add(new ScriptLine(
                LineNumber: l.GetProperty("line_number").GetInt32(),
                Text: l.GetProperty("text").GetString() ?? "",
                ClaimId: l.TryGetProperty("claim_id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() : null,
                IsMaterialFact: l.GetProperty("is_material_fact").GetBoolean(),
                UncertaintyWordingPresent: l.TryGetProperty("uncertainty_wording_present", out var u) && u.ValueKind == JsonValueKind.True));
        }
        var duration = root.TryGetProperty("estimated_spoken_duration_sec", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt32() : 60;
        return new ScriptDocument(productionId, lines, duration);
    }
}
