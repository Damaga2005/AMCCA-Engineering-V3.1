using System;
using System.Net.Http;
using AMCCA.Core.Security;

namespace AMCCA.Core.Tests;

/// <summary>
/// Test double for <see cref="ISafeHttpClientFactory"/>. SEC-02/SEC-11: production code no longer
/// accepts an arbitrary <see cref="HttpClient"/>; tests inject their transport through this factory.
/// Pass <c>wrapInRedirectGuard: true</c> to exercise the real <see cref="SafeRedirectHandler"/>
/// on top of a mock inner handler (SEC-04).
/// </summary>
internal sealed class FakeSafeHttpClientFactory : ISafeHttpClientFactory
{
    private readonly Func<HttpClient> _create;

    public FakeSafeHttpClientFactory(HttpMessageHandler handler, bool wrapInRedirectGuard = false)
    {
        _create = () => new HttpClient(wrapInRedirectGuard ? new SafeRedirectHandler(handler) : handler);
    }

    public HttpClient CreateClient() => _create();
}
