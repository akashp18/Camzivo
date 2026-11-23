using Camzivo.Core.Recording;
using Camzivo.Desktop.Controls;
using Xunit;

namespace Camzivo.Desktop.Tests;

/// <summary>
/// The timeline used to compute its geometry inside a converter and draw it as one Path, which is why bars could
/// disappear when the window was resized or the theme changed. The maths now lives here and is tested without WPF.
/// </summary>
public sealed class TimelineLayoutTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private static RecordingSegment Segment(int startMinute, int endMinute) => new(
        "token",
        "track",
        WindowStart.AddMinutes(startMinute),
        WindowStart.AddMinutes(endMinute));

    private static IReadOnlyList<TimelineBar> Build(params RecordingSegment[] segments) =>
        TimelineLayout.BuildBars(
            WindowStart,
            WindowStart.AddHours(1),
            segments,
            at => at.ToString("HH:mm"),
            segment => $"{segment.Duration.TotalMinutes:F0} min");

    [Fact]
    public void FractionsMapTheWindowEdgesToOne()
    {
        var bars = Build(Segment(0, 60));

        var bar = Assert.Single(bars);
        Assert.Equal(0, bar.Start, 6);
        Assert.Equal(1, bar.End, 6);
    }

    [Fact]
    public void HalfHourRecordingOccupiesHalfTheWindow()
    {
        var bar = Assert.Single(Build(Segment(15, 45)));

        Assert.Equal(0.25, bar.Start, 6);
        Assert.Equal(0.75, bar.End, 6);
    }

    [Fact]
    public void SegmentCrossingTheWindowEdgeIsClippedNotDropped()
    {
        var bar = Assert.Single(Build(Segment(-30, 30)));

        Assert.Equal(0, bar.Start, 6);
        Assert.Equal(0.5, bar.End, 6);
    }

    [Fact]
    public void SegmentEntirelyOutsideTheWindowIsOmitted()
    {
        Assert.Empty(Build(Segment(90, 120)));
    }

    [Fact]
    public void ZeroWidthRecordingIsOmitted()
    {
        Assert.Empty(Build(Segment(20, 20)));
    }

    [Fact]
    public void InvalidWindowProducesNoBarsAndNoFraction()
    {
        var end = WindowStart;

        Assert.Empty(TimelineLayout.BuildBars(WindowStart, end, [Segment(0, 10)], _ => "", _ => ""));
        Assert.True(double.IsNaN(TimelineLayout.FractionOf(WindowStart, WindowStart, end)));
    }

    [Fact]
    public void BarsAreOrderedByStartTime()
    {
        var bars = Build(Segment(40, 50), Segment(10, 20));

        Assert.Equal(0.1667, bars[0].Start, 4);
        Assert.Equal(0.6667, bars[1].Start, 4);
    }

    [Fact]
    public void HitTestReturnsTheBarUnderThePointerAndNullForAGap()
    {
        var bars = Build(Segment(10, 20), Segment(30, 40));

        Assert.NotNull(TimelineLayout.HitTest(bars, 0.25));
        Assert.Null(TimelineLayout.HitTest(bars, 0.45));
        Assert.NotNull(TimelineLayout.HitTest(bars, 0.2));
    }

    [Fact]
    public void HitTestTreatsASharedEdgeAsPartOfARecording()
    {
        var bars = Build(Segment(10, 20), Segment(20, 30));

        // The end of the first bar and the start of the second both map to the same instant; either answer is fine as
        // long as a click on a recording never lands on "no recording".
        Assert.NotNull(TimelineLayout.HitTest(bars, 20.0 / 60.0));
    }

    [Fact]
    public void TicksCoverTheWindowAndOnlyWholeHoursCarryLabels()
    {
        var ticks = TimelineLayout.BuildTicks(new DateTime(2026, 11, 22, 8, 0, 0), 1);

        Assert.Equal(13, ticks.Count);
        Assert.Equal(0, ticks[0].Fraction);
        Assert.Equal(1, ticks[^1].Fraction);
        Assert.Equal("08:00", ticks[0].Label);
        Assert.All(ticks.Where(tick => !tick.IsMajor), tick => Assert.Empty(tick.Label));
    }

    [Fact]
    public void TickStepGrowsWithTheWindow()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), TimelineLayout.TickStep(0.5));
        Assert.Equal(TimeSpan.FromMinutes(15), TimelineLayout.TickStep(3));
        Assert.Equal(TimeSpan.FromHours(2), TimelineLayout.TickStep(24));
    }

    [Fact]
    public void TimeAtMapsFractionsBackToInstantsAndClamps()
    {
        var end = WindowStart.AddHours(2);

        Assert.Equal(WindowStart.AddMinutes(30), TimelineLayout.TimeAt(0.25, WindowStart, end));
        Assert.Equal(WindowStart, TimelineLayout.TimeAt(-5, WindowStart, end));
        Assert.Equal(end, TimelineLayout.TimeAt(5, WindowStart, end));
    }

    [Theory]
    [InlineData(45, "0:45")]
    [InlineData(60, "1:00")]
    [InlineData(90, "1:30")]
    [InlineData(5400, "1:30:00")]
    [InlineData(3661, "1:01:01")]
    public void DurationTextIsCompactAndUnambiguous(int seconds, string expected)
    {
        var segment = new RecordingSegment(
            "token",
            "track",
            WindowStart,
            WindowStart.AddSeconds(seconds));

        var bar = new TimelineBar(segment, 0, 1, "", "");

        Assert.Equal(expected, bar.DurationText);
    }
}
