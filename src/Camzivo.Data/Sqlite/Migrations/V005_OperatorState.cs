using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Sqlite.Migrations;

/// <summary>
/// Schema v5: operator-facing state that outlives a session.
///
/// The v1 tables (<c>workspace_layout</c>, <c>event</c>) reference <c>camera</c>, which only holds recorder channels.
/// A standalone camera's identity is a <c>device</c>, and keeping a wall layout or an event must not require a camera
/// row (or cascade it away when a recorder is removed). These tables therefore store the wall/event identity as text
/// with no foreign key; the shell resolves missing cameras itself and says so, instead of silently dropping history.
/// </summary>
internal static class V005_OperatorState
{
    internal const string Sql = """
        CREATE TABLE wall_layout (
          id           TEXT PRIMARY KEY,
          name         TEXT NOT NULL,
          grid_slots   INTEGER NOT NULL,
          slots_json   TEXT NOT NULL DEFAULT '[]',
          created_utc  TEXT NOT NULL,
          updated_utc  TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_wall_layout_name ON wall_layout(name COLLATE NOCASE);

        CREATE TABLE event_entry (
          id           INTEGER PRIMARY KEY AUTOINCREMENT,
          occurred_utc TEXT NOT NULL,
          camera_id    TEXT,
          camera_name  TEXT NOT NULL,
          kind         TEXT NOT NULL,
          severity     INTEGER NOT NULL DEFAULT 0,
          message      TEXT NOT NULL,
          detail       TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX ix_event_entry_time ON event_entry(occurred_utc DESC);
        CREATE INDEX ix_event_entry_camera ON event_entry(camera_id, occurred_utc DESC);

        CREATE TABLE export_entry (
          id            TEXT PRIMARY KEY,
          camera_name   TEXT NOT NULL,
          path          TEXT NOT NULL,
          bytes         INTEGER NOT NULL,
          samples       INTEGER NOT NULL,
          range_start   TEXT NOT NULL,
          range_end     TEXT NOT NULL,
          created_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_export_entry_time ON export_entry(created_utc DESC);
        """;

    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }
}
