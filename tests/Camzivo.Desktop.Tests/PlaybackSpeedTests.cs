using Camzivo.Core.Recording;
using Camzivo.Core.Streaming.Video;
using Camzivo.Data.Recording;
using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

public sealed class PlaybackSpeedTests
{
    [Fact]
    public void Speeds_are_the_six_supported_values_and_default_to_normal()
    {
        var viewModel = CreateViewModel();

        Assert.Equal([1d, 2d, 4d, 6d, 8d, 16d], viewModel.Speeds.Select(speed => speed.Rate));
        Assert.Equal(1, viewModel.PlaybackRate);
        Assert.True(viewModel.Speeds[0].IsSelected);
        Assert.Equal("1×", viewModel.ActiveSpeedText);
    }

    [Fact]
    public async Task Selecting_a_speed_before_play_highlights_it_and_keeps_it()
    {
        var viewModel = CreateViewModel();

        Assert.True(await viewModel.SetSpeedAsync(8));

        Assert.Equal(8, viewModel.PlaybackRate);
        Assert.Equal("8× requested", viewModel.ActiveSpeedText);
        Assert.Equal("8×", viewModel.Speeds.Single(speed => speed.IsSelected).Label);
    }

    [Fact]
    public async Task Unsupported_speed_is_rejected_with_a_visible_reason()
    {
        var viewModel = CreateViewModel();

        Assert.False(await viewModel.SetSpeedAsync(3));

        Assert.Equal(1, viewModel.PlaybackRate);
        Assert.True(viewModel.HasSpeedNotice);
        Assert.Contains("3×", viewModel.SpeedNotice);
    }

    [Fact]
    public async Task Changing_camera_resets_the_speed_to_normal()
    {
        var viewModel = CreateViewModel();
        var first = Camera();
        var second = Camera();
        viewModel.SetCameras([first, second]);

        await viewModel.SetSpeedAsync(16);
        Assert.Equal(16, viewModel.PlaybackRate);

        viewModel.SelectedCamera = second;

        Assert.Equal(1, viewModel.PlaybackRate);
        Assert.True(viewModel.Speeds[0].IsSelected);
        Assert.False(viewModel.HasSpeedNotice);
    }

    private static PlaybackViewModel CreateViewModel() =>
        new(new EmptyCatalog(), () => throw new InvalidOperationException("No decoder is opened in these tests."));

    private static PlaybackCamera Camera() => new(
        Guid.NewGuid(),
        "Camera",
        new Uri("http://127.0.0.1/onvif/device_service"),
        "admin",
        "password",
        "channel-1");

    private sealed class EmptyCatalog : IRecordingCatalog
    {
        public Task<RecordingAvailability> SearchAsync(
            Guid cameraId,
            Uri deviceServiceUri,
            string? username,
            string? password,
            DateOnly localDate,
            TimeZoneInfo zone,
            string? sourceToken,
            CancellationToken cancellationToken,
            int rtspPort = 554) =>
            Task.FromResult(new RecordingAvailability(
                cameraId,
                localDate,
                [],
                zone.Id,
                DateTimeOffset.UtcNow,
                UnsupportedReason: "not used"));

        public Task<ReplayLookup> GetReplayAsync(
            Uri deviceServiceUri,
            string? username,
            string? password,
            string recordingToken,
            CancellationToken cancellationToken,
            int rtspPort = 554) =>
            Task.FromResult(ReplayLookup.Failed("not used"));
    }
}
