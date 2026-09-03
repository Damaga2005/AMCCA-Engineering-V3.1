using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Security;

[SupportedOSPlatform("windows")]
public class WindowsDpapiSecretStore : ISecretStore
{
    private readonly string _baseDirectory;

    public WindowsDpapiSecretStore(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AMCCA", "secrets");
    }

    private string GetFilePath(SecretReference secretRef)
    {
        var safeVault = SanitizeFileName(secretRef.Vault);
        var safeName = SanitizeFileName(secretRef.Name);
        return Path.Combine(_baseDirectory, safeVault, $"{safeName}.dat");
    }

    private static string SanitizeFileName(string input) =>
        string.Join("_", input.Split(Path.GetInvalidFileNameChars()));

    public Task<string?> GetSecretAsync(SecretReference secretRef, CancellationToken ct = default)
    {
        var path = GetFilePath(secretRef);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);

        var encryptedBytes = File.ReadAllBytes(path);
        var decryptedBytes = ProtectedData.Unprotect(
            encryptedBytes,
            optionalEntropy: Encoding.UTF8.GetBytes(secretRef.Vault),
            scope: DataProtectionScope.CurrentUser);

        return Task.FromResult<string?>(Encoding.UTF8.GetString(decryptedBytes));
    }

    public Task SetSecretAsync(SecretReference secretRef, string value, CancellationToken ct = default)
    {
        var path = GetFilePath(secretRef);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var encryptedBytes = ProtectedData.Protect(
            plainBytes,
            optionalEntropy: Encoding.UTF8.GetBytes(secretRef.Vault),
            scope: DataProtectionScope.CurrentUser);

        File.WriteAllBytes(path, encryptedBytes);
        return Task.CompletedTask;
    }

    public Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
