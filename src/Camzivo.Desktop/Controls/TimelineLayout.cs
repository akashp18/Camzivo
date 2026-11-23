using Camzivo.Core.Recording;

namespace Camzivo.Desktop.Controls;

/// <summary>
/// One recording bar on the timeline. Start and End are fractions of the visible window (0..1), so resizing the
/// control never requires recomputing the model.
/// </summary>
public sealed record TimelineBar(
    RecordingSegment Segment,
    double Start,
    double End,
    string Label,
    string ToolTip)
{
    /// <summary>Compact duration for the segment list (m:ss, or h:mm:ss for long recordings).</summary>
    public string DurationText => Segment.Duration.TotalHours >= 1
        ? $"{(int)Segment.Duration.TotalHours}:{Segment.Duration.Minutes:00}:{Segment.Duration.Seconds:00}"
        : $"{Segment.Duration.Minutes}:{Segment.Duration.Seconds:00}";
}

/// <summary>A ruler tick: label plus its position in the visible window.</summary>
public sealed record TimelineTick(string Label, double Fraction, bool IsMajor);

/// <summary>
/// Deterministic timeline geometry. Kept free of WPF types so it can be tested without a dispatcher or a window.
/// </summary>
public static class TimelineLayout
{
    /// <summary>Fraction of the window for an instant; clamped, and NaN for an invalid window.</summary>
    public static double FractionOf(DateTimeOffset instantUtc, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc)
    {
        var span = (windowEndUtc - windowStartUtc).TotalSeconds;

        if (span <= 0)
        {
            return double.NaN;
        }

        return Math.Clamp((instantUtc - windowStartUtc).TotalSeconds / span, 0, 1);
    }

    /// <summary>
    /// Bars for every segment that intersects the window, clipped to it. A segment entirely outside the window is
    /// omitted; a segment that crosses an edge is clamped rather than dropped.
    /// </summary>
    public static IReadOnlyList<TimelineBar> BuildBars(
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        IEnumerable<RecordingSegment> segments,
        Func<DateTimeOffset, string> label,
        Func<RecordingSegment, string> tooltip)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(tooltip);

        var bars = new List<TimelineBar>();

        if (windowEndUtc <= windowStartUtc)
        {
            return bars;
        }

        foreach (var segment in segments.OrderBy(segment => segment.StartUtc))
        {
            var start = segment.StartUtc < windowStartUtc ? windowStartUtc : segment.StartUtc;
            var end = segment.EndUtc > windowEndUtc ? windowEndUtc : segment.EndUtc;

            if (end <= start)
            {
                continue;
            }

            bars.Add(new TimelineBar(
                segment,
                FractionOf(start, windowStartUtc, windowEndUtc),
                FractionOf(end, windowStartUtc, windowEndUtc),
                label(start),
                tooltip(segment)));
        }

        return bars;
    }

    /// <summary>Ruler spacing for a window width. Chosen so labels never overlap at the supported zoom range.</summary>
    public static TimeSpan TickStep(double windowHours) => windowHours switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        <= 3 => TimeSpan.FromMinutes(15),
        <= 6 => TimeSpan.FromMinutes(30),
        <= 12 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(2),
    };

    /// <summary>Ruler ticks across the visible window, in local time. Always includes both edges.</summary>
    public static IReadOnlyList<TimelineTick> BuildTicks(DateTime windowStartLocal, double windowHours)
    {
        var ticks = new List<TimelineTick>();

        if (windowHours <= 0)
        {
            return ticks;
        }

        var step = TickStep(windowHours);
        var end = windowStartLocal.AddHours(windowHours);

        for (var at = windowStartLocal; at <= end; at = at.Add(step))
        {
            var fraction = (at - windowStartLocal).TotalHours / windowHours;
            // Only whole hours carry a label; a 5-minute window would otherwise be a wall of overlapping numbers.
            var isMajor = at.Minute == 0 && at.Second == 0;

            ticks.Add(new TimelineTick(isMajor ? at.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture) : string.Empty, Math.Clamp(fraction, 0, 1), isMajor));
        }

        return ticks;
    }

    /// <summary>
    /// The segment under a click, or null when the click landed on a gap. Clicking a gap must not select a recording
    /// that is not there.
    /// </summary>
    public static TimelineBar? HitTest(IReadOnlyList<TimelineBar> bars, double fraction) =>
        bars.FirstOrDefault(bar => fraction >= bar.Start && fraction <= bar.End);

    /// <summary>Time at a fraction of the window; used to seek when the operator clicks the timeline.</summary>
    public static DateTimeOffset TimeAt(double fraction, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc) =>
        windowStartUtc + TimeSpan.FromSeconds(
            (windowEndUtc - windowStartUtc).TotalSeconds * Math.Clamp(fraction, 0, 1));
}
