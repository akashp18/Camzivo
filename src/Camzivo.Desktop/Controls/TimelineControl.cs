using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Camzivo.Desktop.Controls;

/// <summary>
/// The recording timeline, drawn directly with <see cref="DrawingContext"/>.
///
/// A single retained <c>Path</c> geometry replaced by a binding was fragile: the visual could survive a data update
/// while rendering nothing (observed as recording blocks disappearing once playback started), and every zoom rebuilt
/// a Freezable graph. Drawing is deterministic — one render pass reads the current bars, ticks and playhead — and the
/// geometry maths lives in <see cref="TimelineLayout"/> where it is tested without a window.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double RulerHeight = 18;
    private const double BarTop = 22;
    private const double BottomGap = 6;

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IEnumerable<TimelineBar>), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TicksProperty = DependencyProperty.Register(
        nameof(Ticks), typeof(IEnumerable<TimelineTick>), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadFractionProperty = DependencyProperty.Register(
        nameof(PlayheadFraction), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedTokenProperty = DependencyProperty.Register(
        nameof(SelectedToken), typeof(string), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(
        nameof(SelectCommand), typeof(ICommand), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(TimelineControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = BrushProperty(nameof(TrackBrush));
    public static readonly DependencyProperty SegmentBrushProperty = BrushProperty(nameof(SegmentBrush));
    public static readonly DependencyProperty SegmentHoverBrushProperty = BrushProperty(nameof(SegmentHoverBrush));
    public static readonly DependencyProperty SegmentSelectedBrushProperty = BrushProperty(nameof(SegmentSelectedBrush));
    public static readonly DependencyProperty PlayheadBrushProperty = BrushProperty(nameof(PlayheadBrush));
    public static readonly DependencyProperty TickBrushProperty = BrushProperty(nameof(TickBrush));
    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(nameof(LabelBrush));
    public static readonly DependencyProperty EmptyBrushProperty = BrushProperty(nameof(EmptyBrush));
    public static readonly DependencyProperty AccentBrushProperty = BrushProperty(nameof(AccentBrush));

    private TimelineBar? _hovered;

    public TimelineControl()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    public IEnumerable<TimelineBar>? Bars
    {
        get => (IEnumerable<TimelineBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public IEnumerable<TimelineTick>? Ticks
    {
        get => (IEnumerable<TimelineTick>?)GetValue(TicksProperty);
        set => SetValue(TicksProperty, value);
    }

    public double PlayheadFraction
    {
        get => (double)GetValue(PlayheadFractionProperty);
        set => SetValue(PlayheadFractionProperty, value);
    }

    public string? SelectedToken
    {
        get => (string?)GetValue(SelectedTokenProperty);
        set => SetValue(SelectedTokenProperty, value);
    }

    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? SegmentBrush
    {
        get => (Brush?)GetValue(SegmentBrushProperty);
        set => SetValue(SegmentBrushProperty, value);
    }

    public Brush? SegmentHoverBrush
    {
        get => (Brush?)GetValue(SegmentHoverBrushProperty);
        set => SetValue(SegmentHoverBrushProperty, value);
    }

    public Brush? SegmentSelectedBrush
    {
        get => (Brush?)GetValue(SegmentSelectedBrushProperty);
        set => SetValue(SegmentSelectedBrushProperty, value);
    }

    public Brush? PlayheadBrush
    {
        get => (Brush?)GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public Brush? TickBrush
    {
        get => (Brush?)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush? EmptyBrush
    {
        get => (Brush?)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        // A transparent full-surface rectangle makes the whole control hit-testable, including gaps.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var barArea = new Rect(0, BarTop, width, Math.Max(8, height - BarTop - BottomGap));
        drawingContext.DrawRoundedRectangle(TrackBrush, null, barArea, 3, 3);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var ticks = Ticks?.ToList() ?? [];

        foreach (var tick in ticks)
        {
            var x = Math.Round(tick.Fraction * width) + 0.5;

            drawingContext.DrawLine(
                new Pen(TickBrush ?? Brushes.Gray, 1),
                new Point(x, tick.IsMajor ? BarTop : BarTop - 4),
                new Point(x, tick.IsMajor ? barArea.Bottom : BarTop));

            if (string.IsNullOrEmpty(tick.Label))
            {
                continue;
            }

            var text = new FormattedText(
                tick.Label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                10.5,
                LabelBrush ?? Brushes.Gray,
                dpi);

            var left = Math.Clamp(x - (text.Width / 2), 0, Math.Max(0, width - text.Width));
            drawingContext.DrawText(text, new Point(left, 1));
        }

        var bars = Bars?.ToList() ?? [];

        foreach (var bar in bars)
        {
            var left = bar.Start * width;
            var right = bar.End * width;
            var barWidth = Math.Max(3, right - left);

            if (left >= width)
            {
                continue;
            }

            var isSelected = !string.IsNullOrEmpty(SelectedToken) &&
                             string.Equals(bar.Segment.RecordingToken, SelectedToken, StringComparison.Ordinal);

            var fill = isSelected
                ? SegmentSelectedBrush
                : ReferenceEquals(bar, _hovered)
                    ? SegmentHoverBrush
                    : SegmentBrush;

            var rect = new Rect(left, barArea.Top, Math.Min(barWidth, width - left), barArea.Height);
            drawingContext.DrawRoundedRectangle(fill, null, rect, 3, 3);

            if (isSelected)
            {
                drawingContext.DrawRoundedRectangle(
                    null,
                    new Pen(LabelBrush ?? Brushes.White, 1.5),
                    new Rect(rect.X + 0.75, rect.Y + 0.75, Math.Max(0, rect.Width - 1.5), Math.Max(0, rect.Height - 1.5)),
                    3,
                    3);
            }

            if (rect.Width < 58 || barArea.Height < 16)
            {
                continue;
            }

            var label = new FormattedText(
                bar.Label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                10.5,
                Brushes.White,
                dpi)
            {
                MaxTextWidth = Math.Max(4, rect.Width - 10),
                Trimming = TextTrimming.CharacterEllipsis,
            };

            drawingContext.DrawText(
                label,
                new Point(rect.X + 5, rect.Y + ((rect.Height - label.Height) / 2)));
        }

        if (bars.Count == 0 && !string.IsNullOrWhiteSpace(EmptyText))
        {
            var empty = new FormattedText(
                EmptyText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                11.5,
                EmptyBrush ?? Brushes.Gray,
                dpi);

            drawingContext.DrawText(
                empty,
                new Point(Math.Max(0, (width - empty.Width) / 2), barArea.Top + ((barArea.Height - empty.Height) / 2)));
        }

        if (!double.IsNaN(PlayheadFraction) && PlayheadFraction is >= 0 and <= 1)
        {
            var x = Math.Round(PlayheadFraction * width) + 0.5;
            var pen = new Pen(PlayheadBrush ?? Brushes.White, 2);
            drawingContext.DrawLine(pen, new Point(x, BarTop - 3), new Point(x, barArea.Bottom));
            drawingContext.DrawEllipse(PlayheadBrush, null, new Point(x, BarTop - 4), 3, 3);
        }

        if (IsKeyboardFocused)
        {
            drawingContext.DrawRoundedRectangle(
                null,
                new Pen(AccentBrush ?? Brushes.DodgerBlue, 1.5),
                new Rect(0.75, 0.75, Math.Max(0, width - 1.5), Math.Max(0, height - 1.5)),
                4,
                4);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (ActualWidth <= 0)
        {
            return;
        }

        var bars = Bars?.ToList() ?? [];
        var hovered = TimelineLayout.HitTest(bars, e.GetPosition(this).X / ActualWidth);

        if (!ReferenceEquals(hovered, _hovered))
        {
            _hovered = hovered;
            ToolTip = hovered?.ToolTip;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hovered is not null)
        {
            _hovered = null;
            ToolTip = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        Focus();

        if (ActualWidth <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1);
        Invoke(fraction);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Left:
                Invoke(Math.Clamp(CurrentFraction - 0.02, 0, 1));
                break;
            case Key.Right:
                Invoke(Math.Clamp(CurrentFraction + 0.02, 0, 1));
                break;
            case Key.Home:
                Invoke(0);
                break;
            case Key.End:
                Invoke(1);
                break;
            case Key.Enter:
            case Key.Space:
                Invoke(CurrentFraction);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private double CurrentFraction => double.IsNaN(PlayheadFraction)
        ? TimelineLayout.HitTest(Bars?.ToList() ?? [], 0)?.Start ?? 0
        : PlayheadFraction;

    private void Invoke(double fraction)
    {
        if (SelectCommand is { } command && command.CanExecute(fraction))
        {
            command.Execute(fraction);
        }
    }

    private static DependencyProperty BrushProperty(string name) =>
        DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(TimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
