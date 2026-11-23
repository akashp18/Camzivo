using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Desktop.Services;
using Camzivo.Data.Devices;

namespace Camzivo.Desktop.Shell;

/// <summary>
/// Add Camera: the operator's whole onboarding path. It runs the same four stages the core tests do, and Save stays
/// disabled until the device has produced a decoded frame, so a camera in the list is a camera that works.
/// </summary>
public partial class AddCameraWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly CameraProbe _probe;
    private CameraProbeResult? _result;
    private CameraRegistrationRequest? _tested;
    private NvrDiscoveryResult? _discoveryResult;
    private NvrRegistrationRequest? _testedNvr;
    private readonly NvrDiscovery _discovery;

    public AddCameraWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _probe = new CameraProbe(new ConnectionTester(
            new Camzivo.Data.Net.HttpOnvifTransport(),
            new Camzivo.Data.Net.TcpRtspEndpointProbe()));

        InitializeComponent();

        _discovery = _runtime.Discovery ?? new NvrDiscovery(
            new Camzivo.Data.Net.HttpOnvifTransport(),
            new Camzivo.Data.Net.TcpRtspEndpointProbe());
    }

    /// <summary>True when the operator saved a camera; the caller reloads the device list.</summary>
    public bool Saved { get; private set; }

    /// <summary>Visual-review hook: opens the recorder surface with its ports and HTTPS option visible.</summary>
    internal void ShowRecorderMode()
    {
        NvrCheck.IsChecked = true;
        SaveButton.Content = "Import channels";
        AdvancedSettings.IsExpanded = true;
    }

    private CameraRegistrationRequest BuildRequest() => new(
        NameBox.Text?.Trim() ?? string.Empty,
        AddressBox.Text?.Trim() ?? string.Empty,
        string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
        string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password,
        TlsCheck.IsChecked == true,
        ParsePort(OnvifPortBox, 80),
        ParsePort(RtspPortBox, 554));

    private static int ParsePort(TextBox box, int fallback) =>
        int.TryParse(box.Text?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : fallback;

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAddress(out var request))
        {
            return;
        }

        SaveButton.IsEnabled = false;
        _result = null;
        _tested = null;
        _discoveryResult = null;
        _testedNvr = null;
        ErrorText.Text = string.Empty;
        ElapsedText.Text = "testing…";
        RenderStages(null, request);

        var stopwatch = Stopwatch.StartNew();

        if (NvrCheck.IsChecked == true)
        {
            await TestRecorderAsync(request, stopwatch);
            return;
        }

        try
        {
            var result = await _probe.RunAsync(request);
            stopwatch.Stop();

            _result = result;
            _tested = request;
            RenderStages(result, request);

            ElapsedText.Text = $"{stopwatch.Elapsed.TotalSeconds:F1} s";

            if (result.Success)
            {
                SaveButton.IsEnabled = true;
                ErrorText.Text = string.Empty;
            }
            else
            {
                ErrorText.Text = result.ValidationError
                    ?? ComposeProbeError(result)
                    ?? "The camera could not be tested.";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UriFormatException)
        {
            stopwatch.Stop();
            ElapsedText.Text = string.Empty;
            ErrorText.Text = exception.Message;
        }
    }

    /// <summary>
    /// Recorder test: reach, sign in, read profiles, then one stream address per profile. Every channel the device
    /// reports is listed with a checkbox, so an import is a decision the operator made, not a surprise.
    /// </summary>
    private async Task TestRecorderAsync(CameraRegistrationRequest request, Stopwatch stopwatch)
    {
        var nvrRequest = new NvrRegistrationRequest(
            request.DisplayName,
            request.Address,
            request.Username,
            request.Password,
            request.UseTls,
            request.OnvifPort,
            request.RtspPort);

        var validation = NvrOnboarding.Validate(nvrRequest);
        if (validation is not null)
        {
            ErrorText.Text = validation;
            return;
        }

        try
        {
            var address = request.Address.Trim();
            var host = address;
            var rtspPort = request.RtspPort ?? 554;

            if (address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(address, UriKind.Absolute, out var manual))
            {
                host = manual.Host;
                rtspPort = request.RtspPort ?? manual.Port;
            }

            var discovery = await _discovery.DiscoverAsync(
                new DeviceEndpoint(host, request.OnvifPort ?? 80, rtspPort, request.UseTls),
                request.Username,
                request.Password);

            stopwatch.Stop();
            ElapsedText.Text = $"{stopwatch.Elapsed.TotalSeconds:F1} s";

            RenderStageRows(discovery.Stages, request);
            _discoveryResult = discovery;
            _testedNvr = nvrRequest;

            // A failed recorder test leaves a redacted record: the stage states and the device's own words, never a
            // credential. Without it a screenshot was the only way to learn what the recorder answered.
            Camzivo.Data.Storage.AppLog.Info(
                $"Recorder test: host={Camzivo.Core.Devices.NvrSafety.MaskHost(host)} success={discovery.Success} " +
                $"stages={string.Join(",", discovery.Stages.Select(stage => $"{stage.Stage}={(stage.Success ? "OK" : stage.Failure.ToString())}"))} " +
                $"channels={discovery.Channels.Count} error={discovery.Failure?.Explanation}");

            if (!discovery.Success)
            {
                ChannelPanel.Visibility = Visibility.Collapsed;
                ErrorText.Text = discovery.ValidationError
                    ?? ComposeRecorderError(discovery);
                return;
            }

            RenderChannels(discovery);
            SaveButton.IsEnabled = true;
            ErrorText.Text = string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UriFormatException)
        {
            stopwatch.Stop();
            ErrorText.Text = exception.Message;
        }
    }

    /// <summary>
    /// The generic explanation names the class of failure; the failing stage's own detail names what the device
    /// actually answered. Both are shown, because "does not follow the protocol" alone sent the operator looking for
    /// the wrong problem.
    /// </summary>
    private static string? ComposeProbeError(CameraProbeResult result)
    {
        var explanation = result.Failure?.Explanation;
        var failing = result.Stages.LastOrDefault(stage => !stage.Success);

        if (explanation is null)
        {
            return failing?.Detail;
        }

        return failing is { Detail.Length: > 0 } &&
               !explanation.Contains(failing.Detail, StringComparison.Ordinal)
            ? $"{explanation} {failing.Detail}"
            : explanation;
    }

    private static string ComposeRecorderError(NvrDiscoveryResult discovery)
    {
        var explanation = discovery.Failure?.Explanation ?? "The recorder could not be read.";
        var failing = discovery.Stages.LastOrDefault(stage => !stage.Success);

        return failing is { Detail.Length: > 0 } &&
               !explanation.Contains(failing.Detail, StringComparison.Ordinal)
            ? $"{explanation} {failing.Detail}"
            : explanation;
    }

    private void RenderChannels(NvrDiscoveryResult discovery)
    {
        ChannelList.Children.Clear();
        ChannelPanel.Visibility = Visibility.Visible;

        var withStreams = discovery.Channels.Count(channel => channel.MainUri is not null && channel.SubUri is not null);
        ChannelHint.Text =
            $"{discovery.Channels.Count} channel(s) reported · {withStreams} with main + sub stream · " +
            $"services: {discovery.Services.Summary}";

        foreach (var channel in discovery.Channels.OrderBy(channel => channel.Channel))
        {
            var row = new CheckBox
            {
                IsChecked = channel.MainUri is not null,
                IsEnabled = channel.MainUri is not null,
                Tag = channel.Channel,
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextPrimary"),
                Content = $"Channel {channel.Channel} · {channel.Name}" +
                          (channel.MainUri is null ? " — no stream address" : channel.SubUri is null ? " — main only" : " — main + sub"),
            };

            ChannelList.Children.Add(row);
        }
    }

    private IReadOnlyList<int> SelectedChannels() =>
        ChannelList.Children
            .OfType<CheckBox>()
            .Where(box => box.IsChecked == true && box.Tag is int)
            .Select(box => (int)box.Tag!)
            .ToList();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllChannels(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllChannels(false);

    private void SetAllChannels(bool selected)
    {
        foreach (var box in ChannelList.Children.OfType<CheckBox>().Where(box => box.IsEnabled))
        {
            box.IsChecked = selected;
        }
    }

    private void NvrCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Switching modes invalidates the previous test: a camera result must not be saved as a recorder.
        _result = null;
        _tested = null;
        _discoveryResult = null;
        _testedNvr = null;
        SaveButton.IsEnabled = false;
        SaveButton.Content = NvrCheck.IsChecked == true ? "Import channels" : "Save camera";
        ChannelPanel.Visibility = Visibility.Collapsed;
    }

    private bool TryGetAddress(out CameraRegistrationRequest request)
    {
        request = BuildRequest();
        var error = DeviceOnboarding.Validate(request);

        if (error is not null)
        {
            ErrorText.Text = error;
            return false;
        }

        return true;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ElapsedText.Text = "scanning…";
        ErrorText.Text = string.Empty;

        var scanner = new OnvifDiscoveryScanner();
        var (devices, warning) = await scanner.ScanAsync(TimeSpan.FromSeconds(4));

        ElapsedText.Text = devices.Count == 0 ? string.Empty : $"{devices.Count} found";

        if (devices.Count == 0)
        {
            ErrorText.Text = warning ?? "No devices answered.";
            return;
        }

        var device = devices[0];

        if (device.PrimaryAddress is { } address)
        {
            AddressBox.Text = address.Host;
        }

        if (!string.IsNullOrWhiteSpace(device.DisplayName))
        {
            NameBox.Text = device.DisplayName;
        }

        ErrorText.Text = devices.Count == 1
            ? "Found one device. Press Test connection."
            : $"Found {devices.Count} devices; the first was filled in. Use its address if you need another.";
    }

    private void RenderStages(CameraProbeResult? result, CameraRegistrationRequest request) =>
        RenderStageRows(result?.Stages, request);

    private void RenderStageRows(IReadOnlyList<StageResult>? stages, CameraRegistrationRequest request)
    {
        Stages.Children.Clear();
        Stages.Children.Add(new TextBlock
        {
            Text = "CONNECTION TEST",
            Style = (Style)FindResource("Micro"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        var labels = new[]
        {
            "1 · Device reachable",
            "2 · Authentication successful",
            "3 · Stream discovered",
            "4 · First video frame rendered",
        };

        for (var index = 0; index < labels.Length; index++)
        {
            var stage = stages?.FirstOrDefault(candidate => (int)candidate.Stage == index);
            var state = stage is null ? "—" : stage.Success ? "✓" : "✗";

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };

            row.Children.Add(new TextBlock
            {
                Text = state,
                Width = 18,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.Mono"),
                Foreground = (System.Windows.Media.Brush)FindResource(stage is null
                    ? "Brush.TextMuted"
                    : stage.Success ? "Brush.Success" : "Brush.Critical"),
            });

            row.Children.Add(new TextBlock
            {
                Text = labels[index],
                Width = 210,
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextPrimary"),
            });

            row.Children.Add(new TextBlock
            {
                Text = stage?.Detail ?? (index == 3 ? "requires a decoded frame" : "not tested"),
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
            });

            Stages.Children.Add(row);
        }

        if (stages is { Count: 0 })
        {
            Stages.Children.Add(new TextBlock
            {
                Text = request.Address,
                Style = (Style)FindResource("Meta"),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (NvrCheck.IsChecked == true)
        {
            await SaveRecorderAsync();
            return;
        }

        if (_runtime.Onboarding is null)
        {
            ErrorText.Text = "The device list could not be opened, so the camera cannot be saved.";
            return;
        }

        if (_result is null || !_result.Success || _tested is null)
        {
            ErrorText.Text = "Test the camera first: a device is only saved after it produced a frame.";
            return;
        }

        SaveButton.IsEnabled = false;

        try
        {
            var outcome = await _runtime.Onboarding.RegisterAsync(
                _tested,
                DateTimeOffset.UtcNow,
                CancellationToken.None,
                _result.Test);

            if (!outcome.Success)
            {
                ErrorText.Text = outcome.ValidationError ?? outcome.Failure?.Explanation ?? "The camera could not be saved.";
                SaveButton.IsEnabled = true;
                return;
            }

            Saved = true;
            DialogResult = true;
            Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ErrorText.Text = $"Saving failed: {exception.Message}";
            SaveButton.IsEnabled = true;
        }
    }

    private async Task SaveRecorderAsync()
    {
        if (_runtime.Nvr is null)
        {
            ErrorText.Text = "The device list could not be opened, so the recorder cannot be saved.";
            return;
        }

        if (_discoveryResult is not { Success: true } discovery || _testedNvr is null)
        {
            ErrorText.Text = "Test the recorder first: channels are only imported after the device answered.";
            return;
        }

        var selected = SelectedChannels();
        if (selected.Count == 0)
        {
            ErrorText.Text = "Select at least one channel to import.";
            return;
        }

        SaveButton.IsEnabled = false;

        try
        {
            var outcome = await _runtime.Nvr.ImportAsync(
                _testedNvr,
                discovery,
                selected,
                DateTimeOffset.UtcNow);

            if (!outcome.Success)
            {
                ErrorText.Text = outcome.Error ?? "The recorder could not be imported.";
                SaveButton.IsEnabled = true;
                return;
            }

            Saved = true;
            DialogResult = true;
            Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ErrorText.Text = $"Importing failed: {exception.Message}";
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Any edit invalidates the previous test: saving a camera that was not tested is exactly what this dialog
        // exists to prevent.
        SaveButton.IsEnabled = false;
        _result = null;
        _discoveryResult = null;
    }
}
