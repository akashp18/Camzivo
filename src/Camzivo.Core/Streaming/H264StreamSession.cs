using System.Runtime.Versioning;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Streaming.Video;

namespace Camzivo.Core.Streaming;

public enum StreamState
{
    Idle = 0,
    Connecting,
    /// <summary>Only reachable after a decoded frame was produced. Never set by a successful handshake.</summary>
    Live,
    Reconnecting,
    Failed,
    Stopped,
}

public sealed record StreamStatus(StreamState State, StreamFailure Failure, string Detail, int Attempt, int Reconnects)
{
    public bool IsLive => State == StreamState.Live;
}

public sealed record StreamTelemetry(
    long AccessUnits,
    long FramesDecoded,
    long FramesDropped,
    long Reconnects,
    long PacketsReceived,
    long PacketGaps,
    long BytesReceived,
    int Width,
    int Height,
    string DecoderName,
    bool HardwareDecoded);

/// <summary>
/// One camera stream: RTSP session → H.264 depacketizer → decoder → frames.
///
/// The state machine is the reason this class exists. <see cref="StreamState.Live"/> is set only after a frame has
/// been decoded and handed to the presenter; a successful DESCRIBE/PLAY leaves it Connecting. If frames stop, the
/// state drops to Reconnecting and a bounded backoff takes over.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class H264StreamSession : IAsyncDisposable
{
    private readonly Func<IVideoDecoder> _decoderFactory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private RtspClient? _client;
    private H264Depacketizer? _depacketizer;
    private IVideoDecoder? _decoder;
    private Task? _supervisor;
    private long _accessUnits;
    private long _frames;
    private long _dropped;
    private long _reconnects;
    private DateTimeOffset _lastFrameUtc;
    private int _attempt;
    private bool _disposed;
    private StreamStatus _status = new(StreamState.Idle, StreamFailure.None, "Not started", 0, 0);

    private readonly Func<IVideoDecoder>? _fallbackDecoderFactory;

    public H264StreamSession(Func<IVideoDecoder>? decoderFactory = null, Func<IVideoDecoder>? fallbackDecoderFactory = null)
    {
        _decoderFactory = decoderFactory ?? (() => new MediaFoundationH264Decoder());
        _fallbackDecoderFactory = fallbackDecoderFactory;
    }

    /// <summary>Why the preferred decoder could not be used, or null when it was used.</summary>
    public string? FallbackReason { get; private set; }

    public bool UsingFallback => FallbackReason is not null;

    /// <summary>
    /// Creates the decoder for one attempt. A preferred backend that cannot initialise (for example a hardware MFT
    /// on a GPU that has gone away) falls back to the next backend instead of failing the camera.
    /// </summary>
    private IVideoDecoder CreateDecoder()
    {
        try
        {
            return _decoderFactory();
        }
        catch (VideoDecoderException exception) when (_fallbackDecoderFactory is not null)
        {
            FallbackReason = exception.Message;
            return _fallbackDecoderFactory();
        }
    }

    /// <summary>Raised on the reader thread for every decoded frame. The presenter must not block here.</summary>
    public event Action<VideoFrame>? FrameReady;

    /// <summary>Raised whenever the state changes, so the tile can show truth instead of a spinner.</summary>
    public event Action<StreamStatus>? StatusChanged;

    public StreamStatus Status => Volatile.Read(ref _status);

    /// <summary>Stage timings for this session; the benchmark reads them to say where the time actually goes.</summary>
    public PipelineProfiler Profiler { get; } = new();

    public StreamTelemetry Telemetry => new(
        Interlocked.Read(ref _accessUnits),
        Interlocked.Read(ref _frames),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _reconnects),
        _client?.Statistics.PacketsReceived ?? 0,
        _client?.Statistics.Gaps ?? 0,
        _client?.Statistics.BytesReceived ?? 0,
        _decoder?.Width ?? 0,
        _decoder?.Height ?? 0,
        _decoder?.Name ?? "none",
        _decoder?.HardwareAccelerated ?? false);

    /// <summary>
    /// Starts the session and returns as soon as the first attempt is under way; use <see cref="StatusChanged"/> or
    /// <see cref="WaitForFirstFrameAsync"/> to observe the outcome.
    /// </summary>
    public void Start(RtspSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_supervisor is not null)
        {
            return;
        }

        _supervisor = Task.Run(() => SuperviseAsync(options, _stopping.Token), CancellationToken.None);
    }

    /// <summary>Completes when the first decoded frame arrives; false on timeout or failure.</summary>
    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        linked.CancelAfter(timeout);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (Interlocked.Read(ref _frames) > 0)
                {
                    return true;
                }

                if (Status.State is StreamState.Failed or StreamState.Stopped)
                {
                    return false;
                }

                await Task.Delay(50, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or disposal: report what actually happened.
        }

        return Interlocked.Read(ref _frames) > 0;
    }

    /// <summary>Waits for the first frame of a specific attempt, so a reconnected stream must prove itself again.</summary>
    private async Task<bool> WaitForFrameAfterAsync(long baseline, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        linked.CancelAfter(timeout);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (Interlocked.Read(ref _frames) > baseline)
                {
                    return true;
                }

                await Task.Delay(20, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or disposal: report what actually happened.
        }

        return Interlocked.Read(ref _frames) > baseline;
    }

    private async Task SuperviseAsync(RtspSessionOptions options, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            _attempt++;
            SetStatus(StreamState.Connecting, StreamFailure.None, $"Connecting to {RtspUriBuilder.Redact(options.Uri)}");

            try
            {
                await RunOnceAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RtspException exception)
            {
                if (exception.Failure is StreamFailure.Unauthorized or StreamFailure.Forbidden)
                {
                    // Credential problems do not fix themselves with retries.
                    SetStatus(StreamState.Failed, exception.Failure, exception.Message);
                    return;
                }

                // A codec this build cannot decode is not a network problem: retrying the same address is pointless,
                // but the channel's next stream (main → sub → third) may well be H.264. Try it once each, without a
                // backoff, and keep the address that worked for every later reconnect.
                if (exception.Failure == StreamFailure.UnsupportedCodec)
                {
                    if (RtspCodecFallback.Next(options.Uri) is { } alternative)
                    {
                        options = options with { Uri = alternative };
                        SetStatus(
                            StreamState.Connecting,
                            StreamFailure.None,
                            $"Stream codec cannot be decoded; trying the {RtspCodecFallback.Describe(alternative)}");
                        continue;
                    }

                    // Every stream of this channel is H.265. Retrying cannot change an encoder setting, and each retry
                    // costs one of the recorder's RTSP sessions — a wall of tiles doing that is what exhausted the
                    // device's connection limit. Stop, and say what actually fixes it.
                    SetStatus(
                        StreamState.Failed,
                        StreamFailure.UnsupportedCodec,
                        $"{exception.Message} Every stream of this channel is H.265; set the channel's encode mode to " +
                        "H.264 on the recorder (SmartPSS → Camera → Encode), or place a camera that offers H.264.");
                    return;
                }

                SetStatus(StreamState.Reconnecting, exception.Failure, exception.Message);
            }
            catch (VideoDecoderException exception)
            {
                SetStatus(StreamState.Failed, StreamFailure.DecoderInitFailed, exception.Message);
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
            {
                SetStatus(StreamState.Reconnecting, StreamFailure.ConnectionRefused, exception.Message);
            }
            catch (Exception exception)
            {
                // A session must never let an unexpected exception escape: the supervisor runs on its own task, so an
                // unhandled one used to surface later as an unobserved exception in whoever happened to be running
                // (a socket torn down mid-connect can throw NullReferenceException from TcpClient). Report it as a
                // stream failure and retry like any other connection problem.
                SetStatus(StreamState.Reconnecting, StreamFailure.Unknown, exception.Message);
            }

            await DisposeSessionAsync().ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Interlocked.Increment(ref _reconnects);
            SetStatus(
                StreamState.Reconnecting,
                Status.Failure,
                $"Reconnecting in {backoff.TotalSeconds:F0} s (attempt {_attempt})");

            // Jitter: a wall of tiles that fail together would otherwise retry in lockstep, and every burst counts
            // against the recorder's connection limit at the same instant.
            var pause = TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * (0.75 + Random.Shared.NextDouble() * 0.5));

            try
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 15));
        }

        SetStatus(StreamState.Stopped, StreamFailure.None, "Stopped");
    }

    private async Task RunOnceAsync(RtspSessionOptions options, CancellationToken cancellationToken)
    {
        _client = new RtspClient();
        _depacketizer = new H264Depacketizer();
        _decoder = CreateDecoder();

        if (_decoder is MediaFoundationH264Decoder mediaFoundation)
        {
            // Attribute NV12→BGRA cost to this stream instead of hiding it inside "decode".
            mediaFoundation.Profiler = Profiler;
        }

        _client.PacketReceived += OnPacket;

        // Every attempt starts its own clock: the frames from an earlier connection must never satisfy this one.
        var framesBefore = Interlocked.Read(ref _frames);
        _lastFrameUtc = DateTimeOffset.UtcNow;

        var description = await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);

        var parameterSets = description.Video?.ParameterSets ?? [];
        if (parameterSets.Count > 0)
        {
            _depacketizer = new H264Depacketizer(parameterSets);
        }

        // A connection that never produces frames is a failure, not "live". The watchdog enforces that.
        var firstFrame = await WaitForFrameAfterAsync(framesBefore, TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
        if (!firstFrame)
        {
            throw new RtspException(StreamFailure.NoVideo, "The stream connected but no video frame was decoded.", null);
        }

        // Stay in this attempt until the stream stops producing frames or the socket dies.
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow - _lastFrameUtc > TimeSpan.FromSeconds(10))
            {
                throw new RtspException(StreamFailure.Timeout, "No frames for 10 s.", null);
            }
        }
    }

    private void OnPacket(RtpPacket packet)
    {
        // Snapshot the collaborators: teardown nulls these fields from the supervisor thread while the reader is
        // still delivering packets, and reading them twice raced into a NullReferenceException on that thread.
        var depacketizer = _depacketizer;
        var decoder = _decoder;

        if (depacketizer is null || decoder is null)
        {
            return;
        }

        try
        {
            var depacketizeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var hasAccessUnit = depacketizer.TryAdd(packet.Payload, packet.Marker, out var accessUnit);
            Profiler.AddDepacketize(System.Diagnostics.Stopwatch.GetTimestamp() - depacketizeStart);

            if (!hasAccessUnit || accessUnit is null)
            {
                return;
            }

            Interlocked.Increment(ref _accessUnits);
            Profiler.AddAccessUnit();

            // RTP timestamps are 90 kHz for H.264; the decoder only needs an ordering hint.
            var timestamp = TimeSpan.FromSeconds(packet.Timestamp / 90000.0);

            var decodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var frames = decoder.Decode(accessUnit, timestamp);
            Profiler.AddDecode(System.Diagnostics.Stopwatch.GetTimestamp() - decodeStart);
            Profiler.AddFrames(frames.Count);

            foreach (var frame in frames)
            {
                Interlocked.Increment(ref _frames);
                _lastFrameUtc = DateTimeOffset.UtcNow;

                if (Status.State != StreamState.Live)
                {
                    SetStatus(StreamState.Live, StreamFailure.None, "Live");
                }

                FrameReady?.Invoke(frame);
            }
        }
        catch (Exception exception) when (exception is VideoDecoderException or InvalidDataException)
        {
            Interlocked.Increment(ref _dropped);
            SetStatus(StreamState.Reconnecting, StreamFailure.DecoderInitFailed, exception.Message);
            _ = exception;
        }
    }

    private void SetStatus(StreamState state, StreamFailure failure, string detail)
    {
        var status = new StreamStatus(state, failure, detail, _attempt, (int)Interlocked.Read(ref _reconnects));
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(status);
    }

    private async Task DisposeSessionAsync()
    {
        if (_client is not null)
        {
            _client.PacketReceived -= OnPacket;
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _depacketizer = null;

        if (_decoder is not null)
        {
            _decoder.Dispose();
            _decoder = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping.Cancel();
            await DisposeSessionAsync().ConfigureAwait(false);

            if (_supervisor is not null)
            {
                try
                {
                    await _supervisor.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
                {
                    _ = exception;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
            _stopping.Dispose();
            _lifecycle.Dispose();
        }
    }
}
