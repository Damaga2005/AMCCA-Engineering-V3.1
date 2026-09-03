using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Security;

namespace AMCCA.Core.Preflight;

public interface IPreflightService
{
    Task<PreflightReport> RunSystemStartupPreflightAsync(
        AmccaConfig config,
        ISecretStore secretStore,
        CancellationToken ct = default);
}

public class PreflightService : IPreflightService
{
    public async Task<PreflightReport> RunSystemStartupPreflightAsync(
        AmccaConfig config,
        ISecretStore secretStore,
        CancellationToken ct = default)
    {
        var report = new PreflightReport();

        // Gate 1: Config is already validated by ConfigService (AMCCA-CFG-001)

        // Gate 2: Secret Store reachability (SPEC/49 check 6)
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

        // Gate 3: Data root check (SPEC/49 check 7)
        // If data_root is not set or cannot be accessed, status degrades
        if (string.IsNullOrWhiteSpace(config.DataRoot))
        {
            report.Status = PreflightStatus.Degraded;
            report.Warnings.Add("Data root is not configured; running in memory / fallback storage.");
        }

        return report;
    }
}
