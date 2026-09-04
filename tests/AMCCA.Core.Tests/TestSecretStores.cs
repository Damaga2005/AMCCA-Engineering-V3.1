using AMCCA.Core.Security;

namespace AMCCA.Core.Tests;

/// <summary>
/// Test-only helpers for building an <see cref="ISecretStore"/> pre-seeded with credentials.
/// SEC-01: production code paths resolve credentials through <see cref="ISecretStore"/>; tests
/// must do the same instead of passing a literal API key.
/// </summary>
internal static class TestSecretStores
{
    /// <summary>Returns an in-memory secret store containing exactly one <c>secret://</c> entry.</summary>
    public static InMemorySecretStore With(string secretRefUri, string value)
    {
        var store = new InMemorySecretStore();
        store.SetSecretAsync(SecretReference.Parse(secretRefUri), value).GetAwaiter().GetResult();
        return store;
    }
}
