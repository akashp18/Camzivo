using System.ComponentModel;
using System.Windows;
using Camzivo.Data.Storage;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Shell;

/// <summary>
/// Manual recorder connection testing.
///
/// Constructing or showing this window performs no network activity: the view model starts idle and only a confirmed
/// click on the panel's own button starts the metadata-only test. The password lives in the WPF controls and is passed
/// to the view model as an argument for a single attempt; both controls are cleared when the window closes.
/// </summary>
public partial class NvrConnectionWindow : Window
{
    private readonly NvrConnectionViewModel _viewModel;

    public NvrConnectionWindow(NvrConnectionViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        Closing += OnClosing;
    }

    /// <summary>Whatever the operator typed, from whichever control is currently visible.</summary>
    private string CurrentPassword => RevealCheck.IsChecked == true ? PlainPasswordBox.Text : PasswordBox.Password;

    private void Test_Click(object sender, RoutedEventArgs e) => _viewModel.RequestTest(CurrentPassword);

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // The confirmation is per attempt: a second click while running must not start another test.
        RunTestButton.IsEnabled = false;
        try
        {
            await _viewModel.ConfirmAsync(CurrentPassword);
        }
        finally
        {
            RunTestButton.IsEnabled = true;
            LogOutcome();
        }
    }

    /// <summary>
    /// One redacted line per attempt, so a failed test leaves a record without any credential or request body. The
    /// host is masked by NvrSafety.MaskHost.
    /// </summary>
    private void LogOutcome()
    {
        var result = _viewModel.Result;
        var requests = result is null
            ? "0"
            : string.Join(",", result.Requests.Select(request => $"{request.Operation}={request.State}"));

        AppLog.Info(
            $"NVR connection test: host={Camzivo.Core.Devices.NvrSafety.MaskHost(_viewModel.Host)} " +
            $"scheme={_viewModel.Scheme} port={_viewModel.PortText} state={_viewModel.State} " +
            $"attempts={result?.HttpAttempts ?? 0} requests={requests} error={_viewModel.Error}");
    }

    private void Decline_Click(object sender, RoutedEventArgs e) => _viewModel.Decline();

    private void CancelTest_Click(object sender, RoutedEventArgs e) => _viewModel.CancelTest();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Copies the secret between the two controls, and drops the plain-text copy as soon as it is hidden again so only
    /// one control holds it at a time.
    /// </summary>
    private void Reveal_Changed(object sender, RoutedEventArgs e)
    {
        if (RevealCheck.IsChecked == true)
        {
            PlainPasswordBox.Text = PasswordBox.Password;
            PlainPasswordBox.Focus();
            PlainPasswordBox.CaretIndex = PlainPasswordBox.Text.Length;
            return;
        }

        PasswordBox.Password = PlainPasswordBox.Text;
        PlainPasswordBox.Clear();
        PasswordBox.Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        PasswordBox.Clear();
        PlainPasswordBox.Clear();
        _viewModel.Reset();
    }
}
