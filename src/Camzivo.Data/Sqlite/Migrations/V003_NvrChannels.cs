using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Sqlite.Migrations;

/// <summary>
/// Recorders are devices with channels. The device row already holds host/ports/credentials, so the only new device
/// fact is what kind of device it is; channels live in the existing <c>camera</c> table.
/// </summary>
internal static class V003_NvrChannels
{
    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var statement in new[]
                 {
                     "ALTER TABLE device ADD COLUMN kind TEXT NOT NULL DEFAULT 'camera';",
                     "ALTER TABLE camera ADD COLUMN source TEXT NOT NULL DEFAULT 'manual';",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX ix_device_kind ON device(kind); CREATE INDEX ix_camera_source ON camera(source);";
        index.ExecuteNonQuery();
    }
}
