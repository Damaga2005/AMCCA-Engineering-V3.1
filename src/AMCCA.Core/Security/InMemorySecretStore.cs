using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Security;

/// <summary>
/// TEST / DEVELOPMENT ONLY. Keeps secrets in a process-memory dictionary — they do not persist,
/// are not OS-protected, and are visible in a memory dump. Marked <see cref="IEphemeralSecretStore"/>
/// so <see cref="SecretStoreGuard.EnsureProductionGrade"/> refuses it in a production runtime (SEC-05).
/// Production uses <see cref="WindowsDpapiSecretStore"/>.
/// </summary>
public class InMemorySecretStore : ISecretStore, IEphemeralSecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly bool _isReachable;

    public InMemorySecretStore(bool isReachable = true)
    {
        _isReachable = isReachable;
    }

    public Task<string?> GetSecretAsync(SecretReference secretRef, CancellationToken ct = default)
    {
        _secrets.TryGetValue(secretRef.Uri, out var val);
        return Task.FromResult(val);
    }

    public Task SetSecretAsync(SecretReference secretRef, string value, CancellationToken ct = default)
    {
        _secrets[secretRef.Uri] = value;
        return Task.CompletedTask;
    }

    public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(_isReachable);
}
