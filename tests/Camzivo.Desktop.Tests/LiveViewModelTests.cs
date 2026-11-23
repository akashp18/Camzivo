using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

/// <summary>
/// The camera wall's rules were previously only reachable through a running window, so the shell had no automated
/// coverage at all. These tests drive the view model directly — it needs no Dispatcher, Application or database.
/// </summary>
public class LiveViewModelTests
{
    private static readonly Guid Gate = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Dock = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Yard = new("33333333-3333-3333-3333-333333333333");

    private static LiveViewModel Wall(params (Guid Id, string Name)[] cameras)
    {
        var model = new LiveViewModel(demo: false);
        model.LoadCameras(cameras.Select(camera => new CameraListEntry(camera.Id, camera.Name, "Group")));
        return model;
    }

    private static List<CameraListEntry> List(LiveViewModel model) => model.FilteredCameras.ToList();

    [Fact]
    public void CamerasArePlacedInOrderAndNeverClaimToBeLiveWithoutAnEngine()
    {
        var wall = Wall((Gate, "Gate North"), (Dock, "Dock"));

        Assert.Equal(9, wall.Tiles.Count);
        Assert.Equal(Gate, wall.Tiles[0].CameraId);
        Assert.Equal(Dock, wall.Tiles[1].CameraId);
        Assert.All(wall.Tiles.Where(tile => tile.HasCamera), tile => Assert.Equal(TileStatus.Waiting, tile.Status));
        Assert.Equal("WAITING", wall.Tiles[0].StatusText);
    }

    [Fact]
    public void DemoContentIsLabelledPreviewRatherThanLive()
    {
        var wall = new LiveViewModel(demo: true);

        Assert.All(wall.Tiles.Where(tile => tile.HasCamera), tile => Assert.Equal(TileStatus.Preview, tile.Status));
    }

    [Fact]
    public void PlacingAnAlreadyPlacedCameraMovesItInsteadOfDuplicatingIt()
    {
        var wall = Wall((Gate, "Gate North"), (Dock, "Dock"));

        wall.Assign(List(wall)[0]);

        Assert.Single(wall.ExportWall(), id => id == Gate);
        Assert.Equal(2, wall.ExportWall().Count(id => id is not null));
    }

    [Fact]
    public void ClearingTheSelectedTileFreesItsSlotAndDisablesTheCommand()
    {
        var wall = Wall((Gate, "Gate North"));
        wall.SelectSlotCommand.Execute(0);

        Assert.True(wall.CanClearSelected);
        wall.ClearSelectedCommand.Execute(null);

        Assert.False(wall.CanClearSelected);
        Assert.Null(wall.ExportWall()[0]);
        Assert.Equal(TileStatus.Empty, wall.Tiles[0].Status);
    }

    [Fact]
    public void UndoRestoresTheWallAfterAClearAndAfterAPlacement()
    {
        var wall = Wall((Gate, "Gate North"), (Dock, "Dock"));
        wall.SelectSlotCommand.Execute(0);
        wall.ClearSelectedCommand.Execute(null);

        Assert.True(wall.UndoCommand.CanExecute(null));
        wall.UndoCommand.Execute(null);
        Assert.Equal(Gate, wall.ExportWall()[0]);

        var before = wall.ExportWall();
        wall.Assign(List(wall)[1]);
        wall.UndoCommand.Execute(null);
        Assert.Equal(before, wall.ExportWall());
    }

    [Fact]
    public void TileViewModelsSurviveARefreshSoTheWallIsNotRebuilt()
    {
        var wall = Wall((Gate, "Gate North"), (Dock, "Dock"));
        var first = wall.Tiles[0];
        var ninth = wall.Tiles[8];

        wall.SelectSlotCommand.Execute(3);
        wall.Assign(List(wall)[0]);
        wall.ApplyLayout(9);

        Assert.Same(first, wall.Tiles[0]);
        Assert.Same(ninth, wall.Tiles[8]);
    }

    [Fact]
    public void ShrinkingTheLayoutDropsTilesButKeepsPlacementsForWhenItGrowsAgain()
    {
        var wall = Wall((Gate, "Gate North"), (Dock, "Dock"), (Yard, "Yard"));

        wall.ApplyLayout(4);

        Assert.Equal(4, wall.Tiles.Count);
        Assert.Equal(3, wall.ExportWall().Count(id => id is not null));

        wall.ApplyLayout(9);

        Assert.Equal(9, wall.Tiles.Count);
        Assert.Equal(3, wall.ExportWall().Count(id => id is not null));
    }

    [Fact]
    public void RestoredWallsIgnoreCamerasThatNoLongerExist()
    {
        var wall = Wall((Gate, "Gate North"));

        wall.RestoreWall([Guid.NewGuid(), Gate]);

        Assert.Null(wall.ExportWall()[0]);
        Assert.Equal(Gate, wall.ExportWall()[1]);
    }

    [Fact]
    public void ASearchWithNoMatchIsReportedInsteadOfLookingLikeAnEmptyTree()
    {
        var wall = Wall((Gate, "Gate North"));

        wall.SearchText = "zzz";

        Assert.Empty(wall.FilteredCameras);
        Assert.True(wall.HasNoMatches);

        wall.SearchText = "gate";
        Assert.Single(wall.FilteredCameras);
        Assert.False(wall.HasNoMatches);
    }

    [Fact]
    public void ThePlacedCountAndAssignmentDotsTrackPlacement()
    {
        var wall = Wall((Gate, "Gate North"), (Dock, "Dock"));

        Assert.Equal("2 of 2 placed", wall.CameraCountText);
        Assert.All(wall.FilteredCameras, entry => Assert.True(entry.IsAssigned));

        wall.ApplyLayout(1);

        Assert.Single(wall.Tiles);
        Assert.Single(wall.FilteredCameras, entry => entry.IsAssigned);
    }
}
