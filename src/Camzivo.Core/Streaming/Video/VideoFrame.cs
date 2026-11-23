using System.Runtime.CompilerServices;

namespace Camzivo.Core.Streaming.Video;

/// <summary>
/// One decoded frame in BGRA32, ready to be presented.
///
/// The stride is explicit: decoders produce padded rows, and ignoring that is how video ends up sheared by a few
/// pixels per row.
/// </summary>
public sealed record VideoFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    TimeSpan Timestamp,
    bool HardwareDecoded = false)
{
    public int ByteCount => Stride * Height;
}

/// <summary>NV12/I420 → BGRA conversion, BT.709 limited range (what cameras send), integer arithmetic.</summary>
public static class YuvConverter
{
    // Fixed-point coefficients (BT.601 limited range, 8 fraction bits) evaluated once into tables. The per-pixel
    // loop then costs three lookups, four additions and three branches instead of four multiplies, two divisions
    // and a bounds check per byte. Conversion was measured at 9.5 ms per 640×360 frame and is the dominant cost of
    // the whole pipeline, so this loop is the one that matters.
    private static readonly int[] Luma = BuildTable(y => 298 * Math.Max(0, y - 16));

    private static readonly int[] ChromaBlue = BuildTable(u => 541 * (u - 128));

    private static readonly int[] ChromaGreenU = BuildTable(u => -55 * (u - 128));

    private static readonly int[] ChromaRed = BuildTable(v => 459 * (v - 128));

    private static readonly int[] ChromaGreenV = BuildTable(v => -136 * (v - 128));

    private static int[] BuildTable(Func<int, int> function)
    {
        var table = new int[256];
        for (var index = 0; index < table.Length; index++)
        {
            table[index] = function(index);
        }

        return table;
    }

    /// <summary>Converts an NV12 (or I420) frame into a tightly packed BGRA buffer.</summary>
    public static VideoFrame Nv12ToBgra(
        ReadOnlySpan<byte> nv12,
        int width,
        int height,
        int yStride,
        int uvStride,
        TimeSpan timestamp,
        bool planar = false,
        int? visibleWidth = null,
        int? visibleHeight = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        // The decoder aligns to macroblocks (360 → 368); the visible picture is the SPS size, and the padding rows
        // must never reach the screen.
        width = Math.Min(width, visibleWidth is > 0 ? visibleWidth.Value : width);
        height = Math.Min(height, visibleHeight is > 0 ? visibleHeight.Value : height);

        var output = new byte[width * height * 4];
        var uvHeight = (height + 1) / 2;
        var uvWidth = (width + 1) / 2;

        if (nv12.Length < (yStride * height) + (uvStride * uvHeight))
        {
            throw new ArgumentException("The YUV buffer is smaller than its declared dimensions.", nameof(nv12));
        }

        var yPlane = nv12[..(yStride * height)];
        var chroma = nv12[(yStride * height)..];

        var planarPlaneSize = planar ? uvWidth * uvHeight : 0;

        unsafe
        {
            fixed (byte* source = nv12)
            fixed (byte* destination = output)
            {
                var yBase = source;
                var chromaBase = source + (yStride * height);
                var destinationBase = destination;

                for (var row = 0; row < height; row++)
                {
                    var lumaRow = yBase + (row * yStride);
                    var chromaRow = chromaBase + ((row >> 1) * uvStride);
                    var outputRow = destinationBase + (row * width * 4);
                    var column = 0;

                    while (column < width)
                    {
                        var pair = column >> 1;
                        var u = chromaRow[planar ? pair : pair * 2];
                        var v = chromaRow[planar ? planarPlaneSize + pair : (pair * 2) + 1];

                        var blue = ChromaBlue[u];
                        var green = ChromaGreenU[u] + ChromaGreenV[v];
                        var red = ChromaRed[v];

                        WritePair(outputRow + (column * 4), lumaRow + column, blue, green, red);

                        if (column + 1 < width)
                        {
                            WritePair(outputRow + ((column + 1) * 4), lumaRow + column + 1, blue, green, red);
                        }

                        column += 2;
                    }
                }
            }
        }

        return new VideoFrame(output, width, height, width * 4, timestamp);
    }

    private static unsafe void WritePair(byte* destination, byte* luma, int blue, int green, int red)
    {
        var y = Luma[*luma];

        destination[0] = Clamp((y + blue + 128) >> 8);
        destination[1] = Clamp((y + green + 128) >> 8);
        destination[2] = Clamp((y + red + 128) >> 8);
        destination[3] = 255;
    }

    private static byte Clamp(int value) => (byte)((uint)value <= 255u ? value : value < 0 ? 0 : 255);
}
