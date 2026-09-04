namespace AMCCA.Core.Security;

/// <summary>
/// SEC-05: marks an <see cref="ISecretStore"/> that keeps secrets in process memory only.
/// Such a store is for tests and local development and MUST NOT back a production runtime.
/// <see cref="SecretStoreGuard.EnsureProductionGrade"/> rejects any store carrying this marker.
/// </summary>
public interface IEphemeralSecretStore
{
}
