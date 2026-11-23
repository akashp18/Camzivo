namespace Camzivo.Core.Devices;

/// <summary>
/// One compatibility fact. A recorder can support live video and still be unsupported for recordings, PTZ or audio;
/// keeping each fact separate is what lets the UI say exactly what is verified.
/// </summary>
public sealed record DeviceDiagnosticRow(string Name, CapabilityState State, string Detail)
{
    public string StateText => State switch
    {
        CapabilityState.Supported => "SUPPORTED",
        CapabilityState.Unsupported => "UNSUPPORTED",
        _ => "UNKNOWN",
    };
}

/// <summary>
/// Read-only compatibility report for a saved device. It is intentionally separate from Live View: an operator opens
/// it when diagnosing a recorder, not while watching the wall.
/// </summary>
public sealed record DeviceDiagnosticReport(
    Guid DeviceId,
    string Name,
    string Address,
    string Kind,
    string? Manufacturer,
    string? Model,
    string? Firmware,
    string? SerialNumber,
    DateTimeOffset? DeviceTimeUtc,
    string? DeviceLocalTime,
    string? DeviceTimeZone,
    int ChannelCount,
    IReadOnlyList<DeviceDiagnosticRow> Rows,
    string? LastError,
    DateTimeOffset ProbedUtc)
{
    public string Identity => string.Join(" · ", new[]
    {
        Manufacturer,
        Model,
        Firmware is null ? null : $"fw {Firmware}",
        SerialNumber is null ? null : $"s/n {SerialNumber}",
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
