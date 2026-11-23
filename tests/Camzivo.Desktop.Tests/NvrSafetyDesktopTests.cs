using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Desktop.Services;
using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

public sealed class NvrSafetyDesktopTests
{
    [Fact]
    public async Task Recorder_ptz_prepare_is_disabled_before_any_request()
    {
        var transport = new CountingTransport();
        using var controller = new PtzController(transport);

        var result = await controller.PrepareAsync(
            Guid.NewGuid(),
            new DeviceEndpoint("192.168.1.108", 80, 554, false),
            OnvifServices.None,
            "admin",
            "password",
            "profile-1",
            CancellationToken.None,
            allowControl: false);

        Assert.False(result.Available);
        Assert.Contains("READ-ONLY", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Recorder_ptz_prepare_leaves_no_addressable_camera()
    {
        var transport = new CountingTransport();
        using var controller = new PtzController(transport);

        await controller.PrepareAsync(
            Guid.NewGuid(),
            new DeviceEndpoint("192.168.1.108", 80, 554, false),
            OnvifServices.None,
            "admin",
            "password",
            "profile-1",
            CancellationToken.None,
            allowControl: false);

        Assert.Null(controller.CameraId);
        Assert.False(controller.IsMoving);
        Assert.False(await controller.NudgeAsync(1, 0, 0));
        Assert.False(await controller.StopAsync());
        Assert.False(await controller.GotoPresetAsync("preset-1"));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void Local_export_rejects_unc_and_device_paths()
    {
        Assert.Null(LocalExportPolicy.Validate(@"C:\Evidence\clip.mp4"));
        Assert.NotNull(LocalExportPolicy.Validate(@"\\server\share\clip.mp4"));
        Assert.NotNull(LocalExportPolicy.Validate(@"\\.\pipe\clip.mp4"));
        Assert.NotNull(LocalExportPolicy.Validate("relative.mp4"));
    }

    [Fact]
    public void Diagnostics_view_always_states_read_only_mode()
    {
        var report = new DeviceDiagnosticReport(
            Guid.NewGuid(), "NVR", "http://192.168.1.108:80", "NVR", "Dahua", "NVR5216", "1.0", "serial",
            null, null, null, 0, [], null, DateTimeOffset.UtcNow);

        var viewModel = new DeviceDiagnosticsViewModel(report);

        Assert.Equal("READ-ONLY NVR MODE", viewModel.Mode);
    }

    private sealed class CountingTransport : Camzivo.Core.Devices.IOnvifTransport
    {
        public int Calls { get; private set; }

        public Task<Camzivo.Core.Devices.OnvifHttpResponse> PostAsync(
            Uri uri,
            string body,
            string? authorization,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Camzivo.Core.Devices.OnvifHttpResponse(200, "<ok/>"));
        }
    }
}
