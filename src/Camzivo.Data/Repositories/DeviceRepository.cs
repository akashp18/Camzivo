using System.Globalization;
using Camzivo.Core.Devices;
using Camzivo.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Repositories;

public interface IDeviceStore
{
    Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Device?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Device> UpsertAsync(Device device, CancellationToken cancellationToken = default);

    Task<Device?> FindByEndpointAsync(string host, int onvifPort, int rtspPort, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Device persistence. Every statement is parameterized; no user-supplied value is ever concatenated into SQL.
/// </summary>
public sealed class DeviceRepository(SqliteConnectionFactory factory) : IDeviceStore
{
    private const string SelectColumns = """
        id, group_id, display_name, host, onvif_port, rtsp_port, http_port, use_tls,
        username, secret_ref, vendor, model, enabled, last_seen_utc, created_utc, updated_utc,
        stream_uri_main, stream_uri_sub, kind, firmware, serial_number, device_time_zone,
        last_connection_error, last_diagnostic_utc, recording_source_token
        """;

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM device ORDER BY display_name;";

        var results = new List<Device>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<Device?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM device WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<Device> UpsertAsync(Device device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device (
              id, group_id, display_name, host, onvif_port, rtsp_port, http_port, use_tls,
              username, secret_ref, vendor, model, enabled, last_seen_utc, created_utc, updated_utc,
              stream_uri_main, stream_uri_sub, kind, firmware, serial_number, device_time_zone,
              last_connection_error, last_diagnostic_utc, recording_source_token)
            VALUES (
              $id, $group, $name, $host, $onvif, $rtsp, $http, $tls,
              $user, $secret, $vendor, $model, $enabled, $seen, $created, $updated,
              $main, $sub, $kind, $firmware, $serial, $zone, $lastError, $diagnosed, $recordingSource)
            ON CONFLICT(id) DO UPDATE SET
              group_id      = excluded.group_id,
              display_name  = excluded.display_name,
              host          = excluded.host,
              onvif_port    = excluded.onvif_port,
              rtsp_port     = excluded.rtsp_port,
              http_port     = excluded.http_port,
              use_tls       = excluded.use_tls,
              username      = excluded.username,
              secret_ref    = excluded.secret_ref,
              vendor        = excluded.vendor,
              model         = excluded.model,
              enabled       = excluded.enabled,
              last_seen_utc = excluded.last_seen_utc,
              updated_utc   = excluded.updated_utc,
              stream_uri_main = excluded.stream_uri_main,
              stream_uri_sub  = excluded.stream_uri_sub,
              kind            = excluded.kind,
              firmware        = excluded.firmware,
              serial_number   = excluded.serial_number,
              device_time_zone = excluded.device_time_zone,
              last_connection_error = excluded.last_connection_error,
              last_diagnostic_utc = excluded.last_diagnostic_utc,
              recording_source_token = excluded.recording_source_token;
            """;

        command.Parameters.AddWithValue("$id", device.Id.ToString());
        command.Parameters.AddWithValue("$group", (object?)device.GroupId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", device.DisplayName);
        command.Parameters.AddWithValue("$host", device.Host);
        command.Parameters.AddWithValue("$onvif", device.OnvifPort);
        command.Parameters.AddWithValue("$rtsp", device.RtspPort);
        command.Parameters.AddWithValue("$http", device.HttpPort);
        command.Parameters.AddWithValue("$tls", device.UseTls ? 1 : 0);
        command.Parameters.AddWithValue("$user", (object?)device.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret", (object?)device.SecretRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$vendor", (object?)device.Vendor ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)device.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", device.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$seen", (object?)device.LastSeenUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", device.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", device.UpdatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$main", (object?)device.StreamUriMain ?? DBNull.Value);
        command.Parameters.AddWithValue("$sub", (object?)device.StreamUriSub ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", device.Kind == DeviceKind.Nvr ? "nvr" : "camera");
        command.Parameters.AddWithValue("$firmware", (object?)device.Firmware ?? DBNull.Value);
        command.Parameters.AddWithValue("$serial", (object?)device.SerialNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$zone", (object?)device.DeviceTimeZone ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastError", (object?)device.LastConnectionError ?? DBNull.Value);
        command.Parameters.AddWithValue("$diagnosed", (object?)device.LastDiagnosticUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$recordingSource", (object?)device.RecordingSourceToken ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return device;
    }

    public async Task<Device?> FindByEndpointAsync(string host, int onvifPort, int rtspPort, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM device WHERE lower(host) = lower($host) AND onvif_port = $onvif AND rtsp_port = $rtsp LIMIT 1;";
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$onvif", onvifPort);
        command.Parameters.AddWithValue("$rtsp", rtspPort);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static Device Map(SqliteDataReader reader) => new(
        Id: Guid.Parse(reader.GetString(0)),
        GroupId: reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
        DisplayName: reader.GetString(2),
        Host: reader.GetString(3),
        OnvifPort: reader.GetInt32(4),
        RtspPort: reader.GetInt32(5),
        HttpPort: reader.GetInt32(6),
        UseTls: reader.GetInt32(7) == 1,
        Username: reader.IsDBNull(8) ? null : reader.GetString(8),
        SecretRef: reader.IsDBNull(9) ? null : reader.GetString(9),
        Vendor: reader.IsDBNull(10) ? null : reader.GetString(10),
        Model: reader.IsDBNull(11) ? null : reader.GetString(11),
        Enabled: reader.GetInt32(12) == 1,
        LastSeenUtc: reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CreatedUtc: DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc: DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        StreamUriMain: reader.IsDBNull(16) ? null : reader.GetString(16),
        StreamUriSub: reader.IsDBNull(17) ? null : reader.GetString(17),
        Kind: !reader.IsDBNull(18) && string.Equals(reader.GetString(18), "nvr", StringComparison.OrdinalIgnoreCase)
            ? DeviceKind.Nvr
            : DeviceKind.Camera,
        Firmware: reader.IsDBNull(19) ? null : reader.GetString(19),
        SerialNumber: reader.IsDBNull(20) ? null : reader.GetString(20),
        DeviceTimeZone: reader.IsDBNull(21) ? null : reader.GetString(21),
        LastConnectionError: reader.IsDBNull(22) ? null : reader.GetString(22),
        LastDiagnosticUtc: reader.IsDBNull(23) ? null : DateTimeOffset.Parse(reader.GetString(23), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        RecordingSourceToken: reader.IsDBNull(24) ? null : reader.GetString(24));
}
