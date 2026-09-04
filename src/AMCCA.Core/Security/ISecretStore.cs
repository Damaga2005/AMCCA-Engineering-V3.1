using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Security;

public interface ISecretStore
{
    Task<string?> GetSecretAsync(SecretReference secretRef, CancellationToken ct = default);
    Task SetSecretAsync(SecretReference secretRef, string value, CancellationToken ct = default);
    Task<bool> IsReachableAsync(CancellationToken ct = default);
}
