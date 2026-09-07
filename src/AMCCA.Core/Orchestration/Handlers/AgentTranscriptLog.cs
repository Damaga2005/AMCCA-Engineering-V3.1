using System;
using System.IO;
using System.Text;
using AMCCA.Core.Agents;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// Dumps an agent run's status + full turn transcript to
/// <c>%LOCALAPPDATA%/AMCCA/logs/agent-&lt;stage&gt;-&lt;productionId&gt;.log</c>. `agent_runs` has no writer
/// yet and the transcript is otherwise discarded, which makes a stalled autonomous run undiagnosable.
/// Best-effort: a logging failure never affects the run.
/// </summary>
internal static class AgentTranscriptLog
{
    public static void Write(string stage, string productionId, AgentRunResult result)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AMCCA", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"agent-{stage}-{productionId}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"# {stage} agent — production {productionId}");
            sb.AppendLine($"# {DateTimeOffset.UtcNow:O}");
            sb.AppendLine($"status={result.Status}  reason={result.ReasonCode}  iterations={result.Iterations}");
            sb.AppendLine($"model_tokens in={result.ModelInputTokens} out={result.ModelOutputTokens}  cost={result.ModelCost}");
            if (!string.IsNullOrWhiteSpace(result.Detail)) sb.AppendLine($"detail={result.Detail}");
            if (!string.IsNullOrWhiteSpace(result.FinalOutput)) sb.AppendLine($"final_output={Trunc(result.FinalOutput, 2000)}");
            sb.AppendLine("---- transcript ----");
            foreach (var turn in result.Transcript)
            {
                sb.AppendLine($"[{turn.Role}] {Trunc(turn.Content, 4000)}");
            }
            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // never let diagnostics break a run
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + " …[truncated]";
}
