using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Security;
using Dapper;

namespace AMCCA.Core.Preflight;

public interface IPreflightService
{
    Task<PreflightReport> RunSystemStartupPreflightAsync(
        AmccaConfig config,
        ISecretStore secretStore,
        CancellationToken ct = default);
}

/// <summary>
/// System startup preflight (SPEC/49). Runs the ten startup gates in order and stops early on the
/// first ABORT/HALT-class failure, since later checks (e.g. clock plausibility against recorded events)
/// are meaningless once the database or secret store is unusable.
/// </summary>
public class PreflightService : IPreflightService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly MigrationService _migrationService;

    public PreflightService(DatabaseConnectionFactory connectionFactory, MigrationService migrationService)
    {
        _connectionFactory = connectionFactory;
        _migrationService = migrationService;
    }

    public async Task<PreflightReport> RunSystemStartupPreflightAsync(
        AmccaConfig config,
        ISecretStore secretStore,
        CancellationToken ct = default)
    {
        var report = new PreflightReport();

        // Checks 1 & 2 (schema validation, no literal credential) already ran when AmccaConfig was
        // constructed via ConfigService.LoadFromYaml/LoadFromJson -- an AmccaConfig instance cannot
        // exist here otherwise. Check 3 (budget consistency) is re-verified below as defense in depth,
        // since a caller could in principle hand-construct an AmccaConfig bypassing ConfigService.

        // Gate 3: budget consistency (SPEC/03, AMCCA-CFG-004)
        if (!TryValidateBudgetConsistency(config, out var budgetError))
        {
            report.Status = PreflightStatus.Abort;
            report.FailureDetails.Add($"{AmccaErrors.Cfg004}: {budgetError}");
            return report;
        }

        // Gate 4 & 5: database opens with WAL/foreign_keys on, and migrations are current with
        // matching checksums. MigrationService.UpgradeAsync opens a connection (enforcing gate 4)
        // and applies/verifies migrations (gate 5), throwing AmccaErrors.Db001/Db002 on failure.
        try
        {
            await _migrationService.UpgradeAsync(ct);
        }
        catch (AmccaException ex)
        {
            report.Status = PreflightStatus.Abort;
            report.FailureDetails.Add(ex.Message);
            return report;
        }

        // Gate 6: secret store reachability
        bool isStoreReachable;
        try
        {
            isStoreReachable = await secretStore.IsReachableAsync(ct);
        }
        catch (Exception ex)
        {
            isStoreReachable = false;
            report.FailureDetails.Add($"Secret store exception: {ex.Message}");
        }

        if (!isStoreReachable)
        {
            report.Status = PreflightStatus.Abort;
            report.FailureDetails.Add("Secret store unreachable. Cannot resolve required credentials at startup.");
            return report;
        }

        // Gate 7: data_root writable, free space above minimum (AMCCA-STO-001, degraded start)
        CheckDataRoot(config, report);

        // Gate 8: FFmpeg present (degraded start; media disabled). No supported version range is
        // documented anywhere in SPEC/DECISIONS, so only presence/executability is enforced here --
        // inventing a version floor/ceiling would violate the "do not invent capabilities" rule.
        await CheckFfmpegAsync(report, ct);

        // Gate 9: system clock plausible against last recorded event time (warn only)
        await CheckClockPlausibilityAsync(report, ct);

        // Gate 10: kill-switch state loaded; EMERGENCY_STOP halts startup
        await CheckKillSwitchAsync(report, ct);

        return report;
    }

    private static bool TryValidateBudgetConsistency(AmccaConfig config, out string error)
    {
        var b = config.Budgets;
        try
        {
            var perProduction = decimal.Parse(b.PerProduction, System.Globalization.CultureInfo.InvariantCulture);
            var perRework = decimal.Parse(b.PerRework, System.Globalization.CultureInfo.InvariantCulture);
            var perRecovery = decimal.Parse(b.PerRecovery, System.Globalization.CultureInfo.InvariantCulture);
            var daily = decimal.Parse(b.Daily, System.Globalization.CultureInfo.InvariantCulture);
            var monthly = decimal.Parse(b.Monthly, System.Globalization.CultureInfo.InvariantCulture);

            if (perProduction < 0 || perRework < 0 || perRecovery < 0 || daily < 0 || monthly < 0)
            {
                error = "Budget amounts must be non-negative.";
                return false;
            }

            if (!(perProduction <= daily && daily <= monthly))
            {
                error = $"Budget window rule violated: per_production ({perProduction}) <= daily ({daily}) <= monthly ({monthly}) does not hold.";
                return false;
            }

            if (!(b.WarnPercent < b.PausePercent && b.PausePercent < b.BlockPercent && b.BlockPercent <= 100))
            {
                error = $"Budget threshold ordering violated: warn ({b.WarnPercent}) < pause ({b.PausePercent}) < block ({b.BlockPercent}) <= 100 does not hold.";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Budget configuration is not parseable: {ex.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void CheckDataRoot(AmccaConfig config, PreflightReport report)
    {
        if (string.IsNullOrWhiteSpace(config.DataRoot))
        {
            report.Status = PreflightStatus.Degraded;
            report.Warnings.Add("Data root is not configured; running in memory / fallback storage.");
            return;
        }

        try
        {
            Directory.CreateDirectory(config.DataRoot);

            var probePath = Path.Combine(config.DataRoot, $".preflight-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "preflight");
            File.Delete(probePath);

            var root = Path.GetPathRoot(Path.GetFullPath(config.DataRoot));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                var minimumBytes = (long)config.Storage.MinimumFreeGb * 1024L * 1024L * 1024L;
                if (drive.AvailableFreeSpace < minimumBytes)
                {
                    report.Status = PreflightStatus.Degraded;
                    report.Warnings.Add(
                        $"{AmccaErrors.Sto001}: free space on '{root}' ({drive.AvailableFreeSpace / (1024 * 1024)} MB) is below the configured minimum ({config.Storage.MinimumFreeGb} GB).");
                }
            }
        }
        catch (Exception ex)
        {
            report.Status = PreflightStatus.Degraded;
            report.Warnings.Add($"{AmccaErrors.Sto001}: data_root '{config.DataRoot}' is not writable: {ex.Message}");
        }
    }

    private static async Task CheckFfmpegAsync(PreflightReport report, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                report.Status = PreflightStatus.Degraded;
                report.Warnings.Add("FFmpeg could not be started; media rendering is disabled for this session.");
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                report.Status = PreflightStatus.Degraded;
                report.Warnings.Add("FFmpeg version check timed out; media rendering is disabled for this session.");
                return;
            }

            if (process.ExitCode != 0)
            {
                report.Status = PreflightStatus.Degraded;
                report.Warnings.Add($"FFmpeg exited with code {process.ExitCode}; media rendering is disabled for this session.");
            }
        }
        catch (Exception ex)
        {
            report.Status = PreflightStatus.Degraded;
            report.Warnings.Add($"FFmpeg is not present or not executable ({ex.Message}); media rendering is disabled for this session.");
        }
    }

    private async Task CheckClockPlausibilityAsync(PreflightReport report, CancellationToken ct)
    {
        try
        {
            using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
            var lastEventAt = await connection.ExecuteScalarAsync<string?>(
                "SELECT MAX(occurred_at) FROM events;");

            if (!string.IsNullOrEmpty(lastEventAt) &&
                DateTimeOffset.TryParse(lastEventAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastEvent))
            {
                if (DateTimeOffset.UtcNow < lastEvent)
                {
                    report.Warnings.Add(
                        $"System clock ({DateTimeOffset.UtcNow:O}) is behind the last recorded event ({lastEvent:O}); lease and budget window logic depends on a monotonic clock.");
                }
            }
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"Could not verify clock plausibility against recorded events: {ex.Message}");
        }
    }

    private async Task CheckKillSwitchAsync(PreflightReport report, CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var mode = await connection.ExecuteScalarAsync<string?>(
            "SELECT mode FROM kill_switch_state WHERE id = 1;");

        if (string.Equals(mode, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase))
        {
            report.Status = PreflightStatus.Halted;
            report.FailureDetails.Add("Kill switch state is EMERGENCY_STOP; startup halted until an operator clears it.");
        }
    }
}
