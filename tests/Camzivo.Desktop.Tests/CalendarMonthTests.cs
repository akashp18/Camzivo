using Camzivo.Desktop.Controls;
using Xunit;

namespace Camzivo.Desktop.Tests;

public sealed class CalendarMonthTests
{
    [Fact]
    public void GridAlwaysHasSixWeeksSoThePopupDoesNotJump()
    {
        Assert.Equal(42, CalendarMonth.Build(new DateTime(2026, 2, 1), DateTime.MinValue, DateTime.MinValue).Count);
        Assert.Equal(42, CalendarMonth.Build(new DateTime(2026, 11, 1), DateTime.MinValue, DateTime.MinValue).Count);
    }

    [Fact]
    public void WeekStartsOnMonday()
    {
        // 1 November 2026 is a Sunday, so the grid starts on Monday 26 October.
        var cells = CalendarMonth.Build(new DateTime(2026, 11, 1), DateTime.MinValue, DateTime.MinValue);

        Assert.Equal(new DateTime(2026, 10, 26), cells[0].Date);
        Assert.Equal(DayOfWeek.Monday, cells[0].Date.DayOfWeek);
    }

    [Fact]
    public void LeadingAndTrailingCellsAreMarkedAsOutsideTheMonth()
    {
        var cells = CalendarMonth.Build(new DateTime(2026, 11, 1), DateTime.MinValue, DateTime.MinValue);

        Assert.False(cells[0].IsCurrentMonth);
        Assert.True(cells[6].IsCurrentMonth); // 1 November
        Assert.True(cells[6].IsSelected is false);
    }

    [Fact]
    public void TodayAndSelectionAreIndependent()
    {
        var today = new DateTime(2026, 11, 22);
        var selected = new DateTime(2026, 11, 20);

        var cells = CalendarMonth.Build(new DateTime(2026, 11, 1), selected, today);

        Assert.True(cells.Single(cell => cell.Date == today).IsToday);
        Assert.False(cells.Single(cell => cell.Date == today).IsSelected);
        Assert.True(cells.Single(cell => cell.Date == selected).IsSelected);
        Assert.False(cells.Single(cell => cell.Date == selected).IsToday);
    }

    [Fact]
    public void LeapDayExistsInFebruary2028()
    {
        var cells = CalendarMonth.Build(new DateTime(2028, 2, 10), DateTime.MinValue, DateTime.MinValue);

        Assert.Contains(cells, cell => cell.Date == new DateTime(2028, 2, 29) && cell.IsCurrentMonth);
    }

    [Fact]
    public void MonthStartIgnoresTimeOfDay()
    {
        Assert.Equal(new DateTime(2026, 11, 1), CalendarMonth.MonthStart(new DateTime(2026, 11, 22, 17, 45, 3)));
    }

    [Fact]
    public void WeekdayHeadersStartOnMonday()
    {
        var headers = CalendarMonth.WeekdayHeaders();

        Assert.Equal(7, headers.Count);
        Assert.Equal("Mo", headers[0]);
        Assert.Equal("Su", headers[6]);
    }
}
