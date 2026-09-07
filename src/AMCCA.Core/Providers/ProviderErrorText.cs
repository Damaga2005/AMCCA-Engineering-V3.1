using System.Text.RegularExpressions;

namespace AMCCA.Core.Providers;

/// <summary>
/// Trims a model-provider error body / exception message before it is allowed into an
/// <see cref="AMCCA.Core.Contracts.AmccaException"/> message (which reaches the Serilog file log and
/// the orchestrator tick report). A 4xx body is useful for diagnosis but is attacker-influenced and
/// some OpenAI-compatible providers echo request material — so cap the length and redact anything
/// shaped like a bearer token or API key. Mirrors the SEC-10 posture for OAuth errors.
/// </summary>
internal static class ProviderErrorText
{
    private static readonly Regex SecretShaped = new(
        @"(?i)(bearer\s+|sk-|api[_-]?key""?\s*[:=]\s*""?)[A-Za-z0-9._\-]{6,}",
        RegexOptions.Compiled);

    public static string Sanitize(string? text, int max = 300)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var s = SecretShaped.Replace(text, "$1[REDACTED]");
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
