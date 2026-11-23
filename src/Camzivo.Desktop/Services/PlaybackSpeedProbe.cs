using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camzivo.Core.Recording;
using Camzivo.Core.Streaming.Replay;
using Camzivo.Desktop.Controls;
using Camzivo.Desktop.Shell;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Acceptance run for accelerated recorded playback. Each speed is requested from a real RTSP replay endpoint, then the
/// measured recording-time progression is compared with wall-clock time. A speed is only a pass when source time
/// advances at the requested rate, decoded frames change, and presentation remains bounded.
/// </summary>
internal static class PlaybackSpeedProbe
{
    private const double SlowWindowSeconds = 4.0;
    private const double FastWindowSeconds = 3.0;

    public static async Task<int> RunAsync(
        MainWindow window,
        MainViewModel viewModel,
        string outputDirectory,
        string simulatorPath,
        string fixturePath)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        using var logWriter = new StreamWriter(
            Path.Combine(outputDirectory, "speed-probe.ndjson"),
            append: false,
            Encoding.UTF8) { AutoFlush = true };
        var steps = new List<(string Name, bool Passed, string Detail)>();
        var results = new List<SpeedResult>();

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

        static string FrameHash(WriteableBitmap? bitmap)
        {
            if (bitmap is null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return string.Empty;
            }

            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            var hash = 14695981039346656037UL;

            foreach (var value in pixels)
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }

            return hash.ToString("X16", CultureInfo.InvariantCulture);
        }

        static (double CpuPercent, long WorkingSet) ResourceSample(TimeSpan wall, TimeSpan cpuBefore, Process process)
        {
            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuBefore;
            var percent = wall.TotalSeconds > 0
                ? cpu.TotalSeconds / (wall.TotalSeconds * Environment.ProcessorCount) * 100
                : 0;
            return (percent, process.WorkingSet64);
        }

        var simulators = new List<Process>();
        var allocatedPorts = new HashSet<int>();
        var honorHttpPort = FreePort(allocatedPorts);
        var honorRtspPort = FreePort(allocatedPorts);
        var ignoreHttpPort = FreePort(allocatedPorts);
        var ignoreRtspPort = FreePort(allocatedPorts);
        var rejectHttpPort = FreePort(allocatedPorts);
        var rejectRtspPort = FreePort(allocatedPorts);

        async Task<bool> StartSimulatorAsync(string scaleMode, int httpPort, int rtspPort)
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = simulatorPath,
                ArgumentList =
                {
                    "--fixture", fixturePath,
                    "--http-port", httpPort.ToString(CultureInfo.InvariantCulture),
                    "--rtsp-port", rtspPort.ToString(CultureInfo.InvariantCulture),
                    "--scale-mode", scaleMode,
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            simulators.Add(process);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var expectedPid = $"\"pid\":{process.Id.ToString(CultureInfo.InvariantCulture)}";

            return await WaitUntil(
                () =>
                {
                    try
                    {
                        return client.GetStringAsync($"http://127.0.0.1:{httpPort}/health")
                            .GetAwaiter().GetResult()
                            .Contains(expectedPid, StringComparison.Ordinal);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(20));
        }

        try
        {
            steps.Add(await Step("simulator.ready.honor", async () =>
                (await StartSimulatorAsync("honor", honorHttpPort, honorRtspPort), $"rtsp :{honorRtspPort} scale-mode=honor")));

            var camera = new PlaybackCamera(
                Guid.NewGuid(),
                "Warehouse 1 (accelerated replay)",
                new Uri($"http://127.0.0.1:{honorHttpPort}/onvif/device_service"),
                "admin",
                "sim-password",
                "channel-1",
                honorRtspPort);

            viewModel.Playback.SetCameras([camera]);
            viewModel.ShowSection(NavSection.Playback);
            await Task.Delay(500);
            viewModel.Playback.SelectedCamera = camera;
            viewModel.Playback.SelectedDate = DateTime.Today;

            RecordingSegment? segment = null;
            TimelineBar? selectedBar = null;

            steps.Add(await Step("recordings.search", async () =>
            {
                await viewModel.Playback.SearchAsync();
                var dayStartUtc = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).ToUniversalTime();
                var morning = viewModel.Playback.Blocks
                    .Where(block => block.Segment.StartUtc >= dayStartUtc)
                    .OrderBy(block => block.Segment.StartUtc)
                    .FirstOrDefault();

                if (morning is null)
                {
                    return (false, "no recording inside today");
                }

                viewModel.Playback.SelectBlockCommand.Execute(morning);
                selectedBar = morning;
                segment = morning.Segment;
                await Task.Delay(200);
                Capture("01-speed-selector.png");
                return (true, $"blocks={viewModel.Playback.Blocks.Count} segment={segment.StartUtc:HH:mm:ss}–{segment.EndUtc:HH:mm:ss}");
            }));

            if (segment is null)
            {
                throw new InvalidOperationException("No recording was available to measure.");
            }

            var process = Process.GetCurrentProcess();

            foreach (var requested in PlaybackRates.Supported)
            {
                var rate = requested;

                steps.Add(await Step($"speed.{rate:0}x", async () =>
                {
                    await viewModel.Playback.StopAsync();
                    viewModel.Playback.SelectBlockCommand.Execute(selectedBar);

                    if (!await viewModel.Playback.SetSpeedAsync(rate))
                    {
                        return (false, $"speed selection refused: {viewModel.Playback.SpeedNotice}");
                    }

                    await viewModel.Playback.PlayAsync(segment.StartUtc + TimeSpan.FromMinutes(5));

                    var ready = await WaitUntil(
                        () => viewModel.Playback.IsPlaying &&
                              viewModel.Playback.FramesShown >= 4 &&
                              viewModel.Playback.PositionUtc is not null,
                        TimeSpan.FromSeconds(20));

                    if (!ready)
                    {
                        return (false, $"playback did not start: {viewModel.Playback.ErrorText ?? viewModel.Playback.StatusText}");
                    }

                    var startPosition = viewModel.Playback.PositionUtc!.Value;
                    var startFrames = viewModel.Playback.FramesShown;
                    var startDecoded = viewModel.Playback.DecodedFrames;
                    var startCoalesced = viewModel.Playback.FramesCoalesced;
                    var startHash = FrameHash(viewModel.Playback.Frame);
                    var midHash = string.Empty;
                    var startClock = Stopwatch.GetTimestamp();
                    var cpuBefore = process.TotalProcessorTime;
                    var window = rate <= 1 ? SlowWindowSeconds : FastWindowSeconds;
                    var samples = new List<object>();

                    // Let the measured window run while video actually plays.
                    for (var sample = 0; sample < window * 2; sample++)
                    {
                        await Task.Delay(500);

                        if (sample == (int)(window * 2 / 2))
                        {
                            midHash = FrameHash(viewModel.Playback.Frame);
                        }

                        var status = viewModel.Playback.CurrentReplayStatus;
                        samples.Add(new
                        {
                            at = Stopwatch.GetElapsedTime(startClock).TotalSeconds,
                            position = viewModel.Playback.PositionUtc,
                            observed = status?.ObservedRate,
                            state = status?.State.ToString(),
                            frame = status?.FramesPresented,
                        });
                    }

                    Event("speed.samples", new { requested = rate, samples });

                    var elapsed = Stopwatch.GetElapsedTime(startClock);
                    var endPosition = viewModel.Playback.PositionUtc ?? startPosition;
                    var sourceSeconds = (endPosition - startPosition).TotalSeconds;
                    var measured = sourceSeconds / Math.Max(0.001, elapsed.TotalSeconds);
                    var endHash = FrameHash(viewModel.Playback.Frame);
                    var distinctPictures = new[] { startHash, midHash, endHash }
                        .Where(hash => hash.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    var presented = viewModel.Playback.FramesShown - startFrames;
                    var decoded = viewModel.Playback.DecodedFrames - startDecoded;
                    var coalesced = viewModel.Playback.FramesCoalesced - startCoalesced;
                    var (cpuPercent, workingSet) = ResourceSample(elapsed, cpuBefore, process);
                    var tolerance = PlaybackRates.Tolerance(rate);
                    var delivered = PlaybackRates.IsDelivered(rate, measured);
                    var framesChanged = distinctPictures > 1;
                    var passed = delivered && framesChanged && presented > 0 && !viewModel.Playback.HasError;

                    results.Add(new SpeedResult(
                        rate,
                        measured,
                        tolerance,
                        window,
                        decoded,
                        presented,
                        coalesced,
                        cpuPercent,
                        workingSet,
                        delivered,
                        framesChanged,
                        viewModel.Playback.SpeedNotice));

                    Event("speed.result", results[^1]);

                    if (rate >= 8)
                    {
                        Capture($"02-speed-{rate:0}x.png");
                    }

                    return (passed,
                        $"requested={PlaybackRates.Label(rate)} measured={measured:F2}× source={sourceSeconds:F1}s wall={elapsed.TotalSeconds:F2}s " +
                        $"decoded={decoded} presented={presented} coalesced={coalesced} changed={framesChanged} " +
                        $"cpu={cpuPercent:F1}% mem={workingSet / 1024 / 1024}MB notice=\"{viewModel.Playback.SpeedNotice}\"");
                }));
            }

            steps.Add(await Step("speed.pauseResume", async () =>
            {
                await viewModel.Playback.StopAsync();
                viewModel.Playback.SelectBlockCommand.Execute(selectedBar);
                await viewModel.Playback.SetSpeedAsync(4);
                await viewModel.Playback.PlayAsync(segment.StartUtc + TimeSpan.FromMinutes(8));
                await WaitUntil(() => viewModel.Playback.FramesShown > 4 && viewModel.Playback.IsPlaying, TimeSpan.FromSeconds(15));

                await viewModel.Playback.PauseAsync();
                await Task.Delay(300);
                var pausedPosition = viewModel.Playback.PositionUtc;
                var pausedFrames = viewModel.Playback.FramesShown;

                static bool IsHeld(PlaybackViewModel playback, DateTimeOffset? position, long frames) =>
                    playback.IsPaused &&
                    playback.PositionUtc is { } current &&
                    position is { } expected &&
                    Math.Abs((current - expected).TotalSeconds) <= 0.25 &&
                    playback.FramesShown - frames <= 2;

                await Task.Delay(800);
                var heldAtFour = IsHeld(viewModel.Playback, pausedPosition, pausedFrames);

                // Change speed while paused: the session reopens at the new rate but must return to Paused, not start.
                var changedWhilePaused = await viewModel.Playback.SetSpeedAsync(8);
                var returnedToPause = await WaitUntil(() => viewModel.Playback.IsPaused, TimeSpan.FromSeconds(5));
                await Task.Delay(250);
                var settledPosition = viewModel.Playback.PositionUtc;
                var settledFrames = viewModel.Playback.FramesShown;
                await Task.Delay(500);
                var stillHeld = returnedToPause && IsHeld(viewModel.Playback, settledPosition, settledFrames);

                await viewModel.Playback.ResumeAsync();
                await Task.Delay(1500);
                var moved = (viewModel.Playback.PositionUtc ?? pausedPosition!.Value) - pausedPosition!.Value;
                var resumedFrames = viewModel.Playback.FramesShown - pausedFrames;
                Capture("03-speed-paused-resumed.png");

                return (heldAtFour && changedWhilePaused && stillHeld && moved.TotalSeconds > 6 && resumedFrames > 0 && viewModel.Playback.PlaybackRate == 8,
                    $"held4={heldAtFour} changedWhilePaused={changedWhilePaused} held8={stillHeld} " +
                    $"pausedFrames={pausedFrames} settledFrames={settledFrames} resumedFrames={resumedFrames} " +
                    $"advanced={moved.TotalSeconds:F1}s at {viewModel.Playback.PlaybackRate:0}×");
            }));

            steps.Add(await Step("speed.seekWhileFast", async () =>
            {
                await viewModel.Playback.SetSpeedAsync(8);
                await WaitUntil(() => viewModel.Playback.FramesShown > 4, TimeSpan.FromSeconds(10));
                var before = viewModel.Playback.PositionUtc ?? segment.StartUtc;
                var target = before + TimeSpan.FromMinutes(10);
                await viewModel.Playback.SeekToAsync(target);
                await WaitUntil(() => viewModel.Playback.IsPlaying, TimeSpan.FromSeconds(10));
                await Task.Delay(700);
                var after = viewModel.Playback.PositionUtc ?? before;
                var drift = (after - target).TotalSeconds;

                return (drift >= 0 && drift < 30, $"seek {before:HH:mm:ss}→{after:HH:mm:ss} target={target:HH:mm:ss} drift={drift:F1}s speed={viewModel.Playback.PlaybackRate:0}×");
            }));

            steps.Add(await Step("speed.endOfRecording", async () =>
            {
                // One connection at the tail: changing speed and then seeking would reopen twice in a row and made
                // this acceptance step sensitive to simulator teardown timing rather than end detection.
                await viewModel.Playback.StopAsync();
                await viewModel.Playback.SetSpeedAsync(16);
                await viewModel.Playback.PlayAsync(segment.EndUtc.AddSeconds(-2));
                var ended = await WaitUntil(() => !viewModel.Playback.IsPlaying && viewModel.Playback.StatusText.Contains("end", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(15));
                return (ended, $"playing={viewModel.Playback.IsPlaying} status=\"{viewModel.Playback.StatusText}\" position={viewModel.Playback.PositionText}");
            }));

            steps.Add(await Step("speed.stopAndRelease", async () =>
            {
                await viewModel.Playback.StopAsync();
                await Task.Delay(300);
                return (!viewModel.Playback.IsPlaying && !viewModel.Playback.IsPaused && viewModel.Engine.ActiveCount == 0,
                    $"playing={viewModel.Playback.IsPlaying} liveSessions={viewModel.Engine.ActiveCount}");
            }));

            // Final acceptance: a recording from the previous day, reviewed at speed in the same Playback page.
            steps.Add(await Step("previousDay.16x", async () =>
            {
                await viewModel.Playback.StopAsync();
                viewModel.Playback.SetCameras([camera]);
                viewModel.Playback.SelectedDate = DateTime.Today.AddDays(-1);
                await viewModel.Playback.SearchAsync();
                var block = viewModel.Playback.Blocks.OrderByDescending(candidate => candidate.Segment.Duration).FirstOrDefault();

                if (block is null ||
                    TimeZoneInfo.ConvertTime(block.Segment.StartUtc, TimeZoneInfo.Local).Date != DateTime.Today.AddDays(-1))
                {
                    return (false, "no previous-day recording returned by the simulator");
                }

                viewModel.Playback.SelectBlockCommand.Execute(block);
                await viewModel.Playback.SetSpeedAsync(16);
                await viewModel.Playback.PlayAsync(block.Segment.StartUtc + TimeSpan.FromMinutes(1));
                var ready = await WaitUntil(() => viewModel.Playback.IsPlaying && viewModel.Playback.FramesShown > 3 && viewModel.Playback.PositionUtc is not null, TimeSpan.FromSeconds(15));

                if (!ready)
                {
                    return (false, $"previous-day playback did not start: {viewModel.Playback.ErrorText ?? viewModel.Playback.StatusText}");
                }

                var start = viewModel.Playback.PositionUtc!.Value;
                var startHash = FrameHash(viewModel.Playback.Frame);
                var clock = Stopwatch.GetTimestamp();
                await Task.Delay(TimeSpan.FromSeconds(3));
                var elapsed = Stopwatch.GetElapsedTime(clock);
                var measured = ((viewModel.Playback.PositionUtc ?? start) - start).TotalSeconds / elapsed.TotalSeconds;
                var changed = startHash.Length > 0 && FrameHash(viewModel.Playback.Frame) != startHash;
                Capture("06-previous-day-16x.png");
                await viewModel.Playback.StopAsync();

                return (PlaybackRates.IsDelivered(16, measured) && changed,
                    $"date={viewModel.Playback.SelectedDate:yyyy-MM-dd} segment={block.Label} measured={measured:F2}× requested=16× changed={changed}");
            }));

            // A recorder that answers 200 and echoes Scale but delivers real time must be reported, never relabelled.
            steps.Add(await Step("scale.ignored", async () =>
            {
                if (!await StartSimulatorAsync("ignore", ignoreHttpPort, ignoreRtspPort))
                {
                    return (false, "ignore-mode simulator did not start");
                }

                await viewModel.Playback.StopAsync();
                var ignoreCamera = new PlaybackCamera(
                    Guid.NewGuid(),
                    "Warehouse 1 (ignores Scale)",
                    new Uri($"http://127.0.0.1:{ignoreHttpPort}/onvif/device_service"),
                    "admin",
                    "sim-password",
                    "channel-1",
                    ignoreRtspPort);

                viewModel.Playback.SetCameras([ignoreCamera]);
                await viewModel.Playback.SearchAsync();
                var block = viewModel.Playback.Blocks.OrderByDescending(candidate => candidate.Segment.Duration).FirstOrDefault();

                if (block is null)
                {
                    return (false, "no recording after ignore-mode restart");
                }

                viewModel.Playback.SelectBlockCommand.Execute(block);
                await viewModel.Playback.SetSpeedAsync(16);
                await viewModel.Playback.PlayAsync(block.Segment.StartUtc + TimeSpan.FromMinutes(2));
                await WaitUntil(() => viewModel.Playback.FramesShown > 3 && viewModel.Playback.PositionUtc is not null, TimeSpan.FromSeconds(15));

                var start = viewModel.Playback.PositionUtc!.Value;
                var clock = Stopwatch.GetTimestamp();
                await Task.Delay(TimeSpan.FromSeconds(2.5));
                var elapsed = Stopwatch.GetElapsedTime(clock);
                var measured = ((viewModel.Playback.PositionUtc ?? start) - start).TotalSeconds / elapsed.TotalSeconds;
                var reported = viewModel.Playback.HasSpeedNotice &&
                               viewModel.Playback.SpeedNotice?.Contains("delivering", StringComparison.OrdinalIgnoreCase) == true;
                Capture("04-scale-ignored.png");

                return (measured < 2 && reported,
                    $"requested=16× measured={measured:F2}× notice=\"{viewModel.Playback.SpeedNotice}\"");
            }));

            steps.Add(await Step("scale.rejected", async () =>
            {
                if (!await StartSimulatorAsync("reject", rejectHttpPort, rejectRtspPort))
                {
                    return (false, "reject-mode simulator did not start");
                }

                await viewModel.Playback.StopAsync();
                var rejectCamera = new PlaybackCamera(
                    Guid.NewGuid(),
                    "Warehouse 1 (rejects Scale)",
                    new Uri($"http://127.0.0.1:{rejectHttpPort}/onvif/device_service"),
                    "admin",
                    "sim-password",
                    "channel-1",
                    rejectRtspPort);

                viewModel.Playback.SetCameras([rejectCamera]);
                await viewModel.Playback.SearchAsync();
                var block = viewModel.Playback.Blocks.OrderByDescending(candidate => candidate.Segment.Duration).FirstOrDefault();

                if (block is null)
                {
                    return (false, "no recording after reject-mode restart");
                }

                viewModel.Playback.SelectBlockCommand.Execute(block);

                // Initial playback at an unsupported rate must fail visibly, not silently show 4×.
                await viewModel.Playback.SetSpeedAsync(4);
                await viewModel.Playback.PlayAsync(block.Segment.StartUtc + TimeSpan.FromMinutes(1));
                await Task.Delay(800);
                var refusedInitial = !viewModel.Playback.IsPlaying && viewModel.Playback.HasError;

                await viewModel.Playback.StopAsync();
                await viewModel.Playback.SetSpeedAsync(1);
                await viewModel.Playback.PlayAsync(block.Segment.StartUtc + TimeSpan.FromMinutes(1));
                await WaitUntil(() => viewModel.Playback.IsPlaying, TimeSpan.FromSeconds(15));

                // A speed change during playback falls back to the speed that worked, with a reason.
                var accepted = await viewModel.Playback.SetSpeedAsync(4);
                await Task.Delay(500);
                var continued = viewModel.Playback.IsPlaying && viewModel.Playback.PlaybackRate == 1 &&
                                viewModel.Playback.SpeedNotice?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true;
                Capture("05-scale-rejected.png");

                return (refusedInitial && !accepted && continued,
                    $"initialRefused={refusedInitial} activeRefused={!accepted} continuedAt={viewModel.Playback.PlaybackRate:0}× notice=\"{viewModel.Playback.SpeedNotice}\"");
            }));
        }
        catch (Exception exception)
        {
            Event("probe.error", new { message = exception.Message, stack = exception.StackTrace });
            steps.Add(("probe", false, exception.Message));
        }
        finally
        {
            foreach (var process in simulators)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    _ = exception;
                }

                process.Dispose();
            }
        }

        var passed = steps.Count(step => step.Passed);
        Event("probe.finished", new
        {
            passed,
            total = steps.Count,
            allPassed = passed == steps.Count && steps.Count > 0,
            tolerance = "1× ±15 %, accelerated ±25 %; measurement windows 4 s (1×) and 3 s (2×–16×)",
        });

        Event("probe.rateStatus", results.Select(result => new
        {
            requested = result.Requested,
            measured = result.Measured,
            notice = result.SpeedNotice,
        }));

        var summary = new StringBuilder();
        summary.AppendLine("REQUESTED vs MEASURED PLAYBACK SPEED");
        summary.AppendLine("speed  measured  tolerance  window  decoded  presented  coalesced  cpu%  memMB  delivered");
        foreach (var result in results)
        {
            summary.AppendLine(
                $"{result.Requested,4:0}×  {result.Measured,7:F2}×  {result.Tolerance,8:P0}  {result.WindowSeconds,5:F1}s  " +
                $"{result.DecodedFrames,7}  {result.PresentedFrames,9}  {result.CoalescedFrames,9}  {result.CpuPercent,5:F1}  " +
                $"{result.WorkingSetBytes / 1024 / 1024,5}  {result.Delivered}");
        }

        summary.AppendLine();
        foreach (var step in steps)
        {
            summary.AppendLine($"{(step.Passed ? "PASS" : "FAIL")}  {step.Name,-24} {step.Detail}");
        }

        File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), summary.ToString());
        File.WriteAllText(
            Path.Combine(outputDirectory, "speeds.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        return passed == steps.Count && steps.Count > 0 ? 0 : 1;
    }

    private static int FreePort(HashSet<int> allocated)
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);

            try
            {
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                if (allocated.Add(port))
                {
                    return port;
                }
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private sealed record SpeedResult(
        double Requested,
        double Measured,
        double Tolerance,
        double WindowSeconds,
        long DecodedFrames,
        long PresentedFrames,
        long CoalescedFrames,
        double CpuPercent,
        long WorkingSetBytes,
        bool Delivered,
        bool FramesChanged,
        string? SpeedNotice);
}
