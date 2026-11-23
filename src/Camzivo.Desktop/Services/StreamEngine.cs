using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Windows.Threading;
using Camzivo.Core.Streaming;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Streaming.Video;

namespace Camzivo.Desktop.Services;

/// <summary>A resolved place to get video; credentials come from the secret store, never from the view model.</summary>
public sealed record StreamTarget(Uri Uri, string? Username, string? Password);

/// <summary>What the UI thread actually did: frames shown, frames replaced before they could be shown, time spent.</summary>
public sealed record PresentationSnapshot(long Presented, long Coalesced, double PresentMs, int Pending)
{
    public double PresentMsPerFrame => Presented > 0 ? PresentMs / Presented : 0;

    public double CoalescedPercent => Presented + Coalesced > 0
        ? 100.0 * Coalesced / (Presented + Coalesced)
        : 0;
}

/// <summary>
/// Owns the live sessions for the wall: one session per camera, regardless of how many tiles show it.
///
/// Frames arrive on decoder threads and are coalesced to the newest one before being marshalled to the UI thread, so
/// a slow render can never build a backlog of stale pictures and a busy wall cannot flood the dispatcher.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamEngine : IAsyncDisposable
{
    private sealed record Entry(H264StreamSession Session, Uri Uri, string? Username, string? Password);

    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly ConcurrentDictionary<Guid, VideoFrame> _pending = new();
    private readonly ConcurrentDictionary<Guid, long> _presented = new();
    private long _presentedTotal;
    private long _coalesced;
    private long _presentTicks;
    private readonly Dispatcher _dispatcher;
    private readonly Func<IVideoDecoder>? _decoderFactory;
    private int _posted;
    private long _sessionStarts;

    public StreamEngine(Dispatcher dispatcher, Func<IVideoDecoder>? decoderFactory = null)
    {
        _dispatcher = dispatcher;
        _decoderFactory = decoderFactory;
    }

    /// <summary>Raised on the UI thread with the newest decoded frame for a camera.</summary>
    public event Action<Guid, VideoFrame>? FramePresented;

    /// <summary>Raised on the UI thread when a session changes state.</summary>
    public event Action<Guid, StreamStatus>? StatusChanged;

    public int ActiveCount
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    public IReadOnlyCollection<Guid> ActiveCameras
    {
        get
        {
            lock (_entries)
            {
                return _entries.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// Presentation cost and loss: how often a decoded frame was replaced by a newer one before the UI thread got to
    /// it. Coalescing keeps the wall responsive, but it is video loss and the benchmark counts it as such.
    /// </summary>
    public PresentationSnapshot Presentation => new(
        Interlocked.Read(ref _presentedTotal),
        Interlocked.Read(ref _coalesced),
        Interlocked.Read(ref _presentTicks) * PipelineProfiler.MillisecondsPerTick,
        _pending.Count);

    /// <summary>Stage timings for one camera (empty when it is not attached).</summary>
    public PipelineSnapshot Pipeline(Guid cameraId)
    {
        lock (_entries)
        {
            return _entries.TryGetValue(cameraId, out var entry) ? entry.Session.Profiler.Snapshot() : PipelineSnapshot.Empty;
        }
    }

    /// <summary>Session telemetry for one camera (decoder name, drops, dimensions), or null when it is not attached.</summary>
    public StreamTelemetry? Telemetry(Guid cameraId)
    {
        lock (_entries)
        {
            return _entries.TryGetValue(cameraId, out var entry) ? entry.Session.Telemetry : null;
        }
    }

    /// <summary>
    /// Sessions this engine has started since launch. Attaching the same address again must not increase it, so it is
    /// the cheap check that a tile move or a refresh did not quietly restart video.
    /// </summary>
    public long SessionStarts => Interlocked.Read(ref _sessionStarts);

    /// <summary>Frames actually handed to a presenter for this camera; the number the wall can honestly report.</summary>
    public long PresentedFrames(Guid cameraId) => _presented.TryGetValue(cameraId, out var count) ? count : 0;

    /// <summary>Starts a stream, or restarts it when the address or credentials changed. Idempotent otherwise.</summary>
    public void Attach(Guid cameraId, Uri uri, string? username, string? password)
    {
        ArgumentNullException.ThrowIfNull(uri);

        lock (_entries)
        {
            if (_entries.TryGetValue(cameraId, out var existing))
            {
                if (existing.Uri == uri &&
                    string.Equals(existing.Username, username, StringComparison.Ordinal) &&
                    string.Equals(existing.Password, password, StringComparison.Ordinal))
                {
                    return;
                }

                DetachCore(cameraId);
            }

            var session = new H264StreamSession(_decoderFactory);
            session.FrameReady += frame => OnFrame(cameraId, frame);
            session.StatusChanged += status => OnStatus(cameraId, status);

            _entries[cameraId] = new Entry(session, uri, username, password);
            Interlocked.Increment(ref _sessionStarts);
            session.Start(new RtspSessionOptions(uri, username, password));
        }
    }

    /// <summary>Restarts a stream with the address and credentials it already had ("Reconnect all").</summary>
    public bool Restart(Guid cameraId)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(cameraId, out var existing))
            {
                return false;
            }

            DetachCore(cameraId);
            Attach(cameraId, existing.Uri, existing.Username, existing.Password);
            return true;
        }
    }

    public void Detach(Guid cameraId)
    {
        lock (_entries)
        {
            DetachCore(cameraId);
        }
    }

    public void DetachAll()
    {
        lock (_entries)
        {
            foreach (var id in _entries.Keys.ToList())
            {
                DetachCore(id);
            }
        }
    }

    private void DetachCore(Guid cameraId)
    {
        if (!_entries.Remove(cameraId, out var entry))
        {
            return;
        }

        _pending.TryRemove(cameraId, out _);
        _ = entry.Session.DisposeAsync().AsTask();
    }

    private void OnFrame(Guid cameraId, VideoFrame frame)
    {
        // Latest frame wins; an older queued frame for the same camera is dropped, and that is counted as lost video.
        if (_pending.TryGetValue(cameraId, out _))
        {
            Interlocked.Increment(ref _coalesced);
        }

        _pending[cameraId] = frame;

        if (Interlocked.Exchange(ref _posted, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(DrainFrames));
    }

    private void DrainFrames()
    {
        Interlocked.Exchange(ref _posted, 0);

        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var frame))
            {
                _presented.AddOrUpdate(pair.Key, 1, (_, count) => count + 1);
                Interlocked.Increment(ref _presentedTotal);

                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                FramePresented?.Invoke(pair.Key, frame);
                Interlocked.Add(ref _presentTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
        }
    }

    private void OnStatus(Guid cameraId, StreamStatus status)
    {
        if (_dispatcher.CheckAccess())
        {
            StatusChanged?.Invoke(cameraId, status);
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => StatusChanged?.Invoke(cameraId, status)));
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> entries;
        lock (_entries)
        {
            entries = _entries.Values.ToList();
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
