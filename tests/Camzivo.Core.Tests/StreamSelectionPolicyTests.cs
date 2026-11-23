using Camzivo.Core.Streaming;
using Xunit;

namespace Camzivo.Core.Tests;

public class StreamSelectionPolicyTests
{
    private static readonly StreamUriSet Both = new("rtsp://cam/main", "rtsp://cam/sub");
    private static readonly StreamUriSet MainOnly = new("rtsp://cam/main", null);
    private static readonly StreamUriSet SubOnly = new(null, "rtsp://cam/sub");

    private static StreamSelection Select(
        StreamQuality preference,
        StreamUriSet uris,
        bool fullScreen = false,
        int width = 320,
        int height = 180,
        bool visible = true) =>
        StreamSelectionPolicy.Select(new StreamSelectionContext(preference, fullScreen, width, height, visible, uris));

    [Fact]
    public void ExplicitMainIsHonoured()
    {
        var selection = Select(StreamQuality.Main, Both, width: 320, height: 180);

        Assert.Equal(StreamQuality.Main, selection.Quality);
        Assert.Equal(StreamSelectionReason.UserSelectedMain, selection.Reason);
    }

    [Fact]
    public void ExplicitMainIsNeverSilentlyDowngraded()
    {
        var selection = Select(StreamQuality.Main, SubOnly);

        Assert.Equal(StreamQuality.Sub, selection.Quality);
        Assert.Equal(StreamSelectionReason.MainUnavailableUsingSub, selection.Reason);
        Assert.True(selection.HasStream);
    }

    [Fact]
    public void ExplicitSubIsHonoured()
    {
        var selection = Select(StreamQuality.Sub, Both, fullScreen: true);

        Assert.Equal(StreamQuality.Sub, selection.Quality);
        Assert.Equal(StreamSelectionReason.UserSelectedSub, selection.Reason);
    }

    [Fact]
    public void ExplicitSubFallsBackToMainWhenNoSubstreamExists()
    {
        var selection = Select(StreamQuality.Sub, MainOnly);

        Assert.Equal(StreamQuality.Main, selection.Quality);
        Assert.Equal(StreamSelectionReason.SubUnavailableUsingMain, selection.Reason);
    }

    [Fact]
    public void AutoUsesMainForFullscreen()
    {
        var selection = Select(StreamQuality.Auto, Both, fullScreen: true, width: 1920, height: 1080);

        Assert.Equal(StreamQuality.Main, selection.Quality);
        Assert.Equal(StreamSelectionReason.AutoFullscreenMain, selection.Reason);
    }

    [Fact]
    public void AutoUsesMainForALargeVisibleTile()
    {
        var selection = Select(StreamQuality.Auto, Both, width: 960, height: 540);

        Assert.Equal(StreamQuality.Main, selection.Quality);
        Assert.Equal(StreamSelectionReason.AutoLargeTileMain, selection.Reason);
    }

    [Fact]
    public void AutoUsesSubstreamForSmallTiles()
    {
        var selection = Select(StreamQuality.Auto, Both, width: 320, height: 180);

        Assert.Equal(StreamQuality.Sub, selection.Quality);
        Assert.Equal(StreamSelectionReason.AutoSmallTileSub, selection.Reason);
    }

    [Fact]
    public void AutoUsesSubstreamForOffscreenTilesEvenWhenLarge()
    {
        var selection = Select(StreamQuality.Auto, Both, width: 1920, height: 1080, visible: false);

        Assert.Equal(StreamQuality.Sub, selection.Quality);
        Assert.Equal(StreamSelectionReason.AutoSmallTileSub, selection.Reason);
    }

    [Fact]
    public void AutoFallsBackToTheOnlyAvailableStream()
    {
        var selection = Select(StreamQuality.Auto, MainOnly, width: 320, height: 180);

        Assert.Equal(StreamQuality.Main, selection.Quality);
        Assert.Equal(StreamSelectionReason.AutoOnlyStreamAvailable, selection.Reason);
    }

    [Fact]
    public void NoUrisMeansNoStream()
    {
        var selection = Select(StreamQuality.Auto, StreamUriSet.Empty);

        Assert.False(selection.HasStream);
        Assert.Equal(StreamSelectionReason.NoStream, selection.Reason);
    }

    [Fact]
    public void PolicyIsDeterministicForIdenticalInput()
    {
        var context = new StreamSelectionContext(StreamQuality.Auto, false, 320, 180, true, Both);

        var first = StreamSelectionPolicy.Select(context);
        var second = StreamSelectionPolicy.Select(context);

        Assert.Equal(first, second);
    }
}
