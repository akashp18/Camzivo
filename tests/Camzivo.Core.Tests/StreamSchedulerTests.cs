using Camzivo.Core.Streaming;
using Xunit;

namespace Camzivo.Core.Tests;

public class StreamSchedulerTests
{
    private static TileDemand Tile(int seed, bool visible = true, bool full = false, int area = 320 * 180, long ticks = 0) =>
        new(new Guid(seed, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]), visible, full, area, ticks);

    [Fact]
    public void ActiveCountNeverExceedsTheBudget()
    {
        var tiles = Enumerable.Range(1, 9).Select(seed => Tile(seed)).ToList();

        var plan = StreamScheduler.Plan(tiles, decodeBudget: 4);

        Assert.Equal(4, plan.Active.Count);
        Assert.Equal(5, plan.Suspended.Count);
    }

    [Fact]
    public void FullscreenOutranksEverythingElseEvenTheNewestInteraction()
    {
        var fullscreen = Tile(9, full: true);
        var interacted = Tile(1, ticks: long.MaxValue);

        var plan = StreamScheduler.Plan([interacted, fullscreen], decodeBudget: 1);

        Assert.Equal(fullscreen.CameraId, Assert.Single(plan.Active));
        Assert.Equal(interacted.CameraId, Assert.Single(plan.Suspended));
    }

    [Fact]
    public void HiddenTilesAreSuspendedAndDoNotConsumeTheBudget()
    {
        var visible = Tile(1);
        var hidden = Tile(2, visible: false);

        var plan = StreamScheduler.Plan([visible, hidden], decodeBudget: 5);

        Assert.Equal(visible.CameraId, Assert.Single(plan.Active));
        Assert.Contains(hidden.CameraId, plan.Suspended);
    }

    [Fact]
    public void RecentlyInteractedTilesWinWithinTheSamePriority()
    {
        var older = Tile(1, ticks: 10);
        var newer = Tile(2, ticks: 20);

        var plan = StreamScheduler.Plan([older, newer], decodeBudget: 1);

        Assert.Equal(newer.CameraId, Assert.Single(plan.Active));
    }

    [Fact]
    public void ZeroBudgetSuspendsEverything()
    {
        var plan = StreamScheduler.Plan([Tile(1), Tile(2)], decodeBudget: 0);

        Assert.Empty(plan.Active);
        Assert.Equal(2, plan.Suspended.Count);
    }

    [Fact]
    public void OrderingIsStableAcrossRuns()
    {
        var tiles = Enumerable.Range(1, 20).Select(seed => Tile(seed)).ToList();

        var first = StreamScheduler.Plan(tiles, 8);
        var second = StreamScheduler.Plan(tiles.AsEnumerable().Reverse().ToList(), 8);

        Assert.Equal(first.Active, second.Active);
    }

    [Fact]
    public void AHiddenDuplicateMustNotShadowTheVisibleTileForTheSameCamera()
    {
        var hidden = Tile(1, visible: false);
        var visible = Tile(1);

        var plan = StreamScheduler.Plan([hidden, visible], decodeBudget: 4);

        Assert.Equal(visible.CameraId, Assert.Single(plan.Active));
        Assert.Empty(plan.Suspended);
    }

    [Fact]
    public void DuplicateDemandsForOneCameraAreCollapsed()
    {
        var camera = Tile(1);
        var duplicate = camera with { TileArea = 1920 * 1080 };

        var plan = StreamScheduler.Plan([camera, duplicate], decodeBudget: 4);

        Assert.Single(plan.Active);
        Assert.Empty(plan.Suspended);
    }

    [Fact]
    public void NegativeBudgetIsAProgrammingError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamScheduler.Plan([Tile(1)], -1));
    }

    [Fact]
    public void LargerTilesArePreferredWhenEverythingElseTies()
    {
        var small = Tile(1, area: 320 * 180);
        var large = Tile(2, area: 960 * 540);

        var plan = StreamScheduler.Plan([small, large], decodeBudget: 1);

        Assert.Equal(large.CameraId, Assert.Single(plan.Active));
    }
}
