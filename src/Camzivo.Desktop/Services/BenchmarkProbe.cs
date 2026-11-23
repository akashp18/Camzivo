using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Camzivo.Core.Streaming;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Synthetic multi-stream benchmark: N sessions against the local RTSP source, decoded with the production pipeline
/// and presented into the same WriteableBitmap the wall uses.
///
/// This measures the decoder and the presentation path under load. It is not a substitute for a real multi-camera
/// network test: every stream here originates from loopback and from the same 640×360 fixture.
/// </summary>
internal static class BenchmarkProbe
{
    public static async Task<int> RunAsync(
        string outputDirectory,
        string testSourcePath,
        IReadOnlyList<int> streamCounts,
        int secondsPerStep,
        int streamPort = 8554)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var rows = new List<string>
        {
            "streams,seconds,connections,frames,decodedFrames,coalesced,presentedFps,perStreamFps,cpuPercentAllCores," +
            "workingSetMb,managedMb,threads,framesDropped,decodeMsPerFrame,convertMsPerFrame,depacketizeMsPerAu," +
            "presentMsPerFrame,firstFrameMs",
        };
        var process = Process.GetCurrentProcess();
        var results = new List<object>();

        using var source = StartSource(testSourcePath);

        try
        {
            await WaitForPort(8554, TimeSpan.FromSeconds(20));

            foreach (var count in streamCounts)
            {
                await using var engine = new StreamEngine(Application.Current.Dispatcher);
                var tiles = new Dictionary<Guid, CameraTileViewModel>();
                var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

                foreach (var id in ids)
                {
                    tiles[id] = new CameraTileViewModel(0, id, "bench", TileStatus.Waiting, "MAIN", "CONNECTING");
                }

                engine.FramePresented += (id, frame) =>
                {
                    // Same call the wall makes: one reused WriteableBitmap per camera.
                    if (tiles.TryGetValue(id, out var tile))
                    {
                        tile.PresentVideoFrame(frame);
                    }
                };

                var attachAt = DateTime.UtcNow;
                foreach (var id in ids)
                {
                    engine.Attach(id, new Uri($"rtsp://127.0.0.1:{streamPort}/live"), null, null);
                }

                // First-frame latency: connect → decoded frame presented, measured to the last stream of the set.
                var allFirst = await WaitUntil(
                    () => ids.All(id => engine.PresentedFrames(id) > 0),
                    TimeSpan.FromSeconds(30));
                var firstFrameMs = allFirst ? (DateTime.UtcNow - attachAt).TotalMilliseconds : -1;

                // Let connections settle and decoders initialise before the measurement window.
                await Task.Delay(TimeSpan.FromSeconds(4));
                process.Refresh();

                var cpuStart = process.TotalProcessorTime;
                var presentedStart = ids.Sum(engine.PresentedFrames);
                var droppedStart = ids.Sum(id => engine.Telemetry(id)?.FramesDropped ?? 0);
                var decodedStart = ids.Sum(id => engine.Telemetry(id)?.FramesDecoded ?? 0);
                var presentationStart = engine.Presentation;
                var stageStart = SumPipeline(engine, ids);
                var wallStart = DateTime.UtcNow;

                await Task.Delay(TimeSpan.FromSeconds(secondsPerStep));

                process.Refresh();
                var elapsed = DateTime.UtcNow - wallStart;
                var cpu = process.TotalProcessorTime - cpuStart;
                var presented = ids.Sum(engine.PresentedFrames) - presentedStart;
                var dropped = ids.Sum(id => engine.Telemetry(id)?.FramesDropped ?? 0) - droppedStart;
                var decoded = ids.Sum(id => engine.Telemetry(id)?.FramesDecoded ?? 0) - decodedStart;
                var presentation = engine.Presentation;
                var coalesced = presentation.Coalesced - presentationStart.Coalesced;
                var presentMs = presentation.PresentMs - presentationStart.PresentMs;
                var stages = SumPipeline(engine, ids).Minus(stageStart);
                var connections = ids.Count(id => engine.Telemetry(id)?.FramesDecoded > 0);
                var fps = presented / elapsed.TotalSeconds;
                var cpuPercent = cpu.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;

                var row = string.Join(
                    ',',
                    count,
                    secondsPerStep,
                    connections,
                    presented,
                    decoded,
                    coalesced,
                    fps.ToString("F2", CultureInfo.InvariantCulture),
                    (fps / count).ToString("F2", CultureInfo.InvariantCulture),
                    cpuPercent.ToString("F1", CultureInfo.InvariantCulture),
                    (process.WorkingSet64 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture),
                    (GC.GetTotalMemory(false) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture),
                    process.Threads.Count,
                    dropped,
                    stages.DecodeMsPerFrame.ToString("F3", CultureInfo.InvariantCulture),
                    stages.ConvertMsPerFrame.ToString("F3", CultureInfo.InvariantCulture),
                    stages.DepacketizeMsPerAccessUnit.ToString("F3", CultureInfo.InvariantCulture),
                    (presented > 0 ? presentMs / presented : 0).ToString("F3", CultureInfo.InvariantCulture),
                    firstFrameMs.ToString("F0", CultureInfo.InvariantCulture));

                rows.Add(row);
                Console.WriteLine(
                    $"  stages decode={stages.DecodeMsPerFrame:F2}ms convert={stages.ConvertMsPerFrame:F2}ms " +
                    $"depacketize={stages.DepacketizeMsPerAccessUnit:F3}ms present={(presented > 0 ? presentMs / presented : 0):F2}ms " +
                    $"coalesced={coalesced} decoded={decoded} firstFrame={firstFrameMs:F0}ms");
                results.Add(new
                {
                    streams = count,
                    connections,
                    decodedFrames = decoded,
                    presentedFrames = presented,
                    coalescedFrames = coalesced,
                    coalescedPercent = Math.Round(decoded > 0 ? 100.0 * coalesced / Math.Max(1, decoded) : 0, 1),
                    presentedFps = Math.Round(fps, 2),
                    perStreamFps = Math.Round(fps / count, 2),
                    cpuPercentAllCores = Math.Round(cpuPercent, 1),
                    workingSetMb = process.WorkingSet64 / (1024 * 1024),
                    managedMb = GC.GetTotalMemory(false) / (1024 * 1024),
                    framesDropped = dropped,
                    decodeMsPerFrame = Math.Round(stages.DecodeMsPerFrame, 3),
                    convertMsPerFrame = Math.Round(stages.ConvertMsPerFrame, 3),
                    depacketizeMsPerAccessUnit = Math.Round(stages.DepacketizeMsPerAccessUnit, 3),
                    presentMsPerFrame = Math.Round(presented > 0 ? presentMs / presented : 0, 3),
                    firstFrameMs = Math.Round(firstFrameMs, 0),
                    decoders = ids.Select(id => engine.Telemetry(id)?.DecoderName).Where(name => name is not null).Distinct().ToList(),
                });

                Console.WriteLine($"streams={count} connections={connections} fps={fps:F1} cpu={cpuPercent:F1}% ram={process.WorkingSet64 / (1024 * 1024)}MB dropped={dropped}");
            }
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "benchmark-error.txt"), exception.ToString());
            return 1;
        }
        finally
        {
            try
            {
                source.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        File.WriteAllLines(Path.Combine(outputDirectory, "benchmark.csv"), rows);
        File.WriteAllText(
            Path.Combine(outputDirectory, "benchmark.json"),
            JsonSerializer.Serialize(new
            {
                machine = Environment.MachineName,
                processors = Environment.ProcessorCount,
                os = Environment.OSVersion.VersionString,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                port = streamPort,
                note = streamPort == 8554
                    ? "Loopback source, 640x360@15fps H.264 fixture, Media Foundation software decode, WriteableBitmap presentation."
                    : "Loopback source, 320x180@15fps substream fixture, Media Foundation software decode, WriteableBitmap presentation.",
                results,
            }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    private static PipelineSnapshot SumPipeline(StreamEngine engine, IReadOnlyList<Guid> ids)
    {
        long packets = 0, bytes = 0, units = 0, frames = 0;
        double receive = 0, depacketize = 0, decode = 0, convert = 0;

        foreach (var id in ids)
        {
            var snapshot = engine.Pipeline(id);
            packets += snapshot.Packets;
            bytes += snapshot.Bytes;
            units += snapshot.AccessUnits;
            frames += snapshot.Frames;
            receive += snapshot.ReceiveMs;
            depacketize += snapshot.DepacketizeMs;
            decode += snapshot.DecodeMs;
            convert += snapshot.ConvertMs;
        }

        return new PipelineSnapshot(packets, bytes, units, frames, receive, depacketize, decode, convert);
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

            await Task.Delay(50);
        }

        return condition();
    }

    private static Process StartSource(string path)
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
        startInfo.ArgumentList.Add("8554");
        startInfo.ArgumentList.Add("--sub-port");
        startInfo.ArgumentList.Add("8555");
        startInfo.ArgumentList.Add("--fps");
        startInfo.ArgumentList.Add("15");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Test source did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
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
}
