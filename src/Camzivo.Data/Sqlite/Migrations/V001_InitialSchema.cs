using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Sqlite.Migrations;

/// <summary>
/// Schema v1. Mirrors <c>docs/DATABASE_SCHEMA.md</c>; the two must change together.
/// Secrets never appear here: <c>device.secret_ref</c> is a key into the DPAPI store.
/// </summary>
internal static class V001_InitialSchema
{
    internal const string Sql = """
        CREATE TABLE camera_group (
          id            TEXT PRIMARY KEY,
          name          TEXT NOT NULL,
          parent_id     TEXT REFERENCES camera_group(id) ON DELETE CASCADE,
          sort_order    INTEGER NOT NULL DEFAULT 0,
          created_utc   TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_group_sibling_name ON camera_group(IFNULL(parent_id,''), name);

        CREATE TABLE device (
          id            TEXT PRIMARY KEY,
          group_id      TEXT REFERENCES camera_group(id) ON DELETE SET NULL,
          display_name  TEXT NOT NULL,
          host          TEXT NOT NULL,
          onvif_port    INTEGER NOT NULL DEFAULT 80,
          rtsp_port     INTEGER NOT NULL DEFAULT 554,
          http_port     INTEGER NOT NULL DEFAULT 80,
          use_tls       INTEGER NOT NULL DEFAULT 0,
          username      TEXT,
          secret_ref    TEXT,
          vendor        TEXT,
          model         TEXT,
          firmware      TEXT,
          enabled       INTEGER NOT NULL DEFAULT 1,
          last_seen_utc TEXT,
          created_utc   TEXT NOT NULL,
          updated_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_device_group ON device(group_id, display_name);
        CREATE UNIQUE INDEX ux_device_endpoint ON device(lower(host), onvif_port, rtsp_port);

        CREATE TABLE camera (
          id             TEXT PRIMARY KEY,
          device_id      TEXT NOT NULL REFERENCES device(id) ON DELETE CASCADE,
          channel        INTEGER NOT NULL DEFAULT 1,
          name           TEXT NOT NULL,
          profile_token  TEXT,
          sub_token      TEXT,
          rtsp_main      TEXT,
          rtsp_sub       TEXT,
          stream_pref    INTEGER NOT NULL DEFAULT 0,
          transport      INTEGER NOT NULL DEFAULT 0,
          enabled        INTEGER NOT NULL DEFAULT 1,
          sort_order     INTEGER NOT NULL DEFAULT 0,
          created_utc    TEXT NOT NULL,
          updated_utc    TEXT NOT NULL
        );
        CREATE INDEX ix_camera_device ON camera(device_id, channel);
        CREATE UNIQUE INDEX ux_camera_channel ON camera(device_id, channel);

        CREATE TABLE device_capability (
          device_id      TEXT PRIMARY KEY REFERENCES device(id) ON DELETE CASCADE,
          profiles_json  TEXT NOT NULL DEFAULT '[]',
          ptz            INTEGER NOT NULL DEFAULT 0,
          events         INTEGER NOT NULL DEFAULT 0,
          replay         INTEGER NOT NULL DEFAULT 0,
          snapshot       INTEGER NOT NULL DEFAULT 0,
          encodings      TEXT NOT NULL DEFAULT '',
          transports     TEXT NOT NULL DEFAULT 'TCP',
          probed_utc     TEXT
        );

        CREATE TABLE workspace_layout (
          id           TEXT PRIMARY KEY,
          name         TEXT NOT NULL,
          preset       INTEGER NOT NULL,
          rows         INTEGER NOT NULL,
          cols         INTEGER NOT NULL,
          is_default   INTEGER NOT NULL DEFAULT 0,
          updated_utc  TEXT NOT NULL
        );

        CREATE TABLE layout_slot (
          layout_id   TEXT NOT NULL REFERENCES workspace_layout(id) ON DELETE CASCADE,
          slot_index  INTEGER NOT NULL,
          camera_id   TEXT REFERENCES camera(id) ON DELETE SET NULL,
          PRIMARY KEY (layout_id, slot_index)
        );

        CREATE TABLE preference (
          key         TEXT PRIMARY KEY,
          value       TEXT NOT NULL,
          updated_utc TEXT NOT NULL
        );

        CREATE TABLE event (
          id           TEXT PRIMARY KEY,
          camera_id    TEXT REFERENCES camera(id) ON DELETE CASCADE,
          kind         TEXT NOT NULL,
          severity     INTEGER NOT NULL DEFAULT 1,
          occurred_utc TEXT NOT NULL,
          payload_json TEXT NOT NULL DEFAULT '{}',
          acknowledged INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_event_time ON event(occurred_utc DESC);
        CREATE INDEX ix_event_camera_time ON event(camera_id, occurred_utc DESC);

        CREATE TABLE export_job (
          id            TEXT PRIMARY KEY,
          camera_id     TEXT REFERENCES camera(id) ON DELETE SET NULL,
          started_utc   TEXT NOT NULL,
          range_start   TEXT NOT NULL,
          range_end     TEXT NOT NULL,
          state         TEXT NOT NULL,
          output_path   TEXT,
          bytes_written INTEGER NOT NULL DEFAULT 0,
          failure       TEXT
        );
        CREATE INDEX ix_export_state ON export_job(state, started_utc DESC);

        CREATE TABLE connection_history (
          id          INTEGER PRIMARY KEY AUTOINCREMENT,
          device_id   TEXT REFERENCES device(id) ON DELETE CASCADE,
          started_utc TEXT NOT NULL,
          ended_utc   TEXT,
          outcome     TEXT NOT NULL,
          detail      TEXT
        );
        CREATE INDEX ix_conhist_device ON connection_history(device_id, started_utc DESC);

        CREATE TABLE audit_entry (
          id      INTEGER PRIMARY KEY AUTOINCREMENT,
          at_utc  TEXT NOT NULL,
          actor   TEXT NOT NULL,
          action  TEXT NOT NULL,
          subject TEXT NOT NULL,
          detail  TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX ix_audit_time ON audit_entry(at_utc DESC);

        CREATE TABLE app_log (
          id      INTEGER PRIMARY KEY AUTOINCREMENT,
          at_utc  TEXT NOT NULL,
          level   TEXT NOT NULL,
          scope   TEXT NOT NULL,
          message TEXT NOT NULL,
          detail  TEXT
        );
        CREATE INDEX ix_applog_time ON app_log(at_utc DESC);
        """;

    public static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }
}
