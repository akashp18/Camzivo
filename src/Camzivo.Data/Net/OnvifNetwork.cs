using System.Diagnostics;
using System.Net.Sockets;
using Camzivo.Core.Devices;
using Camzivo.Core.Diagnostics;

namespace Camzivo.Data.Net;

/// <summary>
/// The production ONVIF transport. A single HttpClient with a bounded timeout and no redirects: a camera that
/// answers 302 to a login page must be reported as "not ONVIF", not followed into HTML.
/// </summary>
public sealed class HttpOnvifTransport : IOnvifTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HttpOnvifTransport(HttpClient? client = null, TimeSpan? timeout = null)
    {
        _ownsClient = client is null;
        _http = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
    }

    public async Task<OnvifHttpResponse> PostAsync(
        Uri uri,
        string body,
        string? authorization,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/soap+xml"),
        };

        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var challenge = response.Headers.WwwAuthenticate.Count > 0
            ? response.Headers.WwwAuthenticate.ToString()
            : null;

        return new OnvifHttpResponse((int)response.StatusCode, text, challenge);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>TCP reachability of an RTSP endpoint: the cheapest honest signal that the port is serving something.</summary>
public sealed class TcpRtspEndpointProbe(TimeSpan? timeout = null) : IRtspEndpointProbe
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(4);

    public async Task<StreamFailure?> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(_timeout);

            await client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554, linked.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StreamFailure.Timeout;
        }
        catch (SocketException exception)
        {
            return exception.SocketErrorCode == SocketError.HostNotFound
                ? StreamFailure.DnsFailure
                : StreamFailure.ConnectionRefused;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            _ = stopwatch;
            return StreamFailure.ConnectionRefused;
        }
    }
}
