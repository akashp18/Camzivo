using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Camzivo.Desktop.Shell;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Drives the real user workflow against a real RTSP source and records what the application actually did:
/// Add Camera dialog → test → save → assign to a tile → decoded frames on screen → layout and fullscreen changes →
/// source disconnect → automatic reconnect → removal.
///
/// This is an acceptance harness, not a feature: it is reachable only through <c>--probe-live-wall</c>, it clicks the
/// real controls, and it screenshots and logs the running window. It never draws a picture of its own.
/// </summary>
internal static class LiveWallProbe
{
    private sealed class ProbeLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly List<string> _lines = [];
        private bool _closed;

        public ProbeLog(string path)
        {
            _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        }

        public IReadOnlyList<string> Lines => _lines;

        public void Event(string name, object? data = null)
        {
            // Stream status events keep arriving on the dispatcher after the probe has finished; late writes are
            // dropped instead of crashing the shutdown path.
            if (_closed)
            {
                return;
            }

            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                eventName = name,
                data,
            });

            _lines.Add(line);

            try
            {
                _writer.WriteLine(line);
                Console.WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
                _closed = true;
            }
        }

        public void Dispose()
        {
            _closed = true;
            _writer.Dispose();
        }
    }

    public static async Task<int> RunAsync(
        MainWindow window,
        MainViewModel viewModel,
        string outputDirectory,
        string testSourcePath,
        int mainPort,
        int subPort)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        using var log = new ProbeLog(Path.Combine(outputDirectory, "probe-log.ndjson"));
        var steps = new List<(string Name, bool Passed, string Detail)>();

        log.Event("probe.start", new { outputDirectory, testSourcePath, mainPort, subPort });
        log.Event("hardware", new
        {
            machine = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            processors = Environment.ProcessorCount,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        });

        var source = StartSource(testSourcePath, mainPort, subPort, log);
        Process? sourceProcess = source;

        try
        {
            steps.Add(await Step(log, "source.ready", async () =>
            {
                await WaitForPort(mainPort, TimeSpan.FromSeconds(20));
                await WaitForPort(subPort, TimeSpan.FromSeconds(20));
                return (true, $"rtsp://127.0.0.1:{mainPort}/live and :{subPort}/live accepting connections");
            }));

            viewModel.ShowSection(NavSection.Devices);
            await Task.Delay(500);
            Capture(window, Path.Combine(outputDirectory, "01-devices.png"));
            log.Event("ui.devices", new { devices = viewModel.Devices.Rows.Count });

            var dialog = new AddCameraWindow(viewModel.Runtime) { Owner = window };
            var dialogEvents = new List<string>();

            dialog.Loaded += async (_, _) =>
            {
                try
                {
                    dialog.NameBox.Text = "Test Camera";
                    dialog.AddressBox.Text = $"rtsp://127.0.0.1:{mainPort}/live";
                    dialog.UserBox.Text = string.Empty;
                    dialog.PasswordBox.Password = string.Empty;
                    dialogEvents.Add($"filled address={dialog.AddressBox.Text}");

                    var testButton = FindButton(dialog, "Test connection")
                        ?? throw new InvalidOperationException("Test connection button not found.");
                    testButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialogEvents.Add("clicked: Test connection");

                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (dialog.SaveButton.IsEnabled || !string.IsNullOrEmpty(dialog.ErrorText.Text))
                        {
                            break;
                        }

                        await Task.Delay(100);
                    }

                    Capture(dialog, Path.Combine(outputDirectory, "02-connection-test.png"));
                    dialogEvents.Add($"test finished: saveEnabled={dialog.SaveButton.IsEnabled} error=\"{dialog.ErrorText.Text}\"");

                    if (!dialog.SaveButton.IsEnabled)
                    {
                        dialog.Close();
                        return;
                    }

                    dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialogEvents.Add("clicked: Save camera");
                }
                catch (Exception exception)
                {
                    dialogEvents.Add("failed: " + exception.Message);
                    dialog.Close();
                }
            };

            var accepted = dialog.ShowDialog() == true;
            var saved = dialog.Saved;
            log.Event("ui.addCamera", new { accepted, saved, steps = dialogEvents });

            steps.Add(("ui.onboard", accepted && saved, string.Join(" | ", dialogEvents)));

            await viewModel.InitializeAsync();
            await Task.Delay(400);

            viewModel.ShowSection(NavSection.Devices);
            await Task.Delay(300);
            Capture(window, Path.Combine(outputDirectory, "03-camera-saved.png"));

            var camera = viewModel.Live.FilteredCameras.FirstOrDefault();
            if (camera is null)
            {
                steps.Add(("ui.assign", false, "no camera in the live list after saving"));
                return Finish(log, steps, outputDirectory);
            }

            viewModel.ShowSection(NavSection.LiveView);
            await Task.Delay(600);

            // The wall auto-fills new devices; clear that slot and place the camera deliberately, which is the
            // click path an operator uses.
            viewModel.Live.SelectSlotCommand.Execute("0");
            if (viewModel.Live.CanClearSelected)
            {
                viewModel.Live.ClearSelectedCommand.Execute(null);
            }

            viewModel.Live.AssignCommand.Execute(camera);
            await Task.Delay(300);
            log.Event("ui.assign", new
            {
                slot = viewModel.Live.Tiles[0].SlotIndex,
                tile = viewModel.Live.Tiles[0].Name,
                engineSessions = viewModel.Engine.ActiveCount,
            });

            var tile = viewModel.Live.Tiles[0];

            steps.Add(await Step(log, "video.firstFrame", async () =>
            {
                var live = await WaitUntil(
                    () => tile.HasFrame && tile.Status == TileStatus.Live,
                    TimeSpan.FromSeconds(45));
                Capture(window, Path.Combine(outputDirectory, "04-live.png"));

                return (live, $"status={tile.StatusText} frame={tile.Frame?.PixelWidth}x{tile.Frame?.PixelHeight}");
            }));

            // Consecutive frames: counters and pixels must both move.
            var framesBefore = viewModel.Engine.PresentedFrames(camera.Id);
            var hashBefore = HashOf(tile);
            await Task.Delay(900);
            var framesAfter = viewModel.Engine.PresentedFrames(camera.Id);
            var hashAfter = HashOf(tile);
            Capture(window, Path.Combine(outputDirectory, "05-live-consecutive.png"));

            steps.Add((
                "video.consecutiveFrames",
                framesAfter > framesBefore && hashBefore != hashAfter,
                $"frames {framesBefore}→{framesAfter}, pixelHash {hashBefore:X8}→{hashAfter:X8}, size={tile.Frame?.PixelWidth}x{tile.Frame?.PixelHeight}"));

            log.Event("video.telemetry", new { framesBefore, framesAfter, hashBefore, hashAfter });

            viewModel.Engine.StatusChanged += (id, status) =>
                log.Event("stream.status", new { camera = id, state = status.State.ToString(), status.Detail });

            viewModel.Live.ApplyLayout(4);
            await Task.Delay(700);
            Capture(window, Path.Combine(outputDirectory, "06-layout-4.png"));

            viewModel.Live.ApplyLayout(9);
            await Task.Delay(700);
            Capture(window, Path.Combine(outputDirectory, "06-layout-9.png"));
            steps.Add(("ui.layouts", viewModel.Live.Tiles.Count == 9, $"{viewModel.Live.Rows}x{viewModel.Live.Columns} back to 9 slots"));

            viewModel.ToggleFullScreenCommand.Execute(null);
            await Task.Delay(900);
            var fullScreenOn = viewModel.IsFullScreen;
            Capture(window, Path.Combine(outputDirectory, "07-fullscreen.png"));

            viewModel.ToggleFullScreenCommand.Execute(null);
            await Task.Delay(900);
            var fullScreenOff = !viewModel.IsFullScreen;
            Capture(window, Path.Combine(outputDirectory, "07-fullscreen-exited.png"));

            steps.Add(await Step(log, "ui.fullscreen", () =>
            {
                var stillLive = tile.Status == TileStatus.Live;
                return Task.FromResult((fullScreenOn && fullScreenOff && stillLive, $"entered={fullScreenOn} exited={fullScreenOff} tile={tile.StatusText}"));
            }));

            // Placement rules the drop handler uses: move, then undo. A move must not restart the decoder.
            var framesBeforeMove = viewModel.Engine.PresentedFrames(camera.Id);
            var sessionsBeforeMove = viewModel.Engine.ActiveCount;
            var sessionStartsBeforeMove = viewModel.Engine.SessionStarts;
            var moved = viewModel.Live.PlaceCamera(camera.Id, 4);

            // Bounded wait on the observable signal (a new presented frame), not a fixed sleep.
            await WaitForAsync(() => viewModel.Engine.PresentedFrames(camera.Id) > framesBeforeMove, TimeSpan.FromSeconds(5));

            var framesAfterMove = viewModel.Engine.PresentedFrames(camera.Id);
            var sessionsAfterMove = viewModel.Engine.ActiveCount;
            Capture(window, Path.Combine(outputDirectory, "11-moved-to-tile-5.png"));

            steps.Add((
                "ui.moveTile",
                moved && framesAfterMove > framesBeforeMove && sessionsAfterMove == sessionsBeforeMove &&
                    viewModel.Engine.SessionStarts == sessionStartsBeforeMove &&
                    viewModel.Live.Tiles[4].Status == TileStatus.Live,
                $"slot={Array.FindIndex(viewModel.Live.ExportWall(), id => id == camera.Id)} sessions {sessionsBeforeMove}→{sessionsAfterMove} " +
                $"starts {sessionStartsBeforeMove}→{viewModel.Engine.SessionStarts} frames {framesBeforeMove}→{framesAfterMove}"));

            var framesBeforeUndo = framesAfterMove;
            viewModel.Live.UndoCommand.Execute(null);

            await WaitForAsync(
                () => viewModel.Engine.PresentedFrames(camera.Id) > framesBeforeUndo,
                TimeSpan.FromSeconds(5));

            var framesAfterUndo = viewModel.Engine.PresentedFrames(camera.Id);
            var undone = Array.FindIndex(viewModel.Live.ExportWall(), id => id == camera.Id) == 0 &&
                         viewModel.Live.Tiles[0].Status == TileStatus.Live &&
                         viewModel.Engine.SessionStarts == sessionStartsBeforeMove &&
                         framesAfterUndo > framesBeforeUndo;
            Capture(window, Path.Combine(outputDirectory, "12-undo.png"));
            steps.Add(("ui.undo", undone, $"slot={Array.FindIndex(viewModel.Live.ExportWall(), id => id == camera.Id)} status={viewModel.Live.Tiles[0].StatusText} frames {framesBeforeUndo}→{framesAfterUndo}"));

            // Drop indicator: the same property the DragOver handler sets, rendered on the tile.
            viewModel.Live.Tiles[2].IsDropTarget = true;
            await Task.Delay(250);
            Capture(window, Path.Combine(outputDirectory, "13-drop-indicator.png"));
            var indicatorShown = viewModel.Live.Tiles[2].IsDropTarget;
            viewModel.Live.Tiles[2].IsDropTarget = false;
            steps.Add(("ui.dropIndicator", indicatorShown, "tile 3 highlighted while a camera is dragged over it"));

            // Real disconnect: the source process goes away.
            sourceProcess?.Kill(entireProcessTree: true);
            if (sourceProcess is not null)
            {
                await sourceProcess.WaitForExitAsync();
            }

            sourceProcess = null;

            steps.Add(await Step(log, "stream.disconnect", async () =>
            {
                var leftLive = await WaitUntil(() => tile.Status != TileStatus.Live, TimeSpan.FromSeconds(20));
                Capture(window, Path.Combine(outputDirectory, "08-disconnected.png"));
                return (leftLive, $"status={tile.StatusText} detail={tile.Detail}");
            }));

            sourceProcess = StartSource(testSourcePath, mainPort, subPort, log);

            steps.Add(await Step(log, "stream.reconnect", async () =>
            {
                await WaitForPort(mainPort, TimeSpan.FromSeconds(20));
                var framesAtReconnect = viewModel.Engine.PresentedFrames(camera.Id);
                var recovered = await WaitUntil(
                    () => tile.Status == TileStatus.Live && tile.HasFrame && viewModel.Engine.PresentedFrames(camera.Id) > framesAtReconnect,
                    TimeSpan.FromSeconds(90));
                Capture(window, Path.Combine(outputDirectory, "09-reconnected.png"));
                return (recovered, $"status={tile.StatusText} frames {framesAtReconnect}→{viewModel.Engine.PresentedFrames(camera.Id)}");
            }));

            var row = viewModel.Devices.Rows.FirstOrDefault(candidate => candidate.Id == camera.Id);
            if (row is null)
            {
                steps.Add(("device.remove", false, "camera row not found"));
            }
            else
            {
                viewModel.Devices.RemoveCommand.Execute(row);

                steps.Add(await Step(log, "device.remove", async () =>
                {
                    var released = await WaitUntil(
                        () => viewModel.Engine.ActiveCount == 0 && viewModel.Live.Tiles[0].Status == TileStatus.Empty,
                        TimeSpan.FromSeconds(20));
                    Capture(window, Path.Combine(outputDirectory, "10-removed.png"));
                    return (released, $"sessions={viewModel.Engine.ActiveCount} tile={viewModel.Live.Tiles[0].StatusText}");
                }));
            }
        }
        catch (Exception exception)
        {
            log.Event("probe.error", new { message = exception.Message, stack = exception.StackTrace });
            steps.Add(("probe", false, exception.Message));
        }
        finally
        {
            if (sourceProcess is not null)
            {
                try
                {
                    sourceProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }

        return Finish(log, steps, outputDirectory);
    }

    private static int Finish(ProbeLog log, List<(string Name, bool Passed, string Detail)> steps, string outputDirectory)
    {
        foreach (var step in steps)
        {
            log.Event("step", new { step.Name, step.Passed, step.Detail });
        }

        var passed = steps.Count(step => step.Passed);
        var summary = string.Join(
            Environment.NewLine,
            steps.Select(step => $"{(step.Passed ? "PASS" : "FAIL")}  {step.Name} — {step.Detail}"));

        File.WriteAllText(Path.Combine(outputDirectory, "probe-summary.txt"), summary + Environment.NewLine);
        log.Event("probe.finished", new { passed, total = steps.Count, allPassed = passed == steps.Count });

        return passed == steps.Count ? 0 : 1;
    }

    private static async Task<(string Name, bool Passed, string Detail)> Step(
        ProbeLog log,
        string name,
        Func<Task<(bool Passed, string Detail)>> action)
    {
        try
        {
            var (passed, detail) = await action();
            log.Event("step.result", new { name, passed, detail });
            return (name, passed, detail);
        }
        catch (Exception exception)
        {
            log.Event("step.error", new { name, message = exception.Message });
            return (name, false, exception.Message);
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    private static async Task WaitForPort(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(150);
            }
        }

        throw new TimeoutException($"Port {port} did not open for the test source.");
    }

    private static Process StartSource(string path, int mainPort, int subPort, ProbeLog log)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--main-port");
        startInfo.ArgumentList.Add(mainPort.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--sub-port");
        startInfo.ArgumentList.Add(subPort.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--fps");
        startInfo.ArgumentList.Add("15");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Test source did not start.");

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                log.Event("source.stdout", args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                log.Event("source.stderr", args.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        if (root is Button button && string.Equals(button.Content as string, content, StringComparison.Ordinal))
        {
            return button;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (FindButton(VisualTreeHelper.GetChild(root, index), content) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Checksum of the pixels currently on the tile: proof that two samples are different pictures.</summary>
    private static uint HashOf(CameraTileViewModel tile)
    {
        if (tile.Frame is not { } frame)
        {
            return 0;
        }

        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);

        var hash = 2166136261u;
        foreach (var value in pixels)
        {
            hash = (hash ^ value) * 16777619u;
        }

        return hash;
    }

    internal static void Capture(Window window, string path)
    {
        window.Dispatcher.VerifyAccess();

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Bounded wait on observable state; returns when the condition holds or the timeout expires.</summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }
    }

}
