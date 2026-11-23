using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Camzivo.Core.Streaming;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Streaming.Video;
using Xunit;
using Xunit.Abstractions;

namespace Camzivo.Core.Tests.Streaming;

/// <summary>
/// The real video path: RTSP over TCP → RTP → H.264 depacketisation → Media Foundation decode → BGRA frame.
///
/// The source is a deterministic RTSP server in this assembly serving a genuine H.264 fixture (encoded once with
/// FFmpeg, committed as bytes). That makes the client and decoder do real work while keeping the test repeatable.
/// It still does not prove a physical camera works; that needs hardware.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamIntegrationTests(ITestOutputHelper output)
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-640x360.h264");

    [Fact]
    public void Decoder_ProducesFramesFromRealH264WithoutRtsp()
    {
        var frames = DecodeFixture(out var width, out var height);

        Assert.NotEmpty(frames);
        Assert.Equal(640, width);
        Assert.Equal(360, height);
        Assert.True(DistinctColours(frames[0]) > 8, "The decoded frame has no picture content.");
    }

    [Fact]
    public async Task RtspClient_AuthenticatesBeforeDescribingSoADeviceThatClosesOnAnonymousRequestsStillWorks()
    {
        if (!File.Exists(FixturePath))
        {
            output.WriteLine($"SKIPPED: fixture not found at {FixturePath}.");
            return;
        }

        // Dahua closes the connection when DESCRIBE arrives without credentials. Asking for the challenge with OPTIONS
        // first lets DESCRIBE carry Authorization from the first byte.
        await using var server = new TestRtspServer(await File.ReadAllBytesAsync(FixturePath), fps: 15)
        {
            RequireDigest = true,
        };

        await using var client = new RtspClient();
        var options = new RtspSessionOptions(new Uri($"rtsp://127.0.0.1:{server.Port}/live"), "admin", "Sup3rSecret!");

        var description = await client.ConnectAsync(options, CancellationToken.None);

        Assert.NotNull(description.Video);

        // The first DESCRIBE is anonymous and takes the 401 challenge; the retry on the same connection carries
        // credentials. One connection, two requests.
        var describes = server.Requests.Where(request => request.Method == "DESCRIBE").ToList();
        Assert.Equal(2, describes.Count);
        Assert.False(describes[0].Authorized);
        Assert.True(describes[1].Authorized, "The retried DESCRIBE was sent without credentials.");
        Assert.DoesNotContain(server.Requests, request => request.Method == "OPTIONS");
    }

    [Fact]
    public async Task RtspClient_RetriesThePlainStreamUrlWhenTheRecorderRefusesTheOnvifAnnotation()
    {
        if (!File.Exists(FixturePath))
        {
            output.WriteLine($"SKIPPED: fixture not found at {FixturePath}.");
            return;
        }

        // A Dahua recorder hands out ?channel=1&subtype=0&unicast=true&proto=Onvif and then answers 503 for exactly
        // that form, while the plain channel/subtype URL plays. The client must not leave the operator stuck on 503.
        await using var server = new TestRtspServer(await File.ReadAllBytesAsync(FixturePath), fps: 15)
        {
            RefuseDescribe = url => url.Contains("proto=Onvif", StringComparison.Ordinal),
        };

        await using var client = new RtspClient();
        var annotated = new Uri($"rtsp://127.0.0.1:{server.Port}/cam/realmonitor?channel=1&subtype=0&unicast=true&proto=Onvif");

        var description = await client.ConnectAsync(new RtspSessionOptions(annotated), CancellationToken.None);

        Assert.NotNull(description.Video);
        Assert.Equal(2, server.DescribeUrls.Count);
        Assert.Contains("proto=Onvif", server.DescribeUrls[0], StringComparison.Ordinal);
        Assert.EndsWith("/cam/realmonitor?channel=1&subtype=0", server.DescribeUrls[1], StringComparison.Ordinal);
        Assert.StartsWith("rtsp://", server.DescribeUrls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RtspSession_DecodesFramesAndReportsLiveOnlyAfterAPicture()
    {
        if (!File.Exists(FixturePath))
        {
            output.WriteLine($"SKIPPED: fixture not found at {FixturePath}.");
            return;
        }

        await using var server = new TestRtspServer(await File.ReadAllBytesAsync(FixturePath), fps: 15, payloadSize: 900);

        await using var session = new H264StreamSession();
        var frames = new List<VideoFrame>();
        var statuses = new List<StreamState>();

        session.FrameReady += frame =>
        {
            lock (frames)
            {
                if (frames.Count < 10)
                {
                    frames.Add(frame);
                }
            }
        };

        session.StatusChanged += status => { lock (statuses) { statuses.Add(status.State); } };

        session.Start(new RtspSessionOptions(new Uri(server.Url)));

        var gotFrame = await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20));

        Assert.True(gotFrame, $"No frame decoded. State: {session.Status.State}, detail: {session.Status.Detail}");

        // The state machine must not say LIVE before the first picture exists.
        lock (statuses)
        {
            var liveIndex = statuses.IndexOf(StreamState.Live);
            Assert.True(liveIndex >= 0, "The session never reported LIVE.");
            Assert.DoesNotContain(StreamState.Live, statuses.Take(liveIndex));
        }

        VideoFrame first;
        lock (frames)
        {
            first = frames[0];
        }

        Assert.Equal(640, first.Width);
        Assert.Equal(360, first.Height);
        Assert.Equal(first.Width * 4, first.Stride);
        Assert.True(DistinctColours(first) > 8, "The decoded frame has no picture content.");

        var telemetry = session.Telemetry;
        Assert.True(telemetry.FramesDecoded >= 1);
        Assert.True(telemetry.PacketsReceived > 0);
        Assert.Equal(1, server.PlayRequests);

        output.WriteLine(
            $"frames={telemetry.FramesDecoded} packets={telemetry.PacketsReceived} size={first.Width}x{first.Height} " +
            $"state={session.Status.State} decoder={telemetry.DecoderName}");

        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "rtsp-decode-proof.bmp"));
        FrameWriter.WriteBmp(first, path);
        output.WriteLine($"proof frame written to {path}");
    }

    [Fact]
    public async Task RtspSession_SurvivesAServerNotificationBetweenFrames()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(FixturePath);
        await using var server = new TestRtspServer(bytes, fps: 15) { SendNotificationOnPlay = true };

        await using var session = new H264StreamSession();
        session.Start(new RtspSessionOptions(new Uri(server.Url)));

        Assert.True(await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20)));

        var framesBefore = session.Telemetry.FramesDecoded;
        await Task.Delay(1200);

        Assert.True(
            session.Telemetry.FramesDecoded > framesBefore,
            $"Frames stopped after the notification: {framesBefore} → {session.Telemetry.FramesDecoded}, state {session.Status.State} ({session.Status.Detail}).");
        Assert.Equal(StreamState.Live, session.Status.State);
    }

    [Fact]
    public async Task RtspSession_KeepsDecodingAcrossRepeatedKeepAlives()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(FixturePath);
        await using var server = new TestRtspServer(bytes, fps: 15);

        // Keep-alive replies arrive between RTP frames on the same socket; the reader must tolerate them.
        await using var client = new RtspClient();
        var options = new RtspSessionOptions(new Uri(server.Url));
        await client.ConnectAsync(options, CancellationToken.None);

        var frames = 0;
        client.PacketReceived += _ => Interlocked.Increment(ref frames);

        for (var index = 0; index < 3; index++)
        {
            await client.KeepAliveAsync(options, CancellationToken.None);
            await Task.Delay(300);
        }

        var before = Interlocked.CompareExchange(ref frames, 0, 0);
        await Task.Delay(800);
        var after = Volatile.Read(ref frames);

        Assert.True(after > before, $"RTP stopped after keep-alives: {before} → {after} packets.");
        Assert.Equal(0, client.Statistics.Gaps);
    }

    [Fact]
    public async Task RtspSession_ReconnectsAfterTheSourceDisappears()
    {
        if (!File.Exists(FixturePath))
        {
            output.WriteLine($"SKIPPED: fixture not found at {FixturePath}.");
            return;
        }

        var bytes = await File.ReadAllBytesAsync(FixturePath);
        await using var server = new TestRtspServer(bytes, fps: 15);

        await using var session = new H264StreamSession();
        session.Start(new RtspSessionOptions(new Uri(server.Url)));

        Assert.True(await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(StreamState.Live, session.Status.State);

        var framesBefore = session.Telemetry.FramesDecoded;

        // A stalled stream: the socket stays open, the pictures stop. The watchdog must notice on its own.
        server.PauseSending();
        Assert.True(
            await WaitForAsync(() => session.Status.State != StreamState.Live, TimeSpan.FromSeconds(20)),
            "The session stayed LIVE on a stalled stream.");

        server.ResumeSending();

        var recovered = await WaitForAsync(
            () => session.Status.State == StreamState.Live && session.Telemetry.FramesDecoded > framesBefore,
            TimeSpan.FromSeconds(40));

        Assert.True(
            recovered,
            $"Did not recover. State: {session.Status.State}, failure: {session.Status.Failure}, detail: {session.Status.Detail}, " +
            $"reconnects: {session.Telemetry.Reconnects}, frames: {session.Telemetry.FramesDecoded}, " +
            $"accepted: {server.ConnectionsAccepted}, plays: {server.PlayRequests}");
        Assert.True(session.Telemetry.Reconnects >= 1, "Recovery did not recycle the session.");
    }

    [Fact]
    public async Task RtspSession_ReportsRefusedConnectionInsteadOfHanging()
    {
        await using var session = new H264StreamSession();
        session.Start(new RtspSessionOptions(new Uri($"rtsp://127.0.0.1:{FreePort()}/live")));

        var live = await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(12));

        Assert.False(live);
        Assert.NotEqual(StreamState.Live, session.Status.State);
        Assert.Contains(session.Status.State, new[] { StreamState.Reconnecting, StreamState.Failed });
    }

    private static List<VideoFrame> DecodeFixture(out int width, out int height)
    {
        var frames = new List<VideoFrame>();
        using var decoder = new MediaFoundationH264Decoder();

        var nals = TestRtspServer.SplitAnnexB(File.ReadAllBytes(FixturePath));
        var pending = new List<byte[]>();
        var timestamp = TimeSpan.Zero;

        foreach (var nal in nals)
        {
            pending.Add(nal);

            if ((nal[0] & 0x1F) is not (1 or 5))
            {
                continue;
            }

            var accessUnit = new List<byte>();
            foreach (var item in pending)
            {
                accessUnit.AddRange(H264Depacketizer.StartCode);
                accessUnit.AddRange(item);
            }

            pending.Clear();
            timestamp += TimeSpan.FromMilliseconds(66);

            frames.AddRange(decoder.Decode(accessUnit.ToArray(), timestamp));

            if (frames.Count >= 5)
            {
                break;
            }
        }

        width = decoder.Width;
        height = decoder.Height;
        return frames;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return condition();
    }

    private static int DistinctColours(VideoFrame frame)
    {
        var seen = new HashSet<int>();

        for (var offset = 0; offset + 3 < frame.Pixels.Length && seen.Count < 32; offset += 4 * 97)
        {
            var value = (frame.Pixels[offset] << 16) | (frame.Pixels[offset + 1] << 8) | frame.Pixels[offset + 2];
            seen.Add(value);
        }

        return seen.Count;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
