using System.Globalization;
using Camzivo.Data.Sqlite;

namespace Camzivo.Data.Repositories;

/// <summary>How much attention an event entry deserves. Stored as an integer so ordering is meaningful.</summary>
public enum EventSeverity
{
    Information = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// One entry in the local event history. <see cref="CameraId"/> is a wall identity (a device or a recorder channel)
/// and may no longer exist; the name is kept so history stays readable after a device is removed.
/// </summary>
public sealed record EventRecord(
    long Id,
    DateTimeOffset OccurredUtc,
    Guid? CameraId,
    string CameraName,
    string Kind,
    EventSeverity Severity,
    string Message,
    string Detail)
{
    public string SeverityLabel => Severity switch
    {
        EventSeverity.Critical => "Critical",
        EventSeverity.Warning => "Warning",
        _ => "Info",
    };
}

/// <summary>Filter for the Events page. Empty fields mean "no restriction".</summary>
public sealed record EventQuery(
    Guid? CameraId = null,
    EventSeverity? MinimumSeverity = null,
    string? Search = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Limit = 500);

/// <summary>
/// Local event history with bounded retention.
///
/// The table deliberately has no foreign key to <c>camera</c>: a stream failure on a standalone device is worth
/// recording even though that device is not a recorder channel, and removing a recorder must not erase the evidence
/// that it was failing. Retention is enforced by the caller at startup, never by an unbounded background job.
/// </summary>
public sealed class EventRepository(SqliteConnectionFactory factory)
{
    public const int DefaultRetentionDays = 30;

    public async Task<long> AppendAsync(EventRecord entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_entry (occurred_utc, camera_id, camera_name, kind, severity, message, detail)
            VALUES ($at, $camera, $name, $kind, $severity, $message, $detail);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$at", entry.OccurredUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$camera", (object?)entry.CameraId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", entry.CameraName);
        command.Parameters.AddWithValue("$kind", entry.Kind);
        command.Parameters.AddWithValue("$severity", (int)entry.Severity);
        command.Parameters.AddWithValue("$message", entry.Message);
        command.Parameters.AddWithValue("$detail", entry.Detail);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<EventRecord>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, 2000);
        var conditions = new List<string>();

        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();

        if (query.CameraId is { } camera)
        {
            conditions.Add("camera_id = $camera");
            command.Parameters.AddWithValue("$camera", camera.ToString());
        }

        if (query.MinimumSeverity is { } severity)
        {
            conditions.Add("severity >= $severity");
            command.Parameters.AddWithValue("$severity", (int)severity);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add("(message LIKE $search OR camera_name LIKE $search OR kind LIKE $search)");
            command.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
        }

        if (query.FromUtc is { } from)
        {
            conditions.Add("occurred_utc >= $from");
            command.Parameters.AddWithValue("$from", from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        if (query.ToUtc is { } to)
        {
            conditions.Add("occurred_utc <= $to");
            command.Parameters.AddWithValue("$to", to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);

        command.CommandText = $"""
            SELECT id, occurred_utc, camera_id, camera_name, kind, severity, message, detail
            FROM event_entry
            {where}
            ORDER BY occurred_utc DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<EventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new EventRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                (EventSeverity)reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return results;
    }

    /// <summary>Deletes entries older than the retention window. Returns how many rows were removed.</summary>
    public async Task<int> PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM event_entry WHERE occurred_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", olderThanUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Number of stored events; used by the shell to keep the history claim honest.</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM event_entry;";

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
