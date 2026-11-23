namespace Camzivo.Core.Streaming.Video;

/// <summary>
/// Writes a decoded frame to a 24-bit BMP. Deliberately dependency-free: this is used by the stream probe and by
/// tests as evidence that a frame exists, and it must not depend on WPF imaging to prove a decoder works.
/// </summary>
public static class FrameWriter
{
    public static void WriteBmp(VideoFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var width = frame.Width;
        var height = frame.Height;
        var rowSize = ((width * 3) + 3) & ~3;
        var pixelBytes = rowSize * height;
        var fileSize = 54 + pixelBytes;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0);
        writer.Write(54);

        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        // BMP rows are bottom-up and padded to four bytes.
        for (var y = height - 1; y >= 0; y--)
        {
            var rowStart = y * frame.Stride;

            for (var x = 0; x < width; x++)
            {
                var offset = rowStart + (x * 4);
                writer.Write(frame.Pixels[offset]);      // B
                writer.Write(frame.Pixels[offset + 1]);  // G
                writer.Write(frame.Pixels[offset + 2]);  // R
            }

            for (var pad = width * 3; pad < rowSize; pad++)
            {
                writer.Write((byte)0);
            }
        }
    }
}
