using Camzivo.Core.Streaming;

namespace Camzivo.Core.Devices;

public enum CapabilityState
{
    Unverified = 0,
    Supported = 1,
    Unsupported = 2,
}

public sealed record DeviceEndpoint(string Host, int OnvifPort, int RtspPort, bool UseTls)
{
    public string Display => UseTls
        ? $"https://{Host}:{OnvifPort}"
        : $"http://{Host}:{OnvifPort}";
}

/// <summary>
/// A device as persisted. Secrets are never part of this record: <see cref="SecretRef"/> is a key into the
/// DPAPI-protected store.
/// </summary>
public sealed record Device(
    Guid Id,
    Guid? GroupId,
    string DisplayName,
    string Host,
    int OnvifPort,
    int RtspPort,
    int HttpPort,
    bool UseTls,
    string? Username,
    string? SecretRef,
    string? Vendor,
    string? Model,
    bool Enabled,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? StreamUriMain = null,
    string? StreamUriSub = null,
    DeviceKind Kind = DeviceKind.Camera,
    string? Firmware = null,
    string? SerialNumber = null,
    string? DeviceTimeZone = null,
    string? LastConnectionError = null,
    DateTimeOffset? LastDiagnosticUtc = null,
    string? RecordingSourceToken = null)
{
    public StreamUriSet Uris => new(StreamUriMain, StreamUriSub);
    public DeviceEndpoint Endpoint => new(Host, OnvifPort, RtspPort, UseTls);

    public static Device CreateNew(string displayName, string host, DateTimeOffset now) => new(
        Id: Guid.NewGuid(),
        GroupId: null,
        DisplayName: displayName,
        Host: host,
        OnvifPort: 80,
        RtspPort: 554,
        HttpPort: 80,
        UseTls: false,
        Username: null,
        SecretRef: null,
        Vendor: null,
        Model: null,
        Enabled: true,
        LastSeenUtc: null,
        CreatedUtc: now,
        UpdatedUtc: now,
        StreamUriMain: null,
        StreamUriSub: null,
        Kind: DeviceKind.Camera);
}

/// <summary>Negotiated capabilities. Unverified is the default: absence of evidence is not support.</summary>
public sealed record DeviceCapabilities(
    CapabilityState Ptz,
    CapabilityState Events,
    CapabilityState Replay,
    CapabilityState Snapshot,
    IReadOnlyList<string> Encodings,
    IReadOnlySet<Streaming.RtspTransport> Transports,
    DateTimeOffset? ProbedUtc)
{
    public static DeviceCapabilities Unknown { get; } = new(
        CapabilityState.Unverified,
        CapabilityState.Unverified,
        CapabilityState.Unverified,
        CapabilityState.Unverified,
        Array.Empty<string>(),
        new HashSet<Streaming.RtspTransport> { Streaming.RtspTransport.Tcp },
        null);
}
