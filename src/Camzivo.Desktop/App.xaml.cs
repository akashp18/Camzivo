using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Camzivo.Core.Devices;
using Camzivo.Core.Security;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Data.Repositories;
using Camzivo.Data.Storage;
using Camzivo.Desktop.Services;
using Camzivo.Desktop.Shell;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop;

public partial class App : Application
{
    public static bool IsDarkTheme { get; private set; } = true;

    private AppRuntime? _runtime;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Logging first: everything after this point must leave a trace if it fails.
        AppLog.Initialize(AppPaths.Default.LogRoot, CredentialRedactor.Sanitize);

        // A refused stream is diagnosed from the recorder's own status line, not from a guess: the trace records the
        // method, the path and the answer ("DESCRIBE -> 503", "connection closed"). Credentials never reach it.
        RtspTracing.Sink = message => AppLog.Info($"rtsp {message}");
        InstallCrashHandlers();

        var startedAt = DateTime.UtcNow;
        ApplyTheme(dark: true);

        var snapshotPath = GetArgument(e.Args, "--snapshot");
        var probePath = GetArgument(e.Args, "--probe-startup");
        var fullScreenProbePath = GetArgument(e.Args, "--probe-fullscreen");
        var addCameraProbePath = GetArgument(e.Args, "--probe-add-camera");
        var diagnosticsProbePath = GetArgument(e.Args, "--probe-diagnostics");
        var nvrConnectionProbePath = GetArgument(e.Args, "--probe-nvr-connection");
        var liveWallProbePath = GetArgument(e.Args, "--probe-live-wall");
        var playbackProbePath = GetArgument(e.Args, "--probe-playback");
        var playbackSpeedProbePath = GetArgument(e.Args, "--probe-playback-speeds");
        var onboardingE2EPath = GetArgument(e.Args, "--probe-onboarding-e2e");
        var simulatorPath = Path.GetFullPath(GetArgument(e.Args, "--simulator")
            ?? Path.Combine(AppContext.BaseDirectory, "Camzivo.RecordingSimulator.exe"));
        var recordingFixturePath = Path.GetFullPath(GetArgument(e.Args, "--fixture")
            ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-640x360.h264"));
        var benchmarkPath = GetArgument(e.Args, "--probe-benchmark");
        var hardwareProbePath = GetArgument(e.Args, "--probe-hardware");
        var nvrProbePath = GetArgument(e.Args, "--probe-nvr");
        var nvrMetadataProbePath = GetArgument(e.Args, "--probe-nvr-metadata");
        var benchmarkCounts = (GetArgument(e.Args, "--benchmark-streams") ?? "1,4,9,16,25")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
            .Where(value => value > 0)
            .ToList();
        var testSourcePath = Path.GetFullPath(GetArgument(e.Args, "--test-source")
            ?? Path.Combine(AppContext.BaseDirectory, "Camzivo.TestSource.exe"));
        var demo = snapshotPath is not null || e.Args.Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase));

        // A/B switch for the benchmark: the previous WritePixels presentation path versus the current one.
        CameraTileViewModel.LegacyPresent = e.Args.Any(arg => arg.Equals("--legacy-present", StringComparison.OrdinalIgnoreCase));

        AppRuntime runtime;
        try
        {
            runtime = AppRuntime.Create();
        }
        catch (Exception exception)
        {
            // Even an unexpected failure here must produce a diagnosable log rather than a silent exit.
            AppLog.Error("Startup failed while opening the local store", exception);
            throw;
        }

        _runtime = runtime;

        if (hardwareProbePath is not null)
        {
            // Evidence for the acceleration decision; needs no window and no database.
            Environment.ExitCode = HardwareCapabilityReport.Run(hardwareProbePath);
            Shutdown();
            return;
        }

        if (nvrMetadataProbePath is not null)
        {
            // Metadata-only verification: identity, services, clock, profiles and sources. It has its own authorization
            // flag, cannot reach the stream or recording operations, and stops as soon as the metadata is collected.
            var metadataHost = GetArgument(e.Args, "--nvr-host");
            var metadataUser = GetArgument(e.Args, "--nvr-user");
            var metadataPassword = Environment.GetEnvironmentVariable("CAMZIVO_NVR_PASSWORD");
            var metadataPort = int.TryParse(GetArgument(e.Args, "--nvr-port"), out var parsedMetadataPort) ? parsedMetadataPort : 80;
            var metadataTls = e.Args.Any(arg => arg.Equals("--nvr-tls", StringComparison.OrdinalIgnoreCase)) ||
                              string.Equals(GetArgument(e.Args, "--nvr-scheme"), "https", StringComparison.OrdinalIgnoreCase);
            var metadataAuthorized = e.Args.Any(arg => arg.Equals("--nvr-metadata-authorized", StringComparison.OrdinalIgnoreCase));
            var metadataConfirmation = GetArgument(e.Args, "--nvr-confirm");

            Environment.ExitCode = NvrMetadataProbeCommand.RunAsync(
                nvrMetadataProbePath, metadataHost, metadataUser, metadataPassword, metadataPort, metadataTls,
                metadataAuthorized, metadataConfirmation).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        if (nvrProbePath is not null)
        {
            // Real hardware acceptance is explicit and read-only. It never substitutes a simulator for a real NVR,
            // and it reports BLOCKED when no host/password was supplied.
            var nvrHost = GetArgument(e.Args, "--nvr-host");
            var nvrUser = GetArgument(e.Args, "--nvr-user");
            var nvrPassword = Environment.GetEnvironmentVariable("CAMZIVO_NVR_PASSWORD");
            var nvrPort = int.TryParse(GetArgument(e.Args, "--nvr-port"), out var parsedNvrPort) ? parsedNvrPort : 80;
            var nvrRtspPort = int.TryParse(GetArgument(e.Args, "--nvr-rtsp-port"), out var parsedRtspPort) ? parsedRtspPort : 554;
            var nvrAuthorized = e.Args.Any(arg => arg.Equals("--nvr-authorized", StringComparison.OrdinalIgnoreCase));
            var nvrConfirmation = GetArgument(e.Args, "--nvr-confirm");

            Environment.ExitCode = NvrAcceptanceProbe.RunAsync(
                nvrProbePath, nvrHost, nvrUser, nvrPassword, nvrPort, nvrRtspPort,
                nvrAuthorized, nvrConfirmation).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        if (e.Args.Any(arg => arg.Equals("--light", StringComparison.OrdinalIgnoreCase)))
        {
            ApplyTheme(dark: false);
        }
        else if (!e.Args.Any(arg => arg.Equals("--dark", StringComparison.OrdinalIgnoreCase))
                 && runtime.Preferences?.GetSync(PreferencesRepository.ThemeKey) is { } savedTheme)
        {
            // The operator's last choice wins over the default, but never over an explicit command-line flag.
            ApplyTheme(dark: !savedTheme.Equals("light", StringComparison.OrdinalIgnoreCase));
        }

        AppLog.Info($"Local store ready in {(DateTime.UtcNow - startedAt).TotalMilliseconds:F0} ms (demo={demo}, persistence={runtime.HasPersistence}).");

        var viewModel = new MainViewModel(runtime, demo);

        if (AppLog.ReadAndClearCrashMarker() is { Length: > 0 } crash)
        {
            viewModel.ReportPreviousCrash(crash);
        }

        if (Enum.TryParse<NavSection>(GetArgument(e.Args, "--section"), ignoreCase: true, out var section))
        {
            viewModel.ShowSection(section);
        }

        var window = new MainWindow { DataContext = viewModel };

        MainWindow = window;

        if (snapshotPath is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            // Give the visual tree one idle pass so layout and templates are applied before capture.
            window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    Capture(window, snapshotPath);
                    Shutdown();
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (fullScreenProbePath is not null)
        {
            // Real check of the fullscreen path: drives the same code the F11 key and the command use, and records
            // the actual window state before, during and after, instead of relying on a code reading.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;

            window.ContentRendered += (_, _) => window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        File.WriteAllText(fullScreenProbePath, window.RunFullScreenProbe());
                        AppLog.Info("Fullscreen probe: result written.");
                    }
                    catch (Exception exception)
                    {
                        File.WriteAllText(fullScreenProbePath, "probe failed: " + exception);
                        AppLog.Error("Fullscreen probe failed", exception);
                    }

                    Shutdown();
                }),
                DispatcherPriority.ApplicationIdle);

            window.Show();
        }
        else if (benchmarkPath is not null)
        {
            // Synthetic multi-stream benchmark of the production decode + present path.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    var streamPort = e.Args.Any(arg => arg.Equals("--benchmark-sub", StringComparison.OrdinalIgnoreCase)) ? 8555 : 8554;
                    Environment.ExitCode = await BenchmarkProbe.RunAsync(benchmarkPath, testSourcePath, benchmarkCounts, 12, streamPort);
                    await viewModel.ShutdownAsync();
                    Shutdown();
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (playbackProbePath is not null)
        {
            // Recording investigation acceptance run: search, timeline, playback, seek and export against the simulator.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    try
                    {
                        Environment.ExitCode = await PlaybackProbe.RunAsync(
                            window,
                            viewModel,
                            playbackProbePath,
                            simulatorPath,
                            recordingFixturePath);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Error("Playback probe failed", exception);
                        Environment.ExitCode = 1;
                    }

                    await viewModel.ShutdownAsync();
                    Shutdown();
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (playbackSpeedProbePath is not null)
        {
            // Accelerated replay acceptance: requested vs measured speed with real decoded frames, plus ignored and
            // rejected Scale recorders.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    try
                    {
                        Environment.ExitCode = await PlaybackSpeedProbe.RunAsync(
                            window,
                            viewModel,
                            playbackSpeedProbePath,
                            simulatorPath,
                            recordingFixturePath);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Error("Playback speed probe failed", exception);
                        Environment.ExitCode = 1;
                    }

                    await viewModel.ShutdownAsync();
                    Shutdown();
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (onboardingE2EPath is not null)
        {
            // Onboarding → persistence → playback → export through the production services, against the simulator.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    try
                    {
                        Environment.ExitCode = await OnboardingE2EProbe.RunAsync(
                            window,
                            viewModel,
                            onboardingE2EPath,
                            simulatorPath,
                            recordingFixturePath);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Error("Onboarding E2E probe failed", exception);
                        Environment.ExitCode = 1;
                    }

                    await viewModel.ShutdownAsync();
                    Shutdown();
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (liveWallProbePath is not null)
        {
            // Full acceptance run of the live workflow against a real RTSP source, on the real window.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    try
                    {
                        Environment.ExitCode = await LiveWallProbe.RunAsync(window, viewModel, liveWallProbePath, testSourcePath, 8554, 8555);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Error("Live wall probe failed", exception);
                        Environment.ExitCode = 1;
                    }

                    await viewModel.ShutdownAsync();
                    Shutdown();
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (addCameraProbePath is not null)
        {
            // Renders the real Add Camera dialog so onboarding can be reviewed without clicking through the shell.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    var dialog = new AddCameraWindow(runtime) { Owner = window };

                    if (e.Args.Any(arg => arg.Equals("--nvr-mode", StringComparison.OrdinalIgnoreCase)))
                    {
                        dialog.ShowRecorderMode();
                    }

                    dialog.Show();

                    dialog.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            try
                            {
                                Capture(dialog, addCameraProbePath);
                            }
                            catch (Exception exception)
                            {
                                AppLog.Error("Add camera probe failed", exception);
                            }

                            dialog.Close();
                            Shutdown();
                        }),
                        DispatcherPriority.ApplicationIdle);
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (diagnosticsProbePath is not null)
        {
            // Renders the read-only diagnostics window so recorder capability rows can be reviewed in both themes.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    var dialog = new DeviceDiagnosticsWindow(new DeviceDiagnosticsViewModel(DiagnosticsSample()))
                    {
                        Owner = window,
                    };

                    dialog.Show();

                    dialog.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            try
                            {
                                Capture(dialog, diagnosticsProbePath);
                            }
                            catch (Exception exception)
                            {
                                AppLog.Error("Diagnostics probe failed", exception);
                            }

                            dialog.Close();
                            Shutdown();
                        }),
                        DispatcherPriority.ApplicationIdle);
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else if (nvrConnectionProbePath is not null)
        {
            // Visual review of the manual connection window. It stays idle and contacts nothing: with
            // --preview-confirmation it only fills the fields and opens the confirmation panel.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = 1600;
            window.Height = 900;
            window.Show();

            window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    var model = new NvrConnectionViewModel();
                    var dialog = new NvrConnectionWindow(model) { Owner = window };

                    if (e.Args.Any(arg => arg.Equals("--preview-confirmation", StringComparison.OrdinalIgnoreCase)))
                    {
                        // A placeholder password that is never sent anywhere: no confirmation is accepted here.
                        model.Host = "192.168.1.108";
                        model.Username = "admin";
                        model.RequestTest("preview-only");
                    }

                    dialog.Show();

                    dialog.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            try
                            {
                                Capture(dialog, nvrConnectionProbePath);
                            }
                            catch (Exception exception)
                            {
                                AppLog.Error("NVR connection probe failed", exception);
                            }

                            dialog.Close();
                            Shutdown();
                        }),
                        DispatcherPriority.ApplicationIdle);
                }),
                DispatcherPriority.ApplicationIdle);
        }
        else
        {
            if (probePath is not null)
            {
                // Cold start to first rendered frame, measured against the OS process start time.
                window.ContentRendered += (_, _) =>
                {
                    var elapsed = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                    File.WriteAllText(probePath, elapsed.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                    AppLog.Info($"Probe: window rendered {elapsed:F0} ms after process start.");
                    Shutdown();
                };
            }

            window.Show();
        }

        AppLog.Info($"Window shown {(DateTime.UtcNow - startedAt).TotalMilliseconds:F0} ms after startup began.");

        if (e.Args.Any(arg => arg.Equals("--nvr-connection-window", StringComparison.OrdinalIgnoreCase)))
        {
            // Manual verification surface. It opens with empty fields: no saved recorder is loaded and nothing is
            // contacted until the operator confirms a test inside it.
            window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    var connection = new NvrConnectionWindow(new NvrConnectionViewModel()) { Owner = window };
                    connection.Show();
                }),
                DispatcherPriority.ApplicationIdle);
        }

        _ = viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException exception)
        {
            AppLog.Error("Preferences could not be flushed during shutdown", exception.GetBaseException());
        }

        AppLog.Info("Shutdown complete.");
        base.OnExit(e);
    }

    /// <summary>
    /// An unexpected UI exception is written to the log and the crash marker, then allowed to terminate the process.
    /// Swallowing it (the previous behaviour) left the application running in an unknown state — the operator would
    /// keep watching a wall that might no longer be truthful, with no record of what broke. Failing loudly after a
    /// durable log write is the honest option: the next start reports the crash once (see <c>ReportPreviousCrash</c>).
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // AppLog appends synchronously, so the record is on disk before the process goes down.
            AppLog.Error("Unhandled UI exception", args.Exception);
            AppLog.WriteCrashMarker($"Unhandled UI exception at {DateTimeOffset.Now:O}: {args.Exception.GetType().Name}: {args.Exception.Message}");

            args.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLog.Error("Fatal unhandled exception", exception);
                AppLog.WriteCrashMarker($"Fatal exception at {DateTimeOffset.Now:O}: {exception.GetType().Name}: {exception.Message}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Swaps the token dictionary. Views bind with DynamicResource, so no restart is needed.</summary>
    public static void ApplyTheme(bool dark)
    {
        IsDarkTheme = dark;

        var dictionaries = Current.Resources.MergedDictionaries;
        var tokens = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source is not null &&
            dictionary.Source.OriginalString.Contains("Tokens.", StringComparison.OrdinalIgnoreCase));

        if (tokens is null)
        {
            return;
        }

        var target = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Tokens.Dark.xaml" : "Themes/Tokens.Light.xaml", UriKind.Relative),
        };

        var index = dictionaries.IndexOf(tokens);
        dictionaries.RemoveAt(index);
        dictionaries.Insert(index, target);
    }

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Representative recorder report for the visual-review probe; it is never shown in normal use.</summary>
    private static DeviceDiagnosticReport DiagnosticsSample() => new(
        Guid.NewGuid(),
        "NVR Warehouse",
        "192.168.1.108 · ONVIF 80 · RTSP 554 · HTTP",
        "NVR recorder",
        "Dahua",
        "NVR5216-16P-4KS2",
        "4.001.0000000.0",
        "serial hidden",
        DateTimeOffset.UtcNow,
        DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        "Eastern Standard Time",
        16,
        [
            new DeviceDiagnosticRow("Device identity", CapabilityState.Supported, "GetDeviceInformation answered"),
            new DeviceDiagnosticRow("Live stream", CapabilityState.Supported, "Main and sub profiles resolved"),
            new DeviceDiagnosticRow("Recording search (Profile G)", CapabilityState.Supported, "Search and replay services advertised"),
            new DeviceDiagnosticRow("Device clock", CapabilityState.Supported, "GetSystemDateAndTime answered"),
            new DeviceDiagnosticRow("PTZ", CapabilityState.Unsupported, "No PTZ service; channels are view-only"),
            new DeviceDiagnosticRow("Audio", CapabilityState.Unverified, "Not verified for this firmware"),
        ],
        "The recorder did not answer GetProfiles within 10 s (read-only; no retry was sent).",
        DateTimeOffset.UtcNow);

    /// <summary>Headless visual review: renders the shell to a PNG so design changes can be checked in CI.</summary>
    private static void Capture(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
