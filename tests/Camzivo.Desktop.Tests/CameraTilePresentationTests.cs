using Camzivo.Core.Streaming.Video;
using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

/// <summary>
/// The tile surface: a decoded frame becomes pixels, an empty tile has no picture, and a different camera never
/// inherits the previous camera's last frame.
/// </summary>
public sealed class CameraTilePresentationTests
{
    [Fact]
    public void PresentingAFrameCreatesABitmapWithTheFrameSize()
    {
        var tile = new CameraTileViewModel(0, Guid.NewGuid(), "Gate", TileStatus.Waiting, "MAIN", "CONNECTING");
        Assert.False(tile.HasFrame);

        tile.PresentVideoFrame(Frame(64, 36));

        Assert.True(tile.HasFrame);
        Assert.NotNull(tile.Frame);
        Assert.Equal(64, tile.Frame!.PixelWidth);
        Assert.Equal(36, tile.Frame.PixelHeight);
    }

    [Fact]
    public void ASmallerFrameAfterALargerOneReplacesTheSurfaceInsteadOfCorruptingIt()
    {
        var tile = new CameraTileViewModel(0, Guid.NewGuid(), "Gate", TileStatus.Waiting, "MAIN", "CONNECTING");

        tile.PresentVideoFrame(Frame(64, 36));
        var first = tile.Frame;

        tile.PresentVideoFrame(Frame(32, 18));

        Assert.NotSame(first, tile.Frame);
        Assert.Equal(32, tile.Frame!.PixelWidth);
        Assert.Equal(18, tile.Frame.PixelHeight);
    }

    [Fact]
    public void SameSizeFramesReuseTheBitmap()
    {
        var tile = new CameraTileViewModel(0, Guid.NewGuid(), "Gate", TileStatus.Waiting, "MAIN", "CONNECTING");

        tile.PresentVideoFrame(Frame(64, 36));
        var first = tile.Frame;

        tile.PresentVideoFrame(Frame(64, 36));

        Assert.Same(first, tile.Frame);
    }

    [Fact]
    public void AssigningADifferentCameraDropsTheOldPicture()
    {
        var tile = new CameraTileViewModel(0, Guid.NewGuid(), "Gate", TileStatus.Live, "MAIN", "LIVE");
        tile.PresentVideoFrame(Frame(64, 36));

        tile.Assign(Guid.NewGuid(), "Dock", TileStatus.Waiting, "MAIN", "CONNECTING");

        Assert.False(tile.HasFrame);
    }

    [Fact]
    public void ClearingTheTileRemovesThePicture()
    {
        var tile = new CameraTileViewModel(0, Guid.NewGuid(), "Gate", TileStatus.Live, "MAIN", "LIVE");
        tile.PresentVideoFrame(Frame(64, 36));

        tile.Clear();

        Assert.False(tile.HasFrame);
        Assert.Equal(TileStatus.Empty, tile.Status);
    }

    private static VideoFrame Frame(int width, int height)
    {
        var pixels = new byte[width * height * 4];

        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 40;
            pixels[index + 1] = 120;
            pixels[index + 2] = 200;
            pixels[index + 3] = 255;
        }

        return new VideoFrame(pixels, width, height, width * 4, TimeSpan.Zero);
    }
}
