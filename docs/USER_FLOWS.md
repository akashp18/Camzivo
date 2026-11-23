# User Flows

Every flow states: entry point, steps, what the user sees, what can go wrong, and where the recovery lives. These
are the acceptance scripts for UI work; the M2 shell implements the flows marked ✅.

---

## 1. First run (no devices)

1. Launch → window appears in ≤1.5 s (target) with Live View active and an `EmptyState`:
   *"No cameras yet — Add a device, or discover devices on this network."* with two actions.
2. Nav rail shows all sections; Devices badge shows `0`. Status bar reads `0 streams · engine idle`.
3. No login dialog, no wizard, no license nag. Egress: none.

Failure: config file unreadable → banner *"Settings could not be read; defaults are active. Backup kept at …"*,
app still usable.

## 2. Discover and add a device ✅ (UI shell) / M3 (network)

1. Devices → **Discover** → scanning state with a cancel button and a live count.
2. Results table: IP, model, ONVIF profile (S/T/?), MAC, already-added flag.
3. Select a row → side panel: host/ports prefilled, credentials empty, **Test** enabled.
4. `Test` runs four stages, each shown separately:
   `Reach host ✓ → ONVIF auth ✓ → media profiles ✓ (2) → first frame ✓ (312 ms)`.
5. Name + group → **Add** → device and its cameras appear; Live View tree updates immediately.
6. If a stage fails: the exact failing stage turns red with an actionable `Diagnostic` (e.g. "ONVIF is disabled in
   the camera's network settings"), and other fields stay filled so the user can retry without retyping.

Manual path: Devices → **Add manually** → same form, no discovery, empty address validation is field-level.

## 3. Build a monitor wall

1. Live View → layout preset `9` (`Ctrl+3`) → nine empty slots with `Drop a camera` placeholders.
2. **Milestone 2:** click a camera in the tree → it takes the first empty slot; click a tile to select it, then
   **Clear slot** (or click another camera while the wall is full) to replace it. Placed cameras get a dot in the
   tree. Drag-and-drop with a drop ghost is Milestone 3 and replaces this click path, not the other way round.
3. Small tiles request substreams → chips read `SUB`; a camera without a substream reads `MAIN`; demo content reads
   `PREVIEW`, and tiles beyond the decode budget read `QUEUED`.
4. Double-click a tile → fullscreen that camera, main stream requested; `Esc` returns to the 9-grid.
5. **Save as workspace** → name prompt inline (no modal), persists layout + assignment + per-camera stream prefs.
6. Restart app → same wall restored, no flicker of empty tiles (layout restored before streams open).

Failure: a camera is offline on restore → its tile shows `OFFLINE` with the last known name and a `Retry` action;
the other eight streams are unaffected.

## 4. Investigate a recording (M5)

1. Playback → pick camera(s) → date picker defaults to today; timeline draws segments, gaps hatched.
2. Click a gap → tooltip *"No recording 14:02–15:11 (device gap)"* instead of an empty seek.
3. Drag playhead → frame preview at pointer; release to seek. Seek latency target ≤700 ms p95.
4. Add a second camera → both play in sync; a chip per camera reports offset (`+1.4 s`), because sync is a claim
   we only make when the sources agree.
5. Keyboard: `Space` play/pause, `←/→` segment jump, `Shift+←/→` frame step where supported (disabled with reason
   otherwise).
6. Select range → **Export** → non-modal task in status bar: progress, bytes, cancel; on completion a toast with
   `Open folder`.

Failure: camera disconnects mid-playback → that pane shows `RECONNECTING` + attempt count; the other pane keeps
playing; timeline segments marked stale.

## 5. Diagnose a stream problem ✅ (Health UI) / M3+ (real probing)

1. Operator sees an amber tile with `RECONNECTING · attempt 2`.
2. Health shows the row: camera, failure code, explanation, actions, `Copy diagnostics`.
3. Example: *"Camera 12 is reachable, but the RTSP stream could not be opened. Check stream credentials, port
   settings, and NVR permissions."* → actions: retry with TCP, edit device, test credentials.
4. `Retry` acts on the row (and all selected rows); the tile transitions visibly through `CONNECTING` stages
   instead of spinning anonymously.

## 6. Switch a camera to main stream permanently

1. Tile context menu → **Stream** → `Auto (currently SUB)` / `Main` / `Sub`.
2. Choose `Main`: tile reconnects with main; chip becomes `MAIN`; a note appears in Health if bandwidth is tight —
   but the preference is honoured.
3. If main is unavailable, tile shows `MAIN UNAVAILABLE` chip with a `Use substream` action; it does **not**
   silently show substream behind a `MAIN` label.

## 7. Export evidence (M5)

1. Playback → range selected → **Export** → format (MP4 copy / re-mux), path (default `Videos\Camzivo`).
2. Job appears in Recordings & Exports with state; can continue in foreground.
3. On finish: `export_job` row `succeeded`, sidecar JSON written (camera, device, range, timezone, SHA-256), audit
   entry created.
4. Cancel → partial file removed; row `cancelled`; no orphaned `.tmp`.

## 8. Change appearance

1. Settings → Appearance → `Dark` / `Light` → applies instantly, persists.
2. All tokens remap; no view hardcodes a colour (verified by a token-only rule in review).

## 9. Recovery from a bad startup

| Symptom | User sees | Under the hood |
|---|---|---|
| Database corrupt | banner: "The database could not be opened. A backup was made at … (Restore / Start empty)" | rename corrupt file, start fresh, keep backup |
| Migration failed | banner naming migration + backup path, app read-only | transaction rolled back, backup restored |
| GPU device lost | tiles show `RECOVERING` then resume, or banner "Switched to software rendering" | device recreated; after 2 failures, software mode |
| Second instance | first window raised, no duplicate state | single-instance mutex |

## 10. Shutdown

1. Closing the window cancels all streams (TEARDOWN), disposes decoders, flushes logs, writes config atomically.
2. If an export is running: prompt "Export in progress — wait, cancel, or continue in background?" with no default
   destructive choice.
3. Window closes within 500 ms even if a socket is unresponsive (network teardown is fire-and-forget on exit).
