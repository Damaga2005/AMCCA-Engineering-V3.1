using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

/// <summary>
/// Safe HTTP client factory enforcing coupled DNS/IP connection validation (DEF-014)
/// and rigorous redirect validation (DEF-CERT-003, SPEC/28, S-06).
/// </summary>
public class SafeHttpClientFactory : ISafeHttpClientFactory
{
    public static readonly SafeHttpClientFactory Default = new();

    public virtual HttpClient CreateClient()
    {
        var socketsHandler = SsrfValidator.CreateSafeSocketsHttpHandler();
        socketsHandler.AllowAutoRedirect = false; // Redirects must be validated hop-by-hop

        var redirectHandler = new SafeRedirectHandler(socketsHandler);
        return new HttpClient(redirectHandler, disposeHandler: true);
    }
}

/// <summary>
/// DelegatingHandler that intercepts all HTTP redirects (301, 302, 303, 307, 308)
/// and strictly validates every redirect target against SSRF policy before following.
/// </summary>
public class SafeRedirectHandler : DelegatingHandler
{
    public const int MaxRedirects = 5;

    public SafeRedirectHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == null)
        {
            throw new AmccaException(
                AmccaErrors.Sec003,
                ErrorCategory.Security,
                "Request URI cannot be null.");
        }

        // Validate initial target URI
        SsrfValidator.ValidateDestinationUri(request.RequestUri);

        var currentRequest = request;
        int redirectCount = 0;

        while (true)
        {
            var response = await base.SendAsync(currentRequest, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                redirectCount++;
                if (redirectCount > MaxRedirects)
                {
                    response.Dispose();
                    throw new AmccaException(
                        AmccaErrors.Sec003,
                        ErrorCategory.Security,
                        $"Exceeded maximum allowed redirect hops ({MaxRedirects}).");
                }

                var location = response.Headers.Location;
                if (location == null)
                {
                    return response;
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentRequest.RequestUri!, location);

                // STRICT VALIDATION OF REDIRECT DESTINATION (DEF-CERT-003, Section 18)
                // If a redirect points to loopback, private IP, cloud metadata, or unsafe scheme, it is blocked immediately.
                SsrfValidator.ValidateDestinationUri(nextUri);

                var nextRequest = new HttpRequestMessage(HttpMethod.Get, nextUri);
                // Copy headers if needed
                foreach (var header in currentRequest.Headers)
                {
                    if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                response.Dispose();
                if (!ReferenceEquals(currentRequest, request))
                {
                    currentRequest.Dispose();
                }

                currentRequest = nextRequest;
                continue;
            }

            return response;
        }
    }

    private static bool IsRedirect(HttpStatusCode code)
    {
        return code == HttpStatusCode.MovedPermanently ||
               code == HttpStatusCode.Found ||
               code == HttpStatusCode.SeeOther ||
               code == HttpStatusCode.TemporaryRedirect ||
               (int)code == 308;
    }
}
