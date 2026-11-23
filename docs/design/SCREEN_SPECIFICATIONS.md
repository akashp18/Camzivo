# Screen Specifications — Camzivo

Each screen lists: purpose, layout, states, data source, keyboard, acceptance criteria, and what exists today.
"Implemented" means the rendered build was inspected; "specced" means it is a target for a named milestone.

Global frame: 56 px nav rail · 34 px command bar · content · 28 px status bar. No page title above content except
on Settings and Health (where the first row is genuine information, not decoration).

---

## 1. Live View

**Purpose:** watch many cameras without thinking about the application.

**Layout:** left panel 200–320 px (resizable, remembers width) with search field and camera tree; centre = wall;
wall toolbar 30 px (layout chips, selected-tile actions, summary on the right).

**States**
| State | Rendering |
|---|---|
| No cameras | wall replaced by one onboarding panel: "No cameras yet" + `Add a camera` (primary) + `Find cameras on this network` (secondary, M3) |
| Cameras, none placed | 9 tiles with `CLICK A CAMERA TO ASSIGN` |
| Placed | tile with name + quality chip + status word |
| Beyond decode budget | `QUEUED · waiting for decode slot` |
| Selected | 1 px accent border; Clear tile enabled |
| Engine absent (today) | `PREVIEW · sample content` and status bar `demo preview · no decoder connected` |

**Data:** `LiveViewModel` (cameras, slot assignments, layout, scheduler plan). Placement persisted under
`ui.wall`.

**Keyboard:** `Ctrl+1..8` layout · `F11`/`Ctrl+F` fullscreen · `Esc` exit fullscreen · arrows move camera-list
focus · `Enter` place focused camera · `Delete` clear selected tile · `Ctrl+Z` undo last wall change · `/` focus
search.

**Acceptance:** window resizes without re-layout of tile content; double-click a tile toggles fullscreen; the wall
restores exactly after restart; no tile can show `LIVE` without a decoded frame.

**Today:** layout, click-to-place, selection, honest states verified; fullscreen, persistence, undo, onboarding,
fullscreen-by-double-click are being added or missing (see `BUG_AUDIT.md`).

## 2. Playback

**Purpose:** answer "what happened at 14:32" in under three actions.

**Layout:** left camera selector (multi-select up to 4); centre wall (1/2×2); bottom timeline 120 px (24 h ruler,
availability shading, event ticks, playhead); transport strip (speed, step, snapshot, export).

**States:** no recording system configured → explain and offer `Connect a recorder`; loading ranges; no data in
range → shaded gap + "No recording for this period"; playback error → inline reason + retry.

**Acceptance:** selecting a camera loads a day of availability in ≤ 1.5 s locally; seeking is ≤ 300 ms to first
frame; export of 10 min completes with a manifest and an explicit completion marker; time shown is camera time,
with the offset displayed when it differs from the workstation.

**Today:** placeholder page (M5).

## 3. Cameras & Devices

**Purpose:** bring cameras in, fix them, and see what is wrong at a glance.

**Layout:** toolbar (`Add camera`, `Discover`, `Import CSV`); table grouped by group: name, address, model, stream,
state, last seen; right drawer with details/capabilities/test actions.

**States:** empty → `Discover` primary; duplicate detected → row warning; testing → 4-stage progress
(reach → authenticate → describe → first frame); failure → `ConnectionDiagnostics` wording with actions.

**Acceptance:** adding a Dahua camera end-to-end takes ≤ 6 fields; a wrong password produces
`Camera rejected the credentials` with the three documented next steps, never a generic error; a deleted device
disappears from the wall and from saved views without leaving orphan slots.

**Today:** demo table; disabled `Add device`; schema + repository ready (M3).

## 4. Events

**Purpose:** see what happened, act, and jump to the moment.

**Layout:** filter bar (type, camera, time range, unacknowledged); list (time, camera, type, severity, ack);
detail pane with snapshot thumbnail and `Open in Playback`.

**States:** none (with "Events appear here when a camera goes offline or reports motion"), filtered-empty,
acknowledged filter.

**Acceptance:** an offline camera creates exactly one event and one recovery event (no flapping spam); clicking
`Open in Playback` opens the right camera at `t−10 s`; acknowledging is idempotent and audited.

**Today:** placeholder (M6).

## 5. Recordings & Exports

**Purpose:** produce evidence.

**Layout:** two tabs: `Recordings` (what exists on devices, by camera and day) and `Exports` (queue with status,
path, size, hash, `Open folder`).

**States:** empty queue; running with cancel; failed with reason and retry; completed with manifest path.

**Acceptance:** an export is never shown as complete before its file is flushed and hashed; filenames carry camera,
start and end time; a cancelled export leaves no partial file in the final folder.

**Today:** placeholder (M5).

## 6. Camera Health

**Purpose:** make it obvious why a camera is not working.

**Layout:** attention summary; per-camera diagnostic cards (severity bar + word, cause, 2–3 actions); collapsed
"All healthy" state; link to `View logs`.

**States:** all healthy (calm, one line), some failing, engine absent.

**Acceptance:** every failure mode in `ConnectionDiagnostics` renders in operator language with actions; severity is
colour **and** word; a card links to Playback for the same camera/time.

**Today:** implemented with synthetic input; severity bar colour-only (U12); no log link (no logger yet).

## 7. Settings

**Purpose:** the small number of things an operator may change.

**Layout:** single column, max 860 px: Appearance (Dark/Light segmented), Live view (decode budget, default
layout, remember wall), Storage (paths, `Open folder`), Diagnostics (log level, log path, `Copy diagnostics`),
Credentials (policy statement + `Manage stored credentials` in M3), About (version, build date, licence).

**States:** demo → "Demo mode: nothing on this screen is written to your profile."; preference write failure →
inline warning once.

**Acceptance:** every setting either applies immediately or states that it needs a restart; a corrupt preference
never blocks startup (falls back to defaults, logs once); storage paths are copyable.

**Today:** appearance/storage/decode/credentials/version implemented; decode budget and default layout are
read-only statements (M4); no diagnostics section.
