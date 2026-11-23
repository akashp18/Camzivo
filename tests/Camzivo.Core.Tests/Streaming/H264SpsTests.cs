using Camzivo.Core.Streaming.Video;
using Xunit;

namespace Camzivo.Core.Tests.Streaming;

/// <summary>
/// The SPS is the only source of the true picture size (decoders pad to macroblock boundaries). These are the two
/// parameter sets the committed fixtures actually stream, captured as bytes.
/// </summary>
public sealed class H264SpsTests
{
    [Fact]
    public void ReadsTheMainStreamParameterSet()
    {
        byte[] sps =
        [
            0x67, 0x42, 0xc0, 0x16, 0x8c, 0x68, 0x0a, 0x02,
            0xf7, 0x97, 0x01, 0x01, 0xe1, 0x10, 0x8d, 0x40,
        ];

        Assert.True(H264Sps.TryReadDimensions(sps, out var width, out var height));
        Assert.Equal(640, width);
        Assert.Equal(360, height);
    }

    [Fact]
    public void ReadsTheSubstreamParameterSet()
    {
        byte[] sps =
        [
            0x67, 0x42, 0xc0, 0x0c, 0x8c, 0x68, 0x14, 0x19,
            0x79, 0xf0, 0x10, 0x1e, 0x11, 0x08, 0xd4,
        ];

        Assert.True(H264Sps.TryReadDimensions(sps, out var width, out var height));
        Assert.Equal(320, width);
        Assert.Equal(180, height);
    }

    [Fact]
    public void RejectsSomethingThatIsNotAnSps()
    {
        byte[] slice = [0x65, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Assert.False(H264Sps.TryReadDimensions(slice, out _, out _));
    }

    [Fact]
    public void RejectsRubbishWithoutThrowing()
    {
        // Truncated inside the header: the reader must give up rather than invent a size.
        byte[] truncated = [0x67, 0x42, 0xc0, 0x0c, 0x8c, 0x68];

        Assert.False(H264Sps.TryReadDimensions(truncated, out _, out _));
    }
}
