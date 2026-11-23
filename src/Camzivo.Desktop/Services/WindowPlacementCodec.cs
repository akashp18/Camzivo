using System.Globalization;

namespace Camzivo.Desktop.Services;

/// <summary>Where a window was, in device-independent pixels.</summary>
public readonly record struct WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>
/// Encodes window placement for the preference store. Decoding is deliberately strict: a corrupt or absurd value
/// restores the default placement instead of putting a window off-screen where it cannot be reached.
/// </summary>
public static class WindowPlacementCodec
{
    public const double MinWidth = 900;
    public const double MinHeight = 560;

    public static string Encode(WindowPlacement placement) => string.Join(
        ',',
        placement.Left.ToString("F0", CultureInfo.InvariantCulture),
        placement.Top.ToString("F0", CultureInfo.InvariantCulture),
        placement.Width.ToString("F0", CultureInfo.InvariantCulture),
        placement.Height.ToString("F0", CultureInfo.InvariantCulture),
        placement.Maximized ? "max" : "normal");

    public static bool TryDecode(string? text, out WindowPlacement placement)
    {
        placement = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 5 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var top) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            return false;
        }

        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width < MinWidth || height < MinHeight ||
            width > 10000 || height > 10000)
        {
            return false;
        }

        // A window far outside any plausible virtual desktop is treated as corrupt.
        if (left < -20000 || left > 20000 || top < -20000 || top > 20000)
        {
            return false;
        }

        placement = new WindowPlacement(left, top, width, height,
            string.Equals(parts[4], "max", StringComparison.OrdinalIgnoreCase));

        return true;
    }
}
