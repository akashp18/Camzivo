using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Camzivo.Core.Tests.Streaming;

/// <summary>
/// A deterministic RTSP/RTP server for tests. It serves real H.264 (a fixture encoded once with FFmpeg) over a real
/// TCP socket, with real interleaved framing, real SDP, and real RTP packetisation including FU-A fragmentation.
///
/// This is a test double for a camera, not a fake video path: the bytes it sends are a genuine H.264 stream, and the
/// client and decoder do the same work they would do against a device.
/// </summary>
public sealed class TestRtspServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly List<byte[][]> _accessUnits;
    private readonly byte[] _sps;
    private readonly byte[] _pps;
    private readonly int _fps;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _clients = [];
    private int _sessionCounter;

    public TestRtspServer(byte[] annexB, int fps, int payloadSize = 1200, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(annexB);

        _fps = fps;
        PayloadSize = payloadSize;

        var nals = SplitAnnexB(annexB);
        _sps = nals.FirstOrDefault(nal => (nal[0] & 0x1F) == 7) ?? throw new ArgumentException("Fixtures needs an SPS.", nameof(annexB));
        _pps = nals.FirstOrDefault(nal => (nal[0] & 0x1F) == 8) ?? throw new ArgumentException("Fixture needs a PPS.", nameof(annexB));

        _accessUnits = [];
        var pending = new List<byte[]>();

        foreach (var nal in nals)
        {
            var type = nal[0] & 0x1F;
            pending.Add(nal);

            if (type is 1 or 5)
            {
                _accessUnits.Add(pending.ToArray());
                pending.Clear();
            }
        }

        if (pending.Count > 0)
        {
            _accessUnits.Add(pending.ToArray());
        }

        _listener = new TcpListener(IPAddress.Loopback, port ?? 0);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public int PayloadSize { get; }

    public string Url => $"rtsp://127.0.0.1:{Port}/live";

    public int AccessUnitCount => _accessUnits.Count;

    /// <summary>Sends an RTSP notification (text, not interleaved) before the first RTP packet, like a real device.</summary>
    public bool SendNotificationOnPlay { get; set; }

    /// <summary>Set to refuse a DESCRIBE with 503 for URLs it matches. Used to reproduce Dahua's ONVIF-annotated URL refusal.</summary>
    public Func<string, bool>? RefuseDescribe { get; set; }

    /// <summary>When set, every request without an Authorization header is answered 401 with a Digest challenge.</summary>
    public bool RequireDigest { get; set; }

    public List<string> DescribeUrls { get; } = [];

    /// <summary>Every request received, with whether it carried an Authorization header.</summary>
    public List<(string Method, string Url, bool Authorized)> Requests { get; } = [];

    public int ConnectionsAccepted { get; private set; }

    public int PlayRequests { get; private set; }

    private volatile bool _paused;

    /// <summary>Stops sending RTP without closing the socket, which is what a stalled network looks like.</summary>
    public void PauseSending() => _paused = true;

    public void ResumeSending() => _paused = false;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                ConnectionsAccepted++;
                _clients.Add(Task.Run(() => HandleClientAsync(client)));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Server shutdown.
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var connection = client;
        using var stream = client.GetStream();

        var session = $"CAMZIVO{Interlocked.Increment(ref _sessionCounter)}";
        CancellationTokenSource? playing = null;

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var head = await ReadHeadAsync(stream, _stopping.Token);
                if (head is null)
                {
                    return;
                }

                var (method, url, headers) = ParseRequest(head);
                var authorized = headers.ContainsKey("Authorization");

                lock (Requests)
                {
                    Requests.Add((method, url, authorized));
                }

                if (RequireDigest && !authorized)
                {
                    await WriteResponseAsync(stream, 401, "Unauthorized", session, [
                        "WWW-Authenticate: Digest realm=\"Camzivo\", nonce=\"abcdef0123456789\", qop=\"auth\"",
                    ]);
                    continue;
                }

                switch (method)
                {
                    case "OPTIONS":
                        await WriteResponseAsync(stream, 200, "OK", session, ["Public: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN", "Require: "]);
                        break;

                    case "DESCRIBE":
                        lock (DescribeUrls)
                        {
                            DescribeUrls.Add(url);
                        }

                        if (RefuseDescribe?.Invoke(url) == true)
                        {
                            await WriteResponseAsync(stream, 503, "Service Unavailable", session, []);
                            break;
                        }

                        var sdp = BuildSdp(url);
                        await WriteResponseAsync(
                            stream,
                            200,
                            "OK",
                            session,
                            ["Content-Type: application/sdp", $"Content-Base: {url.TrimEnd('/')}/"],
                            sdp);
                        break;

                    case "SETUP":
                        await WriteResponseAsync(
                            stream,
                            200,
                            "OK",
                            session,
                            ["Transport: RTP/AVP/TCP;unicast;interleaved=0-1", "Session: " + session + ";timeout=60"]);
                        break;

                    case "PLAY":
                        await WriteResponseAsync(
                            stream,
                            200,
                            "OK",
                            session,
                            ["Session: " + session, "Range: npt=0.000-"]);

                        PlayRequests++;
                        playing?.Cancel();
                        playing = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);

                        if (SendNotificationOnPlay)
                        {
                            var notification = Encoding.UTF8.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n");
                            await stream.WriteAsync(notification, _stopping.Token);
                            await stream.FlushAsync(_stopping.Token);
                        }

                        _ = Task.Run(() => SendLoopAsync(stream, session, playing.Token));
                        break;

                    case "TEARDOWN":
                        playing?.Cancel();
                        await WriteResponseAsync(stream, 200, "OK", session, ["Session: " + session]);
                        return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Client went away.
        }
        finally
        {
            playing?.Cancel();
            playing?.Dispose();
        }
    }

    private async Task SendLoopAsync(NetworkStream stream, string session, CancellationToken cancellationToken)
    {
        _ = session;

        var sequence = (ushort)0;
        uint timestamp = 0;
        var frameIndex = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_paused)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                var accessUnit = _accessUnits[frameIndex % _accessUnits.Count];
                frameIndex++;

                for (var nalIndex = 0; nalIndex < accessUnit.Length; nalIndex++)
                {
                    var nal = accessUnit[nalIndex];
                    var isLastNal = nalIndex == accessUnit.Length - 1;
                    var isVcl = (nal[0] & 0x1F) is 1 or 5;
                    var marker = isLastNal && isVcl;

                    foreach (var payload in Packetize(nal, 96, sequence, timestamp, marker))
                    {
                        await WriteInterleavedAsync(stream, 0, payload, cancellationToken);
                        sequence++;
                    }
                }

                timestamp += (uint)(90000 / _fps);
                await Task.Delay(1000 / _fps, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stream ended: the client closed or the server is stopping.
        }
    }

    /// <summary>RFC 6184 single-NAL or FU-A packetisation.</summary>
    private IEnumerable<byte[]> Packetize(byte[] nal, int payloadType, ushort sequence, uint timestamp, bool marker)
    {
        if (nal.Length <= PayloadSize)
        {
            yield return BuildRtp(nal, payloadType, sequence, timestamp, marker);
            yield break;
        }

        var offset = 1;
        var first = true;

        while (offset < nal.Length)
        {
            var chunk = Math.Min(PayloadSize - 2, nal.Length - offset);
            var last = offset + chunk >= nal.Length;
            var indicator = (byte)((nal[0] & 0xE0) | 28);
            var header = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | (nal[0] & 0x1F));

            var payload = new byte[chunk + 2];
            payload[0] = indicator;
            payload[1] = header;
            Array.Copy(nal, offset, payload, 2, chunk);

            yield return BuildRtp(payload, payloadType, sequence, timestamp, marker && last);

            offset += chunk;
            first = false;
            sequence++;
        }
    }

    private static byte[] BuildRtp(byte[] payload, int payloadType, ushort sequence, uint timestamp, bool marker)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)(timestamp >> 16);
        packet[6] = (byte)(timestamp >> 8);
        packet[7] = (byte)timestamp;
        packet[8] = 0xCA;
        packet[9] = 0xFE;
        packet[10] = 0xBA;
        packet[11] = 0xBE;
        payload.CopyTo(packet, 12);
        return packet;
    }

    private async Task WriteInterleavedAsync(NetworkStream stream, byte channel, byte[] payload, CancellationToken cancellationToken)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)'$';
        frame[1] = channel;
        frame[2] = (byte)(payload.Length >> 8);
        frame[3] = (byte)payload.Length;
        payload.CopyTo(frame, 4);

        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private string BuildSdp(string url)
    {
        _ = url;

        return $"""
            v=0
            o=- 0 0 IN IP4 127.0.0.1
            s=Camzivo Test Source
            c=IN IP4 127.0.0.1
            t=0 0
            a=control:*
            m=video 0 RTP/AVP 96
            a=rtpmap:96 H264/90000
            a=fmtp:96 packetization-mode=1;sprop-parameter-sets={Convert.ToBase64String(_sps)},{Convert.ToBase64String(_pps)}
            a=control:trackID=0

            """;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        string session,
        string[] headers,
        string? body = null)
    {
        var builder = new StringBuilder();
        builder.Append($"RTSP/1.0 {status} {reason}\r\n");
        builder.Append("CSeq: ").Append(Interlocked.Increment(ref _cseq)).Append("\r\n");

        foreach (var header in headers)
        {
            if (header.StartsWith("Session:", StringComparison.Ordinal) || header.StartsWith("CSeq:", StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(header).Append("\r\n");
        }

        builder.Append("Session: ").Append(session).Append("\r\n");

        if (body is not null)
        {
            builder.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        }

        builder.Append("\r\n");
        builder.Append(body ?? string.Empty);

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static int _cseq;

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(256);
        var one = new byte[1];

        while (buffer.Count < 32 * 1024)
        {
            if (await stream.ReadAsync(one.AsMemory(), cancellationToken) == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            var count = buffer.Count;

            if (count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        return null;
    }

    private static (string Method, string Url, Dictionary<string, string> Headers) ParseRequest(string head)
    {
        var lines = head.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var parts = lines[0].Split(' ', 3);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty, headers);
    }

    internal static List<byte[]> SplitAnnexB(byte[] data)
    {
        var starts = new List<int>();
        for (var index = 0; index + 4 <= data.Length; index++)
        {
            if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
            {
                starts.Add(index + 3);
                index += 2;
            }
            else if (index + 5 <= data.Length && data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
            {
                starts.Add(index + 4);
                index += 3;
            }
        }

        var nals = new List<byte[]>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1] - 3 : data.Length;

            while (end > start && (data[end - 1] == 0 || data[end - 1] == 1))
            {
                end--;
            }

            if (end > start)
            {
                var nal = new byte[end - start];
                Array.Copy(data, start, nal, 0, nal.Length);
                nals.Add(nal);
            }
        }

        return nals;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();

        try
        {
            await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            _ = exception;
        }

        _stopping.Dispose();
    }
}
