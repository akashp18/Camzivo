using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camzivo.Core.Recording;
using Camzivo.Desktop.Shell;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Acceptance run of the investigation workflow against the scripted recording device: search → timeline → decode →
/// pause → resume → seek → stop → export. Every step is asserted on real state; screenshots show the real window.
/// </summary>
internal static class PlaybackProbe
{
    private const int HttpPort = 8099;
    private const int RtspPort = 8556;

    public static async Task<int> RunAsync(
        MainWindow window,
        MainViewModel viewModel,
        string outputDirectory,
        string simulatorPath,
        string fixturePath)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var logPath = Path.Combine(outputDirectory, "probe-log.ndjson");
        using var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        var steps = new List<(string Name, bool Passed, string Detail)>();

        void Event(string name, object? data = null)
        {
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                eventName = name,
                data,
            });

            logWriter.WriteLine(line);
            Console.WriteLine(line);
        }

        async Task<(string Name, bool Passed, string Detail)> Step(string name, Func<Task<(bool, string)>> action)
        {
            var started = Stopwatch.GetTimestamp();

            try
            {
                var (passed, detail) = await action();
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var step = (name, passed, $"{detail} [{elapsed:F0} ms]");
                Event("step", new { name, passed, detail = step.Item3 });
                return step;
            }
            catch (Exception exception)
            {
                var step = (name, false, exception.Message);
                steps.Add(step);
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

        try
        {
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
                            return client.GetStringAsync($"http://127.0.0.1:{HttpPort}/health").GetAwaiter().GetResult().Contains("ready", StringComparison.Ordinal);
                        }
                        catch (HttpRequestException)
                        {
                            return false;
                        }
                        catch (TaskCanceledException)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(20));

                return (ready, $"recording simulator on http :{HttpPort} and rtsp :{RtspPort}");
            }));

            var camera = new PlaybackCamera(
                Guid.NewGuid(),
                "Warehouse 1 (scripted recorder)",
                new Uri($"http://127.0.0.1:{HttpPort}/onvif/device_service"),
                "admin",
                "sim-password",
                "channel-1",
                RtspPort);

            viewModel.Playback.SetCameras([camera]);
            viewModel.ShowSection(NavSection.Playback);
            await Task.Delay(600);
            Capture("01-playback-page.png");

            steps.Add(await Step("ui.selectCameraAndDate", async () =>
            {
                viewModel.Playback.SelectedCamera = camera;
                viewModel.Playback.SelectedDate = DateTime.Today;
                await Task.Delay(200);
                return (viewModel.Playback.SelectedCamera?.Id == camera.Id, $"camera=\"{camera.Name}\" date={viewModel.Playback.SelectedDateLabel}");
            }));

            steps.Add(await Step("search.recordings", async () =>
            {
                await viewModel.Playback.SearchAsync();
                var ok = viewModel.Playback.Blocks.Count > 0 && viewModel.Playback.ErrorText is null;
                Capture("02-search-results.png");
                return (ok, $"blocks={viewModel.Playback.Blocks.Count} availability=\"{viewModel.Playback.AvailabilityLabel}\"");
            }));

            var dayStartUtc = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).ToUniversalTime();
            var morning = viewModel.Playback.Blocks
                .Where(block => block.Segment.StartUtc >= dayStartUtc)
                .OrderBy(block => block.Segment.StartUtc)
                .FirstOrDefault();

            steps.Add(await Step("ui.selectSegment", async () =>
            {
                if (morning is null)
                {
                    return (false, "no segment starting inside today");
                }

                viewModel.Playback.SelectBlockCommand.Execute(morning);
                await Task.Delay(200);
                Capture("03-segment-selected.png");
                return (ReferenceEquals(viewModel.Playback.SelectedBlock, morning), viewModel.Playback.SelectedSegmentText);
            }));

            steps.Add(await Step("playback.play", async () =>
            {
                await viewModel.Playback.PlayAsync();
                var playing = await WaitUntil(
                    () => viewModel.Playback.FramesShown >= 8 && viewModel.Playback.Frame is not null,
                    TimeSpan.FromSeconds(20));

                Capture("04-playing.png");
                return (playing, $"frames={viewModel.Playback.FramesShown} position={viewModel.Playback.PositionText} size={viewModel.Playback.Frame?.PixelWidth}x{viewModel.Playback.Frame?.PixelHeight}");
            }));

            var pausedFrames = 0L;

            steps.Add(await Step("playback.pause", async () =>
            {
                await viewModel.Playback.PauseAsync();
                await Task.Delay(400);
                pausedFrames = viewModel.Playback.FramesShown;
                await Task.Delay(1500);
                var stable = viewModel.Playback.FramesShown - pausedFrames <= 2;
                Capture("05-paused.png");
                return (stable && viewModel.Playback.IsPaused, $"frames while paused {pausedFrames}→{viewModel.Playback.FramesShown}");
            }));

            steps.Add(await Step("playback.resume", async () =>
            {
                await viewModel.Playback.ResumeAsync();
                var grown = await WaitUntil(
                    () => viewModel.Playback.FramesShown > pausedFrames + 3,
                    TimeSpan.FromSeconds(10));
                Capture("06-resumed.png");
                return (grown, $"frames {pausedFrames}→{viewModel.Playback.FramesShown} position={viewModel.Playback.PositionText}");
            }));

            steps.Add(await Step("playback.seek", async () =>
            {
                var segment = viewModel.Playback.SelectedBlock!.Segment;
                var before = viewModel.Playback.PositionText;
                var target = segment.StartUtc + TimeSpan.FromMinutes(20);
                await viewModel.Playback.SeekToAsync(target);
                var moved = await WaitUntil(
                    () => viewModel.Playback.FramesShown > pausedFrames + 5,
                    TimeSpan.FromSeconds(10));
                Capture("07-seeked.png");
                var expected = TimeZoneInfo.ConvertTime(target, TimeZoneInfo.Local);
                // Playback advances in real time: accept the target or the couple of seconds that played after it.
                var actual = TimeZoneInfo.ConvertTime(DateTimeOffset.ParseExact(viewModel.Playback.PositionText, "HH:mm:ss", CultureInfo.InvariantCulture), TimeZoneInfo.Local);
                var drift = Math.Abs((actual.TimeOfDay - expected.TimeOfDay).TotalSeconds);
                return (moved && drift <= 3, $"position {before}→{viewModel.Playback.PositionText} target {expected:HH:mm:ss} drift {drift:F1}s");
            }));

            steps.Add(await Step("playback.stop", async () =>
            {
                await viewModel.Playback.StopAsync();
                var stopped = !viewModel.Playback.IsPlaying && !viewModel.Playback.IsPaused;
                Capture("08-stopped.png");
                return (stopped && viewModel.Engine.ActiveCount == 0, $"stopped={stopped} live sessions={viewModel.Engine.ActiveCount} status=\"{viewModel.Playback.StatusText}\"");
            }));

            // Export: one minute after the segment start, six seconds of evidence.
            steps.Add(await Step("export.mp4", async () =>
            {
                if (morning is null)
                {
                    return (false, "no segment to export");
                }

                var path = Path.Combine(outputDirectory, "evidence.mp4");
                var start = morning.Segment.StartUtc + TimeSpan.FromMinutes(1);
                var end = start + TimeSpan.FromSeconds(6);
                var progressSamples = 0;
                var progress = new Progress<ExportProgress>(_ => progressSamples++);

                var result = await viewModel.Playback.ExportRangeAsync(start, end, path, progress);

                Capture("09-exported.png");
                return (result.Success && File.Exists(path) && new FileInfo(path).Length > 10_000,
                    $"file={path} bytes={result.Bytes} samples={result.Samples} progressUpdates={progressSamples} error={result.Error}");
            }));
        }
        catch (Exception exception)
        {
            Event("probe.error", new { message = exception.Message, stack = exception.StackTrace });
            steps.Add(("probe", false, exception.Message));
        }
        finally
        {
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
        }

        var passed = steps.Count(step => step.Passed);
        Event("probe.finished", new { passed, total = steps.Count, allPassed = passed == steps.Count && steps.Count > 0 });

        File.WriteAllLines(
            Path.Combine(outputDirectory, "summary.txt"),
            steps.Select(step => $"{(step.Passed ? "PASS" : "FAIL")}  {step.Name,-28} {step.Detail}"));

        return passed == steps.Count && steps.Count > 0 ? 0 : 1;
    }
}
