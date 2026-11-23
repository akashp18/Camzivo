using Camzivo.Core.Streaming;
using Xunit;

namespace Camzivo.Core.Tests;

public class RtspUriBuilderTests
{
    [Fact]
    public void BuildsADefaultTcpUri()
    {
        var uri = RtspUriBuilder.Build("192.168.18.11", "/cam/realmonitor?channel=1&subtype=0");

        Assert.Equal("rtsp", uri.Scheme);
        Assert.Equal(554, uri.Port);
        Assert.Equal("/cam/realmonitor", uri.AbsolutePath);
        Assert.Contains("channel=1", uri.Query, StringComparison.Ordinal);
        Assert.Contains("transport=tcp", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingQueryGetsAnAmpersandNotASecondQuestionMark()
    {
        var uri = RtspUriBuilder.Build("cam.local", 554, "/stream?profile=sub", RtspTransport.Udp);

        Assert.Single(uri.Query.AsSpan().ToArray(), character => character == '?');
        Assert.Contains("profile=sub", uri.Query, StringComparison.Ordinal);
        Assert.Contains("transport=udp", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPathBecomesRoot()
    {
        Assert.Equal("/", RtspUriBuilder.Build("cam.local", 554, null, RtspTransport.Tcp).AbsolutePath);
        Assert.Equal("/", RtspUriBuilder.Build("cam.local", 554, "   ", RtspTransport.Tcp).AbsolutePath);
    }

    [Fact]
    public void Ipv6LiteralIsBracketed()
    {
        var uri = RtspUriBuilder.Build("fe80::1", 554, "/live", RtspTransport.Tcp);

        Assert.Equal("[fe80::1]", uri.Host);
        Assert.Contains("[fe80::1]", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsInHostAreRejectedRatherThanSilentlyEmbedded()
    {
        Assert.Throws<ArgumentException>(() => RtspUriBuilder.Build("admin:pass@cam.local", 554, "/live", RtspTransport.Tcp));
    }

    [Fact]
    public void SchemeOrPathInHostIsRejected()
    {
        Assert.Throws<ArgumentException>(() => RtspUriBuilder.Build("rtsp://cam.local", 554, "/live", RtspTransport.Tcp));
        Assert.Throws<ArgumentException>(() => RtspUriBuilder.Build("cam.local/live", 554, "/live", RtspTransport.Tcp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void OutOfRangePortsAreRejected(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RtspUriBuilder.Build("cam.local", port, "/live", RtspTransport.Tcp));
    }

    [Fact]
    public void RedactRemovesUserInformation()
    {
        var uri = new Uri("rtsp://admin:secret@192.168.18.11:554/live");

        var redacted = RtspUriBuilder.Redact(uri);

        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }
}
