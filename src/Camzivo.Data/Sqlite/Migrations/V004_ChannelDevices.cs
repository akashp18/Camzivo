using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Sqlite.Migrations;

/// <summary>
/// Removing a recorder can keep its channels as independent cameras, and those cameras necessarily share the
/// recorder's address. The old unique index on (host, onvif_port, rtsp_port) forbade that, so it becomes a plain
/// index; duplicate detection now happens in the import service, which knows the difference between "the same recorder
/// again" and "several channels of one recorder".
/// </summary>
internal static class V004_ChannelDevices
{
    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX IF EXISTS ux_device_endpoint;
            CREATE INDEX ix_device_endpoint ON device(lower(host), onvif_port, rtsp_port);
            """;
        command.ExecuteNonQuery();
    }
}
