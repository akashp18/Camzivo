using Camzivo.Core.Devices;

namespace Camzivo.Desktop.ViewModels;

public sealed class DeviceDiagnosticsViewModel
{
    public DeviceDiagnosticsViewModel(DeviceDiagnosticReport report)
    {
        Report = report;
    }

    public DeviceDiagnosticReport Report { get; }

    public string Name => Report.Name;

    public string Address => Report.Address;

    public string Kind => Report.Kind;

    public string Mode => NvrSafety.ReadOnlyLabel;

    public string Identity => string.IsNullOrWhiteSpace(Report.Identity) ? "No identity returned" : Report.Identity;

    public string DeviceTime => Report.DeviceTimeUtc is { } utc
        ? $"{utc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local · {utc:yyyy-MM-dd HH:mm:ss} UTC"
        : "Device time not returned";

    public string TimeZone => string.IsNullOrWhiteSpace(Report.DeviceTimeZone) ? "Time zone unknown" : Report.DeviceTimeZone!;

    public string Channels => Report.ChannelCount == 1 ? "1 channel" : $"{Report.ChannelCount} channels";

    public IReadOnlyList<DeviceDiagnosticRow> Rows => Report.Rows;

    public string? LastError => Report.LastError;

    public bool HasLastError => !string.IsNullOrWhiteSpace(LastError);

    public string Probed => $"Checked {Report.ProbedUtc.ToLocalTime():HH:mm:ss}";
}
