using System.Net.Http;

namespace AMCCA.Core.Security;

/// <summary>
/// Contract for obtaining HttpClient instances with mandatory SSRF protection (DEF-CERT-003, SPEC/28).
/// </summary>
public interface ISafeHttpClientFactory
{
    HttpClient CreateClient();
}
