using System.Collections.ObjectModel;
using Camzivo.Core.Diagnostics;

namespace Camzivo.Desktop.ViewModels;

public sealed record HealthRowViewModel(
    string Camera,
    string Code,
    string Title,
    string Explanation,
    string Actions,
    bool IsCritical)
{
    /// <summary>Severity as a word, so the coloured bar is reinforcement rather than the only signal.</summary>
    public string SeverityLabel => IsCritical ? "Critical" : "Warning";
}

/// <summary>
/// Operational health. Rows are produced by the same diagnostics engine that will report real stream faults,
/// so the wording reviewed here is the wording operators will see under pressure.
/// </summary>
public sealed class HealthViewModel : ObservableObject
{
    private readonly bool _demo;
    private string _summary = "No active faults";

    public HealthViewModel(bool demo)
    {
        _demo = demo;
    }

    public ObservableCollection<HealthRowViewModel> Rows { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool HasRows => Rows.Count > 0;

    public bool IsDemo => _demo;

    /// <summary>
    /// Real health, from the wall: only a camera that is actually failing appears here. A camera that is simply
    /// connecting is reported as a warning, not as an outage.
    /// </summary>
    public void LoadFromTiles(IEnumerable<CameraTileViewModel> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        if (_demo)
        {
            Load();
            return;
        }

        Rows.Clear();

        foreach (var tile in tiles.Where(tile => tile.HasCamera))
        {
            switch (tile.Status)
            {
                case TileStatus.Offline:
                    Rows.Add(new HealthRowViewModel(
                        tile.Name,
                        "Offline",
                        "Camera offline",
                        string.IsNullOrWhiteSpace(tile.Detail) ? "The stream failed and has not recovered." : tile.Detail,
                        "Check the address and credentials · Reconnect from Devices · Try Snapshot after it recovers",
                        IsCritical: true));
                    break;

                case TileStatus.Queued:
                    Rows.Add(new HealthRowViewModel(
                        tile.Name,
                        "Queued",
                        "Waiting for a decode slot",
                        "This tile is beyond the decode budget, so no decoder is running for it.",
                        "Close unused tiles · Raise the decode budget in Settings",
                        IsCritical: false));
                    break;

                case TileStatus.Waiting when !string.IsNullOrWhiteSpace(tile.Detail):
                    Rows.Add(new HealthRowViewModel(
                        tile.Name,
                        "Connecting",
                        "Stream not established",
                        tile.Detail,
                        "Wait for the next reconnect · Check the device is reachable · Reconnect from Devices",
                        IsCritical: false));
                    break;
            }
        }

        Summary = Rows.Count switch
        {
            0 => "No active faults",
            1 => "1 camera needs attention",
            _ => $"{Rows.Count} cameras need attention",
        };

        Raise(nameof(HasRows));
    }

    public void Load()
    {
        Rows.Clear();

        if (_demo)
        {
            Rows.Add(ToRow("Gate North", ConnectionDiagnostics.Explain(
                StreamFailure.Unauthorized,
                "DESCRIBE rtsp://admin:***@192.168.18.11:554/cam/realmonitor -> 401"), isCritical: true));

            Rows.Add(ToRow("Warehouse Aisle 3", ConnectionDiagnostics.Explain(
                StreamFailure.Timeout,
                "RTSP DESCRIBE timed out after 10000 ms"), isCritical: false));

            Rows.Add(ToRow("Car Park East", ConnectionDiagnostics.Explain(
                StreamFailure.BandwidthInsufficient,
                "presented 1840/2500 frames over 60 s"), isCritical: false));
        }

        Summary = Rows.Count switch
        {
            0 => "No active faults",
            1 => "1 camera needs attention",
            _ => $"{Rows.Count} cameras need attention",
        };

        Raise(nameof(HasRows));
    }

    private static HealthRowViewModel ToRow(string camera, Diagnostic diagnostic, bool isCritical) => new(
        camera,
        Humanize(diagnostic.CodeText),
        diagnostic.Title,
        diagnostic.Explanation,
        string.Join(" · ", diagnostic.Actions),
        isCritical);

    /// <summary>Enum codes read as words in the chip: "BandwidthInsufficient" becomes "Bandwidth insufficient".</summary>
    private static string Humanize(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code;
        }

        var builder = new System.Text.StringBuilder(code.Length + 4);
        for (var index = 0; index < code.Length; index++)
        {
            if (index > 0 && char.IsUpper(code[index]) && !char.IsUpper(code[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(index == 0 ? code[index] : char.ToLowerInvariant(code[index]));
        }

        return builder.ToString();
    }
}
