using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camzivo.Core.Recording;
using Camzivo.Core.Streaming.Replay;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Streaming.Video;
using Camzivo.Data.Recording;
using Camzivo.Data.Storage;
using Camzivo.Desktop.Controls;
using Camzivo.Desktop.Services;

namespace Camzivo.Desktop.ViewModels;

/// <summary>A camera the Playback page can investigate, with everything needed to reach its recordings.</summary>
public sealed record PlaybackCamera(
    Guid Id,
    string Name,
    Uri ServiceUri,
    string? Username,
    string? Password,
    string? SourceToken,
    int RtspPort = 554);

/// <summary>Everything an export produced, for the Exports page and the local audit trail.</summary>
public sealed record EvidenceExportCompleted(
    Guid CameraId,
    string CameraName,
    string Path,
    long Bytes,
    int Samples,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? Sha256);

/// <summary>A selectable playback speed. The page keeps one of these per supported rate so the selector is data-driven.</summary>
public sealed class PlaybackSpeedOption : ObservableObject
{
    private bool _isSelected;

    public PlaybackSpeedOption(double rate)
    {
        Rate = rate;
        Label = PlaybackRates.Label(rate);
    }

    public double Rate { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// The investigation page: choose a camera and date, search the recorder, review the returned ranges on a timeline and
/// play them back, then export a range as evidence.
///
/// Timeline geometry is a list of fractions plus the window bounds; the custom <see cref="TimelineControl"/> renders
/// it. Playback position updates one number, never the block collection.
/// </summary>
public sealed class PlaybackViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRecordingCatalog _catalog;
    private readonly Func<IVideoDecoder> _decoderFactory;
    private readonly TimeZoneInfo _zone;
    private readonly SynchronizationContext? _ui;
    private readonly string _exportRoot;

    private readonly List<PlaybackCamera> _cameras = [];
    private CancellationTokenSource? _searchCancellation;
    private ReplaySession? _session;
    private RtspSessionOptions? _replayTransport;
    private RecordingSegment? _playingSegment;

    private PlaybackCamera? _selectedCamera;
    private DateTime _selectedDate = DateTime.Today;
    private RecordingAvailability? _availability;
    private TimelineBar? _selectedBlock;
    private string _statusText = "Idle";
    private string? _errorText;
    private bool _isSearching;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _hasSearched;
    private string _positionText = "--:--:--";
    private string _rangeText = "No recording selected";
    private double _playheadFraction = double.NaN;
    private double _zoomHours = 24;
    private DateTime _windowStart;
    private WriteableBitmap? _frame;
    private double _playbackRate = 1.0;
    private double _observedRate;
    private long _framesShown;
    private long _framesDecoded;
    private long _framesCoalesced;
    private VideoFrame? _pendingFrame;
    private int _framePostQueued;
    private DateTimeOffset? _positionUtc;
    private string? _lastPlayedToken;
    private string? _speedNotice;
    private string? _speedChangeError;

    private readonly EvidenceExporter _exporter;
    private CancellationTokenSource? _exportCancellation;
    private bool _isExporting;
    private double _exportFraction;
    private string _exportStatus = string.Empty;
    private string _exportStartText = string.Empty;
    private string _exportEndText = string.Empty;
    private string _exportDestination = string.Empty;
    private string? _exportValidation;
    private string? _lastExportPath;
    private string? _lastExportHash;
    private string _exportSummary = "No evidence exported yet.";

    public PlaybackViewModel(
        IRecordingCatalog catalog,
        Func<IVideoDecoder> decoderFactory,
        TimeZoneInfo? zone = null,
        EvidenceExporter? exporter = null,
        string? exportRoot = null)
    {
        _catalog = catalog;
        _decoderFactory = decoderFactory;
        _exporter = exporter ?? new EvidenceExporter(decoderFactory);
        _zone = zone ?? TimeZoneInfo.Local;
        _ui = SynchronizationContext.Current;
        _windowStart = DateTime.Today;
        _exportRoot = exportRoot ?? AppPaths.Default.ExportRoot;

        SearchCommand = new RelayCommand(_ => _ = SearchAsync(), _ => SelectedCamera is not null && !IsSearching);
        PlayCommand = new RelayCommand(_ => _ = PlayAsync(), _ => SelectedBlock is not null && !IsPlaying);
        PauseCommand = new RelayCommand(_ => _ = PauseAsync(), _ => IsPlaying && !IsPaused);
        ResumeCommand = new RelayCommand(_ => _ = ResumeAsync(), _ => IsPaused);
        StopCommand = new RelayCommand(_ => _ = StopAsync(), _ => IsPlaying || IsPaused);
        SeekBackCommand = new RelayCommand(parameter => _ = SeekRelativeAsync(-Seconds(parameter)), _ => IsPlaying);
        SeekForwardCommand = new RelayCommand(parameter => _ = SeekRelativeAsync(Seconds(parameter)), _ => IsPlaying);
        JumpStartCommand = new RelayCommand(_ => _ = SeekRelativeAsync(null, toEnd: false), _ => IsPlaying);
        JumpEndCommand = new RelayCommand(_ => _ = SeekRelativeAsync(null, toEnd: true), _ => IsPlaying);
        ZoomInCommand = new RelayCommand(_ => Zoom(0.5), _ => ZoomHours > 1);
        ZoomOutCommand = new RelayCommand(_ => Zoom(2), _ => ZoomHours < 24);
        MoveBackCommand = new RelayCommand(_ => Pan(-ZoomHours / 2));
        MoveForwardCommand = new RelayCommand(_ => Pan(ZoomHours / 2));
        Speeds = PlaybackRates.Supported.Select(rate => new PlaybackSpeedOption(rate)).ToArray();
        Speeds[0].IsSelected = true;
        SetRateCommand = new RelayCommand(parameter => _ = SetSpeedAsync(RateFrom(parameter)));
        CancelExportCommand = new RelayCommand(_ => _exportCancellation?.Cancel(), _ => IsExporting);
        ExportCommand = new RelayCommand(_ => _ = ExportSelectedAsync(), _ => CanExport);
        ChooseExportFileCommand = new RelayCommand(_ => ChooseExportFile());
        VerifyLastExportCommand = new RelayCommand(_ => _ = VerifyLastExportAsync(), _ => LastExportPath is not null);
        SelectBlockCommand = new RelayCommand(parameter =>
        {
            if (parameter is TimelineBar block)
            {
                SelectedBlock = block;
            }
        });
        TimelineSelectCommand = new RelayCommand(parameter =>
        {
            if (parameter is double fraction)
            {
                SelectAt(fraction);
            }
        });

        Raise(nameof(SelectedDate));
        RebuildTimeline();
    }

    private static TimeSpan Seconds(object? parameter) =>
        double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? TimeSpan.FromSeconds(value)
            : TimeSpan.FromSeconds(10);

    public ObservableCollection<PlaybackCamera> Cameras { get; } = [];

    /// <summary>Recording bars for the visible window, newest geometry written on search and zoom only.</summary>
    public ObservableCollection<TimelineBar> Blocks { get; } = [];

    public ObservableCollection<TimelineTick> Ticks { get; } = [];

    /// <summary>1×, 2×, 4×, 6×, 8× and 16×. The selected one is highlighted by the view.</summary>
    public IReadOnlyList<PlaybackSpeedOption> Speeds { get; }

    public RelayCommand SearchCommand { get; }

    public RelayCommand PlayCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand SeekBackCommand { get; }

    public RelayCommand SeekForwardCommand { get; }

    public RelayCommand JumpStartCommand { get; }

    public RelayCommand JumpEndCommand { get; }

    public RelayCommand ZoomInCommand { get; }

    public RelayCommand ZoomOutCommand { get; }

    public RelayCommand MoveBackCommand { get; }

    public RelayCommand MoveForwardCommand { get; }

    public RelayCommand SetRateCommand { get; }

    /// <summary>Mouse selection on the timeline: selects a block, never rebuilds the timeline.</summary>
    public RelayCommand SelectBlockCommand { get; }

    /// <summary>Receives a fraction of the window from <see cref="TimelineControl"/>.</summary>
    public RelayCommand TimelineSelectCommand { get; }

    public RelayCommand ExportCommand { get; }

    public RelayCommand ChooseExportFileCommand { get; }

    public RelayCommand VerifyLastExportCommand { get; }

    public RelayCommand CancelExportCommand { get; }

    /// <summary>Raised after a successful export so the shell can record it and refresh the Exports page.</summary>
    public event Action<EvidenceExportCompleted>? ExportCompleted;

    public PlaybackCamera? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (!Set(ref _selectedCamera, value))
            {
                return;
            }

            _availability = null;
            _hasSearched = false;
            Blocks.Clear();
            Ticks.Clear();
            SelectedBlock = null;
            _lastPlayedToken = null;
            _speedChangeError = null;
            SetRequestedSpeed(1);
            SpeedNotice = null;
            _observedRate = 0;
            Raise(nameof(HasSearched));
            Raise(nameof(AvailabilityLabel));
            Raise(nameof(EmptyTimelineText));
            RebuildTimeline();
            RefreshCommands();
        }
    }

    public DateTime SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (Set(ref _selectedDate, value))
            {
                _windowStart = value.Date;
                Raise(nameof(SelectedDateLabel));
                RebuildTimeline();
            }
        }
    }

    public string SelectedDateLabel => SelectedDate.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);

    public string ZoneLabel => _zone.Id;

    public bool HasSearched
    {
        get => _hasSearched;
        private set
        {
            if (Set(ref _hasSearched, value))
            {
                Raise(nameof(EmptyTimelineText));
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (Set(ref _isSearching, value))
            {
                if (value)
                {
                    StatusText = "Searching the recorder…";
                }

                RefreshCommands();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (Set(ref _isPlaying, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (Set(ref _isPaused, value))
            {
                RefreshCommands();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string AvailabilityLabel
    {
        get
        {
            if (_availability is null)
            {
                return HasSearched ? "No search has completed for this camera." : "Not searched yet";
            }

            if (_availability.UnsupportedReason is { } reason)
            {
                return reason;
            }

            if (_availability.Segments.Count == 0)
            {
                return $"No recordings on {SelectedDateLabel}.";
            }

            var total = _availability.TotalRecorded;
            return $"{_availability.Segments.Count} recording{(_availability.Segments.Count == 1 ? string.Empty : "s")} · " +
                   $"{total.Hours}h {total.Minutes:00}m recorded · all times {_zone.StandardName}";
        }
    }

    public string EmptyTimelineText => _availability is null
        ? HasSearched ? "No search has completed." : "Search a date to see recordings."
        : Blocks.Count == 0
            ? "No recordings in this window."
            : string.Empty;

    public TimelineBar? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (!Set(ref _selectedBlock, value))
            {
                return;
            }

            RangeText = value is null
                ? "No recording selected"
                : $"{Local(value.Segment.StartUtc):HH:mm:ss} – {Local(value.Segment.EndUtc):HH:mm:ss}";

            if (value is not null)
            {
                SeedExportRange(value.Segment);
            }

            Raise(nameof(SelectedSegmentText));
            Raise(nameof(SelectedToken));
            Raise(nameof(HasSelection));
            Raise(nameof(CanExport));
            Raise(nameof(FrameHint));
            RefreshCommands();
        }
    }

    public string? SelectedToken => SelectedBlock?.Segment.RecordingToken;

    public bool HasSelection => SelectedBlock is not null;

    public string SelectedSegmentText => SelectedBlock is null
        ? "Select a recording on the timeline."
        : $"Recording {SelectedBlock.Label} · {SelectedBlock.Segment.Duration.TotalMinutes:F0} min";

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    /// <summary>What the empty viewport says before a frame exists. It changes with selection, so it never asks for
    /// a search that has already happened.</summary>
    public string FrameHint => SelectedBlock is not null
        ? "Press play to decode the selected recording."
        : "Choose a camera and a date, then search the recorder.";

    public string RangeText
    {
        get => _rangeText;
        private set => Set(ref _rangeText, value);
    }

    /// <summary>Playhead as a fraction of the window; NaN when the position is outside it.</summary>
    public double PlayheadFraction
    {
        get => _playheadFraction;
        private set => Set(ref _playheadFraction, value);
    }

    public bool HasFrame => _frame is not null;

    public WriteableBitmap? Frame
    {
        get => _frame;
        private set
        {
            _frame = value;
            Raise(nameof(Frame));
            Raise(nameof(HasFrame));
        }
    }

    public double ZoomHours
    {
        get => _zoomHours;
        private set
        {
            if (Set(ref _zoomHours, value))
            {
                Raise(nameof(ZoomLabel));
                RebuildTimeline();
            }
        }
    }

    public string ZoomLabel => ZoomHours >= 24 ? "24 hours" : $"{ZoomHours:0.#} hours";

    public string WindowLabel
    {
        get
        {
            var end = _windowStart.AddHours(ZoomHours);
            // Midnight at the end of a same-day window is the next day's 00:00; showing "00:00 – 00:00" reads as an
            // empty window when it actually covers the whole day.
            var endLabel = end.TimeOfDay == TimeSpan.Zero && _windowStart.TimeOfDay == TimeSpan.Zero
                ? "24:00"
                : end.ToString("HH:mm", CultureInfo.CurrentCulture);

            return $"{_windowStart:HH:mm} – {endLabel}";
        }
    }

    /// <summary>The speed requested from the recorder. <see cref="ObservedRate"/> is what it is actually delivering.</summary>
    public double PlaybackRate => _playbackRate;

    public double ObservedRate => _observedRate;

    /// <summary>What the operator should read: requested speed plus, once measured, the active source rate.</summary>
    public string ActiveSpeedText
    {
        get
        {
            if (ObservedRate > 0)
            {
                return PlaybackRates.IsDelivered(PlaybackRate, ObservedRate)
                    ? $"{PlaybackRates.Label(ObservedRate)} active"
                    : $"{PlaybackRates.Label(PlaybackRate)} requested · {PlaybackRates.Label(ObservedRate)} actual";
            }

            return PlaybackRate > 1
                ? $"{PlaybackRates.Label(PlaybackRate)} requested"
                : PlaybackRates.Label(PlaybackRate);
        }
    }

    public string? SpeedNotice
    {
        get => _speedNotice;
        private set
        {
            if (Set(ref _speedNotice, value))
            {
                Raise(nameof(HasSpeedNotice));
            }
        }
    }

    public bool HasSpeedNotice => !string.IsNullOrEmpty(SpeedNotice);

    public long FramesShown => Interlocked.Read(ref _framesShown);

    /// <summary>Frames the decoder produced. Higher than <see cref="FramesShown"/> at high speed when presentation is
    /// coalesced; keeping both makes dropped presentation explicit rather than invisible.</summary>
    public long DecodedFrames => Interlocked.Read(ref _framesDecoded);

    public long FramesCoalesced => Interlocked.Read(ref _framesCoalesced);

    public DateTimeOffset? PositionUtc => _positionUtc;

    /// <summary>Latest engine status, for acceptance evidence; the page itself binds to the projection above.</summary>
    public ReplayStatus? CurrentReplayStatus => _session?.Status;

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (Set(ref _isExporting, value))
            {
                CancelExportCommand.RaiseCanExecuteChanged();
                ExportCommand.RaiseCanExecuteChanged();
                Raise(nameof(CanExport));
                Raise(nameof(ExportButtonText));
            }
        }
    }

    public double ExportFraction
    {
        get => _exportFraction;
        private set => Set(ref _exportFraction, value);
    }

    public string ExportStatus
    {
        get => _exportStatus;
        private set => Set(ref _exportStatus, value);
    }

    public string ExportStartText
    {
        get => _exportStartText;
        set
        {
            if (Set(ref _exportStartText, value))
            {
                ValidateExport();
            }
        }
    }

    public string ExportEndText
    {
        get => _exportEndText;
        set
        {
            if (Set(ref _exportEndText, value))
            {
                ValidateExport();
            }
        }
    }

    public string ExportDestination
    {
        get => _exportDestination;
        set
        {
            if (Set(ref _exportDestination, value))
            {
                Raise(nameof(CanExport));
                ExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ExportValidation
    {
        get => _exportValidation;
        private set
        {
            if (Set(ref _exportValidation, value))
            {
                Raise(nameof(HasExportValidation));
                Raise(nameof(CanExport));
                ExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasExportValidation => !string.IsNullOrEmpty(ExportValidation);

    public bool CanExport => SelectedBlock is not null && !IsExporting && !HasExportValidation &&
                             !string.IsNullOrWhiteSpace(ExportDestination);

    public string ExportButtonText => IsExporting ? "Exporting…" : "Export evidence";

    public string? LastExportPath
    {
        get => _lastExportPath;
        private set
        {
            if (Set(ref _lastExportPath, value))
            {
                VerifyLastExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? LastExportHash
    {
        get => _lastExportHash;
        private set => Set(ref _lastExportHash, value);
    }

    public string ExportSummary
    {
        get => _exportSummary;
        private set => Set(ref _exportSummary, value);
    }

    public void SetCameras(IEnumerable<PlaybackCamera> cameras)
    {
        Cameras.Clear();

        foreach (var camera in cameras)
        {
            Cameras.Add(camera);
        }

        if (SelectedCamera is not null && Cameras.All(candidate => candidate.Id != SelectedCamera.Id))
        {
            SelectedCamera = null;
        }

        if (SelectedCamera is null && Cameras.Count > 0)
        {
            SelectedCamera = Cameras[0];
        }
    }

    /// <summary>Selects a camera, searches its day and starts playback at an instant (event navigation).</summary>
    public async Task<bool> OpenAtAsync(Guid cameraId, DateTimeOffset atUtc)
    {
        var camera = Cameras.FirstOrDefault(candidate => candidate.Id == cameraId);

        if (camera is null)
        {
            ErrorText = "That camera is no longer available, so its recording cannot be opened.";
            return false;
        }

        SelectedCamera = camera;
        SelectedDate = Local(atUtc).Date;

        await SearchAsync().ConfigureAwait(true);

        if (Blocks.Count == 0)
        {
            StatusText = "No recording covers that moment.";
            return false;
        }

        var block = Blocks.FirstOrDefault(candidate => candidate.Segment.Contains(atUtc)) ?? Blocks[0];
        SelectedBlock = block;

        await PlayAsync(atUtc).ConfigureAwait(true);
        return IsPlaying;
    }

    public async Task SearchAsync()
    {
        if (SelectedCamera is not { } camera)
        {
            ErrorText = "Choose a camera first.";
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();

        IsSearching = true;
        ErrorText = null;

        try
        {
            var date = DateOnly.FromDateTime(SelectedDate);
            var availability = await _catalog.SearchAsync(
                camera.Id,
                camera.ServiceUri,
                camera.Username,
                camera.Password,
                date,
                _zone,
                camera.SourceToken,
                _searchCancellation.Token,
                camera.RtspPort).ConfigureAwait(true);

            _availability = availability;
            HasSearched = true;
            SelectedBlock = null;

            if (availability.UnsupportedReason is { } unsupported)
            {
                ErrorText = unsupported;
                StatusText = "This device cannot provide recordings through ONVIF.";
            }
            else if (availability.Error is { } error)
            {
                ErrorText = error;
                StatusText = "The recorder search failed.";
            }
            else
            {
                StatusText = availability.Segments.Count == 0
                    ? $"No recordings returned for {SelectedDateLabel}."
                    : $"Search complete · {availability.Segments.Count} recording(s)";

                RebuildTimeline();
                SelectedBlock = Blocks.FirstOrDefault();
            }

            Raise(nameof(AvailabilityLabel));
            Raise(nameof(EmptyTimelineText));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or IOException)
        {
            ErrorText = exception.Message;
            StatusText = "The recorder search failed.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Speed policy: pause, resume, seek and stop keep the selected speed. Switching camera resets it to 1×, and so
    /// does starting playback on a different recording after one has already played. Selecting a speed before the
    /// first play keeps that choice.
    /// </summary>
    private void ResetSpeedForNewRecording(string token)
    {
        if (_lastPlayedToken is not null && !string.Equals(_lastPlayedToken, token, StringComparison.Ordinal))
        {
            _speedChangeError = null;
            SetRequestedSpeed(1);
            SpeedNotice = null;
        }
    }

    private void SetRequestedSpeed(double rate)
    {
        _playbackRate = PlaybackRates.IsSupported(rate) ? rate : 1;
        _observedRate = 0;

        foreach (var option in Speeds)
        {
            option.IsSelected = Math.Abs(option.Rate - _playbackRate) < 0.001;
        }

        Raise(nameof(PlaybackRate));
        Raise(nameof(ObservedRate));
        Raise(nameof(ActiveSpeedText));
    }

    private static double RateFrom(object? parameter) => parameter switch
    {
        PlaybackSpeedOption option => option.Rate,
        double rate => rate,
        _ when double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 1,
    };

    /// <summary>Changes speed while stopped, playing or paused. Returns false when the recorder rejects the speed.</summary>
    public async Task<bool> SetSpeedAsync(double rate)
    {
        if (!PlaybackRates.IsSupported(rate))
        {
            SpeedNotice = $"Speed {PlaybackRates.Label(rate)} is not supported. Choose 1×, 2×, 4×, 6×, 8× or 16×.";
            return false;
        }

        _speedChangeError = null;

        if (_session is null || _replayTransport is null)
        {
            SetRequestedSpeed(rate);
            SpeedNotice = null;
            StatusText = rate > 1
                ? $"{PlaybackRates.Label(rate)} selected for the next playback"
                : "Normal playback selected";
            return true;
        }

        var result = await _session.ChangeRateAsync(_replayTransport, rate, CancellationToken.None).ConfigureAwait(true);

        if (!result.Accepted)
        {
            SetRequestedSpeed(result.EffectiveRate);
            _speedChangeError = result.Detail;
            ApplyReplayStatus(_session.Status);

            // The rejected attempt briefly reports Failed before the session reopens at the previous speed. The
            // operator is still watching playback, so restore the playing state from the recovered session.
            IsPlaying = _session.Status.State == ReplayState.Playing;
            IsPaused = _session.Status.State == ReplayState.Paused;
            ErrorText = null;
            SpeedNotice = result.Detail;
            StatusText = $"Playing at {PlaybackRates.Label(result.EffectiveRate)}";
            return false;
        }

        SetRequestedSpeed(rate);
        SpeedNotice = null;
        IsPaused = _session.Status.State == ReplayState.Paused;
        StatusText = IsPaused
            ? $"Paused at {PlaybackRates.Label(rate)}"
            : $"Playing at {PlaybackRates.Label(rate)}";
        ApplyReplayStatus(_session.Status);
        return true;
    }

    public async Task PlayAsync(DateTimeOffset? fromUtc = null)
    {
        if (SelectedCamera is not { } camera)
        {
            ErrorText = "Choose a camera first.";
            return;
        }

        var segment = SelectedBlock?.Segment;

        if (segment is null)
        {
            // Without a selected recording, the earliest one in the current window is the honest default.
            segment = _availability?.Segments.FirstOrDefault();
        }

        if (segment is null)
        {
            ErrorText = "There is no recording to play.";
            return;
        }

        await StopAsync().ConfigureAwait(true);
        ResetSpeedForNewRecording(segment.RecordingToken);
        ErrorText = null;
        SpeedNotice = null;
        _playingSegment = segment;
        _lastPlayedToken = segment.RecordingToken;
        RangeText = $"{Local(segment.StartUtc):HH:mm:ss} – {Local(segment.EndUtc):HH:mm:ss}";

        var lookup = await _catalog.GetReplayAsync(
            camera.ServiceUri,
            camera.Username,
            camera.Password,
            segment.RecordingToken,
            CancellationToken.None,
            camera.RtspPort).ConfigureAwait(true);

        if (lookup.ReplayUri is null)
        {
            ErrorText = lookup.Error;
            return;
        }

        var session = new ReplaySession(_decoderFactory) { CameraId = camera.Id };
        session.FrameReady += OnFrame;
        session.StatusChanged += OnReplayStatus;

        _session = session;
        _replayTransport = new RtspSessionOptions(
            lookup.ReplayUri,
            camera.Username,
            camera.Password,
            Timeout: TimeSpan.FromSeconds(20));

        try
        {
            var startAt = fromUtc is { } requested ? segment.Clamp(requested) : segment.StartUtc;

            await session.ConnectAsync(
                _replayTransport,
                segment.StartUtc,
                segment.EndUtc,
                startAt,
                PlaybackRate,
                CancellationToken.None).ConfigureAwait(true);

            IsPlaying = true;
            IsPaused = false;
            StatusText = _playbackRate > 1 ? $"Playing at {PlaybackRates.Label(_playbackRate)}" : "Playing recording";
            ApplyReplayStatus(session.Status);
            UpdatePosition(session.Status.PositionUtc);
        }
        catch (RtspException exception)
        {
            ErrorText = exception.Message;
            StatusText = "Playback could not start.";
            await StopAsync().ConfigureAwait(true);
        }
    }

    public async Task PauseAsync()
    {
        if (_session is null)
        {
            return;
        }

        await _session.PauseAsync().ConfigureAwait(true);
        IsPaused = true;
        _observedRate = 0;
        Raise(nameof(ObservedRate));
        Raise(nameof(ActiveSpeedText));
        StatusText = _playbackRate > 1 ? $"Paused at {PlaybackRates.Label(_playbackRate)}" : "Paused";
        UpdateSpeedNotice();
    }

    public async Task ResumeAsync()
    {
        if (_session is null)
        {
            return;
        }

        await _session.ResumeAsync().ConfigureAwait(true);
        IsPaused = false;
        StatusText = _playbackRate > 1 ? $"Playing at {PlaybackRates.Label(_playbackRate)}" : "Playing recording";
    }

    public async Task StopAsync()
    {
        var session = _session;
        _session = null;

        if (session is not null)
        {
            session.FrameReady -= OnFrame;
            session.StatusChanged -= OnReplayStatus;
            await session.StopAsync().ConfigureAwait(true);
            await session.DisposeAsync().ConfigureAwait(true);
        }

        IsPlaying = false;
        IsPaused = false;

        if (_playingSegment is not null)
        {
            StatusText = "Stopped";
        }
    }

    public async Task SeekToAsync(DateTimeOffset targetUtc)
    {
        if (_session is null || _replayTransport is not { } rtsp || _playingSegment is not { } segment)
        {
            return;
        }

        var clamped = segment.Clamp(targetUtc);
        await _session.SeekAsync(rtsp, clamped, CancellationToken.None).ConfigureAwait(true);
        UpdatePosition(clamped);
        StatusText = $"Seeked to {Local(clamped):HH:mm:ss}" + (_playbackRate > 1 ? $" at {PlaybackRates.Label(_playbackRate)}" : string.Empty);
        ApplyReplayStatus(_session.Status);
    }

    private async Task SeekRelativeAsync(TimeSpan? delta, bool toEnd = false)
    {
        var segment = _playingSegment;

        if (segment is null)
        {
            return;
        }

        var target = toEnd ? segment.EndUtc.AddTicks(-1) : _session?.Status.PositionUtc ?? segment.StartUtc;
        if (delta is { } value)
        {
            target += value;
        }

        await SeekToAsync(target).ConfigureAwait(true);
    }

    private void Zoom(double factor)
    {
        var hours = Math.Clamp(ZoomHours * factor, 1, 24);

        if (hours < 24)
        {
            _windowStart = _windowStart.AddHours(Math.Max(0, (ZoomHours - hours) / 2));
        }
        else
        {
            _windowStart = SelectedDate.Date;
        }

        ZoomHours = hours;
        Raise(nameof(WindowLabel));
    }

    private void Pan(double hours)
    {
        var dayStart = SelectedDate.Date;
        var dayEnd = dayStart.AddDays(1);
        var next = _windowStart.AddHours(hours);

        if (next < dayStart)
        {
            next = dayStart;
        }

        if (next.AddHours(ZoomHours) > dayEnd)
        {
            next = dayEnd.AddHours(-ZoomHours);
        }

        _windowStart = next;
        Raise(nameof(WindowLabel));
        RebuildTimeline();
    }

    /// <summary>Rebuilds bars and ticks for the current zoom window. Called on search/date/zoom/pan only.</summary>
    private void RebuildTimeline()
    {
        Blocks.Clear();
        Ticks.Clear();

        var windowStartUtc = ToUtc(_windowStart);
        var windowEndUtc = ToUtc(_windowStart.AddHours(ZoomHours));

        foreach (var tick in TimelineLayout.BuildTicks(_windowStart, ZoomHours))
        {
            Ticks.Add(tick);
        }

        if (_availability is null)
        {
            Raise(nameof(WindowLabel));
            Raise(nameof(EmptyTimelineText));
            Raise(nameof(WindowStartUtc));
            Raise(nameof(WindowEndUtc));
            return;
        }

        foreach (var bar in TimelineLayout.BuildBars(
                     windowStartUtc,
                     windowEndUtc,
                     _availability.Segments,
                     _ => string.Empty,
                     segment => $"{Local(segment.StartUtc):HH:mm:ss} – {Local(segment.EndUtc):HH:mm:ss} " +
                                $"({segment.Duration.TotalMinutes:F0} min, {_zone.Id})"))
        {
            // The visible label follows the clipped span, so a segment crossing midnight still reads correctly.
            var left = Local(windowStartUtc + TimeSpan.FromSeconds((windowEndUtc - windowStartUtc).TotalSeconds * bar.Start));
            var right = Local(windowStartUtc + TimeSpan.FromSeconds((windowEndUtc - windowStartUtc).TotalSeconds * bar.End));

            Blocks.Add(bar with { Label = $"{left:HH:mm}–{right:HH:mm}" });
        }

        if (SelectedBlock is not null)
        {
            var refreshed = Blocks.FirstOrDefault(block =>
                string.Equals(block.Segment.RecordingToken, SelectedBlock.Segment.RecordingToken, StringComparison.Ordinal));

            if (refreshed is not null && !ReferenceEquals(refreshed, SelectedBlock))
            {
                SelectedBlock = refreshed;
            }
        }

        Raise(nameof(WindowLabel));
        Raise(nameof(EmptyTimelineText));
        Raise(nameof(WindowStartUtc));
        Raise(nameof(WindowEndUtc));
        UpdatePosition(_session?.Status.PositionUtc ?? (_playingSegment?.StartUtc ?? windowStartUtc));
    }

    public DateTimeOffset WindowStartUtc => ToUtc(_windowStart);

    public DateTimeOffset WindowEndUtc => ToUtc(_windowStart.AddHours(ZoomHours));

    /// <summary>
    /// A click on the timeline. A recording is selected and, while playing, the replay seeks to that instant. Clicking a
    /// gap selects nothing and seeks nowhere: the recorder did not record there.
    /// </summary>
    public void SelectAt(double fraction)
    {
        if (Blocks.Count == 0)
        {
            return;
        }

        var bars = Blocks.ToList();
        var hit = TimelineLayout.HitTest(bars, Math.Clamp(fraction, 0, 1));

        if (hit is null)
        {
            StatusText = "No recording at that time.";
            return;
        }

        SelectedBlock = hit;

        if (IsPlaying)
        {
            var target = TimelineLayout.TimeAt(fraction, WindowStartUtc, WindowEndUtc);
            _ = SeekToAsync(target);
        }
    }

    private void SeedExportRange(RecordingSegment segment)
    {
        // The export fields are times of day on the selected date, so a recording that crosses midnight has to be
        // clamped into that day. Seeding it with 23:40 → 00:25 used to show "the end time must be after the start"
        // before the operator touched anything.
        var dayStart = SelectedDate.Date;
        var dayEnd = dayStart.AddDays(1);
        var start = Local(segment.StartUtc);
        var end = Local(segment.EndUtc);

        if (start < dayStart)
        {
            start = dayStart;
        }

        if (end > dayEnd)
        {
            end = dayEnd.AddSeconds(-1);
        }

        if (end <= start)
        {
            start = dayStart;
            end = dayEnd.AddSeconds(-1);
        }

        ExportStartText = start.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        ExportEndText = end.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        ExportDestination = Path.Combine(
            _exportRoot,
            $"{Sanitize(SelectedCamera?.Name ?? "camera")}_{Local(segment.StartUtc):yyyyMMdd_HHmmss}.mp4");
        ExportSummary = "Choose a range inside this recording, then export.";
        ValidateExport();
    }

    private void ValidateExport()
    {
        ExportValidation = null;

        if (SelectedBlock is not { } block)
        {
            return;
        }

        if (!TryParseLocal(ExportStartText, out var start) || !TryParseLocal(ExportEndText, out var end))
        {
            ExportValidation = "Enter a start and end time as HH:mm:ss.";
            return;
        }

        if (end <= start)
        {
            ExportValidation = "The end time must be after the start time.";
            return;
        }

        if (start < Local(block.Segment.StartUtc) || end > Local(block.Segment.EndUtc))
        {
            ExportValidation = "The range must stay inside the selected recording.";
            return;
        }

        if (end - start > TimeSpan.FromMinutes(240))
        {
            ExportValidation = "An export can cover at most 240 minutes.";
        }
    }

    private bool TryParseLocal(string text, out DateTime local)
    {
        local = default;

        if (!TimeSpan.TryParse(text, CultureInfo.CurrentCulture, out var time) &&
            !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time))
        {
            return false;
        }

        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            return false;
        }

        local = SelectedDate.Date + time;
        return true;
    }

    private void ChooseExportFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export evidence",
            Filter = "MP4 video (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileName(ExportDestination),
            InitialDirectory = _exportRoot,
        };

        if (dialog.ShowDialog() == true)
        {
            ExportDestination = dialog.FileName;
        }
    }

    private async Task ExportSelectedAsync()
    {
        if (SelectedBlock is not { } block || SelectedCamera is not { } camera)
        {
            return;
        }

        if (!TryParseLocal(ExportStartText, out var start) || !TryParseLocal(ExportEndText, out var end))
        {
            ExportValidation = "Enter a start and end time as HH:mm:ss.";
            return;
        }

        var destination = ExportDestination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            ExportValidation = "Choose where the evidence file should be written.";
            return;
        }

        if (!destination.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            destination += ".mp4";
            ExportDestination = destination;
        }

        var result = await ExportRangeAsync(ToUtc(start), ToUtc(end), destination).ConfigureAwait(true);

        if (!result.Success)
        {
            ExportSummary = result.Error ?? "The export failed.";
            return;
        }

        ExportSummary = result.Path is null
            ? $"Exported {result.Samples} samples."
            : $"Exported {result.Samples} samples · {result.Bytes / 1024.0:F0} KB · saved to {result.Path}";

        ExportCompleted?.Invoke(new EvidenceExportCompleted(
            camera.Id,
            camera.Name,
            result.Path ?? destination,
            result.Bytes,
            result.Samples,
            ToUtc(start),
            ToUtc(end),
            LastExportHash));
    }

    /// <summary>Exports a range of a recording as MP4 evidence; the job owns its own replay session.</summary>
    public async Task<ExportResult> ExportRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string outputPath,
        IProgress<ExportProgress>? progress = null)
    {
        if (SelectedCamera is not { } camera || SelectedBlock is not { } block)
        {
            return new ExportResult(false, null, "Select a recording first.", 0, 0, TimeSpan.Zero);
        }

        var lookup = await _catalog.GetReplayAsync(
            camera.ServiceUri,
            camera.Username,
            camera.Password,
            block.Segment.RecordingToken,
            CancellationToken.None,
            camera.RtspPort).ConfigureAwait(true);

        if (lookup.ReplayUri is null)
        {
            return new ExportResult(false, null, lookup.Error, 0, 0, TimeSpan.Zero);
        }

        _exportCancellation?.Dispose();
        _exportCancellation = new CancellationTokenSource();
        IsExporting = true;
        ExportFraction = 0;
        ExportStatus = "Exporting evidence…";

        try
        {
            var result = await _exporter.ExportAsync(
                new ExportRequest(
                    camera.Name,
                    new RtspSessionOptions(lookup.ReplayUri, camera.Username, camera.Password, Timeout: TimeSpan.FromSeconds(20)),
                    block.Segment,
                    startUtc,
                    endUtc,
                    outputPath),
                progress ?? new Progress<ExportProgress>(update =>
                {
                    ExportFraction = update.Fraction;
                    ExportStatus = update.Stage;
                }),
                _exportCancellation.Token).ConfigureAwait(true);

            if (result.Success && result.Path is { } path)
            {
                _lastExportPath = path;

                try
                {
                    var manifest = await EvidenceManifestWriter.WriteAsync(
                        path,
                        camera.Name,
                        startUtc,
                        endUtc,
                        result.Samples,
                        result.Duration).ConfigureAwait(true);

                    LastExportHash = manifest.Sha256;
                    ExportStatus = $"Exported {result.Samples} samples · SHA-256 {manifest.Sha256[..16]}…";
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    LastExportHash = null;
                    ExportStatus = $"Exported, but the hash sidecar could not be written: {exception.Message}";
                }
            }
            else
            {
                ExportStatus = result.Success
                    ? $"Exported {result.Samples} samples"
                    : $"Export failed: {result.Error}";
            }

            return result;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task VerifyLastExportAsync()
    {
        if (LastExportPath is not { } path)
        {
            return;
        }

        try
        {
            var valid = await EvidenceManifestWriter.VerifyAsync(path).ConfigureAwait(true);
            ExportSummary = valid
                ? "Hash verified: the file matches its sidecar."
                : "Hash mismatch or missing sidecar: this file cannot be treated as verified evidence.";
        }
        catch (IOException exception)
        {
            ExportSummary = $"Verification failed: {exception.Message}";
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "camera" : clean.Replace(' ', '_');
    }

    /// <summary>
    /// Frames arrive on the network/decoder task. At 16× that can be hundreds per second, so presentation keeps only
    /// the newest frame and one queued dispatcher callback. Every access unit is still decoded: reference frames are
    /// never dropped, only redundant presentations.
    /// </summary>
    private void OnFrame(VideoFrame frame)
    {
        if (Interlocked.Exchange(ref _pendingFrame, frame) is not null)
        {
            Interlocked.Increment(ref _framesCoalesced);
        }

        if (Interlocked.CompareExchange(ref _framePostQueued, 1, 0) != 0)
        {
            return;
        }

        if (_ui is null)
        {
            PresentPendingFrame();
            return;
        }

        _ui.Post(_ => PresentPendingFrame(), null);
    }

    private void PresentPendingFrame()
    {
        Interlocked.Exchange(ref _framePostQueued, 0);
        var frame = Interlocked.Exchange(ref _pendingFrame, null);

        if (frame is null)
        {
            return;
        }

        var bitmap = Frame;

        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            Frame = bitmap;
        }

        bitmap.WritePixels(
            new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels,
            frame.Stride,
            0);

        Interlocked.Increment(ref _framesShown);
    }

    private void OnReplayStatus(ReplayStatus status)
    {
        _ui?.Post(_ => ApplyReplayStatus(status), null);
    }

    private void ApplyReplayStatus(ReplayStatus status)
    {
        if (status.State == ReplayState.Ended)
        {
            IsPlaying = false;
            IsPaused = false;
            StatusText = status.Detail;
            _observedRate = 0;
        }
        else if (status.State == ReplayState.Failed)
        {
            IsPlaying = false;
            IsPaused = false;
            ErrorText = status.Detail;
            StatusText = "Playback failed.";
            _observedRate = 0;
        }
        else
        {
            _observedRate = status.ObservedRate;

            // A rejected speed change can emit Failed for the attempted connection and then Playing for the
            // recovered one; the final status must leave the page playing, not stuck on the transient failure.
            if (status.State == ReplayState.Playing && _session is not null)
            {
                IsPlaying = true;
                IsPaused = false;
                ErrorText = null;
            }
        }

        Interlocked.Exchange(ref _framesDecoded, status.FramesDecoded);
        Raise(nameof(DecodedFrames));
        Raise(nameof(ObservedRate));
        Raise(nameof(ActiveSpeedText));
        UpdateSpeedNotice();
        UpdatePosition(status.PositionUtc);
    }

    private void UpdateSpeedNotice()
    {
        if (_speedChangeError is not null)
        {
            SpeedNotice = _speedChangeError;
            return;
        }

        if (_session is null || _observedRate <= 0)
        {
            SpeedNotice = null;
            return;
        }

        // Ignore a measurement taken while the session is still on the previous Scale. Otherwise a notice can say
        // "not 6×" after the operator has already selected 16×.
        var activeRequest = _session.Status.Rate;

        if (Math.Abs(activeRequest - _playbackRate) > 0.001)
        {
            SpeedNotice = null;
            return;
        }

        if (PlaybackRates.IsDelivered(activeRequest, _observedRate))
        {
            SpeedNotice = null;
            return;
        }

        SpeedNotice = activeRequest <= 1
            ? $"The recorder is delivering {PlaybackRates.Label(_observedRate)} for normal playback."
            : $"The recorder is delivering {PlaybackRates.Label(_observedRate)}, not {PlaybackRates.Label(activeRequest)}. " +
              "This recording source has no accelerated replay for that speed; playback continues at the measured rate.";
    }

    private void UpdatePosition(DateTimeOffset utc)
    {
        var segment = _playingSegment ?? SelectedBlock?.Segment;

        if (segment is null)
        {
            PositionText = "--:--:--";
            PlayheadFraction = double.NaN;
            _positionUtc = null;
            return;
        }

        _positionUtc = utc;
        PositionText = Local(utc).ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        PlayheadFraction = TimelineLayout.FractionOf(utc, WindowStartUtc, WindowEndUtc);
    }

    private DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, _zone), TimeSpan.Zero);
    }

    private DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _zone).DateTime;

    private void RefreshCommands()
    {
        SearchCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        SeekBackCommand.RaiseCanExecuteChanged();
        SeekForwardCommand.RaiseCanExecuteChanged();
        JumpStartCommand.RaiseCanExecuteChanged();
        JumpEndCommand.RaiseCanExecuteChanged();
        ZoomInCommand.RaiseCanExecuteChanged();
        ZoomOutCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        await StopAsync().ConfigureAwait(false);
    }
}
