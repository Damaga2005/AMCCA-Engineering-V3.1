using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Security;

public class OAuthLoopbackReceiver : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly string _redirectUri;

    public string RedirectUri => _redirectUri;

    public OAuthLoopbackReceiver(int? port = null)
    {
        _port = port ?? GetRandomUnusedPort();
        _redirectUri = $"http://127.0.0.1:{_port}/callback/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_redirectUri);
    }

    public void Start()
    {
        _listener.Start();
    }

    public Task<OAuthCallbackResult> WaitForCallbackAsync(string expectedState, TimeSpan timeout, CancellationToken ct = default)
    {
        // A browser callback must not be lost because the thread pool is saturated (the CI chaos
        // and concurrency suites do exactly that). Everything here runs on one dedicated thread
        // using the blocking HttpListener API, so there are no thread-pool-scheduled continuations
        // to starve. The timeout unblocks the blocking GetContext() by stopping the listener.
        var tcs = new TaskCompletionSource<OAuthCallbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(WaitForCallback(expectedState, timeout, ct)); }
            catch (Exception ex) { tcs.SetResult(new OAuthCallbackResult(false, null, null, ex.Message)); }
        })
        {
            IsBackground = true,
            Name = "oauth-loopback-callback",
        };
        thread.Start();
        return tcs.Task;
    }

    private OAuthCallbackResult WaitForCallback(string expectedState, TimeSpan timeout, CancellationToken ct)
    {
        HttpListenerContext context;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(timeout);
            using var reg = timeoutCts.Token.Register(() => { try { _listener.Stop(); } catch { } });
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or ObjectDisposedException)
            {
                return new OAuthCallbackResult(false, null, null, "Timeout waiting for OAuth callback");
            }
        }

        var query = context.Request.QueryString;
        var receivedState = query["state"];
        var code = query["code"];
        var error = query["error"];

        bool stateMatches = !string.IsNullOrEmpty(expectedState) && string.Equals(expectedState, receivedState, StringComparison.Ordinal);
        bool success = stateMatches && !string.IsNullOrEmpty(code);

        // Best-effort browser response: a client that already navigated away (or a CI runner that
        // hung up on its own timeout) must not turn a callback we did parse into a failure.
        try
        {
            var response = context.Response;
            var buffer = Encoding.UTF8.GetBytes(success
                ? "<html><body><h1>AMCCA Authorization Succeeded</h1><p>You can close this tab and return to AMCCA.</p></body></html>"
                : "<html><body><h1>AMCCA Authorization Failed</h1><p>Mismatched state or authorization denied.</p></body></html>");
            response.StatusCode = success ? 200 : 400;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // fall through with whatever we parsed off the request
        }

        if (!stateMatches)
        {
            return new OAuthCallbackResult(false, null, receivedState, "Mismatched OAuth state token (possible CSRF attempt)");
        }

        if (!string.IsNullOrEmpty(error))
        {
            return new OAuthCallbackResult(false, null, receivedState, $"OAuth error: {error}");
        }

        if (string.IsNullOrEmpty(code))
        {
            return new OAuthCallbackResult(false, null, receivedState, "Missing authorization code in callback");
        }

        return new OAuthCallbackResult(true, code, receivedState, null);
    }

    private static int GetRandomUnusedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
            _listener.Close();
        }
        catch { }
    }
}
