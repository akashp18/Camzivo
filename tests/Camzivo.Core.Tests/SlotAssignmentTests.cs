using Camzivo.Core.Layout;
using Xunit;

namespace Camzivo.Core.Tests;

public class SlotAssignmentTests
{
    private static readonly Guid Camera = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void FirstEmptySlotWins()
    {
        Guid?[] slots = [Camera, null, Camera, null];

        Assert.Equal(1, SlotAssignment.NextTarget(slots, selectedSlot: 3));
    }

    [Fact]
    public void AFullWallReplacesTheSelectedSlot()
    {
        Guid?[] slots = [Camera, Camera, Camera];

        Assert.Equal(2, SlotAssignment.NextTarget(slots, selectedSlot: 2));
    }

    [Fact]
    public void SelectedSlotIsClampedIntoRange()
    {
        Guid?[] slots = [Camera, Camera];

        Assert.Equal(0, SlotAssignment.NextTarget(slots, selectedSlot: -5));
        Assert.Equal(1, SlotAssignment.NextTarget(slots, selectedSlot: 99));
    }

    [Fact]
    public void AnEmptyWallHasNoTarget()
    {
        Assert.Equal(SlotAssignment.NoSlot, SlotAssignment.NextTarget([], selectedSlot: 0));
    }

    [Fact]
    public void AnAlreadyPlacedCameraIsFoundSoItCanBeMoved()
    {
        Guid?[] slots = [null, Other, Camera];

        Assert.Equal(2, SlotAssignment.ExistingSlot(slots, Camera));
        Assert.Equal(SlotAssignment.NoSlot, SlotAssignment.ExistingSlot(slots, Guid.NewGuid()));
        Assert.Equal(SlotAssignment.NoSlot, SlotAssignment.ExistingSlot([], Camera));
    }
}
