namespace Camzivo.Core.Layout;

/// <summary>Grid presets offered by the Live View layout control.</summary>
public enum LayoutPreset
{
    Custom = -1,
    Single = 1,
    Two = 2,
    Four = 4,
    Six = 6,
    Eight = 8,
    Nine = 9,
    Twelve = 12,
    Sixteen = 16,
    TwentyFive = 25,
    ThirtySix = 36,
    FortyNine = 49,
    SixtyFour = 64,
}

/// <summary>A cell position inside a layout, in reading order.</summary>
public readonly record struct LayoutCell(int Index, int Row, int Column);

/// <summary>Normalised slot rectangle (0..1) so geometry is independent of pixel size.</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height);

/// <summary>
/// Immutable, validated grid geometry. The single source of truth for tile placement; the UI never
/// computes rows/columns on its own.
/// </summary>
public sealed record LayoutGrid
{
    public const int MaxRowsOrColumns = 8;
    public const int MaxSlots = 64;

    private readonly LayoutCell[] _cells;

    private LayoutGrid(int rows, int columns, LayoutPreset preset)
    {
        Rows = rows;
        Columns = columns;
        Preset = preset;

        _cells = new LayoutCell[rows * columns];
        for (var index = 0; index < _cells.Length; index++)
        {
            _cells[index] = new LayoutCell(index, index / columns, index % columns);
        }
    }

    public int Rows { get; }

    public int Columns { get; }

    /// <summary>The matching preset, or <see cref="LayoutPreset.Custom"/> for user-defined grids.</summary>
    public LayoutPreset Preset { get; }

    public int SlotCount => _cells.Length;

    public IReadOnlyList<LayoutCell> Cells => _cells;

    public static LayoutGrid Create(int rows, int columns)
    {
        if (rows is < 1 or > MaxRowsOrColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, $"Rows must be between 1 and {MaxRowsOrColumns}.");
        }

        if (columns is < 1 or > MaxRowsOrColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), columns, $"Columns must be between 1 and {MaxRowsOrColumns}.");
        }

        if (rows * columns > MaxSlots)
        {
            throw new ArgumentException($"A layout may hold at most {MaxSlots} slots.", nameof(columns));
        }

        return new LayoutGrid(rows, columns, PresetFor(rows, columns));
    }

    public static LayoutGrid FromPreset(LayoutPreset preset) => preset switch
    {
        LayoutPreset.Single => Create(1, 1),
        LayoutPreset.Two => Create(1, 2),
        LayoutPreset.Four => Create(2, 2),
        LayoutPreset.Six => Create(2, 3),
        LayoutPreset.Eight => Create(2, 4),
        LayoutPreset.Nine => Create(3, 3),
        LayoutPreset.Twelve => Create(3, 4),
        LayoutPreset.Sixteen => Create(4, 4),
        LayoutPreset.TwentyFive => Create(5, 5),
        LayoutPreset.ThirtySix => Create(6, 6),
        LayoutPreset.FortyNine => Create(7, 7),
        LayoutPreset.SixtyFour => Create(8, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Custom is not a concrete preset."),
    };

    public static LayoutPreset PresetFor(int rows, int columns) => (rows, columns) switch
    {
        (1, 1) => LayoutPreset.Single,
        (1, 2) => LayoutPreset.Two,
        (2, 2) => LayoutPreset.Four,
        (2, 3) => LayoutPreset.Six,
        (2, 4) => LayoutPreset.Eight,
        (3, 3) => LayoutPreset.Nine,
        (3, 4) => LayoutPreset.Twelve,
        (4, 4) => LayoutPreset.Sixteen,
        (5, 5) => LayoutPreset.TwentyFive,
        (6, 6) => LayoutPreset.ThirtySix,
        (7, 7) => LayoutPreset.FortyNine,
        (8, 8) => LayoutPreset.SixtyFour,
        _ => LayoutPreset.Custom,
    };

    /// <summary>
    /// Normalised geometry for a slot. Slots tile the whole surface with no gaps; the pixel gap is a
    /// visual separator applied by the renderer, not part of the layout maths.
    /// </summary>
    public LayoutRect GetSlotRect(int index)
    {
        if (index < 0 || index >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Slot index must be between 0 and {SlotCount - 1}.");
        }

        var cell = _cells[index];
        return new LayoutRect(
            X: (double)cell.Column / Columns,
            Y: (double)cell.Row / Rows,
            Width: 1d / Columns,
            Height: 1d / Rows);
    }

    /// <summary>Slot index under a normalised point, or -1 when the point is outside.</summary>
    public int HitTest(double normalizedX, double normalizedY)
    {
        if (normalizedX < 0 || normalizedX >= 1 || normalizedY < 0 || normalizedY >= 1)
        {
            return -1;
        }

        var column = Math.Min((int)(normalizedX * Columns), Columns - 1);
        var row = Math.Min((int)(normalizedY * Rows), Rows - 1);
        return (row * Columns) + column;
    }

    public override string ToString() => $"{Rows}x{Columns} ({Preset})";
}
