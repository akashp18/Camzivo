# Interaction Patterns — Camzivo

Rules for behaviour that repeats across the product. When a screen disagrees with this file, the screen is wrong
unless this file is updated in the same change.

---

## 1. Placement (camera → tile)

- **Click a camera** in the tree: places it in the first empty slot, else replaces the selected slot.
- **Double-click / Enter** on a focused camera: same placement (the keyboard equivalent).
- **Click a tile**: selects it. Selection is the replacement target and the subject of tile actions.
- Placing always announces the result in the tile's own header (name + quality chip) — no toast.
- Placement is **persisted** (`ui.wall`) and restored before the first frame is presented.
- **Undo**: `Ctrl+Z` restores the wall to the state before the last placement, clear or layout change (in-memory
  stack of 20; cleared on exit).

Rejected alternative: drag-and-drop only. Drag is added in M3 as an *additional* path because it fails on
touchpads, remote sessions and for keyboard users.

## 2. Selection

- Exactly one tile is selected, always (default slot 0). "Nothing selected" is not a state, which removes a class
  of null checks in the UI.
- Selection survives layout changes by slot index; if the new layout is smaller, selection clamps into range.
- Selected tile is the only place destructive tile actions apply.

## 3. Destructive actions

| Action | Confirmation | Undo |
|---|---|---|
| Clear tile | none (cheap, reversible) | `Ctrl+Z` |
| Remove camera | inline confirm in the row ("Remove? Keep / Remove") | none for device data (audited) |
| Cancel export | none | partial file removed |
| Reset settings | modal, states what is kept | none |

Never a modal for something reversible. Never a silent destructive action.

## 4. Keyboard map (target)

| Key | Action | State |
|---|---|---|
| `Ctrl+1..8` | layout 1/4/9/16/25/36/49/64 | implemented |
| `Ctrl+F`, `F11` | toggle fullscreen | implemented, probe-verified |
| `Esc` | leave fullscreen; close overlay | implemented |
| `/` | focus camera search | specced |
| `↑`/`↓` | move in camera list | specced |
| `Enter` | place focused camera | specced |
| `Delete` | clear selected tile | implemented (ignored while a text box has focus) |
| `Ctrl+Z` | undo wall change (20 steps) | implemented, unit-tested |
| `Ctrl+T` | toggle theme | implemented |
| `?` | shortcut overlay | specced |
| `Space` | play/pause (Playback only) | M5 |
| `,`/`.` | step frame (Playback only) | M5 |

Rules: shortcuts never fire while a text field has focus (except `Esc`); every shortcut is discoverable in the `?`
overlay; no shortcut requires the numeric keypad.

## 5. Long-running operations

- Anything over 400 ms shows determinate progress if a total is known, otherwise an indeterminate bar **with
  elapsed seconds**.
- Cancellable by default; the cancel button stays enabled until the operation reports done.
- Never block the dispatcher: work happens on a background task; only the final state update returns to the UI.
- Failures leave the system in the state it was in before the operation started.

## 6. Loading, empty and error states

| Situation | Pattern |
|---|---|
| First paint of a tile | dark surface + `CONNECTING` + elapsed seconds after 2 s |
| Empty collection | one sentence + one primary action, centred, no illustration |
| Filter with no matches | `No cameras match "xyz".` + `Clear search` |
| Recoverable failure | inline message in the affected element + `Retry` |
| Trust-changing failure | name banner (persistence, engine) + link to Health |
| Fatal | written to log, banner on next start, never a raw stack trace in the UI |

## 7. Language (from the naming review)

| Current | Proposed | Reason |
|---|---|---|
| Devices | **Cameras & Devices** | Operators look for cameras; recorders are the minority |
| System Health | **Camera Health** | The page is about cameras, not the OS |
| Clear slot | **Clear tile** | "Slot" is internal vocabulary |
| NO STREAM ENGINE | **Video engine not started** | Chip text is a sentence in the operator's language |
| MEM 123 MB · 13 THR | (removed) → Settings → Diagnostics | Not operator information |
| demo preview · no decoder connected | **Sample data · no video engine** | Same meaning, no jargon |
| Reconnect all | kept | Standard term, universally understood |
| QUEUED | **Waiting for decode slot** | Explains itself; keeps `QUEUED` as a tile chip only |

Kept unchanged because they are industry standard and clearer than alternatives: **Live View**, **Playback**,
**Events**, **Recordings & Exports**, **PTZ**, **Substream** (with a tooltip: "lower-resolution copy from the
camera"), **Export**.

## 8. Notifications

Local only. No email, no cloud, no sound by default. A notification is used for: camera offline > 30 s, export
completed, export failed. Everything else is visible in place.

## 9. Multi-monitor (target, M6)

- The wall can be detached to a chosen monitor (`Move wall to display ▸`), fullscreen, chrome-free, with a small
  translucent overlay that fades on mouse idle.
- Layout is per display; the saved view records the display assignment.
- Moving between displays with different DPI must not resize tile content or reset selection.
