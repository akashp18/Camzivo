namespace Camzivo.Core.Recording;

/// <summary>A contiguous stretch of stored video. Times are UTC; the UI converts for display.</summary>
public sealed record RecordingSegment(
    string RecordingToken,
    string? TrackToken,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? SourceName = null,
    string? SourceToken = null)
{
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;

    public bool Contains(DateTimeOffset atUtc) => atUtc >= StartUtc && atUtc < EndUtc;

    /// <summary>Clamps a requested position into the segment, so a seek can never leave the recording.</summary>
    public DateTimeOffset Clamp(DateTimeOffset atUtc) =>
        atUtc < StartUtc ? StartUtc : atUtc >= EndUtc ? EndUtc.AddTicks(-1) : atUtc;
}

/// <summary>What a recorder said about one day, including the gaps the operator must be able to see.</summary>
public sealed record RecordingAvailability(
    Guid CameraId,
    DateOnly LocalDate,
    IReadOnlyList<RecordingSegment> Segments,
    string TimeZoneId,
    DateTimeOffset QueriedUtc,
    string? UnsupportedReason = null,
    string? Error = null)
{
    public bool IsSupported => UnsupportedReason is null;

    public bool Failed => Error is not null;

    public TimeSpan TotalRecorded =>
        Segments.Aggregate(TimeSpan.Zero, (total, segment) => total + segment.Duration);

    /// <summary>The day in the operator's time zone, as UTC bounds. DST days are 23 or 25 hours long, by design.</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) DayBounds(DateOnly date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);

        return (
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), zone));
    }
}

/// <summary>Validates a requested recording window before any network call.</summary>
public static class RecordingWindow
{
    public const int MaxDays = 7;

    public static string? Validate(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc)
        {
            return "The end of the range must be after its start.";
        }

        if (endUtc - startUtc > TimeSpan.FromDays(MaxDays))
        {
            return $"A search can cover at most {MaxDays} days.";
        }

        if (startUtc > DateTimeOffset.UtcNow.AddDays(1))
        {
            return "That range is in the future.";
        }

        return null;
    }
}
