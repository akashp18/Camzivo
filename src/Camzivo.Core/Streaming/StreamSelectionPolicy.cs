namespace Camzivo.Core.Streaming;

public enum StreamQuality
{
    Auto = 0,
    Main = 1,
    Sub = 2,
}

public enum RtspTransport
{
    Tcp = 0,
    Udp = 1,
}

/// <summary>Main/substream URIs as negotiated from the device. Never contains credentials.</summary>
public sealed record StreamUriSet(string? Main, string? Sub)
{
    public bool HasMain => !string.IsNullOrWhiteSpace(Main);

    public bool HasSub => !string.IsNullOrWhiteSpace(Sub);

    public bool HasAny => HasMain || HasSub;

    public static StreamUriSet Empty { get; } = new(null, null);
}

/// <summary>Why the policy picked a stream. Surfaced in the tile and in health so a downgrade is never silent.</summary>
public enum StreamSelectionReason
{
    NoStream = 0,
    UserSelectedMain,
    UserSelectedSub,
    MainUnavailableUsingSub,
    SubUnavailableUsingMain,
    AutoFullscreenMain,
    AutoLargeTileMain,
    AutoSmallTileSub,
    AutoOnlyStreamAvailable,
}

public sealed record StreamSelectionContext(
    StreamQuality Preference,
    bool IsFullScreen,
    int TileWidth,
    int TileHeight,
    bool IsVisible,
    StreamUriSet Uris);

public sealed record StreamSelection(StreamQuality Quality, StreamSelectionReason Reason)
{
    public bool HasStream => Reason != StreamSelectionReason.NoStream;
}

/// <summary>
/// Decides main vs substream for one tile.
/// Contract: an explicit <see cref="StreamQuality.Main"/> preference is never silently downgraded — when main
/// is unavailable the result says so via <see cref="StreamSelectionReason.MainUnavailableUsingSub"/> and the UI
/// must show a persistent warning.
/// </summary>
public static class StreamSelectionPolicy
{
    /// <summary>A tile at or above this size is worth the main stream in Auto mode (≈480×270 at 1080p).</summary>
    public const int LargeTileMinWidth = 480;

    public const int LargeTileMinHeight = 270;

    public static StreamSelection Select(StreamSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Uris);

        var uris = context.Uris;

        if (!uris.HasAny)
        {
            return new StreamSelection(StreamQuality.Auto, StreamSelectionReason.NoStream);
        }

        return context.Preference switch
        {
            StreamQuality.Main => SelectMain(uris),
            StreamQuality.Sub => SelectSub(uris),
            _ => SelectAuto(context, uris),
        };
    }

    private static StreamSelection SelectMain(StreamUriSet uris)
    {
        if (uris.HasMain)
        {
            return new StreamSelection(StreamQuality.Main, StreamSelectionReason.UserSelectedMain);
        }

        return uris.HasSub
            ? new StreamSelection(StreamQuality.Sub, StreamSelectionReason.MainUnavailableUsingSub)
            : new StreamSelection(StreamQuality.Auto, StreamSelectionReason.NoStream);
    }

    private static StreamSelection SelectSub(StreamUriSet uris)
    {
        if (uris.HasSub)
        {
            return new StreamSelection(StreamQuality.Sub, StreamSelectionReason.UserSelectedSub);
        }

        return uris.HasMain
            ? new StreamSelection(StreamQuality.Main, StreamSelectionReason.SubUnavailableUsingMain)
            : new StreamSelection(StreamQuality.Auto, StreamSelectionReason.NoStream);
    }

    private static StreamSelection SelectAuto(StreamSelectionContext context, StreamUriSet uris)
    {
        if (uris.HasMain && context.IsFullScreen)
        {
            return new StreamSelection(StreamQuality.Main, StreamSelectionReason.AutoFullscreenMain);
        }

        var isLargeTile = context.IsVisible
            && context.TileWidth >= LargeTileMinWidth
            && context.TileHeight >= LargeTileMinHeight;

        if (uris.HasMain && isLargeTile)
        {
            return new StreamSelection(StreamQuality.Main, StreamSelectionReason.AutoLargeTileMain);
        }

        if (uris.HasSub)
        {
            return new StreamSelection(StreamQuality.Sub, StreamSelectionReason.AutoSmallTileSub);
        }

        return new StreamSelection(StreamQuality.Main, StreamSelectionReason.AutoOnlyStreamAvailable);
    }
}
