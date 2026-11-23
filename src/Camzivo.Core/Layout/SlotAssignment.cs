namespace Camzivo.Core.Layout;

/// <summary>
/// Placement rule for the camera wall, extracted from the view-model so the interaction is unit-testable:
/// a clicked camera goes to the first empty slot; if the wall is full it replaces the selected slot, so a
/// full wall is a deliberate replacement rather than a silent no-op.
/// </summary>
public static class SlotAssignment
{
    public const int NoSlot = -1;

    public static int NextTarget(IReadOnlyList<Guid?> slots, int selectedSlot)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count == 0)
        {
            return NoSlot;
        }

        for (var index = 0; index < slots.Count; index++)
        {
            if (slots[index] is null)
            {
                return index;
            }
        }

        return Math.Clamp(selectedSlot, 0, slots.Count - 1);
    }

    /// <summary>
    /// Slot already showing this camera, or <see cref="NoSlot"/>. Placing a camera that is already on the wall must
    /// move it rather than show it twice: the scheduler allocates one decoder per camera, so a duplicate tile would
    /// report a state it does not own.
    /// </summary>
    public static int ExistingSlot(IReadOnlyList<Guid?> slots, Guid cameraId)
    {
        ArgumentNullException.ThrowIfNull(slots);

        for (var index = 0; index < slots.Count; index++)
        {
            if (slots[index] == cameraId)
            {
                return index;
            }
        }

        return NoSlot;
    }
}
