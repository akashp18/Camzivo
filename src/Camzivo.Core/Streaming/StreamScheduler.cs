namespace Camzivo.Core.Streaming;

/// <summary>What a tile is asking for right now.</summary>
/// <param name="CameraId">Stable identity; also the deterministic tie-breaker.</param>
/// <param name="IsVisible">Occluded, collapsed or off-screen tiles are never decoded.</param>
/// <param name="IsFullScreen">Promoted tiles outrank everything else.</param>
/// <param name="TileArea">Pixel area, used to prefer large tiles when priorities tie.</param>
/// <param name="LastInteractedTicks">Monotonic tick of the operator's last interaction with this tile.</param>
public sealed record TileDemand(
    Guid CameraId,
    bool IsVisible,
    bool IsFullScreen,
    int TileArea,
    long LastInteractedTicks);

public sealed record SchedulingPlan(IReadOnlyList<Guid> Active, IReadOnlyList<Guid> Suspended)
{
    public bool IsActive(Guid cameraId) => Active.Contains(cameraId);
}

/// <summary>
/// Allocates a fixed decode budget across tiles. This is what makes 49/64-tile walls survivable on weak
/// hardware: the degradation order is defined and explainable instead of random.
/// </summary>
public static class StreamScheduler
{
    public static SchedulingPlan Plan(IReadOnlyList<TileDemand> tiles, int decodeBudget)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        if (decodeBudget < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeBudget), decodeBudget, "Decode budget cannot be negative.");
        }

        // Priority first, then one demand per camera. Deduplicating before filtering visibility let a hidden
        // duplicate shadow the visible tile for the same camera, which suspended a camera that was on screen.
        var ordered = tiles
            .Where(t => t is not null && t.IsVisible)
            .OrderByDescending(t => t.IsFullScreen)
            .ThenByDescending(t => t.LastInteractedTicks)
            .ThenByDescending(t => t.TileArea)
            .ThenBy(t => t.CameraId)
            .ToList();

        var seen = new HashSet<Guid>();
        var candidates = new List<TileDemand>(ordered.Count);
        foreach (var demand in ordered)
        {
            if (seen.Add(demand.CameraId))
            {
                candidates.Add(demand);
            }
        }

        var active = new List<Guid>(Math.Min(decodeBudget, candidates.Count));
        var suspended = new List<Guid>(tiles.Count);

        foreach (var candidate in candidates)
        {
            if (active.Count < decodeBudget)
            {
                active.Add(candidate.CameraId);
            }
            else
            {
                suspended.Add(candidate.CameraId);
            }
        }

        foreach (var hidden in tiles.Where(t => t is not null && !t.IsVisible).Select(t => t.CameraId))
        {
            if (!active.Contains(hidden) && !suspended.Contains(hidden))
            {
                suspended.Add(hidden);
            }
        }

        return new SchedulingPlan(active, suspended);
    }
}
