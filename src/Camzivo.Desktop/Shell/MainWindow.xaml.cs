using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shell;
using Camzivo.Data.Repositories;
using Camzivo.Desktop.Services;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Shell;

public partial class MainWindow : Window
{
    private readonly List<WallWindow> _wallWindows = [];
    private WindowStyle _previousStyle = WindowStyle.SingleBorderWindow;
    private WindowState _previousState = WindowState.Normal;
    private ResizeMode _previousResize = ResizeMode.CanResize;
    private double _previousCaptionHeight = 40;
    private bool _isFullScreen;
    private bool _placementRestored;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
        {
            previous.FullScreenRequested -= OnFullScreenRequested;
            previous.AddCameraRequested -= OnAddCameraRequested;
            previous.OpenWallOnDisplayRequested -= OnOpenWallOnDisplayRequested;
            previous.Devices.RenameRequested -= OnRenameDeviceRequested;
            previous.Devices.DiagnosticsRequested -= OnDiagnosticsRequested;
            previous.Devices.TestNvrConnectionRequested -= OnTestNvrConnectionRequested;
        }

        if (e.NewValue is MainViewModel current)
        {
            current.FullScreenRequested += OnFullScreenRequested;
            current.AddCameraRequested += OnAddCameraRequested;
            current.OpenWallOnDisplayRequested += OnOpenWallOnDisplayRequested;
            current.Devices.RenameRequested += OnRenameDeviceRequested;
            current.Devices.DiagnosticsRequested += OnDiagnosticsRequested;
            current.Devices.TestNvrConnectionRequested += OnTestNvrConnectionRequested;
        }
    }

    /// <summary>Opens the manual connection test. The window starts idle and contacts nothing until it is confirmed.</summary>
    private void OnTestNvrConnectionRequested(object? sender, EventArgs e)
    {
        var window = new NvrConnectionWindow(new NvrConnectionViewModel()) { Owner = this };
        window.ShowDialog();
    }

    private void OnFullScreenRequested(object? sender, bool enter) => ApplyFullScreen(enter);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        RestoreMonitorWindow();
    }

    private void RestorePlacement()
    {
        if (_placementRestored || ViewModel?.Runtime.Preferences is not { } preferences)
        {
            return;
        }

        _placementRestored = true;

        if (!WindowPlacementCodec.TryDecode(preferences.GetSync(PreferencesRepository.WindowKey), out var placement))
        {
            return;
        }

        if (!IsOnVirtualDesktop(new Rect(placement.Left, placement.Top, placement.Width, placement.Height)))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsOnVirtualDesktop(Rect window)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        virtualScreen.Intersect(window);
        return virtualScreen.Width >= 120 && virtualScreen.Height >= 80;
    }

    private void SavePlacement()
    {
        if (ViewModel?.Runtime.Preferences is null)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width < 200 || bounds.Height < 200 || !double.IsFinite(bounds.Left))
        {
            return;
        }

        ViewModel.Runtime.Persist(
            PreferencesRepository.WindowKey,
            WindowPlacementCodec.Encode(new WindowPlacement(
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                WindowState == WindowState.Maximized)));
    }

    private void RestoreMonitorWindow()
    {
        if (ViewModel is not { } viewModel ||
            viewModel.Runtime.Preferences?.GetSync(PreferencesRepository.MonitorWindowKey) is not { Length: > 0 } saved)
        {
            return;
        }

        var display = DisplayService.All().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, saved, StringComparison.OrdinalIgnoreCase));

        // A disconnected display is not an error: the wall simply stays on this window.
        if (display is not null)
        {
            OpenWallWindow(viewModel, display);
        }
    }

    /// <summary>A minimised main window must not stop decoding while a wall window is still showing it.</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (ViewModel?.Live is { } live)
        {
            live.SetWallSuspended(WindowState == WindowState.Minimized && !WallWindow.IsAnyOpen);
        }
    }

    private void OnOpenWallOnDisplayRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var displays = DisplayService.All();
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };

        foreach (var display in displays)
        {
            var item = new MenuItem { Header = display.DisplayName };
            item.Click += (_, _) => OpenWallWindow(viewModel, display);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OpenWallWindow(MainViewModel viewModel, DisplayInfo display)
    {
        var window = new WallWindow(viewModel.Live, display);
        window.Closed += (_, _) =>
        {
            _wallWindows.Remove(window);
            viewModel.Live.SetWallSuspended(WindowState == WindowState.Minimized && !WallWindow.IsAnyOpen);
        };

        _wallWindows.Add(window);
        window.OpenOnDisplay();

        viewModel.Runtime.Persist(PreferencesRepository.MonitorWindowKey, display.Name);
    }

    private async void OnRenameDeviceRequested(object? sender, Guid id)
    {
        if (ViewModel is not { } viewModel || viewModel.Runtime.Devices is not { } devices)
        {
            return;
        }

        try
        {
            var device = await devices.GetAsync(id).ConfigureAwait(true);
            if (device is null)
            {
                viewModel.Notify("That device is no longer in the list.");
                return;
            }

            var prompt = new PromptWindow(
                "Rename device",
                "The name appears on the wall, in playback and in event history.",
                device.DisplayName)
            {
                Owner = this,
            };

            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
            {
                await viewModel.RenameDeviceAsync(id, prompt.Value).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            viewModel.Notify($"The device could not be renamed: {exception.Message}");
        }
    }

    private async void OnDiagnosticsRequested(object? sender, Guid id)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.Runtime.Devices is not { } devices || viewModel.Runtime.Diagnostics is not { } diagnostics)
        {
            viewModel.Notify("Device diagnostics are unavailable because the device list could not be opened.");
            return;
        }

        try
        {
            var device = await devices.GetAsync(id).ConfigureAwait(true);
            if (device is null)
            {
                viewModel.Notify("That device is no longer in the list.");
                return;
            }

            var password = string.IsNullOrEmpty(device.SecretRef)
                ? null
                : viewModel.Runtime.Secrets.Load(device.SecretRef);
            var report = await diagnostics.InspectAsync(device, password).ConfigureAwait(true);

            var dialog = new DeviceDiagnosticsWindow(new DeviceDiagnosticsViewModel(report)) { Owner = this };
            dialog.ShowDialog();
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.Net.Http.HttpRequestException
                                              or TaskCanceledException
                                              or Microsoft.Data.Sqlite.SqliteException)
        {
            viewModel.Notify($"Diagnostics failed: {exception.Message}");
        }
    }

    private async void OnAddCameraRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new AddCameraWindow(viewModel.Runtime) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control))
        {
            ApplyFullScreen(!_isFullScreen);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullScreen)
        {
            ApplyFullScreen(false);
            e.Handled = true;
            return;
        }

        // Wall shortcuts must not fire while the operator is typing.
        if (Keyboard.FocusedElement is TextBox || ViewModel?.Live is not { } live)
        {
            return;
        }

        if (e.Key == Key.Delete && live.ClearSelectedCommand.CanExecute(null))
        {
            live.ClearSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && live.UndoCommand.CanExecute(null))
        {
            live.UndoCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Fullscreen: borderless, maximised, title bar collapsed. Layout is untouched; the previous build switched to a
    /// one-tile grid and called that fullscreen (BUG-01).
    /// </summary>
    private void ApplyFullScreen(bool enter)
    {
        if (enter == _isFullScreen)
        {
            return;
        }

        if (enter)
        {
            _previousStyle = WindowStyle;
            _previousState = WindowState;
            _previousResize = ResizeMode;

            TitleBar.Visibility = Visibility.Collapsed;
            TitleBarRow.Height = new GridLength(0);

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome is not null)
            {
                _previousCaptionHeight = chrome.CaptionHeight;
                chrome.CaptionHeight = 0;
            }

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _previousStyle;
            ResizeMode = _previousResize;
            WindowState = _previousState;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome is not null)
            {
                chrome.CaptionHeight = _previousCaptionHeight;
            }

            TitleBar.Visibility = Visibility.Visible;
            TitleBarRow.Height = new GridLength(40);
        }

        _isFullScreen = enter;

        if (ViewModel is { } viewModel)
        {
            viewModel.IsFullScreen = enter;
        }
    }

    /// <summary>Exercises the real fullscreen transition for <c>--probe-fullscreen</c>.</summary>
    internal string RunFullScreenProbe()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        var lines = new List<string> { "before:   " + Describe(chrome) };

        ApplyFullScreen(true);
        UpdateLayout();
        lines.Add("fullscreen: " + Describe(chrome));

        ApplyFullScreen(false);
        UpdateLayout();
        lines.Add("restored: " + Describe(chrome));

        return string.Join(Environment.NewLine, lines);
    }

    private string Describe(WindowChrome? chrome) =>
        $"style={WindowStyle} state={WindowState} resize={ResizeMode} titleBar={TitleBar.Visibility} " +
        $"titleRow={TitleBarRow.Height.Value} caption={chrome?.CaptionHeight} vmIsFullScreen={ViewModel?.IsFullScreen}";

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (_isFullScreen)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the button was released before the drag started; harmless.
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => ViewModel?.ToggleThemeCommand.Execute(null);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();

        foreach (var window in _wallWindows.ToList())
        {
            window.Close();
        }

        _wallWindows.Clear();

        // Decoders and sockets must be released before the window goes away.
        ViewModel?.Engine.DetachAll();
        base.OnClosing(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
