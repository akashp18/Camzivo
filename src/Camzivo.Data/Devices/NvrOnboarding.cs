using Camzivo.Core.Devices;
using Camzivo.Core.Security;
using Camzivo.Data.Repositories;

namespace Camzivo.Data.Devices;

/// <summary>What the operator typed for a recorder. Same rules as a camera, plus channels.</summary>
public sealed record NvrRegistrationRequest(
    string DisplayName,
    string Address,
    string? Username = null,
    string? Password = null,
    bool UseTls = false,
    int? OnvifPort = null,
    int? RtspPort = null)
{
    public override string ToString() =>
        $"{nameof(NvrRegistrationRequest)} {{ Name = {DisplayName}, Address = {Address}, Username = {Username}, Password = *** }}";
}

public sealed record ChannelImportOutcome(
    bool Success,
    Device? Device,
    int Imported,
    int Updated,
    IReadOnlyList<string> Skipped,
    string? Error = null);

public enum NvrRemovalMode
{
    /// <summary>Delete the recorder, its channels and its stored password.</summary>
    RemoveEverything = 0,

    /// <summary>Delete the recorder but keep each channel as an independent camera with its own credentials copy.</summary>
    KeepChannelsAsCameras = 1,
}

public sealed record NvrRemovalOutcome(bool Success, int ChannelsRemoved, int ChannelsPromoted, string? Error = null);

/// <summary>
/// Recorder onboarding and channel lifecycle.
///
/// A recorder is one device row plus N channel rows; channels are what the wall actually plays. Import is idempotent
/// per (device, channel number): importing the same recorder twice updates names and stream addresses and reports the
/// duplicates rather than creating a second set of tiles.
/// </summary>
public sealed class NvrOnboarding(IDeviceStore devices, ICameraStore cameras, ISecretStore secrets)
{
    /// <summary>Validates without touching the network, so the dialog can validate as the operator types.</summary>
    public static string? Validate(NvrRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DeviceOnboarding.Validate(new CameraRegistrationRequest(
            request.DisplayName,
            request.Address,
            request.Username,
            request.Password,
            request.UseTls,
            request.OnvifPort,
            request.RtspPort));
    }

    /// <summary>
    /// Persists a recorder and the selected channels. When the recorder already exists (same endpoint), it is reused:
    /// its channels are updated and any channel that is already present is reported as skipped.
    /// </summary>
    public async Task<ChannelImportOutcome> ImportAsync(
        NvrRegistrationRequest request,
        NvrDiscoveryResult discovery,
        IReadOnlyCollection<int> selectedChannels,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(discovery);

        var validation = Validate(request);
        if (validation is not null)
        {
            return new ChannelImportOutcome(false, null, 0, 0, [], validation);
        }

        if (!discovery.Success)
        {
            return new ChannelImportOutcome(
                false,
                null,
                0,
                0,
                [],
                discovery.ValidationError ?? discovery.Failure?.Explanation ?? "The recorder could not be read.");
        }

        var address = request.Address.Trim();
        var host = address;
        var rtspPort = request.RtspPort ?? 554;

        if (address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(address, UriKind.Absolute, out var manual))
        {
            host = manual.Host;
            rtspPort = request.RtspPort ?? manual.Port;
        }

        var onvifPort = request.OnvifPort ?? 80;
        var existing = await devices.FindByEndpointAsync(host, onvifPort, rtspPort, cancellationToken).ConfigureAwait(false);

        var selected = discovery.Channels
            .Where(channel => selectedChannels.Count == 0 || selectedChannels.Contains(channel.Channel))
            .ToList();

        if (selected.Count == 0)
        {
            return new ChannelImportOutcome(false, existing, 0, 0, [], "Select at least one channel to import.");
        }

        var deviceId = existing?.Id ?? Guid.NewGuid();
        var first = selected[0];

        var device = (existing ?? new Device(
            Id: deviceId,
            GroupId: null,
            DisplayName: request.DisplayName.Trim(),
            Host: host,
            OnvifPort: onvifPort,
            RtspPort: rtspPort,
            HttpPort: onvifPort,
            UseTls: request.UseTls,
            Username: null,
            SecretRef: null,
            Vendor: null,
            Model: null,
            Enabled: true,
            LastSeenUtc: now,
            CreatedUtc: now,
            UpdatedUtc: now)) with
        {
            DisplayName = request.DisplayName.Trim(),
            Host = host,
            OnvifPort = onvifPort,
            RtspPort = rtspPort,
            HttpPort = onvifPort,
            UseTls = request.UseTls,
            Username = string.IsNullOrWhiteSpace(request.Username) ? existing?.Username : request.Username.Trim(),
            Vendor = discovery.Information?.Manufacturer?.Trim() ?? existing?.Vendor,
            Model = discovery.Information?.Model?.Trim() ?? existing?.Model,
            Firmware = discovery.Information?.FirmwareVersion?.Trim() ?? existing?.Firmware,
            SerialNumber = discovery.Information?.SerialNumber?.Trim() ?? existing?.SerialNumber,
            DeviceTimeZone = discovery.SystemTime?.TimeZone ?? existing?.DeviceTimeZone,
            Enabled = true,
            LastSeenUtc = now,
            UpdatedUtc = now,
            StreamUriMain = first.MainUri ?? existing?.StreamUriMain,
            StreamUriSub = first.SubUri ?? existing?.StreamUriSub,
            Kind = DeviceKind.Nvr,
        };

        var password = request.Password;
        if (!string.IsNullOrEmpty(password))
        {
            var reference = secrets.Save(deviceId, password);
            device = device with { SecretRef = reference };
        }

        await devices.UpsertAsync(device, cancellationToken).ConfigureAwait(false);

        var (imported, updated, skipped) = await PersistChannelsAsync(deviceId, selected, now, cancellationToken).ConfigureAwait(false);

        return new ChannelImportOutcome(true, device, imported, updated, skipped);
    }

    /// <summary>Re-reads the channels of a recorder that is already saved (import new ones, refresh existing).</summary>
    public async Task<ChannelImportOutcome> ImportChannelsAsync(
        Guid deviceId,
        IReadOnlyList<DiscoveredChannel> channels,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var device = await devices.GetAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return new ChannelImportOutcome(false, null, 0, 0, [], "The recorder is no longer in the device list.");
        }

        if (channels.Count == 0)
        {
            return new ChannelImportOutcome(false, device, 0, 0, [], "The recorder reported no channels.");
        }

        var (imported, updated, skipped) = await PersistChannelsAsync(deviceId, channels, now, cancellationToken).ConfigureAwait(false);

        await devices.UpsertAsync(device with { LastSeenUtc = now, UpdatedUtc = now }, cancellationToken).ConfigureAwait(false);

        return new ChannelImportOutcome(true, device, imported, updated, skipped);
    }

    public async Task<NvrChannel?> RenameChannelAsync(Guid channelId, string name, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > DeviceOnboarding.MaxNameLength)
        {
            return null;
        }

        var existing = await cameras.GetAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        return await cameras.UpsertAsync(existing with { Name = name.Trim(), UpdatedUtc = now }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes one channel; the recorder and its other channels stay.</summary>
    public Task<bool> RemoveChannelAsync(Guid channelId, CancellationToken cancellationToken = default) =>
        cameras.DeleteAsync(channelId, cancellationToken);

    /// <summary>
    /// Removes a recorder. The operator's choice is explicit: take its channels with it, or keep each channel as a
    /// standalone camera. Keeping channels copies the stored password per channel, so removing one camera later cannot
    /// break the others.
    /// </summary>
    public async Task<NvrRemovalOutcome> RemoveNvrAsync(
        Guid deviceId,
        NvrRemovalMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var device = await devices.GetAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return new NvrRemovalOutcome(false, 0, 0, "The recorder is no longer in the device list.");
        }

        var channels = await cameras.GetForDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var promoted = 0;

        if (mode == NvrRemovalMode.KeepChannelsAsCameras)
        {
            var password = string.IsNullOrEmpty(device.SecretRef) ? null : secrets.Load(device.SecretRef);

            foreach (var channel in channels)
            {
                var id = Guid.NewGuid();

                var standalone = device with
                {
                    Id = id,
                    DisplayName = channel.Name,
                    Kind = DeviceKind.Camera,
                    StreamUriMain = channel.MainUri ?? device.StreamUriMain,
                    StreamUriSub = channel.SubUri ?? device.StreamUriSub,
                    RecordingSourceToken = channel.SourceToken,
                    SecretRef = null,
                    LastSeenUtc = now,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };

                if (!string.IsNullOrEmpty(password))
                {
                    standalone = standalone with { SecretRef = secrets.Save(id, password) };
                }

                await devices.UpsertAsync(standalone, cancellationToken).ConfigureAwait(false);
                promoted++;
            }
        }

        if (!string.IsNullOrEmpty(device.SecretRef))
        {
            secrets.Delete(device.SecretRef);
        }

        // Deleting the device cascades to its channels, so promoted copies must be written first (they are).
        await devices.DeleteAsync(deviceId, cancellationToken).ConfigureAwait(false);

        return new NvrRemovalOutcome(
            true,
            mode == NvrRemovalMode.RemoveEverything ? channels.Count : 0,
            promoted);
    }

    private async Task<(int Imported, int Updated, IReadOnlyList<string> Skipped)> PersistChannelsAsync(
        Guid deviceId,
        IReadOnlyList<DiscoveredChannel> channels,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var updated = 0;
        var skipped = new List<string>();

        foreach (var discovered in channels.OrderBy(channel => channel.Channel))
        {
            if (string.IsNullOrWhiteSpace(discovered.MainUri))
            {
                skipped.Add($"Channel {discovered.Channel}: no stream address");
                continue;
            }

            var existing = await cameras.FindByChannelAsync(deviceId, discovered.Channel, cancellationToken).ConfigureAwait(false);

            if (existing is not null &&
                string.Equals(existing.MainUri, discovered.MainUri, StringComparison.Ordinal) &&
                string.Equals(existing.SubUri, discovered.SubUri, StringComparison.Ordinal) &&
                string.Equals(existing.Name, discovered.Name, StringComparison.Ordinal))
            {
                skipped.Add($"Channel {discovered.Channel}: already imported");
                continue;
            }

            var channel = new NvrChannel(
                Id: existing?.Id ?? Guid.NewGuid(),
                NvrId: deviceId,
                Channel: discovered.Channel,
                Name: discovered.Name,
                ProfileToken: discovered.ProfileToken,
                SubProfileToken: discovered.SubProfileToken,
                MainUri: discovered.MainUri,
                SubUri: discovered.SubUri,
                SourceToken: discovered.SourceToken,
                Enabled: true,
                SortOrder: discovered.Channel,
                CreatedUtc: existing?.CreatedUtc ?? now,
                UpdatedUtc: now);

            await cameras.UpsertAsync(channel, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                imported++;
            }
            else
            {
                updated++;
            }
        }

        // Channels are derived data: the recorder is the source of truth. A channel it no longer reports (a wrong
        // number from an earlier import, or one the operator deleted on the recorder) would otherwise stay in the
        // wall forever and keep failing with whatever address it was saved with.
        var reported = channels.Select(channel => channel.Channel).ToHashSet();
        var stored = await cameras.GetForDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);

        foreach (var stale in stored.Where(channel => !reported.Contains(channel.Channel)))
        {
            await cameras.DeleteAsync(stale.Id, cancellationToken).ConfigureAwait(false);
        }

        return (imported, updated, skipped);
    }
}
