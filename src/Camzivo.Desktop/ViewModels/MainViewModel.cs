using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Layout;
using Camzivo.Core.Streaming;
using Camzivo.Core.Streaming.Video;
using Camzivo.Data.Devices;
using Camzivo.Data.Net;
using Camzivo.Data.Recording;
using Camzivo.Data.Repositories;
using Camzivo.Data.Storage;
using Camzivo.Desktop.Services;

namespace Camzivo.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly int[] OfferedLayouts = [1, 4, 9, 16, 25, 36, 49, 64];

    private readonly AppRuntime _runtime;
    private readonly NavItemViewModel[] _sections;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly PlaybackViewModel _playback;
    private readonly EventsViewModel _events;
    private readonly ExportsViewModel _exports;
    private readonly StreamEngine _engine;
    private readonly Dictionary<Guid, StreamState> _lastStreamState = [];
    private readonly Dictionary<string, DateTimeOffset> _eventThrottle = new(StringComparer.Ordinal);
    private readonly NavItemViewModel _healthSection;
    private bool _layoutRestored;
    private bool _openLastSection;
    private bool _isSidebarCollapsed;
    private int _eventRetentionDays = EventRepository.DefaultRetentionDays;
    private readonly Dictionary<Guid, Device> _devicesById = [];
    private readonly Dictionary<Guid, (Device Device, NvrChannel Channel)> _channelsById = [];
    private readonly PtzController _ptz;
    private PtzPreset? _selectedPtzPreset;
    private bool _ptzAvailable;
    private string _ptzDetail = "No PTZ camera selected";
    private string? _toast;
    private readonly Process _self = Process.GetCurrentProcess();
    private NavItemViewModel _selected;
    private object _currentPage;
    private bool _isDark;
    private bool _isFullScreen;
    private string _statusRight = string.Empty;
    private string _statusLeft;
    private string? _bannerText;
    private string _headerContext = string.Empty;

    public MainViewModel(AppRuntime runtime, bool demo)
    {
        _runtime = runtime;
        IsDemo = demo;
        _isDark = App.IsDarkTheme;
        _statusLeft = demo ? "Sample data · no video engine" : "Video engine not started";

        Live = new LiveViewModel(demo);
        Devices = new DevicesViewModel(demo);

        _engine = new StreamEngine(Application.Current.Dispatcher);
        Live.BindStreams(_engine, ResolveStream);
        Live.SnapshotRequested += OnSnapshotRequested;

        _ptz = new PtzController(new HttpOnvifTransport());

        _engine.StatusChanged += OnEngineStatus;

        Devices.AddRequested += (_, _) => AddCameraRequested?.Invoke(this, EventArgs.Empty);
        Devices.RemoveRequested += OnRemoveRequested;
        Devices.ImportChannelsRequested += OnImportChannelsRequested;
        Devices.RemoveChannelRequested += OnRemoveChannelRequested;
        Devices.ReconnectChannelRequested += OnReconnectChannelRequested;
        Health = new HealthViewModel(demo);
        Settings = new SettingsViewModel(runtime.Paths, VersionLabel, demo);

        _playback = new PlaybackViewModel(
            new OnvifRecordingCatalog(new HttpOnvifTransport()),
            () => new MediaFoundationH264Decoder(),
            exportRoot: runtime.Paths.ExportRoot);
        _events = new EventsViewModel(runtime.Events, TimeZoneInfo.Local, _eventRetentionDays);
        _exports = new ExportsViewModel(runtime.ExportLog, runtime.Paths.ExportRoot);

        _events.OpenPlaybackRequested += (cameraId, at) => _ = OpenPlaybackAtAsync(cameraId, at);
        _exports.RevealRequested += Reveal;
        _playback.ExportCompleted += OnEvidenceExported;
        Live.SaveLayoutRequested += OnSaveLayoutRequested;
        Live.RenameLayoutRequested += OnRenameLayoutRequested;
        Live.DuplicateLayoutRequested += OnDuplicateLayoutRequested;
        Live.DeleteLayoutRequested += OnDeleteLayoutRequested;
        Live.ApplyLayoutRequested += OnApplyLayoutRequested;
        Live.OpenWallOnDisplayRequested += () => OpenWallOnDisplayRequested?.Invoke(this, EventArgs.Empty);
        Settings.PreferencesChanged += OnSettingsPreferencesChanged;
        _engine.StatusChanged += OnStreamStatusForEvents;

        PtzPresets = [];

        PtzNudgeCommand = new RelayCommand(
            parameter =>
            {
                var (pan, tilt, zoom) = parameter?.ToString() switch
                {
                    "left" => (-0.6, 0d, 0d),
                    "right" => (0.6, 0d, 0d),
                    "up" => (0d, 0.6, 0d),
                    "down" => (0d, -0.6, 0d),
                    "zoomIn" => (0d, 0d, 0.5),
                    "zoomOut" => (0d, 0d, -0.5),
                    _ => (0d, 0d, 0d),
                };

                _ = NudgePtzAsync(pan, tilt, zoom);
            },
            _ => PtzAvailable);

        PtzStopCommand = new RelayCommand(_ => _ = StopPtzAsync(), _ => PtzAvailable);

        PtzGotoPresetCommand = new RelayCommand(
            _ => _ = GotoPtzPresetAsync(),
            _ => PtzAvailable && SelectedPtzPreset is not null);

        _sections =
        [
            new NavItemViewModel(NavSection.LiveView, "Live View", "Icon.Live", "Camera wall and layouts"),
            new NavItemViewModel(NavSection.Playback, "Playback", "Icon.Playback", "Recording investigation"),
            new NavItemViewModel(NavSection.Devices, "Devices", "Icon.Devices", "Cameras, recorders and onboarding"),
            new NavItemViewModel(NavSection.Events, "Events", "Icon.Events", "Stream and device history"),
            new NavItemViewModel(NavSection.Exports, "Exports", "Icon.Exports", "Evidence files"),
            new NavItemViewModel(NavSection.Settings, "Settings", "Icon.Settings", "Preferences and storage"),
        ];

        _healthSection = new NavItemViewModel(NavSection.Health, "Camera health", "Icon.Health", "Diagnostics");

        _selected = _sections[0];
        _currentPage = Live;

        SelectSectionCommand = new RelayCommand(parameter =>
        {
            if (parameter is NavItemViewModel item)
            {
                Select(item);
            }
        });

        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);

        ShowHealthCommand = new RelayCommand(_ => Select(_healthSection));

        ToggleThemeCommand = new RelayCommand(_ => SetTheme(!IsDark));

        SetThemeCommand = new RelayCommand(
            parameter =>
            {
                if (bool.TryParse(parameter?.ToString(), out var dark))
                {
                    SetTheme(dark);
                }
            });

        ApplyLayoutPresetCommand = new RelayCommand(parameter =>
        {
            if (int.TryParse(parameter?.ToString(), out var count) && OfferedLayouts.Contains(count))
            {
                Live.ApplyLayout(count);
            }
        });

        // Fullscreen is a window concern. This command asks the window to change state; it must not change the
        // wall layout, which is what the previous build did (BUG-01).
        ToggleFullScreenCommand = new RelayCommand(_ => FullScreenRequested?.Invoke(this, !IsFullScreen));

        ReconnectAllCommand = new RelayCommand(
            _ =>
            {
                foreach (var camera in _engine.ActiveCameras.ToList())
                {
                    _engine.Restart(camera);
                }
            },
            _ => _engine.ActiveCount > 0);

        Devices.Load(Array.Empty<Device>());
        Health.Load();

        Live.PropertyChanged += OnLiveChanged;

        if (!runtime.HasPersistence)
        {
            BannerText = $"The settings database could not be opened ({runtime.PersistenceError}). Camzivo is running without persistence.";
        }

        RestorePreferences();
        Select(_selected);

        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _telemetryTimer.Tick += (_, _) => UpdateTelemetry();
        _telemetryTimer.Start();
        UpdateTelemetry();
    }

    /// <summary>Raised when the operator asks for fullscreen; the window owns the actual state change.</summary>
    public event EventHandler<bool>? FullScreenRequested;

    /// <summary>Raised when the operator wants the Add Camera dialog; the window owns the dialog.</summary>
    public event EventHandler? AddCameraRequested;

    public AppRuntime Runtime => _runtime;

    public PlaybackViewModel Playback => _playback;

    public StreamEngine Engine => _engine;

    public LiveViewModel Live { get; }

    public DevicesViewModel Devices { get; }

    public HealthViewModel Health { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItemViewModel> Sections => _sections;

    public EventsViewModel Events => _events;

    public ExportsViewModel Exports => _exports;

    public bool IsDemo { get; }

    public ICommand SelectSectionCommand { get; }

    public ICommand ToggleSidebarCommand { get; }

    public ICommand ShowHealthCommand { get; }

    public ICommand SetThemeCommand { get; }

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (Set(ref _isSidebarCollapsed, value))
            {
                Raise(nameof(SidebarWidth));
                Raise(nameof(SidebarToggleHint));
            }
        }
    }

    public System.Windows.GridLength SidebarWidth => new(IsSidebarCollapsed ? 60 : 216);

    public string SidebarToggleHint => IsSidebarCollapsed ? "Expand navigation" : "Collapse navigation";

    /// <summary>Raised when the operator asks for the wall on another display; the window owns the displays.</summary>
    public event EventHandler? OpenWallOnDisplayRequested;

    public ICommand ToggleThemeCommand { get; }

    public ICommand ApplyLayoutPresetCommand { get; }

    public ICommand ToggleFullScreenCommand { get; }

    /// <summary>Recycles every attached session: the operator's "fix it now" control for a stalled wall.</summary>
    public ICommand ReconnectAllCommand { get; }

    /// <summary>Moves the selected camera's PTZ head for a moment; the controller always stops it again.</summary>
    public ICommand PtzNudgeCommand { get; }

    public ICommand PtzStopCommand { get; }

    public ICommand PtzGotoPresetCommand { get; }

    public ObservableCollection<PtzPreset> PtzPresets { get; }

    public PtzPreset? SelectedPtzPreset
    {
        get => _selectedPtzPreset;
        set
        {
            if (Set(ref _selectedPtzPreset, value))
            {
                (PtzGotoPresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True only when the selected camera answered its PTZ service; never a placeholder panel.</summary>
    public bool PtzAvailable
    {
        get => _ptzAvailable;
        private set
        {
            if (Set(ref _ptzAvailable, value))
            {
                (PtzNudgeCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PtzStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PtzGotoPresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string PtzDetail
    {
        get => _ptzDetail;
        private set => Set(ref _ptzDetail, value);
    }

    /// <summary>Operator-facing confirmation of a saved snapshot or a failed action.</summary>
    public string? Toast
    {
        get => _toast;
        private set
        {
            if (Set(ref _toast, value))
            {
                Raise(nameof(HasToast));
            }
        }
    }

    public bool HasToast => !string.IsNullOrWhiteSpace(Toast);

    /// <summary>Truthful engine badge: it says idle only when no session is attached.</summary>
    public bool HasLiveStreams => _engine.ActiveCount > 0;

    public string EngineBadgeText => HasLiveStreams
        ? $"{_engine.ActiveCount} stream{(_engine.ActiveCount == 1 ? string.Empty : "s")} live"
        : "Video engine idle";

    public object CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    public string HeaderTitle => _selected.Title;

    public string HeaderContext
    {
        get => _headerContext;
        private set => Set(ref _headerContext, value);
    }

    public bool IsDark
    {
        get => _isDark;
        private set
        {
            if (Set(ref _isDark, value))
            {
                Raise(nameof(ThemeLabel));
                Raise(nameof(ThemeToggleHint));
            }
        }
    }

    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            if (Set(ref _isFullScreen, value))
            {
                Raise(nameof(FullScreenHint));
            }
        }
    }

    public string FullScreenHint => IsFullScreen ? "Leave fullscreen (F11)" : "Fullscreen (F11)";

    public string ThemeLabel => IsDark ? "DARK" : "LIGHT";

    public string ThemeToggleHint => IsDark ? "Switch to light theme (Ctrl+T)" : "Switch to dark theme (Ctrl+T)";

    public string StatusLeft
    {
        get => _statusLeft;
        private set => Set(ref _statusLeft, value);
    }

    public string StatusRight
    {
        get => _statusRight;
        private set => Set(ref _statusRight, value);
    }

    public string? BannerText
    {
        get => _bannerText;
        private set
        {
            if (Set(ref _bannerText, value))
            {
                Raise(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerText);

    public string VersionLabel => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.2.0"}";

    public async Task InitializeAsync()
    {
        if (_runtime.Devices is null || IsDemo)
        {
            return;
        }

        try
        {
            var devices = await _runtime.Devices!.GetAllAsync().ConfigureAwait(true);
            var channels = _runtime.Cameras is null
                ? []
                : await _runtime.Cameras.GetAllAsync().ConfigureAwait(true);

            Devices.Load(devices, channels);

            _devicesById.Clear();
            foreach (var device in devices)
            {
                _devicesById[device.Id] = device;
            }

            _channelsById.Clear();
            foreach (var channel in channels)
            {
                if (_devicesById.TryGetValue(channel.NvrId, out var parent))
                {
                    _channelsById[channel.Id] = (parent, channel);
                }
            }

            // Recorder channels are first-class cameras on the wall; standalone cameras keep their own identity.
            var cameras = new List<CameraListEntry>();

            foreach (var device in devices.Where(device => device.Kind == DeviceKind.Camera))
            {
                cameras.Add(new CameraListEntry(device.Id, device.DisplayName, device.Vendor ?? "ungrouped", device.Uris));
            }

            foreach (var (parent, channel) in _channelsById.Values.OrderBy(pair => pair.Device.DisplayName).ThenBy(pair => pair.Channel.Channel))
            {
                cameras.Add(new CameraListEntry(
                    channel.Id,
                    $"{parent.DisplayName} · {channel.Name}",
                    parent.DisplayName,
                    channel.Uris));
            }

            Live.LoadCameras(cameras);

            // Playback works on the same identity as the wall: a recorder channel keeps its recorder's service address
            // and its own source token, so a search asks only for that channel's recordings.
            var playbackCameras = new List<PlaybackCamera>();

            foreach (var device in devices.Where(device => device.Kind == DeviceKind.Camera))
            {
                playbackCameras.Add(new PlaybackCamera(
                    device.Id,
                    device.DisplayName,
                    OnvifSoap.DeviceServiceUri(device.Host, device.OnvifPort, device.UseTls),
                    device.Username,
                    string.IsNullOrEmpty(device.SecretRef) ? null : _runtime.Secrets.Load(device.SecretRef),
                    device.RecordingSourceToken,
                    device.RtspPort));
            }

            foreach (var (parent, channel) in _channelsById.Values
                         .OrderBy(pair => pair.Device.DisplayName)
                         .ThenBy(pair => pair.Channel.Channel))
            {
                playbackCameras.Add(new PlaybackCamera(
                    channel.Id,
                    $"{parent.DisplayName} · {channel.Name}",
                    OnvifSoap.DeviceServiceUri(parent.Host, parent.OnvifPort, parent.UseTls),
                    parent.Username,
                    string.IsNullOrEmpty(parent.SecretRef) ? null : _runtime.Secrets.Load(parent.SecretRef),
                    channel.SourceToken,
                    parent.RtspPort));
            }

            _playback.SetCameras(playbackCameras);
            _events.SetCameras(cameras.Select(camera => (camera.Id, camera.Name)));

            await ReloadLayoutsAsync().ConfigureAwait(true);

            if (!_layoutRestored)
            {
                _layoutRestored = true;

                // Last-used named layout wins over the raw wall: it is the operator's explicit choice.
                var active = _runtime.Preferences?.GetSync(PreferencesRepository.ActiveLayoutKey);
                var layout = Live.Layouts.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, active, StringComparison.OrdinalIgnoreCase));

                if (layout is not null)
                {
                    Live.ApplySavedLayout(layout);
                }
                else
                {
                    RestoreWall();
                }
            }

            await PruneEventsAsync().ConfigureAwait(true);

            // PTZ is optional for a camera. A device that cannot answer its PTZ service must say so in the PTZ panel,
            // not replace the whole page with a camera-list error.
            try
            {
                await RefreshPtzAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                  or System.Net.Http.HttpRequestException
                                                  or TaskCanceledException
                                                  or UriFormatException)
            {
                PtzAvailable = false;
                PtzDetail = $"PTZ could not be queried: {exception.Message}";
                AppLog.Warn($"PTZ query failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Error("Camera list could not be read", exception);
            BannerText = $"Camera list could not be read: {exception.Message}";
        }
    }

    /// <summary>Surfaces a crash from the previous run once, without blocking the operator.</summary>
    public void ReportPreviousCrash(string detail)
    {
        BannerText = $"The previous session ended unexpectedly. Details were written to the log. ({detail})";
    }

    /// <summary>Shows a short operator-facing message in the page notification strip.</summary>
    public void Notify(string message) => Toast = message;

    /// <summary>Renames a device through the onboarding service and reloads the wall.</summary>
    public async Task<bool> RenameDeviceAsync(Guid id, string name)
    {
        if (_runtime.Onboarding is null)
        {
            Notify("Devices cannot be renamed because the local database is unavailable.");
            return false;
        }

        var renamed = await _runtime.Onboarding.RenameAsync(id, name, DateTimeOffset.UtcNow).ConfigureAwait(true);

        if (renamed is null)
        {
            Notify("The device could not be renamed. Use a name of 1 to 120 characters.");
            return false;
        }

        await InitializeAsync().ConfigureAwait(true);
        Notify($"Renamed to \"{renamed.DisplayName}\".");
        return true;
    }

    /// <summary>Used by <c>--section</c> for design review of a page without clicking through the app.</summary>
    public void ShowSection(NavSection section)
    {
        var item = _sections.FirstOrDefault(candidate => candidate.Section == section) ??
                   (section == NavSection.Health ? _healthSection : null);

        if (item is not null)
        {
            Select(item);
        }
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveViewModel.SelectedCameraId))
        {
            _ = RefreshPtzAsync();
            return;
        }

        if (e.PropertyName != nameof(LiveViewModel.LayoutSummary))
        {
            return;
        }

        if (_selected.Section == NavSection.LiveView)
        {
            HeaderContext = $"{Live.Rows} × {Live.Columns} tiles · decode budget {LiveViewModel.DecodeBudget}";
        }

        Persist(PreferencesRepository.LayoutKey, Live.Tiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Persist(PreferencesRepository.WallKey, WallLayoutCodec.Encode(Live.ExportWall()));
    }

    private void RestorePreferences()
    {
        if (IsDemo)
        {
            return;
        }

        var preferences = _runtime.Preferences;
        if (preferences is null)
        {
            return;
        }

        try
        {
            if (int.TryParse(preferences.GetSync(PreferencesRepository.LayoutKey), out var slots) && OfferedLayouts.Contains(slots))
            {
                Live.ApplyLayout(slots);
            }

            _openLastSection = string.Equals(
                preferences.GetSync(PreferencesRepository.OpenLastSectionKey),
                "true",
                StringComparison.OrdinalIgnoreCase);

            Settings.OpenLastSection = _openLastSection;

            if (int.TryParse(preferences.GetSync(PreferencesRepository.EventRetentionKey), out var retention))
            {
                _eventRetentionDays = Math.Clamp(retention, 1, 3650);
            }

            Settings.EventRetentionDays = _eventRetentionDays;
            _events.RetentionDays = _eventRetentionDays;

            if (_openLastSection &&
                Enum.TryParse<NavSection>(preferences.GetSync(PreferencesRepository.SectionKey), out var section) &&
                _sections.FirstOrDefault(item => item.Section == section) is { } restored)
            {
                _selected = restored;
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // Preferences are a convenience: a bad row must not stop the shell from opening.
        }
    }

    private void RestoreWall()
    {
        var preferences = _runtime.Preferences;
        if (preferences is null)
        {
            return;
        }

        try
        {
            if (WallLayoutCodec.TryDecode(preferences.GetSync(PreferencesRepository.WallKey), out var slots))
            {
                Live.RestoreWall(slots);
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // A wall that cannot be read is auto-filled by the view model; never fatal.
        }
    }

    /// <summary>Turns a wall camera into a playable address, resolving the password from DPAPI storage.</summary>
    private StreamTarget? ResolveStream(CameraListEntry camera, StreamQuality quality)
    {
        var uris = camera.Uris;
        Device? owner = null;

        if (_channelsById.TryGetValue(camera.Id, out var channel))
        {
            owner = channel.Device;
            uris = channel.Channel.Uris;
        }
        else if (_devicesById.TryGetValue(camera.Id, out var device))
        {
            owner = device;
        }

        if (owner is null)
        {
            return null;
        }

        var text = quality == StreamQuality.Sub
            ? uris.Sub ?? uris.Main
            : uris.Main ?? uris.Sub;

        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // A recorder may advertise a channel on a camera IP or a different RTSP port. That address is not opened
        // until the operator approves it explicitly; Camzivo never discovers or contacts camera endpoints directly.
        if (owner.Kind == DeviceKind.Nvr &&
            !NvrAccessPolicy.ForEndpoint(owner.Endpoint).AllowsStreamUri(uri))
        {
            Toast = $"Blocked stream outside the approved NVR RTSP endpoint ({NvrSafety.Redact(uri)}). " +
                    "No camera address was contacted; approve the endpoint explicitly.";
            return null;
        }

        var password = string.IsNullOrEmpty(owner.SecretRef) ? null : _runtime.Secrets.Load(owner.SecretRef);
        return new StreamTarget(uri, owner.Username, password);
    }

    private async void OnRemoveRequested(object? sender, Guid id)
    {
        if (_runtime.Onboarding is null || _runtime.Devices is null)
        {
            return;
        }

        try
        {
            var device = await _runtime.Devices.GetAsync(id).ConfigureAwait(true);

            if (device is { Kind: DeviceKind.Nvr } && _runtime.Nvr is not null)
            {
                await RemoveRecorderAsync(device).ConfigureAwait(true);
                return;
            }

            _engine.Detach(id);
            await _runtime.Onboarding.RemoveAsync(id);
            await InitializeAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            BannerText = $"The camera could not be removed: {exception.Message}";
        }
    }

    /// <summary>
    /// Removing a recorder asks explicitly what happens to its channels: take them with it, or keep each one as a
    /// standalone camera. Cancel is a real option, not a default that deletes tiles.
    /// </summary>
    private async Task RemoveRecorderAsync(Device device)
    {
        var answer = MessageBox.Show(
            $"Remove \"{device.DisplayName}\"?\n\n" +
            "Yes: remove the recorder and its imported channels.\n" +
            "No: keep the channels as standalone cameras.\n" +
            "Cancel: leave everything as it is.",
            "Remove recorder",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel)
        {
            return;
        }

        foreach (var channel in _channelsById.Where(pair => pair.Value.Device.Id == device.Id).ToList())
        {
            _engine.Detach(channel.Key);
        }

        var mode = answer == MessageBoxResult.Yes
            ? NvrRemovalMode.RemoveEverything
            : NvrRemovalMode.KeepChannelsAsCameras;

        var outcome = await _runtime.Nvr!.RemoveNvrAsync(device.Id, mode, DateTimeOffset.UtcNow).ConfigureAwait(true);

        Toast = outcome.Success
            ? mode == NvrRemovalMode.RemoveEverything
                ? $"Recorder removed with {outcome.ChannelsRemoved} channel(s)."
                : $"Recorder removed; {outcome.ChannelsPromoted} channel(s) kept as cameras."
            : outcome.Error;

        await InitializeAsync().ConfigureAwait(true);
    }

    /// <summary>Stops every decoder before the process exits.</summary>
    public async Task ShutdownAsync()
    {
        _ptz.Dispose();
        await _playback.DisposeAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSnapshotRequested(Guid cameraId)
    {
        var tile = Live.Tiles.FirstOrDefault(candidate => candidate.CameraId == cameraId);

        if (tile?.LastFrame is null)
        {
            Toast = "No decoded frame is available for that camera yet.";
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Camzivo Snapshots");

            var path = SnapshotWriter.Save(tile.LastFrame, tile.Name, DateTimeOffset.Now, directory);
            Toast = $"Snapshot saved: {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Toast = $"The snapshot could not be saved: {exception.Message}";
        }
    }

    /// <summary>A failed stream must not leave the head moving.</summary>
    private void OnEngineStatus(Guid cameraId, StreamStatus status)
    {
        if (status.State is StreamState.Failed or StreamState.Reconnecting)
        {
            _ptz.Forget(cameraId);
        }
    }

    private async Task RefreshPtzAsync()
    {
        PtzPresets.Clear();
        SelectedPtzPreset = null;
        PtzAvailable = false;

        if (Live.SelectedCameraId is not { } cameraId)
        {
            PtzDetail = "Select a camera to use PTZ";
            return;
        }

        Device device;
        NvrChannel? channel = null;

        if (_channelsById.TryGetValue(cameraId, out var pair))
        {
            device = pair.Device;
            channel = pair.Channel;
        }
        else if (_devicesById.TryGetValue(cameraId, out var standalone))
        {
            device = standalone;
        }
        else
        {
            PtzDetail = "This camera is not a saved device";
            return;
        }

        if (device.Kind == DeviceKind.Nvr && channel is null)
        {
            PtzDetail = "Select a channel to use PTZ";
            return;
        }

        var password = string.IsNullOrEmpty(device.SecretRef) ? null : _runtime.Secrets.Load(device.SecretRef);

        var availability = await _ptz.PrepareAsync(
            cameraId,
            device.Endpoint,
            OnvifServices.None,
            device.Username,
            password,
            channel?.ProfileToken,
            CancellationToken.None,
            allowControl: device.Kind != DeviceKind.Nvr).ConfigureAwait(true);

        PtzAvailable = availability.Available;
        PtzDetail = availability.Detail;

        foreach (var preset in availability.Presets)
        {
            PtzPresets.Add(preset);
        }

        SelectedPtzPreset = PtzPresets.FirstOrDefault();
    }

    private async Task NudgePtzAsync(double pan, double tilt, double zoom)
    {
        try
        {
            var moved = await _ptz.NudgeAsync(pan, tilt, zoom).ConfigureAwait(true);
            PtzDetail = moved ? "Moving · stops automatically" : _ptz.LastError ?? "The camera did not move";
        }
        catch (OperationCanceledException)
        {
            // A cancelled command leaves the auto-stop to finish the job.
        }
    }

    private async Task StopPtzAsync()
    {
        await _ptz.StopAsync().ConfigureAwait(true);
        PtzDetail = "Stopped";
    }

    private async Task GotoPtzPresetAsync()
    {
        if (SelectedPtzPreset is not { } preset)
        {
            return;
        }

        var moved = await _ptz.GotoPresetAsync(preset.Token).ConfigureAwait(true);
        PtzDetail = moved ? $"Moved to {preset.Name}" : _ptz.LastError ?? "The preset could not be applied";
    }

    private async void OnImportChannelsRequested(object? sender, Guid deviceId)
    {
        if (_runtime.Nvr is null || _runtime.Discovery is null || _runtime.Devices is null)
        {
            Toast = "Recorder import is unavailable because the device list could not be opened.";
            return;
        }

        var device = await _runtime.Devices.GetAsync(deviceId).ConfigureAwait(true);
        if (device is null)
        {
            Toast = "The recorder is no longer in the device list.";
            return;
        }

        var password = string.IsNullOrEmpty(device.SecretRef) ? null : _runtime.Secrets.Load(device.SecretRef);
        Toast = $"Reading channels from {device.DisplayName}…";

        var discovery = await _runtime.Discovery
            .DiscoverAsync(device.Endpoint, device.Username, password)
            .ConfigureAwait(true);

        if (!discovery.Success)
        {
            Toast = discovery.Failure?.Explanation ?? "The recorder's channels could not be read.";
            return;
        }

        var outcome = await _runtime.Nvr
            .ImportChannelsAsync(deviceId, discovery.Channels, DateTimeOffset.UtcNow)
            .ConfigureAwait(true);

        Toast = outcome.Success
            ? $"Channels refreshed: {outcome.Imported} new, {outcome.Updated} updated, {outcome.Skipped.Count} unchanged"
            : outcome.Error;

        await InitializeAsync().ConfigureAwait(true);
    }

    private async void OnRemoveChannelRequested(object? sender, Guid channelId)
    {
        if (_runtime.Nvr is null || _runtime.Cameras is null)
        {
            return;
        }

        var channel = await _runtime.Cameras.GetAsync(channelId).ConfigureAwait(true);
        if (channel is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Remove channel \"{channel.Name}\" from the wall? The recorder and its other channels stay.",
            "Remove channel",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _engine.Detach(channelId);
        await _runtime.Nvr.RemoveChannelAsync(channelId).ConfigureAwait(true);
        Toast = $"Channel \"{channel.Name}\" removed.";
        await InitializeAsync().ConfigureAwait(true);
    }

    private void OnReconnectChannelRequested(object? sender, Guid channelId)
    {
        if (!_engine.Restart(channelId))
        {
            // Not attached right now (suspended by the decode budget or not on the wall): reloading re-plans it.
            _ = InitializeAsync();
        }

        Toast = "Reconnecting channel…";
    }

    /// <summary>Applies and stores the theme; the single source of truth for appearance.</summary>
    public void SetTheme(bool dark)
    {
        if (IsDark == dark)
        {
            return;
        }

        IsDark = dark;
        App.ApplyTheme(dark);
        Persist(PreferencesRepository.ThemeKey, dark ? "dark" : "light");
    }

    /// <summary>
    /// Turns engine state changes into event history. Only transitions are recorded, and identical transitions are
    /// throttled, so a flapping camera cannot fill the table with the same line.
    /// </summary>
    private void OnStreamStatusForEvents(Guid cameraId, StreamStatus status)
    {
        if (_runtime.Events is null || status.State == StreamState.Connecting || status.State == StreamState.Idle ||
            status.State == StreamState.Stopped)
        {
            return;
        }

        var previous = _lastStreamState.TryGetValue(cameraId, out var known) ? known : StreamState.Idle;
        _lastStreamState[cameraId] = status.State;

        if (previous == status.State)
        {
            return;
        }

        var name = CameraNameFor(cameraId);
        var detail = status.Detail ?? string.Empty;

        switch (status.State)
        {
            case StreamState.Live:
                LogEvent(cameraId, name, "stream", EventSeverity.Information,
                    previous == StreamState.Reconnecting || previous == StreamState.Failed ? "Stream restored" : "Stream online",
                    detail);
                break;

            case StreamState.Reconnecting:
                LogEvent(cameraId, name, "stream", EventSeverity.Warning, "Stream interrupted", detail);
                break;

            case StreamState.Failed:
                var authentication = detail.Contains("401", StringComparison.Ordinal) ||
                                     detail.Contains("auth", StringComparison.OrdinalIgnoreCase);

                LogEvent(
                    cameraId,
                    name,
                    authentication ? "authentication" : "stream",
                    EventSeverity.Critical,
                    authentication ? "Authentication failed" : "Stream failed",
                    detail);
                break;
        }
    }

    private void LogEvent(Guid? cameraId, string cameraName, string kind, EventSeverity severity, string message, string detail)
    {
        if (_runtime.Events is null)
        {
            return;
        }

        var key = $"{cameraId}:{kind}:{message}";
        var now = DateTimeOffset.UtcNow;

        if (_eventThrottle.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _eventThrottle[key] = now;

        _ = AppendEventAsync(new EventRecord(0, now, cameraId, cameraName, kind, severity, message, detail));
    }

    private async Task AppendEventAsync(EventRecord entry)
    {
        if (_runtime.Events is null)
        {
            return;
        }

        try
        {
            var id = await _runtime.Events.AppendAsync(entry).ConfigureAwait(true);
            _events.Prepend(entry with { Id = id });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            AppLog.Error("Event could not be stored", exception);
        }
    }

    private string CameraNameFor(Guid cameraId)
    {
        if (Live.Tiles.FirstOrDefault(tile => tile.CameraId == cameraId) is { } tile)
        {
            return tile.Name;
        }

        if (_channelsById.TryGetValue(cameraId, out var channel))
        {
            return $"{channel.Device.DisplayName} · {channel.Channel.Name}";
        }

        return _devicesById.TryGetValue(cameraId, out var device) ? device.DisplayName : "Camera";
    }

    private async Task PruneEventsAsync()
    {
        if (_runtime.Events is null)
        {
            return;
        }

        try
        {
            await _runtime.Events
                .PruneAsync(DateTimeOffset.UtcNow.AddDays(-_eventRetentionDays))
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Error("Event retention could not be applied", exception);
        }
    }

    private async Task ReloadLayoutsAsync(string? selectedName = null)
    {
        if (_runtime.WallLayouts is null)
        {
            Live.SetLayouts([]);
            return;
        }

        try
        {
            var records = await _runtime.WallLayouts.ListAsync().ConfigureAwait(true);
            var active = selectedName ?? _runtime.Preferences?.GetSync(PreferencesRepository.ActiveLayoutKey);

            Live.SetLayouts(
                records.Select(record => new SavedLayoutOption(record.Id, record.Name, record.GridSlots, record.Slots)),
                active);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Toast = $"Saved layouts could not be read: {exception.Message}";
        }
    }

    private async void OnSaveLayoutRequested(string name, IReadOnlyList<Guid?> slots, int gridSlots)
    {
        if (_runtime.WallLayouts is null)
        {
            Live.ShowLayoutError("Saved layouts need the local database, which could not be opened.");
            return;
        }

        try
        {
            var record = await _runtime.WallLayouts
                .SaveAsync(name, gridSlots, slots, DateTimeOffset.UtcNow)
                .ConfigureAwait(true);

            Persist(PreferencesRepository.ActiveLayoutKey, record.Name);
            await ReloadLayoutsAsync(record.Name).ConfigureAwait(true);
            Toast = $"Layout \"{record.Name}\" saved.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            Live.ShowLayoutError(exception.Message);
        }
    }

    private async void OnRenameLayoutRequested(string name, string newName)
    {
        if (_runtime.WallLayouts is null)
        {
            return;
        }

        try
        {
            if (!await _runtime.WallLayouts.RenameAsync(name, newName, DateTimeOffset.UtcNow).ConfigureAwait(true))
            {
                Live.ShowLayoutError("That layout no longer exists.");
                await ReloadLayoutsAsync().ConfigureAwait(true);
                return;
            }

            Persist(PreferencesRepository.ActiveLayoutKey, newName.Trim());
            await ReloadLayoutsAsync(newName.Trim()).ConfigureAwait(true);
            Toast = $"Layout renamed to \"{newName.Trim()}\".";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            Live.ShowLayoutError(exception.Message);
        }
    }

    private async void OnDuplicateLayoutRequested(string name, string copyName)
    {
        if (_runtime.WallLayouts is null)
        {
            return;
        }

        try
        {
            var copy = await _runtime.WallLayouts
                .DuplicateAsync(name, copyName, DateTimeOffset.UtcNow)
                .ConfigureAwait(true);

            await ReloadLayoutsAsync(copy.Name).ConfigureAwait(true);
            Toast = $"Layout duplicated as \"{copy.Name}\".";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            Live.ShowLayoutError(exception.Message);
        }
    }

    private async void OnDeleteLayoutRequested(string name)
    {
        if (_runtime.WallLayouts is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Delete the saved layout \"{name}\"?\n\nThe cameras on the wall are not removed.",
            "Delete layout",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _runtime.WallLayouts.DeleteAsync(name).ConfigureAwait(true);
            Persist(PreferencesRepository.ActiveLayoutKey, string.Empty);
            await ReloadLayoutsAsync().ConfigureAwait(true);
            Toast = $"Layout \"{name}\" deleted.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Toast = $"The layout could not be deleted: {exception.Message}";
        }
    }

    private void OnApplyLayoutRequested(SavedLayoutOption layout)
    {
        Live.ApplySavedLayout(layout);
        Persist(PreferencesRepository.ActiveLayoutKey, layout.Name);
        Toast = $"Layout \"{layout.Name}\" applied.";
    }

    private async void OnEvidenceExported(EvidenceExportCompleted export)
    {
        try
        {
            if (_runtime.ExportLog is not null)
            {
                await _runtime.ExportLog.RecordAsync(new ExportRecord(
                    Guid.NewGuid(),
                    export.CameraName,
                    export.Path,
                    export.Bytes,
                    export.Samples,
                    export.StartUtc,
                    export.EndUtc,
                    DateTimeOffset.UtcNow)).ConfigureAwait(true);
            }

            await _exports.RefreshAsync().ConfigureAwait(true);
            LogEvent(export.CameraId, export.CameraName, "export", EventSeverity.Information,
                "Evidence exported", Path.GetFileName(export.Path));

            Toast = $"Evidence exported: {Path.GetFileName(export.Path)}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            BannerText = $"The export completed, but its record could not be stored: {exception.Message}";
        }
    }

    private async Task OpenPlaybackAtAsync(Guid cameraId, DateTimeOffset atUtc)
    {
        Select(_sections.First(item => item.Section == NavSection.Playback));

        var opened = await _playback.OpenAtAsync(cameraId, atUtc).ConfigureAwait(true);

        Toast = opened
            ? $"Playing the recorded moment around {TimeZoneInfo.ConvertTime(atUtc, TimeZoneInfo.Local):HH:mm:ss}."
            : _playback.ErrorText ?? _playback.StatusText;
    }

    private void Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Toast = "That folder or file does not exist yet.";
            }
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            Toast = $"Explorer could not be opened: {exception.Message}";
        }
    }

    private void OnSettingsPreferencesChanged()
    {
        _openLastSection = Settings.OpenLastSection;
        _eventRetentionDays = Math.Clamp(Settings.EventRetentionDays <= 0 ? EventRepository.DefaultRetentionDays : Settings.EventRetentionDays, 1, 3650);
        _events.RetentionDays = _eventRetentionDays;

        Persist(PreferencesRepository.OpenLastSectionKey, _openLastSection ? "true" : "false");
        Persist(PreferencesRepository.EventRetentionKey, _eventRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Persist(string key, string value) => _runtime.Persist(key, value);

    private void Select(NavItemViewModel item)
    {
        if (item.Section == NavSection.Health)
        {
            // Health is derived from the wall every time it is opened, so it can never show a stale outage.
            Health.LoadFromTiles(Live.Tiles);
        }

        _selected = item;

        foreach (var section in _sections)
        {
            section.IsSelected = ReferenceEquals(section, item);
        }

        CurrentPage = item.Section switch
        {
            NavSection.LiveView => Live,
            NavSection.Devices => Devices,
            NavSection.Health => Health,
            NavSection.Playback => _playback,
            NavSection.Events => _events,
            NavSection.Exports => _exports,
            _ => Settings,
        };

        HeaderContext = item.Section switch
        {
            NavSection.LiveView => $"{Live.Rows} × {Live.Columns} tiles · decode budget {LiveViewModel.DecodeBudget}",
            NavSection.Devices => Devices.Summary,
            NavSection.Health => Health.Summary,
            NavSection.Playback => $"{_playback.Cameras.Count} camera(s) · ONVIF Profile G search and replay",
            NavSection.Events => _events.Summary,
            NavSection.Exports => _exports.Summary,
            _ => "Local installation",
        };

        Persist(PreferencesRepository.SectionKey, item.Section.ToString());
        Raise(nameof(HeaderTitle));

        if (item.Section == NavSection.Events)
        {
            _ = _events.RefreshAsync();
        }
        else if (item.Section == NavSection.Exports)
        {
            _ = _exports.RefreshAsync();
        }
    }

    private void UpdateTelemetry()
    {
        // Cached Process instance: the previous version allocated one every second (BUG-12).
        _self.Refresh();

        // Memory and thread counts are development facts, not operator information: they appear only in sample /
        // design mode (the --demo snapshots), so a real control room sees the clock and stream truth instead of
        // "MEM 123 MB · 13 THR" (UX finding U5).
        StatusRight = IsDemo
            ? $"MEM {_self.WorkingSet64 / (1024 * 1024)} MB · {_self.Threads.Count} THR · {DateTime.Now:HH:mm:ss}"
            : DateTime.Now.ToString("HH:mm:ss");

        if (!IsDemo)
        {
            // The status bar must describe the engine that is actually running, not a startup hint that never
            // changes. "Video engine not started" was still displayed while four cameras were decoding.
            var sessions = _engine.ActiveCount;
            StatusLeft = sessions == 0
                ? "Video engine idle · no streams attached"
                : $"Live · {sessions} stream{(sessions == 1 ? string.Empty : "s")} attached";

            Raise(nameof(HasLiveStreams));
            Raise(nameof(EngineBadgeText));
            (ReconnectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
