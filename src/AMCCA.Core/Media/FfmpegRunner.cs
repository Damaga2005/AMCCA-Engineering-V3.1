using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Media;

public sealed record FfmpegResult(int ExitCode, string StdErrTail, bool TimedOut);

/// <summary>Runs an ffmpeg command. Seam so the render job is testable without ffmpeg installed.</summary>
public interface IFfmpegRunner
{
    Task<FfmpegResult> RunAsync(
        IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// SPEC/33 (D-008): ffmpeg is invoked through ProcessStartInfo with an argument list — never a shell
/// string. stdin is closed (<c>-nostdin</c> and no redirect), stderr is captured for the error tail,
/// and a timeout kills the whole process tree.
/// </summary>
public sealed class ProcessFfmpegRunner : IFfmpegRunner
{
    private readonly string _ffmpegPath;
    private const int StdErrTailChars = 4000;

    public ProcessFfmpegRunner(string ffmpegPath = "ffmpeg") => _ffmpegPath = ffmpegPath;

    public async Task<FfmpegResult> RunAsync(
        IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"ffmpeg ('{_ffmpegPath}') could not be started.");

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.Append(e.Data).Append('\n');
            if (stderr.Length > StdErrTailChars * 2) stderr.Remove(0, stderr.Length - StdErrTailChars);
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new FfmpegResult(-1, Tail(stderr), TimedOut: true);
        }

        return new FfmpegResult(process.ExitCode, Tail(stderr), TimedOut: false);
    }

    private static string Tail(StringBuilder sb)
        => sb.Length <= StdErrTailChars ? sb.ToString() : sb.ToString(sb.Length - StdErrTailChars, StdErrTailChars);
}
