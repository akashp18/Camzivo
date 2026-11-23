using System.IO;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Streaming.Video;
using Camzivo.Desktop.Services;
using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

/// <summary>
/// Snapshots come from presented frames, not placeholders, and PTZ commands always end in a stop. Both are properties
/// a security operator depends on: evidence must be the real picture, and a camera head must never keep moving.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SnapshotAndPtzTests
{
    private static VideoFrame Frame(int width = 64, int height = 36)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 40;
            pixels[index + 1] = 120;
            pixels[index + 2] = 200;
            pixels[index + 3] = 255;
        }

        return new VideoFrame(pixels, width, height, width * 4, TimeSpan.Zero);
    }

    [Fact]
    public void SnapshotFileNameCarriesCameraAndTimestamp()
    {
        var at = DateTimeOffset.Parse("2026-11-22T14:05:06Z", System.Globalization.CultureInfo.InvariantCulture);

        var name = SnapshotWriter.FileName("Gate North", at);

        Assert.Matches(@"^Gate_North_\d{8}_\d{6}\.png$", name);
    }

    [Fact]
    public void SnapshotFileNameRemovesCharactersWindowsRejects()
    {
        var name = SnapshotWriter.FileName("NVR/1: channel\\2?", DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('?', name);
        Assert.EndsWith(".png", name, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotIsARealPngAtTheFrameSize()
    {
        var directory = Path.Combine(Path.GetTempPath(), "camzivo-snapshots", Guid.NewGuid().ToString("N"));

        try
        {
            var path = SnapshotWriter.Save(Frame(64, 36), "Gate North", DateTimeOffset.Now, directory);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 100, "the PNG is suspiciously small");

            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // The caption strip is drawn inside the picture, so the frame keeps its aspect ratio.
            Assert.Equal(64, frame.PixelWidth);
            Assert.Equal(36, frame.PixelHeight);
            Assert.Equal(64d / 36d, (double)frame.PixelWidth / frame.PixelHeight, 3);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ASnapshotNeedsAPresentedFrame()
    {
        var wall = new LiveViewModel(demo: false);
        wall.LoadCameras([new CameraListEntry(Guid.NewGuid(), "Gate", "Test")]);
        wall.SelectSlotCommand.Execute(0);

        Assert.False(wall.CanSnapshot);

        wall.Tiles[0].PresentVideoFrame(Frame());

        Assert.True(wall.CanSnapshot);
    }

    [Fact]
    public void TheSnapshotCommandNamesTheSelectedCamera()
    {
        var wall = new LiveViewModel(demo: false);
        var camera = new CameraListEntry(Guid.NewGuid(), "Gate", "Test");
        wall.LoadCameras([camera]);
        wall.SelectSlotCommand.Execute(0);
        wall.Tiles[0].PresentVideoFrame(Frame());

        Guid? asked = null;
        wall.SnapshotRequested += id => asked = id;

        wall.SnapshotCommand.Execute(null);

        Assert.Equal(camera.Id, asked);
    }

    [Fact]
    public async Task MovingTheCameraAlwaysStopsItAgain()
    {
        var transport = new ScriptedTransport();
        using var ptz = new PtzController(transport);

        var availability = await ptz.PrepareAsync(
            Guid.NewGuid(),
            new DeviceEndpoint("192.168.1.108", 80, 554, false),
            OnvifServices.None,
            "admin",
            "secret",
            "Profile1");

        Assert.True(availability.Available, availability.Detail);
        Assert.Equal(2, availability.Presets.Count);

        Assert.True(await ptz.NudgeAsync(-0.6, 0, 0));
        Assert.True(ptz.IsMoving);

        // The controller stops itself within its auto-stop window; the test waits for that observable transition.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ptz.IsMoving && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.False(ptz.IsMoving);
        Assert.Contains(transport.Bodies, body => body.Contains("<ContinuousMove", StringComparison.Ordinal));
        Assert.Contains(transport.Bodies, body => body.Contains("<Stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForgettingTheCameraStopsMovement()
    {
        var transport = new ScriptedTransport();
        using var ptz = new PtzController(transport);
        var cameraId = Guid.NewGuid();

        await ptz.PrepareAsync(cameraId, new DeviceEndpoint("192.168.1.108", 80, 554, false), OnvifServices.None, null, null, "Profile1");
        await ptz.NudgeAsync(0.6, 0, 0);

        ptz.Forget(cameraId);

        Assert.False(ptz.IsMoving);
        Assert.Null(ptz.CameraId);
    }

    [Fact]
    public async Task ADeviceWithoutPTZNodesIsReportedUnavailable()
    {
        var transport = new ScriptedTransport(nodes: "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body /></s:Envelope>");
        using var ptz = new PtzController(transport);

        var availability = await ptz.PrepareAsync(
            Guid.NewGuid(),
            new DeviceEndpoint("192.168.1.108", 80, 554, false),
            OnvifServices.None,
            null,
            null,
            "Profile1");

        Assert.False(availability.Available);
        Assert.False(string.IsNullOrWhiteSpace(availability.Detail));
        Assert.False(await ptz.NudgeAsync(0.5, 0, 0));
    }

    [Fact]
    public async Task PresetsComeFromTheCameraAndCanBeApplied()
    {
        var transport = new ScriptedTransport();
        using var ptz = new PtzController(transport);
        await ptz.PrepareAsync(Guid.NewGuid(), new DeviceEndpoint("192.168.1.108", 80, 554, false), OnvifServices.None, null, null, "Profile1");

        Assert.True(await ptz.GotoPresetAsync("2"));
        Assert.Contains(transport.Bodies, body =>
            body.Contains("<GotoPreset", StringComparison.Ordinal) &&
            body.Contains("<PresetToken>2</PresetToken>", StringComparison.Ordinal));
    }

    private sealed class ScriptedTransport(string? nodes = null) : IOnvifTransport
    {
        public List<string> Bodies { get; } = [];

        public Task<OnvifHttpResponse> PostAsync(Uri uri, string body, string? authorization, CancellationToken cancellationToken)
        {
            Bodies.Add(body);

            if (body.Contains("GetNodes", StringComparison.Ordinal))
            {
                return Task.FromResult(new OnvifHttpResponse(200, nodes ?? """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                        <tptz:PTZNode token="node1">
                          <tt:SupportedPTZSpaces>
                            <tt:ContinuousPanTiltVelocitySpace />
                            <tt:ContinuousZoomVelocitySpace />
                          </tt:SupportedPTZSpaces>
                        </tptz:PTZNode>
                      </tptz:GetNodesResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            if (body.Contains("GetPresets", StringComparison.Ordinal))
            {
                return Task.FromResult(new OnvifHttpResponse(200, """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <tptz:GetPresetsResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                        <tptz:Preset token="1"><tt:Name>Gate</tt:Name></tptz:Preset>
                        <tptz:Preset token="2"><tt:Name>Car park</tt:Name></tptz:Preset>
                      </tptz:GetPresetsResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            return Task.FromResult(new OnvifHttpResponse(200, "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body /></s:Envelope>"));
        }
    }
}
