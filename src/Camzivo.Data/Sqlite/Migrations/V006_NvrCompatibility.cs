using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Sqlite.Migrations;

/// <summary>
/// Real-recorder compatibility facts that must survive a restart: firmware/serial identity, the recorder's time zone,
/// the last connection error, and the Profile G source token that maps a channel to its recordings.
/// </summary>
internal static class V006_NvrCompatibility
{
    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var statement in new[]
                 {
                     "ALTER TABLE device ADD COLUMN serial_number TEXT;",
                     "ALTER TABLE device ADD COLUMN device_time_zone TEXT;",
                     "ALTER TABLE device ADD COLUMN last_connection_error TEXT;",
                     "ALTER TABLE device ADD COLUMN last_diagnostic_utc TEXT;",
                     "ALTER TABLE device ADD COLUMN recording_source_token TEXT;",
                     "ALTER TABLE camera ADD COLUMN source_token TEXT;",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
