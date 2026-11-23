using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Camzivo.Desktop.Controls;

/// <summary>
/// A date field that owns its calendar popup.
///
/// The stock WPF DatePicker brings a system-themed calendar whose chrome cannot follow the application tokens, which is
/// how a white popup appeared over a dark application. This control builds a month grid from
/// <see cref="CalendarMonth"/> and styles every cell with the shared component library.
/// </summary>
public partial class DateField : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate),
        typeof(DateTime),
        typeof(DateField),
        new FrameworkPropertyMetadata(
            DateTime.Today,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (dependencyObject, _) => ((DateField)dependencyObject).Refresh()));

    private readonly ObservableCollection<DateCell> _days = [];
    private DateTime _month;

    public DateField()
    {
        InitializeComponent();

        _month = CalendarMonth.MonthStart(SelectedDate);
        Weekdays.ItemsSource = CalendarMonth.WeekdayHeaders();
        Days.ItemsSource = _days;

        Refresh();
    }

    public DateTime SelectedDate
    {
        get => (DateTime)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    private void Refresh()
    {
        if (!IsInitialized)
        {
            return;
        }

        FieldText.Text = SelectedDate.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
        MonthTitle.Text = CalendarMonth.Title(_month);

        _days.Clear();
        foreach (var cell in CalendarMonth.Build(_month, SelectedDate, DateTime.Today))
        {
            _days.Add(cell);
        }
    }

    private void Field_Click(object sender, RoutedEventArgs e)
    {
        _month = CalendarMonth.MonthStart(SelectedDate);
        Refresh();
        MonthPopup.IsOpen = !MonthPopup.IsOpen;
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        Refresh();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        Refresh();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        SelectedDate = DateTime.Today;
        _month = CalendarMonth.MonthStart(SelectedDate);
        MonthPopup.IsOpen = false;
        Refresh();
    }

    private void Day_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DateCell cell })
        {
            SelectedDate = cell.Date;
            MonthPopup.IsOpen = false;
            Refresh();
        }
    }
}
