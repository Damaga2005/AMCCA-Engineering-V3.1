using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Security;

public class InMemorySecretStore : ISecretStore
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
