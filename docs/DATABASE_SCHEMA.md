# Database Schema and Persistence

Engine: SQLite 3 (WAL), `Microsoft.Data.Sqlite`. Location: `%APPDATA%\Camzivo\camzivo.db` (per user, never under
`Program Files`). Schema version tracked with `PRAGMA user_version`; every migration is a numbered, transactional,
forward-only step with an automatic pre-migration backup.

Secrets are **not** in the database. `devices.secret_ref` is an opaque key into the DPAPI-protected secret store.

---

## 1. Entity relationship overview

```
camera_group 1───* device
device       1───* camera              (an NVR has many channels; an IPC has one)
device       1───1 device_capability   (negotiated matrix, JSON columns per area)
camera       *───* workspace_layout    via layout_slot
workspace_layout 1──* layout_slot
camera       1───* event
camera       1───* connection_history
export_job   *───1 camera              (ranged export)
audit_entry  (append-only, no FK)
preference   (key/value, per user)
app_log      (rolling, pruned by retention)
```

## 2. DDL (v1, as implemented in `V001_InitialSchema`)

```sql
CREATE TABLE camera_group (
  id            TEXT PRIMARY KEY,          -- GUID
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
  secret_ref    TEXT,                      -- key into DPAPI store, never a password
  vendor        TEXT,                      -- 'dahua' | 'generic' | null (detected)
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
  channel        INTEGER NOT NULL DEFAULT 1,   -- NVR channel
  name           TEXT NOT NULL,
  profile_token  TEXT,                          -- ONVIF media profile for main
  sub_token      TEXT,                          -- ONVIF media profile for sub
  rtsp_main      TEXT,
  rtsp_sub       TEXT,
  stream_pref    INTEGER NOT NULL DEFAULT 0,    -- 0 Auto, 1 Main, 2 Sub
  transport      INTEGER NOT NULL DEFAULT 0,    -- 0 TCP, 1 UDP
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
  ptz            INTEGER NOT NULL DEFAULT 0,    -- 0 unverified, 1 supported, 2 unsupported
  events         INTEGER NOT NULL DEFAULT 0,
  replay         INTEGER NOT NULL DEFAULT 0,
  snapshot       INTEGER NOT NULL DEFAULT 0,
  encodings      TEXT NOT NULL DEFAULT '',      -- 'H264,H265'
  transports     TEXT NOT NULL DEFAULT 'TCP',
  probed_utc     TEXT
);

CREATE TABLE workspace_layout (
  id           TEXT PRIMARY KEY,
  name         TEXT NOT NULL,
  preset       INTEGER NOT NULL,                -- LayoutPreset or -1 for custom
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
-- Keys in use (v1): 'ui.theme' = dark|light, 'ui.layout' = 1|4|9|16|25|36|49|64,
-- 'ui.section' = NavSection name. Unknown keys are ignored, never fatal.

CREATE TABLE event (
  id          TEXT PRIMARY KEY,
  camera_id   TEXT REFERENCES camera(id) ON DELETE CASCADE,
  kind        TEXT NOT NULL,                    -- 'motion' | 'tamper' | 'linecross' | 'offline' ...
  severity    INTEGER NOT NULL DEFAULT 1,
  occurred_utc TEXT NOT NULL,
  payload_json TEXT NOT NULL DEFAULT '{}',
  acknowledged INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_event_time ON event(occurred_utc DESC);
CREATE INDEX ix_event_camera_time ON event(camera_id, occurred_utc DESC);

CREATE TABLE export_job (
  id           TEXT PRIMARY KEY,
  camera_id    TEXT REFERENCES camera(id) ON DELETE SET NULL,
  started_utc  TEXT NOT NULL,
  range_start  TEXT NOT NULL,
  range_end    TEXT NOT NULL,
  state        TEXT NOT NULL,        -- queued|running|succeeded|failed|cancelled
  output_path  TEXT,
  bytes_written INTEGER NOT NULL DEFAULT 0,
  failure      TEXT
);
CREATE INDEX ix_export_state ON export_job(state, started_utc DESC);

CREATE TABLE connection_history (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  device_id   TEXT REFERENCES device(id) ON DELETE CASCADE,
  started_utc TEXT NOT NULL,
  ended_utc   TEXT,
  outcome     TEXT NOT NULL,   -- connected|auth_failed|timeout|refused|codec_unsupported
  detail      TEXT             -- redacted
);
CREATE INDEX ix_conhist_device ON connection_history(device_id, started_utc DESC);

CREATE TABLE audit_entry (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  at_utc     TEXT NOT NULL,
  actor      TEXT NOT NULL,      -- windows user (Environment.UserName)
  action     TEXT NOT NULL,      -- device.add, device.credential.update, export.create ...
  subject    TEXT NOT NULL,
  detail     TEXT NOT NULL DEFAULT ''
);
CREATE INDEX ix_audit_time ON audit_entry(at_utc DESC);

CREATE TABLE app_log (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  at_utc    TEXT NOT NULL,
  level     TEXT NOT NULL,
  scope     TEXT NOT NULL,
  message   TEXT NOT NULL,
  detail    TEXT
);
CREATE INDEX ix_applog_time ON app_log(at_utc DESC);
```

## 3. Index strategy

| Query | Index |
|---|---|
| Camera tree render (grouped, sorted) | `ix_device_group`, `ux_group_sibling_name` |
| Device lookup by address during discovery dedupe | `ux_device_endpoint` (case-insensitive host) |
| Camera list per device | `ix_camera_device` |
| Event feed page 1 | `ix_event_time` |
| Events for one camera over a range | `ix_event_camera_time` |
| Active export list | `ix_export_state` |
| Connection history for diagnostics | `ix_conhist_device` |
| Log tail | `ix_applog_time` |

No index on `layout_slot` beyond the primary key: layouts are read whole, max 64 rows.

## 4. Migration strategy

- `MigrationRunner` reads `PRAGMA user_version`, applies each migration with a higher version inside one
  transaction, then bumps `user_version`.
- Before applying anything, it copies `camzivo.db` to `camzivo.db.bak-<version>-<utc>` (SQLite backup API, not a file
  copy, so WAL content is included).
- On failure: roll back the transaction, keep the backup, open the app in read-only mode with a banner naming the
  failing migration and the backup path. Never leave a half-migrated schema.
- Migrations are forward-only and immutable once released; a mistake is fixed by a new migration, never by editing
  an old one.
- Migration tests run every version chain (v0→vN, vN-1→vN) against a temp file and assert data preservation
  (`testing/TEST_STRATEGY.md` MT-1..MT-4).

## 5. Transaction boundaries

| Operation | Boundary |
|---|---|
| Add device + its cameras + capability row | single transaction (a device never exists without cameras) |
| Delete device | single transaction, `ON DELETE CASCADE` |
| Save layout | single transaction (delete + insert slots) |
| Discovery import of N devices | one transaction per device; a bad device does not roll back the batch |
| Event ingestion | batched insert, 1 transaction per 100 events or 1 s, whichever first |
| Export progress | separate short transaction per update (throttled to 1 Hz) |

## 6. Retention

| Table | Policy | Enforcement |
|---|---|---|
| `app_log` | 7 days, max 200k rows | nightly prune at startup |
| `connection_history` | 30 days | nightly prune |
| `event` | 90 days (configurable 7–365) | nightly prune, acknowledged flag respected |
| `export_job` | keep rows 1 year, files never auto-deleted | manual cleanup UI |
| `audit_entry` | **never pruned** (security value) | export-only |
| `camera`, `device`, `layout` | never pruned | user-managed |
| `preference` | current value per key, overwritten in place | user-managed |
| `camzivo.db.bak-v*` (files, not tables) | newest **5** kept, pruned on every launch | `MigrationRunner.PruneBackups` |

All pruning is bounded (`DELETE ... WHERE id IN (SELECT ... LIMIT 5000)`) so it never blocks for long.

## 7. Backup and restore

- `Camzivo → Settings → Data` exposes "Back up now" (SQLite online backup API → timestamped `.db` in a chosen
  folder), "Restore", and the current paths.
- Restore validates schema version, takes a safety backup, then swaps; requires app restart (stated in UI).
- Secrets are DPAPI per-user, so a restored database on another Windows user cannot decrypt credentials — the UI
  says so and offers re-entry rather than failing silently.

## 8. Consistency with `Core`

Repositories map rows to `Core` records explicitly (no attributes, no reflection). `Core` types are immutable
records, so a loaded camera cannot be mutated by accident; updates go through repository methods that write and
return the new instance.
