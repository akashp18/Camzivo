using Camzivo.Core.Devices;
using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

/// <summary>
/// Tile placement rules used by drag-and-drop: move, swap, replace, duplicate prevention, undo and layout changes.
/// The drop handler in the window only reports a slot; every rule lives here so it can be tested without a mouse.
/// </summary>
public sealed class CameraDragPlacementTests
{
    private static readonly Guid Gate = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Dock = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Yard = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static LiveViewModel Wall(params (Guid Id, string Name)[] cameras)
    {
        var wall = new LiveViewModel(demo: false);
        wall.LoadCameras(cameras.Select(camera => new CameraListEntry(camera.Id, camera.Name, "Test")));
        return wall;
    }

    private static int SlotOf(LiveViewModel wall, Guid cameraId) =>
        Array.FindIndex(wall.ExportWall(), id => id == cameraId);

    [Fact]
    public void DroppingACameraOnAnEmptyTilePlacesIt()
    {
        var wall = Wall((Gate, "Gate"));

        Assert.True(wall.PlaceCamera(Gate, 4));

        Assert.Equal(4, SlotOf(wall, Gate));
        Assert.Equal(4, wall.SelectedSlot);
    }

    [Fact]
    public void DroppingAPlacedCameraOnAnotherEmptyTileMovesIt()
    {
        var wall = Wall((Gate, "Gate"), (Dock, "Dock"));
        wall.PlaceCamera(Gate, 0);

        Assert.True(wall.PlaceCamera(Gate, 5));

        Assert.Equal(5, SlotOf(wall, Gate));
        Assert.Equal(1, SlotOf(wall, Dock));
        Assert.Single(wall.ExportWall(), id => id == Gate);
    }

    [Fact]
    public void DroppingOntoAnOccupiedTileSwapsTheTwoCameras()
    {
        var wall = Wall((Gate, "Gate"), (Dock, "Dock"));
        wall.PlaceCamera(Gate, 0);
        wall.PlaceCamera(Dock, 1);

        Assert.True(wall.PlaceCamera(Gate, 1));

        Assert.Equal(1, SlotOf(wall, Gate));
        Assert.Equal(0, SlotOf(wall, Dock));
    }

    [Fact]
    public void DroppingAnUnplacedCameraOnAnOccupiedTileReplacesTheOccupant()
    {
        var wall = Wall((Gate, "Gate"), (Dock, "Dock"));

        // Empty the tile Dock sits on, so Dock is in the list but not on the wall.
        wall.SelectSlotCommand.Execute("1");
        wall.ClearSelectedCommand.Execute(null);

        Assert.True(wall.PlaceCamera(Dock, 0));

        Assert.Equal(0, SlotOf(wall, Dock));
        Assert.Equal(-1, SlotOf(wall, Gate));
        Assert.DoesNotContain(Gate, wall.ExportWall());
    }

    [Fact]
    public void ACameraCanNeverOccupyTwoTiles()
    {
        var wall = Wall((Gate, "Gate"));
        wall.PlaceCamera(Gate, 0);

        wall.PlaceCamera(Gate, 3);

        Assert.Single(wall.ExportWall(), id => id == Gate);
    }

    [Fact]
    public void DroppingWhereNothingWouldChangeIsIgnored()
    {
        var wall = Wall((Gate, "Gate"));
        wall.PlaceCamera(Gate, 2);

        Assert.False(wall.PlaceCamera(Gate, 2));
    }

    [Fact]
    public void ADropWithNoChangeDoesNotPolluteUndoHistory()
    {
        var wall = Wall((Gate, "Gate"));
        wall.PlaceCamera(Gate, 2);
        wall.PlaceCamera(Gate, 5);

        // One move back should land on slot 2, not on an identical intermediate snapshot.
        wall.UndoCommand.Execute(null);

        Assert.Equal(2, SlotOf(wall, Gate));
    }

    [Fact]
    public void UndoRestoresThePreviousWall()
    {
        var wall = Wall((Gate, "Gate"), (Dock, "Dock"));
        wall.PlaceCamera(Gate, 0);
        wall.PlaceCamera(Dock, 1);
        wall.PlaceCamera(Dock, 0);

        wall.UndoCommand.Execute(null);

        Assert.Equal(0, SlotOf(wall, Gate));
        Assert.Equal(1, SlotOf(wall, Dock));
    }

    [Fact]
    public void ADeviceRemovedDuringADragIsAnInvalidDrop()
    {
        var wall = Wall((Gate, "Gate"));
        var removed = Gate;

        wall.LoadCameras([]); // the device disappears while the drag is in flight

        Assert.False(wall.PlaceCamera(removed, 0));
        Assert.DoesNotContain(removed, wall.ExportWall());
    }

    [Fact]
    public void PlacementSurvivesALayoutChangeThatShrinksTheWall()
    {
        var wall = Wall((Gate, "Gate"));
        wall.PlaceCamera(Gate, 8);

        wall.ApplyLayout(4);
        wall.ApplyLayout(9);

        Assert.Equal(8, SlotOf(wall, Gate));
    }

    [Fact]
    public void RapidRepeatedDropsKeepOneSlotPerCameraAndRemainUndoable()
    {
        var wall = Wall((Gate, "Gate"), (Dock, "Dock"), (Yard, "Yard"));
        var random = new Random(7);

        for (var index = 0; index < 200; index++)
        {
            var camera = new[] { Gate, Dock, Yard }[random.Next(3)];
            wall.PlaceCamera(camera, random.Next(9));
        }

        foreach (var camera in new[] { Gate, Dock, Yard })
        {
            Assert.True(wall.ExportWall().Count(id => id == camera) <= 1, "a camera appeared on two tiles");
        }

        wall.UndoCommand.Execute(null);
        Assert.True(wall.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void ClickAssignmentStillUsesTheFirstEmptySlot()
    {
        var wall = new LiveViewModel(demo: false);

        wall.AssignCommand.Execute(new CameraListEntry(Gate, "Gate", "Test"));
        wall.AssignCommand.Execute(new CameraListEntry(Dock, "Dock", "Test"));

        Assert.Equal(0, SlotOf(wall, Gate));
        Assert.Equal(1, SlotOf(wall, Dock));
    }
}
