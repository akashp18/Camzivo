using System.Diagnostics;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Streaming.Video;

namespace Camzivo.Core.Streaming.Replay;

public enum ReplayState
{
    Idle,
    Connecting,
    Playing,
    Paused,
    Ended,
    Failed,
    Stopped,
}

public sealed record ReplayStatus(
    ReplayState State,
    StreamFailure Failure,
    string Detail,
    DateTimeOffset PositionUtc,
    DateTimeOffset SegmentStartUtc,
    DateTimeOffset SegmentEndUtc,
    double Rate,
    int Reconnects,
    long Frames,
    double ObservedRate = 0,
    long FramesDecoded = 0,
    long FramesPresented = 0);

/// <summary>Replay-specific RTSP behaviour: ONVIF replay is addressed by absolute clock time, not npt=0.</summary>
public sealed record ReplayPlaybackOptions(DateTimeOffset FromUtc, double Rate = 1.0)
{
    /// <summary>RTSP Range header for a replay request. UTC, second resolution, open-ended end.</summary>
    public string RangeHeader => $"clock={FromUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-";

    /// <summary>
    /// ONVIF replay Scale header. Invariant culture is deliberate: a locale comma would make the header invalid and a
    /// recorder would silently play at 1×.
    /// </summary>
    public string ScaleHeader => $"Scale: {Rate.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}";
}

/// <summary>
/// A recorded-video playback session. It is deliberately separate from <see cref="H264StreamSession"/>: replay has a
/// clock position, pause, seek and an end, while live has none of those, and a playback failure must never touch the
/// live wall. It reuses the same RTSP client, depacketizer and decoder, so it is the same video path.
///
/// Position comes from RTP timestamps (90 kHz), not from a frame counter: that is what makes the reported time
/// meaningful when a device serves a variable-rate recording.
/// </summary>
public sealed class ReplaySession : IAsyncDisposable
{
    private readonly Func<IVideoDecoder> _decoderFactory;
    private readonly object _gate = new();

    private RtspClient? _client;
    private H264Depacketizer? _depacketizer;
    private IVideoDecoder? _decoder;
    private CancellationTokenSource? _running;
    private Task? _watchdog;

    private long _framesDecoded;
    private long _framesPresented;
    private long _lastFrameTicks;
    private long _reconnects;
    private uint _baseTimestamp;
    private bool _hasBaseTimestamp;
    private DateTimeOffset _basePosition;
    private DateTimeOffset _position;

    private readonly PlaybackRateMeter _rateMeter = new();
    private long _rateClockStart;
    private double _observedRate;
    private long _lastStatusTicks;
    private long _lastPresentedTicks;

    private volatile ReplayState _state = ReplayState.Idle;
    private volatile StreamFailure _failure = StreamFailure.None;
    private volatile string _detail = "Not started";
    private double _rate = 1.0;

    /// <param name="decoderFactory">
    /// Supplied by the caller so Core stays platform-neutral: the desktop passes its Media Foundation decoder, tests
    /// pass the fixture decoder. A playback session never silently falls back to a different video path.
    /// </param>
    public ReplaySession(Func<IVideoDecoder> decoderFactory)
    {
        ArgumentNullException.ThrowIfNull(decoderFactory);
        _decoderFactory = decoderFactory;
    }

    public Guid CameraId { get; init; }

    public DateTimeOffset SegmentStartUtc { get; private set; }

    public DateTimeOffset SegmentEndUtc { get; private set; }

    public ReplayStatus Status => new(
        _state,
        _failure,
        _detail,
        _position,
        SegmentStartUtc,
        SegmentEndUtc,
        _rate,
        (int)Interlocked.Read(ref _reconnects),
        Interlocked.Read(ref _framesPresented),
        _observedRate,
        Interlocked.Read(ref _framesDecoded),
        Interlocked.Read(ref _framesPresented));

    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    /// <summary>SPS/PPS from the replay SDP; the muxer needs them to describe the track, not to decode anything.</summary>
    public IReadOnlyList<byte[]> ParameterSets { get; private set; } = [];

    public int DecodedWidth { get; private set; }

    public int DecodedHeight { get; private set; }

    /// <summary>Raised on the network reader task with each decoded frame.</summary>
    public event Action<VideoFrame>? FrameReady;

    /// <summary>Raw access unit plus its 90 kHz RTP timestamp, for evidence export (no decode needed).</summary>
    public event Action<byte[], uint>? AccessUnitReady;

    public event Action<ReplayStatus>? StatusChanged;

    /// <summary>Opens the replay URI at a clock position inside the segment.</summary>
    public async Task ConnectAsync(
        RtspSessionOptions rtsp,
        DateTimeOffset segmentStartUtc,
        DateTimeOffset segmentEndUtc,
        DateTimeOffset fromUtc,
        double rate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rtsp);

        if (segmentEndUtc <= segmentStartUtc)
        {
            throw new ArgumentException("The recording segment has no duration.", nameof(segmentEndUtc));
        }

        if (!PlaybackRates.IsSupported(rate))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rate),
                rate,
                $"Supported replay speeds are {string.Join(", ", PlaybackRates.Supported.Select(PlaybackRates.Label))}.");
        }

        SegmentStartUtc = segmentStartUtc;
        SegmentEndUtc = segmentEndUtc;
        _rate = rate;
        _observedRate = 0;
        _rateMeter.Reset();

        var from = fromUtc < segmentStartUtc ? segmentStartUtc : fromUtc >= segmentEndUtc ? segmentEndUtc.AddTicks(-1) : fromUtc;
        _position = from;
        _basePosition = from;

        _running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await OpenAsync(rtsp, from, _running.Token).ConfigureAwait(false);

        _watchdog = Task.Run(() => WatchAsync(rtsp, _running.Token), CancellationToken.None);
    }

    private async Task OpenAsync(
        RtspSessionOptions rtsp,
        DateTimeOffset fromUtc,
        CancellationToken cancellationToken,
        bool startPaused = false)
    {
        await CloseTransportAsync().ConfigureAwait(false);

        SetState(ReplayState.Connecting, StreamFailure.None, $"Opening replay at {fromUtc:HH:mm:ss} UTC");

        var client = new RtspClient();
        var decoder = _decoderFactory();
        var depacketizer = new H264Depacketizer();

        if (decoder is IPresentationFilteredDecoder filtered)
        {
            filtered.ShouldPresent = ShouldPresentFrame;
        }

        lock (_gate)
        {
            _client = client;
            _decoder = decoder;
            _depacketizer = depacketizer;
            _basePosition = fromUtc;
            _position = fromUtc;
            _hasBaseTimestamp = false;
            _rateMeter.Reset();
            Volatile.Write(ref _lastPresentedTicks, 0);
            Volatile.Write(ref _rateClockStart, Stopwatch.GetTimestamp());
            Volatile.Write(ref _lastFrameTicks, DateTimeOffset.UtcNow.UtcTicks);
        }

        client.PacketReceived += OnPacket;

        // A recorder that accepts the socket but never answers DESCRIBE/SETUP/PLAY must not leave a speed change
        // awaiting forever. The caller still gets a typed RTSP failure to report.
        using var openTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        openTimeout.CancelAfter(rtsp.RequestTimeout + TimeSpan.FromSeconds(5));

        try
        {
            var description = await client
                .ConnectAsync(rtsp, openTimeout.Token, new ReplayPlaybackOptions(fromUtc, _rate))
                .ConfigureAwait(false);

            var parameterSets = description.Video?.ParameterSets ?? [];
            ParameterSets = parameterSets;

            if (parameterSets.Count > 0)
            {
                lock (_gate)
                {
                    _depacketizer = new H264Depacketizer(parameterSets);
                }
            }

            if (startPaused)
            {
                await client.SendCommandAsync("PAUSE", CancellationToken.None).ConfigureAwait(false);
                _rateMeter.Reset();
                SetState(ReplayState.Paused, StreamFailure.None, "Paused");
            }
            else
            {
                SetState(ReplayState.Playing, StreamFailure.None, "Playing");
            }
        }
        catch (RtspException exception)
        {
            SetState(ReplayState.Failed, exception.Failure, exception.Message);
            await CloseTransportAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string detail = "The recorder did not answer the replay request in time.";
            SetState(ReplayState.Failed, StreamFailure.Timeout, detail);
            await CloseTransportAsync().ConfigureAwait(false);
            throw new RtspException(StreamFailure.Timeout, detail, null);
        }
    }

    public async Task PauseAsync()
    {
        var client = _client;
        if (client is null || _state != ReplayState.Playing)
        {
            return;
        }

        await client.SendCommandAsync("PAUSE", CancellationToken.None).ConfigureAwait(false);
        SetState(ReplayState.Paused, StreamFailure.None, "Paused");
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null || _state != ReplayState.Paused)
        {
            return;
        }

        await client.SendCommandAsync(
            "PLAY",
            cancellationToken,
            new ReplayPlaybackOptions(_position, _rate).ScaleHeader).ConfigureAwait(false);

        _rateMeter.Reset();
        Volatile.Write(ref _rateClockStart, Stopwatch.GetTimestamp());
        Volatile.Write(ref _lastFrameTicks, DateTimeOffset.UtcNow.UtcTicks);
        SetState(ReplayState.Playing, StreamFailure.None, "Playing");
    }

    /// <summary>
    /// Seeks by reopening the replay at the target clock position. Reusing a live-style PLAY on the same session is
    /// not portable across replay servers, and a fresh connection also guarantees no old frames are mistaken for the
    /// new position.
    /// </summary>
    public async Task SeekAsync(RtspSessionOptions rtsp, DateTimeOffset targetUtc, CancellationToken cancellationToken)
    {
        var target = targetUtc < SegmentStartUtc ? SegmentStartUtc
            : targetUtc >= SegmentEndUtc ? SegmentEndUtc.AddTicks(-1)
            : targetUtc;

        var wasPaused = _state == ReplayState.Paused;
        _basePosition = target;
        _position = target;
        SetState(ReplayState.Connecting, StreamFailure.None, $"Seeking to {target:HH:mm:ss} UTC");

        await OpenAsync(rtsp, target, cancellationToken, wasPaused).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes speed by reopening the replay at the current source position with a new Scale. Reopening is deliberate:
    /// a replay server is not required to honour a Scale change on an existing PLAY, and a fresh connection cannot
    /// confuse frames from the old speed with the new one. A paused session stays paused at the new speed.
    /// </summary>
    public async Task<PlaybackRateChange> ChangeRateAsync(
        RtspSessionOptions rtsp,
        double rate,
        CancellationToken cancellationToken = default)
    {
        if (!PlaybackRates.IsSupported(rate))
        {
            return new PlaybackRateChange(false, rate, _rate, $"Speed {PlaybackRates.Label(rate)} is not supported.");
        }

        if (Math.Abs(rate - _rate) < 0.001 && _state is ReplayState.Playing or ReplayState.Paused)
        {
            return new PlaybackRateChange(true, rate, _rate, $"Already playing at {PlaybackRates.Label(rate)}.");
        }

        if (_state is not (ReplayState.Playing or ReplayState.Paused))
        {
            _rate = rate;
            _observedRate = 0;
            _rateMeter.Reset();
            return new PlaybackRateChange(true, rate, rate, $"Speed {PlaybackRates.Label(rate)} will be used at the next play.");
        }

        var previous = _rate;
        var wasPaused = _state == ReplayState.Paused;
        var position = _position;
        _rate = rate;

        try
        {
            await OpenAsync(rtsp, position, cancellationToken, wasPaused).ConfigureAwait(false);
            return new PlaybackRateChange(true, rate, rate, $"Requested {PlaybackRates.Label(rate)} from the recorder.");
        }
        catch (RtspException exception)
        {
            // Put the session back on the speed that worked, at the same source position, rather than leaving the
            // operator with a dead player after a rejected speed request.
            _rate = previous;

            try
            {
                await OpenAsync(rtsp, position, cancellationToken, wasPaused).ConfigureAwait(false);
            }
            catch (RtspException)
            {
                SetState(ReplayState.Failed, exception.Failure, exception.Message);
            }

            return new PlaybackRateChange(
                false,
                rate,
                previous,
                $"The recorder rejected {PlaybackRates.Label(rate)} ({exception.Message}). " +
                $"Playback continued at {PlaybackRates.Label(previous)}.");
        }
    }

    public async Task StopAsync()
    {
        var running = _running;
        running?.Cancel();
        SetState(ReplayState.Stopped, StreamFailure.None, "Stopped");
        await CloseTransportAsync().ConfigureAwait(false);
    }

    private void OnPacket(RtpPacket packet)
    {
        var depacketizer = _depacketizer;
        var decoder = _decoder;

        if (depacketizer is null || decoder is null)
        {
            return;
        }

        try
        {
            if (!depacketizer.TryAdd(packet.Payload, packet.Marker, out var accessUnit) || accessUnit is null)
            {
                return;
            }

            long sourceDelta;
            var wallSeconds = Stopwatch.GetElapsedTime(Volatile.Read(ref _rateClockStart)).TotalSeconds;

            // Source timestamps may not jump further than the requested speed could plausibly have carried the
            // recording. A single corrupt/reordered timestamp used to fling the playhead to the end of the segment.
            var maxPlausibleSourceTicks = (long)((wallSeconds * _rate + 10) * 90_000 * 3);

            lock (_gate)
            {
                if (!_hasBaseTimestamp)
                {
                    _baseTimestamp = packet.Timestamp;
                    _hasBaseTimestamp = true;
                }

                sourceDelta = (long)packet.Timestamp - _baseTimestamp;

                // A negative or absurd jump is a discontinuity: rebase at the current position instead of letting
                // the playhead run backwards or into the next day.
                if (sourceDelta < 0 || sourceDelta > maxPlausibleSourceTicks)
                {
                    _baseTimestamp = packet.Timestamp;
                    _basePosition = _position;
                    sourceDelta = 0;
                    _rateMeter.Reset();
                }
            }

            AccessUnitReady?.Invoke(accessUnit, packet.Timestamp);

            var measured = _rateMeter.Sample(packet.Timestamp / 90000.0, wallSeconds);

            if (measured > 0)
            {
                _observedRate = measured;
            }

            Interlocked.Increment(ref _framesDecoded);
            var frames = decoder.Decode(accessUnit, TimeSpan.FromSeconds(packet.Timestamp / 90000.0));

            var position = _basePosition + TimeSpan.FromSeconds(sourceDelta / 90000.0);
            if (position > SegmentEndUtc)
            {
                position = SegmentEndUtc;
            }

            _position = position;

            foreach (var frame in frames)
            {
                DecodedWidth = frame.Width;
                DecodedHeight = frame.Height;
                Volatile.Write(ref _lastFrameTicks, DateTimeOffset.UtcNow.UtcTicks);

                if (_state == ReplayState.Playing)
                {
                    Interlocked.Increment(ref _framesPresented);
                    FrameReady?.Invoke(frame);
                }
            }

            RaiseStatus();
        }
        catch (Exception exception) when (exception is VideoDecoderException or InvalidDataException)
        {
            SetState(ReplayState.Failed, StreamFailure.DecoderInitFailed, exception.Message);
        }
    }

    /// <summary>
    /// Presentation is capped while decoding continues. At 16× a 15 fps recording produces 240 access units a
    /// second; converting all of them to BGRA only to coalesce them on the UI thread would spend most of the CPU on
    /// pictures the operator never sees.
    /// </summary>
    private bool ShouldPresentFrame(TimeSpan sourceTimestamp)
    {
        _ = sourceTimestamp;

        if (_rate <= 1)
        {
            return true;
        }

        var targetFps = _rate <= 4 ? 30 : 20;
        var interval = Stopwatch.Frequency / targetFps;
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastPresentedTicks);

        if (last != 0 && now - last < interval)
        {
            return false;
        }

        Volatile.Write(ref _lastPresentedTicks, now);
        return true;
    }

    private async Task WatchAsync(RtspSessionOptions rtsp, CancellationToken cancellationToken)
    {
        var reconnects = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);

                if (_state is ReplayState.Connecting or ReplayState.Paused or ReplayState.Stopped or ReplayState.Failed or ReplayState.Ended)
                {
                    continue;
                }

                var silent = DateTimeOffset.UtcNow.UtcTicks - Volatile.Read(ref _lastFrameTicks);

                if (silent < TimeSpan.FromSeconds(3).Ticks)
                {
                    continue;
                }

                if (_position >= SegmentEndUtc.AddMilliseconds(-800))
                {
                    _position = SegmentEndUtc;
                    _observedRate = 0;
                    SetState(ReplayState.Ended, StreamFailure.None, "End of recording");
                    return;
                }

                // A stall inside the recording is worth one automatic retry from the last known position.
                if (reconnects >= 2)
                {
                    SetState(ReplayState.Ended, StreamFailure.None, "The recording ended or the connection was lost");
                    return;
                }

                reconnects++;
                Interlocked.Increment(ref _reconnects);

                try
                {
                    await OpenAsync(rtsp, _position, cancellationToken).ConfigureAwait(false);
                }
                catch (RtspException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the owner.
        }
    }

    private async Task CloseTransportAsync()
    {
        RtspClient? client;
        IVideoDecoder? decoder;

        lock (_gate)
        {
            client = _client;
            decoder = _decoder;
            _client = null;
            _decoder = null;
            _depacketizer = null;
            _hasBaseTimestamp = false;
        }

        if (client is not null)
        {
            client.PacketReceived -= OnPacket;
            await client.DisposeAsync().ConfigureAwait(false);
        }

        decoder?.Dispose();
    }

    private void SetState(ReplayState state, StreamFailure failure, string detail)
    {
        _state = state;
        _failure = failure;
        _detail = detail;
        RaiseStatus(force: true);
    }

    /// <summary>
    /// Status is throttled to ten updates a second. At 16× the decoder can produce hundreds of frames a second;
    /// raising a property change per frame would spend the UI thread on timestamps instead of video.
    /// </summary>
    private void RaiseStatus(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastStatusTicks);

        if (!force && now - last < Stopwatch.Frequency / 10)
        {
            return;
        }

        Volatile.Write(ref _lastStatusTicks, now);
        StatusChanged?.Invoke(Status);
    }

    public async ValueTask DisposeAsync()
    {
        _running?.Cancel();
        await CloseTransportAsync().ConfigureAwait(false);

        if (_watchdog is not null)
        {
            try
            {
                await _watchdog.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                _ = exception;
            }
        }

        _running?.Dispose();
        _running = null;
    }
}
