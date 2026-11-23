using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Data.Repositories;

namespace Camzivo.Data.Devices;

/// <summary>
/// Builds the compact Devices diagnostics report. It only reads: identity, services, channel discovery, stream
/// reachability and advertised capabilities. It never changes NVR configuration, schedules, storage or recordings.
///
/// Speed support is deliberately not guessed from a vendor or model. RTSP Scale is measured during actual playback,
/// so this report says UNKNOWN until that measurement exists.
/// </summary>
public sealed class DeviceDiagnosticsService(NvrDiscovery discovery, IDeviceStore? devices = null)
{
    public async Task<DeviceDiagnosticReport> InspectAsync(
        Device device,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var result = await discovery
            .DiscoverAsync(device.Endpoint, device.Username, password, cancellationToken)
            .ConfigureAwait(false);

        var services = result.Services;
        var information = result.Information;
        var channelCount = result.Channels.Count;
        var hasProfiles = result.Profiles is { Count: > 0 };
        var audio = result.Profiles?.Any(profile => profile.HasAudio) == true;
        var onvifOk = result.Stages.FirstOrDefault(stage => stage.Stage == ConnectionStage.Reach)?.Success == true;
        var rtspOk = result.Stages.FirstOrDefault(stage => stage.Stage == ConnectionStage.Stream)?.Success == true;

        var rows = new List<DeviceDiagnosticRow>
        {
            new("ONVIF", onvifOk ? CapabilityState.Supported : CapabilityState.Unsupported,
                onvifOk ? $"Device service answered · {services.Summary}" : "The ONVIF device service did not answer."),
            new("RTSP stream", rtspOk ? CapabilityState.Supported : CapabilityState.Unverified,
                rtspOk
                    ? "The advertised stream address accepted a connection."
                    : result.Failure?.Explanation ?? "No stream address was verified."),
            new("Channels", channelCount > 0 ? CapabilityState.Supported : CapabilityState.Unsupported,
                channelCount > 0
                    ? $"{channelCount} channel(s) discovered"
                    : "No channels were discovered from ONVIF media profiles."),
            new("Recording search", services.Search is not null ? CapabilityState.Supported : CapabilityState.Unsupported,
                services.Search is not null
                    ? $"Profile G search advertised at {services.Search.Host}"
                    : "Profile G search is not advertised. This is not the same as an empty recording day."),
            new("Playback", services.Replay is not null ? CapabilityState.Supported : CapabilityState.Unsupported,
                services.Replay is not null
                    ? $"Profile G replay advertised at {services.Replay.Host}"
                    : "Profile G replay is not advertised."),
            new("Playback speed", CapabilityState.Unverified,
                "RTSP Scale is measured during playback; this device has not been asked for 2×–16× yet."),
            new("PTZ", services.Ptz is null ? CapabilityState.Unsupported : CapabilityState.Unverified,
                services.Ptz is null
                    ? "No PTZ service advertised."
                    : $"PTZ service advertised at {services.Ptz.Host}; move a camera to verify."),
            new("Audio", !hasProfiles ? CapabilityState.Unverified : audio ? CapabilityState.Supported : CapabilityState.Unsupported,
                !hasProfiles
                    ? "No media profiles were readable."
                    : audio
                        ? "At least one profile advertises an audio encoder."
                        : "Profiles advertise no audio encoder."),
        };

        var probed = DateTimeOffset.UtcNow;
        var lastError = result.Failure?.Explanation ?? device.LastConnectionError;

        if (devices is not null)
        {
            var updated = device with
            {
                Vendor = information?.Manufacturer?.Trim() ?? device.Vendor,
                Model = information?.Model?.Trim() ?? device.Model,
                Firmware = information?.FirmwareVersion?.Trim() ?? device.Firmware,
                SerialNumber = information?.SerialNumber?.Trim() ?? device.SerialNumber,
                DeviceTimeZone = result.SystemTime?.TimeZone ?? device.DeviceTimeZone,
                LastConnectionError = result.Success ? null : lastError,
                LastDiagnosticUtc = probed,
                UpdatedUtc = probed,
            };

            await devices.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        return new DeviceDiagnosticReport(
            device.Id,
            device.DisplayName,
            device.Endpoint.Display,
            device.Kind == DeviceKind.Nvr ? "NVR" : "CAMERA",
            information?.Manufacturer ?? device.Vendor,
            information?.Model ?? device.Model,
            information?.FirmwareVersion ?? device.Firmware,
            information?.SerialNumber ?? device.SerialNumber,
            result.SystemTime?.Utc,
            result.SystemTime?.Local,
            result.SystemTime?.TimeZone ?? device.DeviceTimeZone,
            channelCount,
            rows,
            lastError,
            probed);
    }
}
