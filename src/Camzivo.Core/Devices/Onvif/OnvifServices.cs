namespace Camzivo.Core.Devices.Onvif;

/// <summary>Service endpoints a device advertises. Null means the device did not advertise that service.</summary>
public sealed record OnvifServices(
    Uri? Media,
    Uri? Ptz,
    Uri? Replay,
    IReadOnlyList<string> Names,
    Uri? Recording = null,
    Uri? Search = null)
{
    public static OnvifServices None { get; } = new(null, null, null, []);

    public bool HasPtz => Ptz is not null;

    public bool HasReplay => Replay is not null;

    public bool HasRecording => Recording is not null;

    public bool HasSearch => Search is not null;

    public bool CanSearchAndReplay => Search is not null && Replay is not null;

    public string Summary => Names.Count == 0 ? "no service list" : string.Join(", ", Names);
}
