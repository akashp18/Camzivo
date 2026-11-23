using System.Globalization;
using Camzivo.Data.Sqlite;

namespace Camzivo.Data.Repositories;

/// <summary>
/// A completed evidence export. The file itself lives on disk; this row is the operator's record of what was exported,
/// from which camera and for which recorded range, so the Exports page never has to guess from file names.
/// </summary>
public sealed record ExportRecord(
    Guid Id,
    string CameraName,
    string Path,
    long Bytes,
    int Samples,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    DateTimeOffset CreatedUtc);

public sealed class ExportRepository(SqliteConnectionFactory factory)
{
    public async Task RecordAsync(ExportRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO export_entry (id, camera_name, path, bytes, samples, range_start, range_end, created_utc)
            VALUES ($id, $camera, $path, $bytes, $samples, $start, $end, $created)
            ON CONFLICT(id) DO UPDATE SET
              camera_name = excluded.camera_name,
              path = excluded.path,
              bytes = excluded.bytes,
              samples = excluded.samples,
              range_start = excluded.range_start,
              range_end = excluded.range_end,
              created_utc = excluded.created_utc;
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$camera", record.CameraName);
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$bytes", record.Bytes);
        command.Parameters.AddWithValue("$samples", record.Samples);
        command.Parameters.AddWithValue("$start", record.RangeStartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", record.RangeEndUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$created", record.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExportRecord>> ListAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, camera_name, path, bytes, samples, range_start, range_end, created_utc
            FROM export_entry
            ORDER BY created_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));

        var results = new List<ExportRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ExportRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return results;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM export_entry WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
