using System;
using System.Text.RegularExpressions;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

public sealed partial class SecretReference
{
    private static readonly Regex SecretUriRegex = new(
        @"^secret://(?<vault>[a-zA-Z0-9_\-]+)/(?<name>[a-zA-Z0-9_\-]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Vault { get; }
    public string Name { get; }
    public string Uri { get; }

    private SecretReference(string vault, string name, string uri)
    {
        Vault = vault;
        Name = name;
        Uri = uri;
    }

    public static SecretReference Parse(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new AmccaException(
                AmccaErrors.Sec002,
                ErrorCategory.Security,
                "Secret reference URI cannot be null or empty.");
        }

        var match = SecretUriRegex.Match(uri.Trim());
        if (!match.Success)
        {
            throw new AmccaException(
                AmccaErrors.Sec002,
                ErrorCategory.Security,
                $"Invalid secret reference '{uri}'. Format must be 'secret://<vault>/<name>'. Literal credentials are forbidden.");
        }

        return new SecretReference(
            match.Groups["vault"].Value,
            match.Groups["name"].Value,
            uri.Trim());
    }

    public static bool TryParse(string? uri, out SecretReference? secretRef)
    {
        secretRef = null;
        if (string.IsNullOrWhiteSpace(uri)) return false;

        var match = SecretUriRegex.Match(uri.Trim());
        if (!match.Success) return false;

        secretRef = new SecretReference(
            match.Groups["vault"].Value,
            match.Groups["name"].Value,
            uri.Trim());
        return true;
    }

    public override string ToString() => Uri;
}
