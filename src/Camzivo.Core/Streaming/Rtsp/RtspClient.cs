using System.Net;
using System.Net.Sockets;
using System.Text;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Net;

namespace Camzivo.Core.Streaming.Rtsp;

public sealed record RtspSessionOptions(
    Uri Uri,
    string? Username = null,
    string? Password = null,
    RtspTransport Transport = RtspTransport.Tcp,
    TimeSpan? Timeout = null)
{
    public TimeSpan RequestTimeout => Timeout ?? TimeSpan.FromSeconds(10);
}

public sealed record RtspSessionStatistics(long PacketsReceived, long PacketsDropped, long BytesReceived, int LastSequence, long Gaps);

/// <summary>
/// A minimal, honest RTSP client: DESCRIBE → SETUP → PLAY, reading RTP over the TCP connection (interleaved).
///
/// Deliberate limits: TCP interleaved only (UDP needs a second socket pair and NAT handling that this milestone does
/// not need), one video track, and no RTP/RTCP reporting. Everything it cannot do it reports as a failure instead
/// of pretending. Credentials never appear in logs or exceptions; only the redacted URL is used in messages.
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    private const int MaxHeadBytes = 64 * 1024;
    private const int MaxPayloadBytes = 4 * 1024 * 1024;

    private TcpClient _tcp = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NetworkStream? _stream;
    private int _cseq;
    private string? _session;
    private int _lastSequence = -1;
    private long _packets;
    private long _dropped;
    private long _bytes;
    private long _gaps;
    private bool _disposed;

    public SdpDescription? Description { get; private set; }

    public Uri? ContentBase { get; private set; }

    public RtspSessionStatistics Statistics => new(
        Interlocked.Read(ref _packets),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _bytes),
        Volatile.Read(ref _lastSequence),
        Interlocked.Read(ref _gaps));

    /// <summary>Raised for every RTP packet of the negotiated video track, on the reader task.</summary>
    public event Action<RtpPacket>? PacketReceived;

    /// <summary>Connects and starts the stream. Returns when PLAY has been accepted and the read loop is running.</summary>
    /// <param name="replay">
    /// When supplied, PLAY carries an absolute clock range and scale (ONVIF replay). Live playback is npt-based and
    /// must not be sent a clock range: a device that ignores it would replay its oldest recording instead of live.
    /// </param>
    public async Task<SdpDescription> ConnectAsync(
        RtspSessionOptions options,
        CancellationToken cancellationToken,
        Camzivo.Core.Streaming.Replay.ReplayPlaybackOptions? replay = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = options.Uri.Host;
        var port = options.Uri.Port > 0 ? options.Uri.Port : RtspUriBuilder.DefaultRtspPort;

        await OpenSocketAsync(host, port, cancellationToken).ConfigureAwait(false);

        // Ask for a challenge with OPTIONS before DESCRIBE. Dahua firmware closes the connection on an
        // unauthenticated DESCRIBE instead of answering 401, which left every channel with "the connection closed
        // during DESCRIBE" and no way to authenticate. OPTIONS is answered with the challenge, so DESCRIBE can carry
        // its Authorization header from the first byte.
        // One connection per attempt on the happy path: DESCRIBE first, and only ask for a challenge when the
        // recorder refuses. Probing with OPTIONS up front doubled the connections to a device that counts them.
        RtspAttempt describe;
        string? authorization = null;

        try
        {
            describe = await RequestAsync(
                    "DESCRIBE",
                    options.Uri,
                    options,
                    cancellationToken,
                    ["Accept: application/sdp"])
                .ConfigureAwait(false);
        }
        catch (RtspException) when (!cancellationToken.IsCancellationRequested && !string.IsNullOrEmpty(options.Username))
        {
            // Some firmware hangs up instead of answering 401. Reopen once, take the challenge from OPTIONS, and
            // describe again with credentials on the first byte.
            await ReopenAsync(host, port, cancellationToken).ConfigureAwait(false);
            authorization = await RequestChallengeAsync(options, cancellationToken).ConfigureAwait(false);

            if (authorization is null)
            {
                throw;
            }

            describe = await RequestAsync(
                    "DESCRIBE",
                    options.Uri,
                    options,
                    cancellationToken,
                    ["Accept: application/sdp"],
                    authorization)
                .ConfigureAwait(false);
        }

        // The address came from the recorder's own GetStreamUri, which annotates it for ONVIF clients
        // (unicast=true&proto=Onvif). Some Dahua firmware refuses that annotated form with 503 while serving the
        // same channel and stream type under the plain URL other clients use. One retry, same host and port, only
        // the annotation removed — nothing else about the device is touched.
        var requestUri = options.Uri;

        if (describe.Response.StatusCode == 503 && PlainStreamUri(requestUri) is { } plain)
        {
            // The trace showed the recorder answering 503 and then closing that socket: the retry used to go out on
            // the dead connection and reported "connection closed", hiding the 503 completely. Reopen first.
            requestUri = plain;
            await ReopenAsync(host, port, cancellationToken).ConfigureAwait(false);
            describe = await RequestAsync(
                    "DESCRIBE",
                    plain,
                    options,
                    cancellationToken,
                    ["Accept: application/sdp"],
                    authorization)
                .ConfigureAwait(false);
        }

        if (describe.Response.StatusCode != 200)
        {
            throw new RtspException(
                OnvifFailure(describe.Response.StatusCode),
                DescribeMessage("DESCRIBE", describe.Response.StatusCode, describe.Authenticated),
                null);
        }

        Description = SdpDescription.Parse(describe.Response.Body);
        ContentBase = describe.Response.Header("Content-Base") is { Length: > 0 } contentBase && Uri.TryCreate(contentBase, UriKind.Absolute, out var parsedBase)
            ? parsedBase
            : requestUri;

        var video = Description.Video
            ?? throw new RtspException(StreamFailure.NotFound, "The device described no video track.", null);

        if (video.VideoCodec is not { IsH264: true })
        {
            throw new RtspException(
                StreamFailure.UnsupportedCodec,
                video.VideoCodec is null
                    ? "The video track uses an unknown codec."
                    : $"The video track uses {video.VideoCodec.Encoding}, which this build cannot decode.",
                null);
        }

        var setupUri = video.ResolveControl(ContentBase);
        var setup = await RequestAsync(
                "SETUP",
                setupUri,
                options,
                cancellationToken,
                ["Transport: RTP/AVP/TCP;unicast;interleaved=0-1"])
            .ConfigureAwait(false);

        if (setup.Response.StatusCode != 200)
        {
            throw new RtspException(OnvifFailure(setup.Response.StatusCode), DescribeMessage("SETUP", setup.Response.StatusCode, setup.Authenticated), null);
        }

        _session = NormalizeSession(setup.Response.Header("Session"))
            ?? throw new RtspException(StreamFailure.ProtocolViolation, "SETUP returned no session identifier.", null);

        var playHeaders = replay is null
            ? new[] { "Range: npt=0.000-" }
            : new[] { $"Range: {replay.RangeHeader}", replay.ScaleHeader };

        var play = await RequestAsync("PLAY", ContentBase, options, cancellationToken, playHeaders)
            .ConfigureAwait(false);

        if (play.Response.StatusCode is not (200 or 202))
        {
            throw new RtspException(OnvifFailure(play.Response.StatusCode), DescribeMessage("PLAY", play.Response.StatusCode, play.Authenticated), null);
        }

        _ = Task.Run(() => ReadLoopAsync(cancellationToken), CancellationToken.None);
        return Description;
    }

    /// <summary>
    /// RTSP keep-alive; the supervisor calls this well inside the device's session timeout.
    ///
    /// Send-only on purpose: the read loop owns the socket once PLAY has been accepted, so a second reader here would
    /// race it for the response bytes and desynchronise the interleaved framing.
    /// </summary>
    public async Task KeepAliveAsync(RtspSessionOptions options, CancellationToken cancellationToken)
    {
        if (_stream is null || _session is null)
        {
            return;
        }

        var request = new RtspRequest("OPTIONS", options.Uri, ++_cseq).WithHeader("Session", _session);
        var bytes = Encoding.UTF8.GetBytes(request.Build());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Asks the device for an authentication challenge with OPTIONS and builds the DESCRIBE header from it. Best
    /// effort: a device that answers OPTIONS without a challenge, or drops the connection, simply gets an
    /// unauthenticated DESCRIBE and the normal 401 retry.
    /// </summary>
    private async Task OpenSocketAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            await _tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            RtspTracing.Write($"tcp {host}:{port} connected");
        }
        catch (SocketException exception)
        {
            throw new RtspException(
                exception.SocketErrorCode == SocketError.HostNotFound ? StreamFailure.DnsFailure : StreamFailure.ConnectionRefused,
                $"Could not open a TCP connection to {host}:{port}.",
                exception);
        }

        _stream = _tcp.GetStream();
    }

    private async Task ReopenAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            _stream?.Dispose();
            _tcp.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _ = exception;
        }

        _tcp = new TcpClient();
        await OpenSocketAsync(host, port, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> RequestChallengeAsync(RtspSessionOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.Username))
        {
            return null;
        }

        try
        {
            var probe = await SendAndReadAsync("OPTIONS", options.Uri, options, cancellationToken, [], authorization: null)
                .ConfigureAwait(false);

            return probe.Response.StatusCode is 401 or 503
                ? BuildAuthorization(probe.Challenge, "DESCRIBE", options.Uri.AbsoluteUri, options)
                : null;
        }
        catch (RtspException)
        {
            return null;
        }
    }

    private async Task<RtspAttempt> RequestAsync(
        string method,
        Uri uri,
        RtspSessionOptions options,
        CancellationToken cancellationToken,
        string[] extraHeaders,
        string? initialAuthorization = null)
    {
        var first = await SendAndReadAsync(method, uri, options, cancellationToken, extraHeaders, initialAuthorization)
            .ConfigureAwait(false);

        // 401 is the documented challenge. Some Dahua firmware answers 503 to an unauthenticated request and only
        // then offers the challenge, so one retry with credentials covers both shapes; a second refusal is reported
        // as it came back.
        if (first.Response.StatusCode is 401 or 503 &&
            BuildAuthorization(first.Challenge, method, uri.AbsoluteUri, options) is { } header)
        {
            return new RtspAttempt(
                (await SendAndReadAsync(method, uri, options, cancellationToken, extraHeaders, header).ConfigureAwait(false)).Response,
                Authenticated: true);
        }

        return new RtspAttempt(first.Response, Authenticated: false);
    }

    private async Task<(RtspMessage Response, string Challenge)> SendAndReadAsync(
        string method,
        Uri uri,
        RtspSessionOptions options,
        CancellationToken cancellationToken,
        string[] extraHeaders,
        string? authorization)
    {
        var request = new RtspRequest(method, uri, ++_cseq);

        if (_session is not null)
        {
            request.WithHeader("Session", _session);
        }

        if (!string.IsNullOrEmpty(authorization))
        {
            request.WithHeader("Authorization", authorization);
        }

        foreach (var extraHeader in extraHeaders)
        {
            var separator = extraHeader.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                request.WithHeader(extraHeader[..separator].Trim(), extraHeader[(separator + 1)..].Trim());
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        try
        {
            await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(request.Build());
                await _stream!.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            RtspTracing.Write($"{method} {uri.PathAndQuery}");

            var head = await ReadHeadAsync(timeout.Token).ConfigureAwait(false);
            var response = RtspMessage.ParseResponse(head);

            RtspTracing.Write($"{method} -> {response.StatusCode} {response.Reason}");

            if (response.Header("Content-Length") is { } lengthText &&
                int.TryParse(lengthText, out var length) &&
                length > 0)
            {
                var body = new byte[length];
                await _stream!.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                response = response with { Body = Encoding.UTF8.GetString(body) };
            }

            return (response, response.Header("WWW-Authenticate") ?? string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RtspException(StreamFailure.Timeout, $"{method} timed out after {options.RequestTimeout.TotalSeconds:F0} s.", null);
        }
        catch (IOException exception)
        {
            RtspTracing.Write($"{method} -> connection closed by the device");
            throw new RtspException(StreamFailure.ConnectionRefused, $"The connection closed during {method}.", exception);
        }
        catch (SocketException exception)
        {
            throw new RtspException(StreamFailure.ConnectionRefused, $"The connection failed during {method}.", exception);
        }
    }

    private async Task<string> ReadHeadAsync(CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];

        while (buffer.Count < MaxHeadBytes)
        {
            var read = await _stream!.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The device closed the connection before sending a response.");
            }

            buffer.Add(one[0]);

            var count = buffer.Count;
            if (count >= 4 &&
                buffer[count - 4] == (byte)'\r' && buffer[count - 3] == (byte)'\n' &&
                buffer[count - 2] == (byte)'\r' && buffer[count - 1] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        throw new InvalidDataException("The device sent a response header larger than 64 KB.");
    }

    /// <summary>
    /// Reads the interleaved stream. RTP arrives as <c>$ channel length payload</c>; anything else is a server
    /// message and is skipped. This loop owns the socket until cancellation.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var stream = _stream;

        if (stream is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Scan for the interleaved frame marker instead of assuming alignment: a device may send an RTSP
                // notification ("RTSP/1.0 200 OK…") or a keep-alive reply between frames, and skipping a fixed four
                // bytes would leave the reader permanently misaligned.
                var marker = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                if (marker < 0)
                {
                    throw new IOException("The device closed the connection.");
                }

                if (marker != (byte)'$')
                {
                    continue;
                }

                await stream.ReadExactlyAsync(header.AsMemory(1, 3), cancellationToken).ConfigureAwait(false);
                header[0] = (byte)'$';

                var channel = header[1];
                var length = (header[2] << 8) | header[3];

                if (length <= 0 || length > MaxPayloadBytes)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

                Interlocked.Add(ref _bytes, length);

                // Channel 1 is RTCP: receiver reports are useful later, but they are not video.
                if (channel != 0)
                {
                    continue;
                }

                var packet = new RtpPacket(0, false, 0, 0, 0, []);
                if (!packet.TryParse(payload, out var rtp, out _))
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                var sequence = rtp!.Sequence;
                var previous = _lastSequence;
                if (previous >= 0 && sequence != (ushort)(previous + 1) && sequence != previous)
                {
                    Interlocked.Increment(ref _gaps);
                }

                _lastSequence = sequence;
                Interlocked.Increment(ref _packets);

                PacketReceived?.Invoke(rtp);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            // The reader dies silently: the owner learns about it from the reconnect supervisor, which sees the
            // stream stall. Logging here would need a logger dependency in Core; the session owner records it.
            _ = exception;
        }
    }

    private static async Task<int> ReadByteAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : buffer[0];
    }

    /// <summary>
    /// Sends a session command (PAUSE/PLAY) without reading its response. Send-only on purpose: the reader task owns
    /// the socket once PLAY has been accepted, so reading here would steal interleaved bytes and desynchronise it.
    /// Servers acknowledge these commands; the next video packet is the real confirmation.
    /// </summary>
    public async Task SendCommandAsync(string method, CancellationToken cancellationToken, params string[] headers)
    {
        var stream = _stream;
        var session = _session;

        if (stream is null || session is null || _disposed)
        {
            return;
        }

        var request = new RtspRequest(method, ContentBase ?? new Uri("rtsp://localhost/"), ++_cseq)
            .WithHeader("Session", session);

        foreach (var header in headers)
        {
            var separator = header.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                request.WithHeader(header[..separator].Trim(), header[(separator + 1)..].Trim());
            }
        }

        var bytes = Encoding.UTF8.GetBytes(request.Build());

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string? BuildAuthorization(string? challenge, string method, string requestUri, RtspSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrEmpty(options.Username))
        {
            return null;
        }

        if (HttpDigest.TryParse(challenge, out var digest) && !string.IsNullOrEmpty(digest.Nonce))
        {
            // The digest uri must be byte-identical to the request line, which is now the absolute URL.
            return HttpDigest.BuildAuthorization(
                digest,
                method,
                requestUri,
                options.Username,
                options.Password ?? string.Empty,
                clientNonce: Guid.NewGuid().ToString("N"));
        }

        if (challenge.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            return "Basic " + raw;
        }

        return null;
    }

    private static string? NormalizeSession(string? session) =>
        string.IsNullOrWhiteSpace(session) ? null : session.Split(';')[0].Trim();

    /// <summary>One response plus whether credentials were already presented when it was received.</summary>
    private sealed record RtspAttempt(RtspMessage Response, bool Authenticated);

    /// <summary>
    /// The same stream address with the ONVIF annotation removed, keeping only <c>channel</c> and <c>subtype</c>.
    /// Returns null when there is nothing to strip, so the plain form is only ever tried as a second attempt.
    /// </summary>
    private static Uri? PlainStreamUri(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }

        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = parameters
            .Where(parameter =>
                parameter.StartsWith("channel=", StringComparison.OrdinalIgnoreCase) ||
                parameter.StartsWith("subtype=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (kept.Count == 0 || kept.Count == parameters.Length)
        {
            return null;
        }

        return new UriBuilder(uri) { Query = string.Join("&", kept) }.Uri;
    }

    /// <summary>
    /// Names the status a category alone cannot explain. Dahua answers 503 for a channel with no camera attached, a
    /// disabled stream, or an exhausted RTSP session count, and "unexpected status" sent the operator looking for a
    /// client bug instead of the channel that has nothing to send.
    /// </summary>
    private static string DescribeMessage(string method, int status, bool authenticated)
    {
        var credentials = authenticated ? "credentials were sent" : "no credentials were sent";

        return status switch
        {
            503 => $"{method} returned 503 ({credentials}): the recorder has no stream for this address — the channel " +
                   "has no camera attached or its stream is disabled, or its RTSP session limit is already reached.",
            404 => $"{method} returned 404 ({credentials}): the recorder does not know this stream address.",
            401 => $"{method} returned 401 ({credentials}): the recorder rejected the credentials for this stream.",
            461 => $"{method} returned 461 ({credentials}): the recorder refused the requested transport.",
            _ => $"{method} returned {status} ({credentials}).",
        };
    }

    private static StreamFailure OnvifFailure(int status) => status switch
    {
        401 => StreamFailure.Unauthorized,
        403 => StreamFailure.Forbidden,
        404 => StreamFailure.NotFound,
        405 => StreamFailure.RtspMethodNotAllowed,
        454 => StreamFailure.Unauthorized,
        461 => StreamFailure.UnsupportedTransport,
        >= 500 => StreamFailure.ProtocolViolation,
        _ => StreamFailure.Unknown,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_stream is not null && _session is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var teardown = new RtspRequest("TEARDOWN", ContentBase ?? new Uri("rtsp://localhost/"), ++_cseq)
                    .WithHeader("Session", _session);
                var bytes = Encoding.UTF8.GetBytes(teardown.Build());
                await _stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The session may already be gone; teardown is best-effort by definition.
        }

        try
        {
            _stream?.Dispose();
            _tcp.Dispose();
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            _ = exception;
        }

        _writeLock.Dispose();
    }
}

/// <summary>An RTSP failure with the diagnostic vocabulary attached, so the UI can explain it.</summary>
public sealed class RtspException(StreamFailure failure, string message, Exception? inner) : Exception(message, inner)
{
    public StreamFailure Failure { get; } = failure;
}
