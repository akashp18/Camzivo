using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camzivo.Core.Devices;
using Camzivo.Data.Devices;
using Camzivo.Data.Net;
using Camzivo.Data.Repositories;
using Camzivo.Data.Storage;
using Camzivo.Desktop.Shell;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Services;

/// <summary>
/// End-to-end acceptance of the persisted onboarding path, against the recording simulator:
/// ONVIF profile → RTSP live frame → save → reopen the store → playback search → decode → pause/resume/seek → export.
///
/// Nothing is injected: the camera row comes from SQLite, its password from DPAPI, and the playback page is the one
/// the shell would show after a restart. A temporary profile is used so the operator's real database is untouched.
/// </summary>
internal static class OnboardingE2EProbe
{
    private const int HttpPort = 8109;
    private const int RtspPort = 8566;

    public static async Task<int> RunAsync(
        MainWindow window,
        MainViewModel shell,
        string outputDirectory,
        string simulatorPath,
        string fixturePath)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var steps = new List<(string Name, bool Passed, string Detail)>();
        var logPath = Path.Combine(outputDirectory, "e2e-log.ndjson");
        using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };

        void Event(string name, object? data = null) =>
            log.WriteLine(JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                eventName = name,
                data,
            }));

        async Task<(string Name, bool Passed, string Detail)> Step(string name, Func<Task<(bool, string)>> action)
        {
            var started = Stopwatch.GetTimestamp();

            try
            {
                var (passed, detail) = await action();
                var step = (name, passed, $"{detail} [{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms]");
                Event("step", new { name, passed, detail = step.Item3 });
                return step;
            }
            catch (Exception exception)
            {
                var step = (name, false, exception.GetType().Name + ": " + exception.Message);
                Event("step.error", new { name, message = exception.Message, stack = exception.StackTrace });
                return step;
            }
        }

        static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(50);
            }

            return condition();
        }

        void Capture(string name)
        {
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = File.Create(Path.Combine(outputDirectory, name));
            encoder.Save(stream);
        }

        Process? simulator = null;
        var profileRoot = Path.Combine(outputDirectory, "profile");
        AppRuntime? first = null;
        AppRuntime? reopened = null;
        MainViewModel? restarted = null;

        try
        {
            if (Directory.Exists(profileRoot))
            {
                Directory.Delete(profileRoot, recursive: true);
            }

            simulator = Process.Start(new ProcessStartInfo
            {
                FileName = simulatorPath,
                ArgumentList =
                {
                    "--fixture", fixturePath,
                    "--http-port", HttpPort.ToString(CultureInfo.InvariantCulture),
                    "--rtsp-port", RtspPort.ToString(CultureInfo.InvariantCulture),
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            steps.Add(await Step("simulator.ready", async () =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var ready = await WaitUntil(
                    () =>
                    {
                        try
                        {
                            return client.GetStringAsync($"http://127.0.0.1:{HttpPort}/health")
                                .GetAwaiter().GetResult()
                                .Contains("ready", StringComparison.Ordinal);
                        }
                        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(20));

                return (ready, $"http :{HttpPort} · rtsp :{RtspPort}");
            }));

            var paths = new AppPaths(profileRoot);
            first = AppRuntime.Create(paths);

            var request = new CameraRegistrationRequest(
                "Simulator Gate",
                "127.0.0.1",
                "admin",
                "sim-password",
                UseTls: false,
                OnvifPort: HttpPort,
                RtspPort: RtspPort);

            var probe = new CameraProbe(new ConnectionTester(new HttpOnvifTransport(), new TcpRtspEndpointProbe()));
            CameraProbeResult? cameraTest = null;

            steps.Add(await Step("onboarding.test", async () =>
            {
                cameraTest = await probe.RunAsync(request);
                var stages = cameraTest.Stages.Count(stage => stage.Success);
                return (cameraTest.Success && stages == cameraTest.Stages.Count && stages >= 4,
                    $"success={cameraTest.Success} stages={stages}/{cameraTest.Stages.Count} detail=\"{cameraTest.Stages.LastOrDefault()?.Detail}\"");
            }));

            steps.Add(await Step("onboarding.save", async () =>
            {
                if (cameraTest is null || first.Onboarding is null)
                {
                    return (false, "no tested camera");
                }

                var outcome = await first.Onboarding.RegisterAsync(request, DateTimeOffset.UtcNow, CancellationToken.None, cameraTest.Test);
                return (outcome.Success, $"device={(outcome.Device is null ? "null" : outcome.Device.Id.ToString())} error={outcome.ValidationError}");
            }));

            Device? saved = null;

            steps.Add(await Step("persistence.readBack", async () =>
            {
                var devices = await first.Devices!.GetAllAsync();
                saved = devices.SingleOrDefault(device => device.DisplayName == "Simulator Gate");
                return (saved is not null && saved.StreamUriMain?.Contains("/live", StringComparison.Ordinal) == true,
                    $"devices={devices.Count} main=\"{saved?.StreamUriMain}\" sub=\"{saved?.StreamUriSub}\"");
            }));

            await first.DisposeAsync();
            first = null;

            // Reopening the store is the restart that matters for persisted onboarding. A second AppRuntime reads the
            // same SQLite file and the same DPAPI store, so nothing survives in memory.
            reopened = AppRuntime.Create(paths);

            steps.Add(await Step("restart.reopenStore", async () =>
            {
                var devices = await reopened.Devices!.GetAllAsync();
                var again = devices.SingleOrDefault(device => device.DisplayName == "Simulator Gate");
                var secret = again?.SecretRef is { Length: > 0 } reference ? reopened.Secrets.Load(reference) : null;
                return (again is not null && !string.IsNullOrEmpty(secret),
                    $"devices={devices.Count} id={again?.Id} passwordRestored={!string.IsNullOrEmpty(secret)}");
            }));

            restarted = new MainViewModel(reopened, demo: false);
            await restarted.InitializeAsync();
            window.DataContext = restarted;
            restarted.ShowSection(NavSection.Playback);
            await Task.Delay(700);
            Capture("01-restarted-playback.png");

            steps.Add(await Step("ui.cameraFromDatabase", () =>
            {
                var camera = restarted.Playback.Cameras.FirstOrDefault(entry => entry.Name == "Simulator Gate");
                return Task.FromResult((camera is not null && !string.IsNullOrEmpty(camera.Password),
                    $"cameras={restarted.Playback.Cameras.Count} name=\"{camera?.Name}\" source=\"{camera?.SourceToken}\""));
            }));

            steps.Add(await Step("search.recordings", async () =>
            {
                restarted.Playback.SelectedCamera = restarted.Playback.Cameras.First(entry => entry.Name == "Simulator Gate");
                restarted.Playback.SelectedDate = DateTime.Today;
                await restarted.Playback.SearchAsync();
                Capture("02-search-results.png");
                return (restarted.Playback.Blocks.Count > 0 && restarted.Playback.ErrorText is null,
                    $"segments={restarted.Playback.Blocks.Count} availability=\"{restarted.Playback.AvailabilityLabel}\"");
            }));

            var morning = restarted.Playback.Blocks
                .OrderBy(block => block.Segment.StartUtc)
                .FirstOrDefault(block => block.Segment.StartUtc >= DateTimeOffset.Now.Date);

            steps.Add(await Step("playback.play", async () =>
            {
                if (morning is null)
                {
                    return (false, "no segment from today");
                }

                restarted.Playback.SelectBlockCommand.Execute(morning);
                await restarted.Playback.PlayAsync();

                var playing = await WaitUntil(
                    () => restarted.Playback.FramesShown >= 8 && restarted.Playback.Frame is not null,
                    TimeSpan.FromSeconds(20));

                Capture("03-playing.png");
                return (playing, $"segments={restarted.Playback.Blocks.Count} frames={restarted.Playback.FramesShown} size={restarted.Playback.Frame?.PixelWidth}x{restarted.Playback.Frame?.PixelHeight}");
            }));

            var pausedAt = 0L;
            steps.Add(await Step("playback.pauseResume", async () =>
            {
                await restarted.Playback.PauseAsync();
                await Task.Delay(300);
                pausedAt = restarted.Playback.FramesShown;
                await Task.Delay(1200);
                var stable = restarted.Playback.FramesShown - pausedAt <= 2;

                await restarted.Playback.ResumeAsync();
                var resumed = await WaitUntil(
                    () => restarted.Playback.FramesShown > pausedAt + 3,
                    TimeSpan.FromSeconds(10));

                return (stable && resumed && restarted.Playback.IsPlaying,
                    $"paused {pausedAt}→{pausedAt} stable={stable} resumed={restarted.Playback.FramesShown}");
            }));

            steps.Add(await Step("playback.stop", async () =>
            {
                await restarted.Playback.StopAsync();
                var stopped = !restarted.Playback.IsPlaying && !restarted.Playback.IsPaused;
                return (stopped, $"stopped={stopped} status=\"{restarted.Playback.StatusText}\"");
            }));

            steps.Add(await Step("export.mp4", async () =>
            {
                if (morning is null)
                {
                    return (false, "no segment to export");
                }

                var path = Path.Combine(outputDirectory, "e2e-evidence.mp4");
                var start = morning.Segment.StartUtc + TimeSpan.FromMinutes(1);
                var result = await restarted.Playback.ExportRangeAsync(start, start + TimeSpan.FromSeconds(6), path);

                var verified = result.Success && File.Exists(path) &&
                               await EvidenceManifestWriter.VerifyAsync(path);

                return (verified,
                    $"file={Path.GetFileName(path)} bytes={result.Bytes} samples={result.Samples} hashVerified={verified} error={result.Error}");
            }));

            restarted.ShowSection(NavSection.Devices);
            await Task.Delay(400);
            Capture("04-devices-after-restart.png");
            restarted.ShowSection(NavSection.LiveView);
            await Task.Delay(300);
            Capture("05-live-after-restart.png");
        }
        catch (Exception exception)
        {
            Event("probe.error", new { message = exception.Message, stack = exception.StackTrace });
            steps.Add(("probe", false, exception.Message));
        }
        finally
        {
            if (restarted is not null)
            {
                await restarted.ShutdownAsync();
            }

            if (reopened is not null)
            {
                await reopened.DisposeAsync();
            }

            if (first is not null)
            {
                await first.DisposeAsync();
            }

            if (simulator is not null)
            {
                try
                {
                    simulator.Kill(entireProcessTree: true);
                    await simulator.WaitForExitAsync();
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _ = exception;
                }

                simulator.Dispose();
            }

            // The temporary profile holds a test camera and its DPAPI secret; it is evidence, not the operator's data.
            try
            {
                if (Directory.Exists(profileRoot))
                {
                    Directory.Delete(profileRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // A still-open SQLite handle is reported by the step above, not hidden by cleanup.
            }
        }

        var passed = steps.Count(step => step.Passed);
        Event("probe.finished", new { passed, total = steps.Count, allPassed = passed == steps.Count && steps.Count > 0 });

        File.WriteAllLines(
            Path.Combine(outputDirectory, "summary.txt"),
            steps.Select(step => $"{(step.Passed ? "PASS" : "FAIL")}  {step.Name,-28} {step.Detail}"));

        _ = shell;

        return passed == steps.Count && steps.Count > 0 ? 0 : 1;
    }
}
