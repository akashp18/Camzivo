# Camzivo Improvements Over SmartPSS Lite

Scope rule: every item below is an improvement to a workflow Camzivo actually implements, with a concrete mechanism.
Items that depend on the video engine are marked **M4+** because they cannot be built before frames exist.

## Live view

| Improvement | Mechanism | Why it is better | Status |
|---|---|---|---|
| LIVE means a presented frame | `CameraTileViewModel` only reaches LIVE after the renderer reports a presented frame; connection success alone leaves the tile in Connecting | SmartPSS-style "online" dots claim health the picture does not support | State machine exists; renderer hook in M4 |
| Sub stream by default, main on demand | Tile scale decides the requested profile; fullscreen/spotlight promotes to main; demotion is debounced | 16+ tiles at 1080p main streams is the classic VMS CPU sink | Profile discovery done; policy hook in M4 |
| Switching costs one decoder restart, not a reconnect storm | `StreamScheduler` (tested) owns one connection per camera; tiles borrow frames, never open their own | Duplicate connections are the most common cause of NVR session limits | Scheduler exists |
| Layout changes are instant | Slot assignment + undo are pure functions (tested); tiles are reused, not recreated | No flicker, no re-subscription while dragging | Done |
| Any layout, everywhere | One wall component for live, playback and events (1–64 slots) | SmartPSS's event centre is limited to 1/4/9 splits | Codec supports 1–64; pages in M5/M6 |
| Status you can act on | Four stage results with plain-language reasons (unauthorized / refused / DNS / timeout / not ONVIF) | "Offline" costs a support call | Core done; dialog wiring pending |

## Playback (M5)

- **Timeline first, dialog last.** Selecting a camera loads the day immediately; the search form is an advanced
  filter. Seeking is a drag, not a form submission.
- **Per-camera lanes.** Multiple cameras share one time axis with aligned cursors, instead of switching a single
  player between channels.
- **Event ticks on the timeline.** Events from the event store are drawn where they happened; clicking one seeks
  there.
- **Export is a job, not a modal.** Choose range → job runs with progress and cancel → the file is verified playable
  before it is reported as done.
- **Frame-accurate stepping.** Arrow keys step one frame; the timestamp shown is the frame's PTS, not the wall clock.

## Camera management

- **Onboarding is a vertical flow, not a wizard maze:** Add → Connect → Authenticate → Test stream → Save, with the
  four stages visible as they complete. The current `DeviceOnboarding` service already fails atomically: nothing is
  saved when the test fails, so there is no half-added camera.
- **Credentials can never be pasted into the URL.** `rtsp://user:pass@host` is rejected with an explanation and the
  fields are used instead, so the password lands in DPAPI rather than in a device string.
- **Bulk import from an NVR (M3.5):** one address, one credential, all channels listed with resolution and encoding,
  each enabled individually.
- **Rename/group without touching the connection.** Rename is a row update (tested), not a re-registration.
- **Remove means remove.** Deleting a device deletes its stored secret in the same operation (tested); no orphan
  ciphertext.

## Monitoring

- **Health as history, not a dot:** last successful contact, latency, reconnect count, last failure reason.
- **Reconnect with backoff and a budget:** a camera that is off the network does not spin the CPU reconnecting.
- **Diagnostics page that answers "why":** the stage that failed, the exact endpoint, and the operator-facing reason.
- **Resource visibility (M6):** decoders active, frames dropped per stream, queue depth — the numbers needed to tell
  "the camera is bad" from "the PC is overloaded".

## User experience

- **Four sections, no modules:** Live, Playback, Devices, Health. Device configuration lives inside the device.
- **Keyboard first:** `1`–`9` layouts, arrows to move selection, `F` fullscreen, `Esc` to leave, `/` to search a
  camera by name.
- **One interaction pattern:** every list supports select, multi-select, context menu, and drag; no page-specific
  rules.
- **Plain language:** "Sign in failed — the camera rejected the username or password", not "error 401".
- **Themes that do not fight the video:** neutral chrome in both themes so tile borders and OSD stay readable.
- **Honest empty states:** "No cameras yet — discover or add one" instead of an empty grid.

## Deliberately not built

- Access control, attendance, intercom, P2P cloud linking, vendor SDK bundling. Each adds weight or licence
  obligations without improving surveillance, which is the product.
