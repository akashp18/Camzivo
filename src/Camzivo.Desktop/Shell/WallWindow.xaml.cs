using System.Windows;
using System.Windows.Input;
using Camzivo.Desktop.Services;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Shell;

/// <summary>
/// A second wall on another display.
///
/// It shares the main window's <see cref="LiveViewModel"/> and therefore its <see cref="StreamEngine"/>: the same
/// camera id is one RTSP session regardless of how many windows show it, so opening a second display does not double
/// the decoder load. The trade-off is explicit: this window mirrors the main wall, it does not hold an independent
/// layout (see docs/FEATURE_PARITY_SMARTPSS.md).
/// </summary>
public partial class WallWindow : Window
{
    private static readonly List<WallWindow> OpenWindows = [];

    private readonly DisplayInfo _display;
    private bool _maximized = true;

    public WallWindow(LiveViewModel live, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(display);

        InitializeComponent();

        _display = display;
        DataContext = live;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = display.Bounds.Left;
        Top = display.Bounds.Top;
        Width = Math.Max(320, display.Bounds.Width);
        Height = Math.Max(240, display.Bounds.Height);

        PreviewKeyDown += OnPreviewKeyDown;
        MouseDoubleClick += (_, _) => ToggleFullScreen();
        Closed += (_, _) =>
        {
            lock (OpenWindows)
            {
                OpenWindows.Remove(this);
            }
        };
    }

    public static bool IsAnyOpen
    {
        get
        {
            lock (OpenWindows)
            {
                return OpenWindows.Count > 0;
            }
        }
    }

    public string DisplayName => _display.Name;

    /// <summary>Shows the window on its display. Maximising places it on the monitor it was positioned on.</summary>
    public void OpenOnDisplay()
    {
        Show();

        // Give the window a layout pass so maximising uses the monitor it was moved to, not the primary one.
        UpdateLayout();
        WindowState = WindowState.Maximized;
        _maximized = true;

        lock (OpenWindows)
        {
            OpenWindows.Add(this);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        _maximized = !_maximized;
        WindowState = _maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
