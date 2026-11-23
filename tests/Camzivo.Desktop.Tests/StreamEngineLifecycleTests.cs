using System.Runtime.Versioning;
using System.Windows.Threading;
using Camzivo.Core.Streaming;
using Camzivo.Desktop.Services;
using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

/// <summary>
/// Stream ownership rules, asserted against observable engine state rather than sleeps: attaching the same address
/// twice is a no-op, a changed address replaces exactly one session, a moved tile (and an undo) never restarts a
/// session, and shutdown releases every session.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamEngineLifecycleTests
{
    private static readonly Guid Gate = new("44444444-4444-4444-4444-444444444444");

    private static StreamEngine Engine() => new(Dispatcher.CurrentDispatcher);

    private static CameraListEntry Camera(Guid id, string? main = "rtsp://127.0.0.1:9/main", string? sub = null) =>
        new(id, "Gate", "Test", new StreamUriSet(main, sub));

    private static LiveViewModel.StreamTargetResolver Resolver => (camera, _) => new StreamTarget(new Uri(camera.Uris.Main!), null, null);

    [Fact]
    public async Task AttachingTheSameAddressTwiceKeepsOneSession()
    {
        await using var engine = Engine();

        var uri = new Uri("rtsp://127.0.0.1:9/live");
        engine.Attach(Gate, uri, "admin", "secret");
        engine.Attach(Gate, uri, "admin", "secret");
        engine.Attach(Gate, uri, "admin", "secret");

        Assert.Equal(1, engine.ActiveCount);
        Assert.Equal(1, engine.SessionStarts);
    }

    [Fact]
    public async Task ChangingTheAddressReplacesTheSessionAndLeavesOneEntry()
    {
        await using var engine = Engine();

        var main = new Uri("rtsp://127.0.0.1:9/main");
        var sub = new Uri("rtsp://127.0.0.1:9/sub");

        for (var index = 0; index < 10; index++)
        {
            engine.Attach(Gate, index % 2 == 0 ? main : sub, null, null);
        }

        Assert.Equal(1, engine.ActiveCount);
        Assert.Equal(10, engine.SessionStarts);
        Assert.Equal([Gate], engine.ActiveCameras);
    }

    [Fact]
    public async Task DetachingDuringDecodingLeavesNoEntry()
    {
        await using var engine = Engine();
        engine.Attach(Gate, new Uri("rtsp://127.0.0.1:9/live"), null, null);

        engine.Detach(Gate);

        Assert.Equal(0, engine.ActiveCount);
        Assert.Empty(engine.ActiveCameras);
        Assert.Equal(0, engine.PresentedFrames(Gate));
    }

    [Fact]
    public async Task RemovingHalfTheWallDuringPlaybackKeepsTheRest()
    {
        await using var engine = Engine();
        var cameras = Enumerable.Range(0, 8).Select(index => Guid.NewGuid()).ToList();

        foreach (var camera in cameras)
        {
            engine.Attach(camera, new Uri($"rtsp://127.0.0.1:9/{camera}"), null, null);
        }

        Assert.Equal(8, engine.ActiveCount);

        foreach (var camera in cameras.Take(4))
        {
            engine.Detach(camera);
        }

        Assert.Equal(4, engine.ActiveCount);
        Assert.Equal(cameras.Skip(4).OrderBy(id => id), engine.ActiveCameras.OrderBy(id => id));
    }

    [Fact]
    public async Task ShuttingDownWithActiveStreamsReleasesEverySession()
    {
        var engine = Engine();
        var cameras = Enumerable.Range(0, 8).Select(index => Guid.NewGuid()).ToList();

        foreach (var camera in cameras)
        {
            engine.Attach(camera, new Uri($"rtsp://127.0.0.1:9/{camera}"), null, null);
        }

        Assert.Equal(8, engine.ActiveCount);

        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, engine.ActiveCount);
        Assert.Empty(engine.ActiveCameras);
    }

    [Fact]
    public async Task RapidLayoutChangesKeepOneTilePerSlotAndNoDuplicateCameras()
    {
        await using var engine = Engine();
        var wall = new LiveViewModel(demo: false);
        wall.BindStreams(engine, Resolver);

        var cameras = Enumerable.Range(0, 6)
            .Select(index => Camera(Guid.NewGuid(), $"rtsp://127.0.0.1:9/{index}"))
            .ToList();

        wall.LoadCameras(cameras);

        foreach (var count in new[] { 9, 4, 16, 1, 9, 25, 4, 9 })
        {
            wall.ApplyLayout(count);
        }

        Assert.Equal(9, wall.Tiles.Count);
        Assert.True(engine.ActiveCount <= 9, $"{engine.ActiveCount} sessions for a 9-tile wall");
        Assert.All(wall.ExportWall().Where(id => id is not null).GroupBy(id => id), group => Assert.Single(group));

        engine.DetachAll();
        Assert.Equal(0, engine.ActiveCount);
    }

    [Fact]
    public async Task ATileMoveDoesNotRestartTheStream()
    {
        await using var engine = Engine();
        var wall = new LiveViewModel(demo: false);
        wall.BindStreams(engine, Resolver);

        var camera = Camera(Guid.NewGuid());
        wall.LoadCameras([camera]);

        // LoadCameras → Refresh → ApplyStreams is synchronous, so the engine state is already final here.
        var starts = engine.SessionStarts;
        Assert.Equal(1, starts);

        Assert.True(wall.PlaceCamera(camera.Id, 6));

        Assert.Equal(6, Array.FindIndex(wall.ExportWall(), id => id == camera.Id));
        Assert.Equal(starts, engine.SessionStarts);
        Assert.Equal(1, engine.ActiveCount);
    }

    [Fact]
    public async Task UndoRestoresTheTileWithoutRestartingTheStream()
    {
        await using var engine = Engine();
        var wall = new LiveViewModel(demo: false);
        wall.BindStreams(engine, Resolver);

        var camera = Camera(Guid.NewGuid());
        wall.LoadCameras([camera]);

        wall.PlaceCamera(camera.Id, 6);
        var starts = engine.SessionStarts;

        wall.UndoCommand.Execute(null);

        Assert.Equal(0, Array.FindIndex(wall.ExportWall(), id => id == camera.Id));
        Assert.Equal(starts, engine.SessionStarts);
        Assert.Equal(1, engine.ActiveCount);
    }

    [Fact]
    public async Task MinimisingTheWallStopsAndRestoringResumesDecoding()
    {
        await using var engine = Engine();
        var wall = new LiveViewModel(demo: false);
        wall.BindStreams(engine, Resolver);

        wall.LoadCameras([Camera(Guid.NewGuid())]);
        Assert.Equal(1, engine.ActiveCount);

        wall.SetWallSuspended(true);
        Assert.Equal(0, engine.ActiveCount);

        wall.SetWallSuspended(false);
        Assert.Equal(1, engine.ActiveCount);
    }

    [Fact]
    public async Task ACameraWithoutAnAddressNeverOpensASession()
    {
        await using var engine = Engine();
        var wall = new LiveViewModel(demo: false);
        wall.BindStreams(engine, Resolver);

        wall.LoadCameras([new CameraListEntry(Guid.NewGuid(), "Gate", "Test")]);

        Assert.Equal(0, engine.ActiveCount);
        Assert.Equal("NO STREAM ADDRESS", wall.Tiles[0].Detail);
    }
}
