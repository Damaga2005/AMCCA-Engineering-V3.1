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

    public async Task<OAuthCallbackResult> WaitForCallbackAsync(string expectedState, TimeSpan timeout, CancellationToken ct = default)
    {
        HttpListenerContext context;
        try
        {
            // .WaitAsync handles the timeout without a leaked Task.Delay timer or a redundant
            // linked CTS; an orphaned GetContextAsync after a timeout just faults on Dispose.
            context = await _listener.GetContextAsync().WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return new OAuthCallbackResult(false, null, null, "Timeout waiting for OAuth callback");
        }
        catch (Exception ex)
        {
            return new OAuthCallbackResult(false, null, null, ex.Message);
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
            await response.OutputStream.WriteAsync(buffer, ct);
            response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
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
