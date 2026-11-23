using Camzivo.Core.Streaming.Video;
using Xunit;

namespace Camzivo.Core.Tests.Streaming;

/// <summary>
/// The converter was rewritten for speed (table lookups, pointer writes). These tests pin the output to the exact
/// integer formula it replaced, so a faster loop can never quietly change colours, crop or alpha.
/// </summary>
public sealed class YuvConverterTests
{
    [Fact]
    public void Nv12ConversionMatchesTheReferenceFormula()
    {
        const int width = 64;
        const int height = 36;
        var nv12 = Buffer(width, height, out var chromaOffset);

        var frame = YuvConverter.Nv12ToBgra(nv12, width, height, width, width, TimeSpan.Zero);

        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        AssertFrameMatchesReference(frame, nv12, width, height, chromaOffset, planar: false);
    }

    [Fact]
    public void I420PlanarConversionMatchesTheReferenceFormula()
    {
        const int width = 48;
        const int height = 32;
        var planar = Planar(width, height, out var uOffset, out var vOffset);

        var frame = YuvConverter.Nv12ToBgra(planar, width, height, width, width / 2, TimeSpan.Zero, planar: true);

        var uvWidth = width / 2;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var u = planar[uOffset + ((y / 2) * uvWidth) + (x / 2)];
                var v = planar[vOffset + ((y / 2) * uvWidth) + (x / 2)];

                AssertPixel(frame, planar, width, x, y, u, v);
            }
        }
    }

    [Fact]
    public void PaddingRowsBelowTheVisiblePictureAreCropped()
    {
        const int codedWidth = 64;
        const int codedHeight = 40;
        var nv12 = Buffer(codedWidth, codedHeight, out _);

        var frame = YuvConverter.Nv12ToBgra(
            nv12,
            codedWidth,
            codedHeight,
            codedWidth,
            codedWidth,
            TimeSpan.Zero,
            visibleWidth: 64,
            visibleHeight: 36);

        Assert.Equal(64, frame.Width);
        Assert.Equal(36, frame.Height);
        Assert.Equal(64 * 36 * 4, frame.Pixels.Length);
    }

    [Fact]
    public void AlphaIsAlwaysOpaque()
    {
        var nv12 = Buffer(16, 8, out _);
        var frame = YuvConverter.Nv12ToBgra(nv12, 16, 8, 16, 16, TimeSpan.Zero);

        for (var index = 3; index < frame.Pixels.Length; index += 4)
        {
            Assert.Equal(255, frame.Pixels[index]);
        }
    }

    [Fact]
    public void AShortBufferIsRejected()
    {
        Assert.Throws<ArgumentException>(() => YuvConverter.Nv12ToBgra(new byte[10], 64, 36, 64, 64, TimeSpan.Zero));
    }

    private static void AssertFrameMatchesReference(
        VideoFrame frame,
        byte[] source,
        int width,
        int height,
        int chromaOffset,
        bool planar)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var uv = chromaOffset + ((y / 2) * width) + (x / 2 * 2);
                var u = source[uv];
                var v = source[uv + 1];

                AssertPixel(frame, source, width, x, y, u, v);
            }
        }
    }

    private static void AssertPixel(VideoFrame frame, byte[] source, int width, int x, int y, byte u, byte v)
    {
        var c = Math.Max(0, source[(y * width) + x] - 16);
        var d = u - 128;
        var e = v - 128;

        var expectedB = Clamp(((298 * c) + (541 * d) + 128) >> 8);
        var expectedG = Clamp(((298 * c) - (55 * d) - (136 * e) + 128) >> 8);
        var expectedR = Clamp(((298 * c) + (459 * e) + 128) >> 8);

        var offset = ((y * width) + x) * 4;
        Assert.Equal(expectedB, frame.Pixels[offset]);
        Assert.Equal(expectedG, frame.Pixels[offset + 1]);
        Assert.Equal(expectedR, frame.Pixels[offset + 2]);
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    private static byte[] Buffer(int width, int height, out int chromaOffset)
    {
        var random = new Random(1234);
        var bytes = new byte[width * height + (width * height / 2)];
        for (var index = 0; index < width * height; index++)
        {
            bytes[index] = (byte)random.Next(0, 256);
        }

        // Chroma pair per 2×2 block; keep the last byte available for every pair read by the converter.
        for (var index = width * height; index < bytes.Length; index++)
        {
            bytes[index] = (byte)random.Next(0, 256);
        }

        chromaOffset = width * height;
        return bytes;
    }

    private static byte[] Planar(int width, int height, out int uOffset, out int vOffset)
    {
        var ySize = width * height;
        var uvSize = (width / 2) * (height / 2);
        var bytes = new byte[ySize + (uvSize * 2)];

        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index * 7 % 256);
        }

        uOffset = ySize;
        vOffset = ySize + uvSize;
        return bytes;
    }
}
