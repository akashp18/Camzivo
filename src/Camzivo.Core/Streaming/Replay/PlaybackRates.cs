using System.Globalization;

namespace Camzivo.Core.Streaming.Replay;

/// <summary>
/// The speeds the playback engine agrees to request. A recorder may not honour them; <see cref="PlaybackRateMeter"/>
/// is how the application tells the difference between a requested speed and the speed the source is actually
/// delivering.
/// </summary>
public static class PlaybackRates
{
    public static readonly IReadOnlyList<double> Supported = [1, 2, 4, 6, 8, 16];

    public static bool IsSupported(double rate) =>
        Supported.Any(candidate => Math.Abs(candidate - rate) < 0.001);

    /// <summary>Rounds a requested rate to the nearest supported speed, or 1× when it is unusable.</summary>
    public static double Normalize(double requested)
    {
        if (!double.IsFinite(requested) || requested <= 0)
        {
            return 1;
        }

        return Supported.OrderBy(rate => Math.Abs(rate - requested)).First();
    }

    public static string Label(double rate) =>
        $"{rate.ToString("0.####", CultureInfo.InvariantCulture)}×";

    /// <summary>
    /// How far the measured rate may sit from the requested rate before the speed is reported as not delivered.
    /// Normal playback has a tighter tolerance because its measurement window is short; accelerated playback is
    /// allowed more because a recorder commonly starts with a burst after PLAY.
    /// </summary>
    public static double Tolerance(double requested) => IsSupported(requested) && requested <= 1 ? 0.15 : 0.25;

    public static bool IsDelivered(double requested, double observed) =>
        observed > 0 && Math.Abs(observed - requested) <= requested * Tolerance(requested);
}

/// <summary>
/// Measures effective source playback speed from source timestamps and a monotonic wall clock. It deliberately
/// never derives speed from a frame count: a recorder may repeat or drop frames, while its RTP clock is the
/// recording-time truth used for the playhead.
/// </summary>
public sealed class PlaybackRateMeter
{
    public const double MeasurementWindowSeconds = 1.0;

    private bool _anchored;
    private double _sourceAnchor;
    private double _wallAnchor;
    private double _observed;

    /// <summary>0 until a full measurement window has elapsed.</summary>
    public double ObservedRate => _observed;

    public bool HasMeasurement => _observed > 0;

    public void Reset()
    {
        _anchored = false;
        _observed = 0;
    }

    /// <summary>
    /// Adds a sample. <paramref name="wallSeconds"/> must be monotonic for this session. Returns the latest measured
    /// rate, or 0 while the first window is still filling.
    /// </summary>
    public double Sample(double sourceSeconds, double wallSeconds)
    {
        if (!_anchored)
        {
            _sourceAnchor = sourceSeconds;
            _wallAnchor = wallSeconds;
            _anchored = true;
            return 0;
        }

        var wallDelta = wallSeconds - _wallAnchor;
        var sourceDelta = sourceSeconds - _sourceAnchor;

        // A backwards source timestamp is a discontinuity (reconnect or a recorder clock reset), not negative
        // playback. Start a new measurement instead of reporting a nonsensical rate.
        if (wallDelta < 0 || sourceDelta < -0.05)
        {
            Reset();
            _sourceAnchor = sourceSeconds;
            _wallAnchor = wallSeconds;
            _anchored = true;
            return 0;
        }

        if (wallDelta < MeasurementWindowSeconds)
        {
            return _observed;
        }

        _observed = Math.Clamp(sourceDelta / wallDelta, 0, 64);
        _sourceAnchor = sourceSeconds;
        _wallAnchor = wallSeconds;
        return _observed;
    }
}

/// <summary>Outcome of asking the recorder/session to change speed. Requested and effective are kept separate.</summary>
public sealed record PlaybackRateChange(bool Accepted, double RequestedRate, double EffectiveRate, string Detail);
