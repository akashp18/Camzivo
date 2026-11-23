using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camzivo.Core.Streaming.Video;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Saves the frame the operator is actually looking at.
///
/// The picture comes from the decoder, never from a placeholder: the tile only offers a snapshot once a real frame was
/// presented. The camera name and capture time are written both into the file name and into a caption strip, so an
/// exported still is self-describing once it leaves the application.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SnapshotWriter
{
    private const int CaptionHeight = 28;

    public static string Save(VideoFrame frame, string cameraName, DateTimeOffset capturedAt, string directory)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraName);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        var picture = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Pixels,
            frame.Stride);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(picture, new Rect(0, 0, frame.Width, frame.Height));

            var caption = new Rect(0, Math.Max(0, frame.Height - CaptionHeight), frame.Width, CaptionHeight);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 12, 14, 17)), null, caption);

            var text = new FormattedText(
                $"{cameraName} · {capturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Min(14, Math.Max(10, frame.Height / 24.0)),
                Brushes.White,
                1.0);

            context.DrawText(text, new Point(8, Math.Max(0, frame.Height - CaptionHeight) + ((CaptionHeight - text.Height) / 2)));
        }

        var target = new RenderTargetBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        var path = Path.Combine(directory, FileName(cameraName, capturedAt));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);

        return path;
    }

    /// <summary>Readable, sortable and safe on Windows: no characters a file name cannot hold.</summary>
    public static string FileName(string cameraName, DateTimeOffset capturedAt)
    {
        var builder = new StringBuilder(cameraName.Length);

        foreach (var character in cameraName.Trim())
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) || character is ' '
                ? '_'
                : character);
        }

        var stem = builder.ToString().Trim('_');
        if (stem.Length == 0)
        {
            stem = "camera";
        }

        if (stem.Length > 60)
        {
            stem = stem[..60];
        }

        return $"{stem}_{capturedAt.ToLocalTime():yyyyMMdd_HHmmss}.png";
    }
}
