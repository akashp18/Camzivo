using System.Globalization;

namespace Camzivo.Desktop.Controls;

/// <summary>One cell in the date field's month grid.</summary>
public sealed record DateCell(DateTime Date, bool IsCurrentMonth, bool IsToday, bool IsSelected)
{
    public int DayNumber => Date.Day;
}

/// <summary>
/// Month grid for the date field. Weeks start on Monday (ISO), the grid is always six rows so the popup never changes
/// height as months change, and cells outside the month are shown muted rather than left blank.
/// </summary>
public static class CalendarMonth
{
    public const int Cells = 42;

    public static DateTime MonthStart(DateTime anyDayInMonth) => new(anyDayInMonth.Year, anyDayInMonth.Month, 1);

    /// <summary>Monday-based start of the week containing <paramref name="day"/>.</summary>
    public static DateTime StartOfWeek(DateTime day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7;
        return day.Date.AddDays(-offset);
    }

    public static string Title(DateTime monthContaining) =>
        MonthStart(monthContaining).ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    public static IReadOnlyList<DateCell> Build(DateTime monthContaining, DateTime selected, DateTime today)
    {
        var start = MonthStart(monthContaining);
        var gridStart = StartOfWeek(start);
        var selectedDate = selected.Date;
        var todayDate = today.Date;
        var cells = new List<DateCell>(Cells);

        for (var index = 0; index < Cells; index++)
        {
            var date = gridStart.AddDays(index);

            cells.Add(new DateCell(
                date,
                date.Month == start.Month && date.Year == start.Year,
                date == todayDate,
                date == selectedDate));
        }

        return cells;
    }

    public static IReadOnlyList<string> WeekdayHeaders() =>
        ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
}
