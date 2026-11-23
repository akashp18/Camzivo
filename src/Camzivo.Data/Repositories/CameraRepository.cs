using System.Globalization;
using Camzivo.Core.Devices;
using Camzivo.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Repositories;

public interface ICameraStore
{
    Task<IReadOnlyList<NvrChannel>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NvrChannel>> GetForDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<NvrChannel?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<NvrChannel?> FindByChannelAsync(Guid deviceId, int channel, CancellationToken cancellationToken = default);

    Task<NvrChannel> UpsertAsync(NvrChannel channel, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Channels of recorders, and standalone cameras, in one table. The channel identity is (device, channel number) and
/// the database enforces it, so re-importing a recorder updates its channels instead of duplicating them.
/// </summary>
public sealed class CameraRepository(SqliteConnectionFactory factory) : ICameraStore
{
    private const string SelectColumns = """
        id, device_id, channel, name, profile_token, sub_token, rtsp_main, rtsp_sub,
        enabled, sort_order, created_utc, updated_utc, source_token
        """;

    public async Task<IReadOnlyList<NvrChannel>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM camera ORDER BY device_id, sort_order, channel;";

        var results = new List<NvrChannel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<NvrChannel>> GetForDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM camera WHERE device_id = $device ORDER BY sort_order, channel;";
        command.Parameters.AddWithValue("$device", deviceId.ToString());

        var results = new List<NvrChannel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<NvrChannel?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM camera WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<NvrChannel?> FindByChannelAsync(Guid deviceId, int channel, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM camera WHERE device_id = $device AND channel = $channel;";
        command.Parameters.AddWithValue("$device", deviceId.ToString());
        command.Parameters.AddWithValue("$channel", channel);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<NvrChannel> UpsertAsync(NvrChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO camera (
              id, device_id, channel, name, profile_token, sub_token, rtsp_main, rtsp_sub,
              enabled, sort_order, created_utc, updated_utc, source_token)
            VALUES (
              $id, $device, $channel, $name, $profile, $subToken, $main, $sub,
              $enabled, $sort, $created, $updated, $source)
            ON CONFLICT(id) DO UPDATE SET
              name        = excluded.name,
              channel     = excluded.channel,
              profile_token = excluded.profile_token,
              sub_token   = excluded.sub_token,
              rtsp_main   = excluded.rtsp_main,
              rtsp_sub    = excluded.rtsp_sub,
              enabled     = excluded.enabled,
              sort_order  = excluded.sort_order,
              updated_utc = excluded.updated_utc,
              source_token = excluded.source_token;
            """;

        command.Parameters.AddWithValue("$id", channel.Id.ToString());
        command.Parameters.AddWithValue("$device", channel.NvrId.ToString());
        command.Parameters.AddWithValue("$channel", channel.Channel);
        command.Parameters.AddWithValue("$name", channel.Name);
        command.Parameters.AddWithValue("$profile", (object?)channel.ProfileToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$subToken", (object?)channel.SubProfileToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$main", (object?)channel.MainUri ?? DBNull.Value);
        command.Parameters.AddWithValue("$sub", (object?)channel.SubUri ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", channel.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$sort", channel.SortOrder);
        command.Parameters.AddWithValue("$created", channel.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", channel.UpdatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", (object?)channel.SourceToken ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return channel;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM camera WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static NvrChannel Map(SqliteDataReader reader) => new(
        Id: Guid.Parse(reader.GetString(0)),
        NvrId: Guid.Parse(reader.GetString(1)),
        Channel: reader.GetInt32(2),
        Name: reader.GetString(3),
        ProfileToken: reader.IsDBNull(4) ? null : reader.GetString(4),
        SubProfileToken: reader.IsDBNull(5) ? null : reader.GetString(5),
        MainUri: reader.IsDBNull(6) ? null : reader.GetString(6),
        SubUri: reader.IsDBNull(7) ? null : reader.GetString(7),
        Enabled: reader.GetInt32(8) == 1,
        SortOrder: reader.GetInt32(9),
        CreatedUtc: DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc: DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        SourceToken: reader.IsDBNull(12) ? null : reader.GetString(12));
}
