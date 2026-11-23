using Camzivo.Desktop.Services;
using Xunit;

namespace Camzivo.Desktop.Tests;

public sealed class WindowPlacementCodecTests
{
    [Fact]
    public void PlacementRoundTrips()
    {
        var original = new WindowPlacement(120, 64, 1440, 900, Maximized: false);

        Assert.True(WindowPlacementCodec.TryDecode(WindowPlacementCodec.Encode(original), out var decoded));
        Assert.Equal(original.Left, decoded.Left, 0);
        Assert.Equal(original.Top, decoded.Top, 0);
        Assert.Equal(original.Width, decoded.Width, 0);
        Assert.Equal(original.Height, decoded.Height, 0);
        Assert.False(decoded.Maximized);
    }

    [Fact]
    public void MaximizedFlagSurvives()
    {
        Assert.True(WindowPlacementCodec.TryDecode(
            WindowPlacementCodec.Encode(new WindowPlacement(-1920, 0, 1920, 1080, true)),
            out var decoded));
        Assert.True(decoded.Maximized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1,2,3")]
    [InlineData("0,0,400,300,normal")] // smaller than the minimum window
    [InlineData("NaN,0,1200,800,normal")]
    [InlineData("0,0,99999,800,normal")]
    [InlineData("999999,0,1200,800,normal")]
    public void ImplausibleValuesAreRejected(string? text)
    {
        Assert.False(WindowPlacementCodec.TryDecode(text, out _));
    }

    [Fact]
    public void CultureDoesNotChangeTheEncoding()
    {
        var encoded = WindowPlacementCodec.Encode(new WindowPlacement(10.6, 20.4, 1280, 720, false));

        Assert.Equal("11,20,1280,720,normal", encoded);
    }
}
