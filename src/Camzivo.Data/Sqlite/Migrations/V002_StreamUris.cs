using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Sqlite.Migrations;

/// <summary>
/// Stores the stream URIs discovered during onboarding. Without them the wall would have to run an ONVIF round trip
/// before every attach, which is both slow and fragile (a device can be streaming but temporarily refuse SOAP).
/// </summary>
public static class V002_StreamUris
{
    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var statement in new[]
                 {
                     "ALTER TABLE device ADD COLUMN stream_uri_main TEXT;",
                     "ALTER TABLE device ADD COLUMN stream_uri_sub TEXT;",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
