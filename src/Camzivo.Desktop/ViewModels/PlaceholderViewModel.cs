namespace Camzivo.Desktop.ViewModels;

/// <summary>
/// Sections whose engine arrives in a later milestone. They state what is planned and when, instead of showing
/// fake numbers or an empty panel with no explanation.
/// </summary>
public sealed class PlaceholderViewModel(string title, string summary, string milestone, IReadOnlyList<string> planned) : ObservableObject
{
    public string Title { get; } = title;

    public string Summary { get; } = summary;

    public string Milestone { get; } = milestone;

    public IReadOnlyList<string> Planned { get; } = planned;

    public static PlaceholderViewModel Playback() => new(
        "Playback",
        "Recording search, a gap-aware timeline and multi-camera synchronised playback are built in Milestone 5, on top of the same stream engine.",
        "Milestone 5",
        [
            "Calendar search with per-device recording availability",
            "Timeline that distinguishes a recording gap from an empty selection",
            "Frame stepping where the source supports it, disabled with a reason when it does not",
            "Multi-camera sync with the real offset reported per camera",
        ]);

    public static PlaceholderViewModel Events() => new(
        "Events",
        "Event ingestion arrives with ONVIF PullPoint support in Milestone 6. The database schema and event timeline markers are already defined.",
        "Milestone 6",
        [
            "ONVIF PullPoint subscription with reconnect",
            "Event markers on the playback timeline",
            "Acknowledge workflow with an audit entry",
            "Optional per-event sounds, off by default",
        ]);

    public static PlaceholderViewModel Exports() => new(
        "Recordings & Exports",
        "Evidence export arrives with playback in Milestone 5: range export, progress, cancellation and a SHA-256 sidecar so an export can be verified later.",
        "Milestone 5",
        [
            "Range export with progress and cancel",
            "Original timestamps preserved in the container",
            "SHA-256 recorded in the sidecar and the audit log",
            "Output re-opened and verified before the job is marked succeeded",
        ]);

    public static PlaceholderViewModel Settings() => new(
        "Settings",
        "Theme switching and storage paths are live now. Device defaults, decode budget and retention controls follow the features they configure.",
        "Milestone 2 → 6",
        [
            "Appearance: dark and light tokens, applied without restart",
            "Data: database and log locations, backup and restore",
            "Decode: automatic budget with an override and the measured reason",
            "Retention: log, event and connection-history windows",
        ]);
}
