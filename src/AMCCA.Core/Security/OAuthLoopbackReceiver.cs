using System;
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var getContextTask = _listener.GetContextAsync();
            var completedTask = await Task.WhenAny(getContextTask, Task.Delay(timeout, cts.Token));

            if (completedTask != getContextTask)
            {
                return new OAuthCallbackResult(false, null, null, "Timeout waiting for OAuth callback");
            }

            var context = await getContextTask;
            var request = context.Request;
            var query = request.QueryString;

            var receivedState = query["state"];
            var code = query["code"];
            var error = query["error"];

            bool stateMatches = !string.IsNullOrEmpty(expectedState) && string.Equals(expectedState, receivedState, StringComparison.Ordinal);

            // Respond to browser
            var response = context.Response;
            string responseHtml = stateMatches && !string.IsNullOrEmpty(code)
                ? "<html><body><h1>AMCCA Authorization Succeeded</h1><p>You can close this tab and return to AMCCA.</p></body></html>"
                : "<html><body><h1>AMCCA Authorization Failed</h1><p>Mismatched state or authorization denied.</p></body></html>";

            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            response.ContentLength64 = buffer.Length;
            response.ContentType = "text/html; charset=utf-8";
            response.StatusCode = stateMatches && !string.IsNullOrEmpty(code) ? 200 : 400;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();

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
        catch (OperationCanceledException)
        {
            return new OAuthCallbackResult(false, null, null, "Timeout waiting for OAuth callback");
        }
        catch (Exception ex)
        {
            return new OAuthCallbackResult(false, null, null, ex.Message);
        }
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
