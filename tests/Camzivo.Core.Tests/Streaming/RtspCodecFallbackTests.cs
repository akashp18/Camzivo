using Camzivo.Core.Streaming.Rtsp;
using Xunit;

namespace Camzivo.Core.Tests.Streaming;

public sealed class RtspCodecFallbackTests
{
    [Fact]
    public void MainStreamFallsBackToTheSubStreamKeepingChannelAndDroppingTheOnvifAnnotation()
    {
        var main = new Uri("rtsp://192.168.88.241:554/cam/realmonitor?channel=3&subtype=0&unicast=true&proto=Onvif");

        var next = RtspCodecFallback.Next(main);

        Assert.NotNull(next);
        Assert.Equal("/cam/realmonitor", next!.AbsolutePath);
        Assert.Contains("channel=3", next.Query, StringComparison.Ordinal);
        Assert.Contains("subtype=1", next.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void SubStreamFallsBackToTheThirdAndThenStops()
    {
        var sub = new Uri("rtsp://192.168.88.241:554/cam/realmonitor?channel=1&subtype=1");

        var third = RtspCodecFallback.Next(sub);

        Assert.NotNull(third);
        Assert.Contains("subtype=2", third!.Query, StringComparison.Ordinal);
        Assert.Null(RtspCodecFallback.Next(third));
    }

    [Fact]
    public void AnAddressWithoutSubtypeHasNoFallback()
    {
        Assert.Null(RtspCodecFallback.Next(new Uri("rtsp://127.0.0.1:8554/live")));
    }

    [Theory]
    [InlineData("subtype=0", "main stream")]
    [InlineData("subtype=1", "sub stream")]
    [InlineData("subtype=2", "third stream")]
    public void DescribeNamesTheStreamForTheTileDetail(string query, string expected)
    {
        Assert.Equal(expected, RtspCodecFallback.Describe(new Uri($"rtsp://host/cam/realmonitor?channel=1&{query}")));
    }
}
