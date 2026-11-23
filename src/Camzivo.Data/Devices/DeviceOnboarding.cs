using Camzivo.Core.Devices;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Security;
using Camzivo.Data.Repositories;

namespace Camzivo.Data.Devices;

/// <summary>What the operator typed. Nothing here is trusted: the service normalises and validates it.</summary>
public sealed record CameraRegistrationRequest(
    string DisplayName,
    string Address,
    string? Username = null,
    string? Password = null,
    bool UseTls = false,
    int? OnvifPort = null,
    int? RtspPort = null)
{
    /// <summary>Never render or log this object directly: it can hold a password.</summary>
    public override string ToString() =>
        $"{nameof(CameraRegistrationRequest)} {{ Name = {DisplayName}, Address = {Address}, Username = {Username}, Password = *** }}";
}

public sealed record RegistrationOutcome(
    bool Success,
    Device? Device,
    IReadOnlyList<StageResult> Stages,
    Diagnostic? Failure,
    string? ValidationError = null);

/// <summary>
/// Add-camera workflow: validate → test (four stages) → store the password via DPAPI → persist the device.
///
/// A failed test persists nothing. That matters: a half-added camera in the wall is indistinguishable from a broken
/// one, and the operator would have no idea which state they are looking at.
/// </summary>
public sealed class DeviceOnboarding(IDeviceStore devices, ISecretStore secrets, IConnectionTester tester)
{
    public const int MaxNameLength = 120;

    /// <summary>Validates the request without touching the network, so the form can show errors as they are typed.</summary>
    public static string? Validate(CameraRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return "A name is required.";
        }

        if (request.DisplayName.Trim().Length > MaxNameLength)
        {
            return $"The name must be {MaxNameLength} characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(request.Address))
        {
            return "Enter an IP address, a host name, or an rtsp:// URL.";
        }

        var address = request.Address.Trim();

        if (address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var rtsp))
            {
                return "That is not a valid rtsp:// URL.";
            }

            if (!string.IsNullOrEmpty(rtsp.UserInfo))
            {
                return "Remove the username and password from the URL; enter them in the fields below so they can be stored securely.";
            }

            if (rtsp.IsDefaultPort && rtsp.Port is < 1 or > 65535)
            {
                return "The RTSP port is out of range.";
            }

            return null;
        }

        if (address.Contains("://", StringComparison.Ordinal))
        {
            return "Only rtsp:// URLs are accepted here; for ONVIF enter just the address.";
        }

        if (address.Contains(' ', StringComparison.Ordinal) || address.Contains('/', StringComparison.Ordinal) || address.Contains('@', StringComparison.Ordinal))
        {
            return "Enter just the address, without a path, spaces or credentials.";
        }

        if (request.OnvifPort is < 1 or > 65535)
        {
            return "The ONVIF port must be between 1 and 65535.";
        }

        if (request.RtspPort is < 1 or > 65535)
        {
            return "The RTSP port must be between 1 and 65535.";
        }

        return null;
    }

    /// <summary>
    /// Registers a camera. Passing <paramref name="preTested"/> skips the network test and persists that result, so
    /// the dialog's Test button and Save button cannot disagree about what was verified.
    /// </summary>
    public async Task<RegistrationOutcome> RegisterAsync(
        CameraRegistrationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        ConnectionTestResult? preTested = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = Validate(request);
        if (validation is not null)
        {
            return new RegistrationOutcome(false, null, [], null, validation);
        }

        var address = request.Address.Trim();
        var isRtsp = address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);

        var host = address;
        var rtspPort = request.RtspPort ?? 554;
        Uri? manualStream = null;

        if (isRtsp)
        {
            manualStream = new Uri(address);
            host = manualStream.Host;
            rtspPort = request.RtspPort ?? manualStream.Port;
        }

        var endpoint = new DeviceEndpoint(
            host,
            request.OnvifPort ?? 80,
            rtspPort,
            request.UseTls);

        var test = preTested ?? await tester.TestAsync(endpoint, request.Username, request.Password, cancellationToken)
            .ConfigureAwait(false);

        if (!test.Success)
        {
            return new RegistrationOutcome(false, null, test.Stages, test.Failure);
        }

        // A manual rtsp:// registration skips ONVIF: the operator supplied the URL, so only the transport matters.
        if (isRtsp && manualStream is not null)
        {
            test = test with { MainStream = manualStream };
        }

        var device = new Device(
            Id: Guid.NewGuid(),
            GroupId: null,
            DisplayName: request.DisplayName.Trim(),
            Host: host,
            OnvifPort: endpoint.OnvifPort,
            RtspPort: endpoint.RtspPort,
            HttpPort: endpoint.OnvifPort,
            UseTls: endpoint.UseTls,
            Username: string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
            SecretRef: null,
            Vendor: test.Information?.Manufacturer?.Trim(),
            Model: test.Information?.Model?.Trim(),
            Firmware: test.Information?.FirmwareVersion?.Trim(),
            SerialNumber: test.Information?.SerialNumber?.Trim(),
            DeviceTimeZone: test.SystemTime?.TimeZone,
            Enabled: true,
            LastSeenUtc: now,
            CreatedUtc: now,
            UpdatedUtc: now,
            StreamUriMain: test.MainStream?.AbsoluteUri,
            StreamUriSub: test.SubStream?.AbsoluteUri);

        if (!string.IsNullOrEmpty(request.Password))
        {
            var reference = secrets.Save(device.Id, request.Password);
            device = device with { SecretRef = reference };
        }

        await devices.UpsertAsync(device, cancellationToken).ConfigureAwait(false);

        return new RegistrationOutcome(true, device, test.Stages, null);
    }

    /// <summary>Renames a device, keeping everything else intact.</summary>
    public async Task<Device?> RenameAsync(Guid id, string newName, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim().Length > MaxNameLength)
        {
            return null;
        }

        var existing = await devices.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var updated = existing with
        {
            DisplayName = newName.Trim(),
            UpdatedUtc = now,
        };

        return await devices.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a device and its stored password together, so no orphan secret can remain.</summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await devices.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(existing.SecretRef))
        {
            secrets.Delete(existing.SecretRef);
        }

        return await devices.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
