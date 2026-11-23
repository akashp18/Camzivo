using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

public sealed class HealthViewModelTests
{
    private static CameraTileViewModel Tile(TileStatus status, string detail) =>
        new(0, Guid.NewGuid(), "Gate North", status, "MAIN", detail);

    [Fact]
    public void OfflineCameraIsCriticalAndKeepsTheEngineDetail()
    {
        var health = new HealthViewModel(demo: false);

        health.LoadFromTiles([Tile(TileStatus.Offline, "Connection refused")]);

        var row = Assert.Single(health.Rows);
        Assert.True(row.IsCritical);
        Assert.Equal("Gate North", row.Camera);
        Assert.Equal("Offline", row.Code);
        Assert.Equal("Connection refused", row.Explanation);
    }

    [Fact]
    public void QueuedTileIsAWarningNotAFailure()
    {
        var health = new HealthViewModel(demo: false);

        health.LoadFromTiles([Tile(TileStatus.Queued, "WAITING FOR DECODE SLOT")]);

        var row = Assert.Single(health.Rows);
        Assert.False(row.IsCritical);
        Assert.Equal("Queued", row.Code);
    }

    [Fact]
    public void HealthyWallReportsNoFaults()
    {
        var health = new HealthViewModel(demo: false);

        health.LoadFromTiles([Tile(TileStatus.Live, "LIVE"), Tile(TileStatus.Empty, string.Empty)]);

        Assert.Empty(health.Rows);
        Assert.False(health.HasRows);
        Assert.Equal("No active faults", health.Summary);
    }
}
