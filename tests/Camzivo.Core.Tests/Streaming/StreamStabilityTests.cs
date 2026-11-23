using System.Runtime.Versioning;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Streaming;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Streaming.Video;
using Xunit;

namespace Camzivo.Core.Tests.Streaming;

/// <summary>
/// Failure paths that must never look like success: a decoder that cannot start, a preferred backend that disappears,
/// resolution changes, teardown while decoding, and many sessions shutting down at once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamStabilityTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-640x360.h264");

    private static string SubFixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-320x180.h264");

    [Fact]
    public async Task ADecoderThatCannotInitialiseFailsTheStreamInsteadOfGoingLive()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }

        await using var server = new TestRtspServer(await File.ReadAllBytesAsync(FixturePath), fps: 15);
        await using var session = new H264StreamSession(() => throw new VideoDecoderException("simulated decoder initialisation failure"));

        session.Start(new RtspSessionOptions(new Uri(server.Url)));

        var live = await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(10));

        Assert.False(live);
        Assert.Equal(StreamState.Failed, session.Status.State);
        Assert.Equal(StreamFailure.DecoderInitFailed, session.Status.Failure);
        Assert.False(session.Status.IsLive);
    }

    [Fact]
    public async Task AnUnavailablePreferredDecoderFallsBackAndStillShowsVideo()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }

        await using var server = new TestRtspServer(await File.ReadAllBytesAsync(FixturePath), fps: 15);
        await using var session = new H264StreamSession(
            () => throw new VideoDecoderException("simulated hardware decoder unavailable"),
            () => new MediaFoundationH264Decoder());

        session.Start(new RtspSessionOptions(new Uri(server.Url)));

        Assert.True(await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(StreamState.Live, session.Status.State);
        Assert.True(session.UsingFallback);
        Assert.Contains("hardware decoder unavailable", session.FallbackReason);
        Assert.Equal(640, session.Telemetry.Width);
    }

    [Fact]
    public void TheDecoderFollowsAResolutionChangeOnANewStream()
    {
        if (!File.Exists(FixturePath) || !File.Exists(SubFixturePath))
        {
            return;
        }

        using var decoder = new MediaFoundationH264Decoder();
        var sub = FeedFirstFrames(File.ReadAllBytes(SubFixturePath), decoder);
        Assert.True(sub);

        var sps = TestRtspServer.SplitAnnexB(File.ReadAllBytes(SubFixturePath)).First(nal => (nal[0] & 0x1F) == 7);
        Assert.True(H264Sps.TryReadDimensions(sps, out var spsWidth, out var spsHeight), "the fixture SPS must parse");
        Assert.Equal(320, spsWidth);
        Assert.Equal(180, spsHeight);

        Assert.True(decoder.Width == 320, $"decoder width was {decoder.Width}x{decoder.Height}, SPS says {spsWidth}x{spsHeight}");
        Assert.Equal(180, decoder.Height);

        // Same decoder object, new stream: the main-stream SPS must resize the output.
        decoder.Reset();
        var main = FeedFirstFrames(File.ReadAllBytes(FixturePath), decoder);
        Assert.True(main);
        Assert.True(decoder.Width == 640, $"decoder width was {decoder.Width}x{decoder.Height} after the resolution change");
        Assert.Equal(360, decoder.Height);
    }

    [Fact]
    public async Task DisposalDuringDecodingStopsTheSessionWithoutThrowing()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }

        await using var server = new TestRtspServer(await File.ReadAllBytesAsync(FixturePath), fps: 15);
        var session = new H264StreamSession();
        session.Start(new RtspSessionOptions(new Uri(server.Url)));

        Assert.True(await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20)));

        var framesBefore = session.Telemetry.FramesDecoded;
        await session.DisposeAsync();
        await Task.Delay(600);

        Assert.Equal(StreamState.Stopped, session.Status.State);
        Assert.Equal(framesBefore, session.Telemetry.FramesDecoded);
    }

    [Fact]
    public async Task ShuttingDownSeveralActiveStreamsAtOnceIsClean()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(FixturePath);
        await using var server = new TestRtspServer(bytes, fps: 15);

        var sessions = Enumerable.Range(0, 4)
            .Select(_ => new H264StreamSession())
            .ToList();

        foreach (var session in sessions)
        {
            session.Start(new RtspSessionOptions(new Uri(server.Url)));
        }

        var live = await Task.WhenAll(sessions.Select(session => session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(25))));
        Assert.All(live, Assert.True);

        await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));

        Assert.All(sessions, session => Assert.Equal(StreamState.Stopped, session.Status.State));
        Assert.All(sessions, session => Assert.Equal(0, session.Profiler.Snapshot().Frames - session.Telemetry.FramesDecoded));
    }

    [Fact]
    public async Task AStalledStreamDoesNotStopTheOthers()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(FixturePath);
        await using var healthy = new TestRtspServer(bytes, fps: 15);
        await using var stalled = new TestRtspServer(bytes, fps: 15) { SendNotificationOnPlay = false };

        await using var good = new H264StreamSession();
        await using var bad = new H264StreamSession();

        good.Start(new RtspSessionOptions(new Uri(healthy.Url)));
        bad.Start(new RtspSessionOptions(new Uri(stalled.Url)));

        Assert.True(await good.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20)));
        Assert.True(await bad.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20)));

        stalled.PauseSending();

        var framesAtPause = good.Telemetry.FramesDecoded;
        await Task.Delay(1500);

        // The healthy camera keeps producing frames while the other one is being recycled.
        Assert.True(good.Telemetry.FramesDecoded > framesAtPause, "a stalled camera stopped a healthy one");
        Assert.Equal(StreamState.Live, good.Status.State);
    }

    private static bool FeedFirstFrames(byte[] annexB, IVideoDecoder decoder)
    {
        var nals = TestRtspServer.SplitAnnexB(annexB);
        var unit = new List<byte>();

        foreach (var nal in nals)
        {
            unit.AddRange(H264Depacketizer.StartCode);
            unit.AddRange(nal);

            if ((nal[0] & 0x1F) is 1 or 5)
            {
                if (decoder.Decode(unit.ToArray(), TimeSpan.Zero).Count > 0)
                {
                    return true;
                }

                unit.Clear();
            }
        }

        return false;
    }
}
