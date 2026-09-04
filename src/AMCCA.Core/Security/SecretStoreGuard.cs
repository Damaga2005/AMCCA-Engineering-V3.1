using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

/// <summary>
/// SEC-05: the production composition root calls this before startup completes. If the resolved
/// secret store is ephemeral (in-memory), or absent, startup fails closed with AMCCA-SEC-002
/// rather than silently running with credentials that never persist and are visible in a dump.
/// </summary>
public static class SecretStoreGuard
{
    public static void EnsureProductionGrade(ISecretStore? secretStore)
    {
        if (secretStore is null)
        {
            throw new AmccaException(
                AmccaErrors.Sec002,
                ErrorCategory.Security,
                "No secret store is configured. A production runtime requires an OS-backed secret store (SEC-05).");
        }

        if (secretStore is IEphemeralSecretStore)
        {
            throw new AmccaException(
                AmccaErrors.Sec002,
                ErrorCategory.Security,
                $"Secret store '{secretStore.GetType().Name}' is an in-memory development store and cannot be used in production (SEC-05).");
        }
    }
}
