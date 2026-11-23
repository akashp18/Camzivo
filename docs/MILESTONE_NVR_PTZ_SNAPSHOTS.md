# Milestone: reliability, NVR import, PTZ, snapshots (2026-11-22)

Scope delivered in this pass: Phase 0 (flaky test eliminated), Phase 1 (NVR onboarding and multi-channel import),
Phase 2 (PTZ, snapshots). Phases 3–7 are not implemented; see "Remaining gaps".

## 1. Phase 0 — the flaky test was a real bug

The tile-move test was not timing-flaky: a background stream session let an **unexpected exception escape** its
supervisor loop. `TcpClient.ConnectAsync` can throw `NullReferenceException` when the socket is torn down mid-connect,
and that surfaced inside whatever test happened to be running — which is why the failure moved around and why adding
sleeps never fixed it.

Fixes and verification:

- `H264StreamSession.SuperviseAsync` now has a catch-all that reports the failure as a stream problem and retries.
  Unexpected exceptions can no longer reach the test runner (or the app).
- The tile-move and undo tests assert **synchronous engine state** (`SessionStarts`, `ActiveCount`, placement) with no
  sleeps at all.
- The live acceptance probe now waits on observable progress (a new presented frame, bounded to 5 s) and asserts:
  `ui.moveTile` — session count unchanged, `SessionStarts` unchanged (2 → 2), frames 45 → 48;
  `ui.undo` — slot restored, still LIVE, `SessionStarts` unchanged, frames 48 → 49.
- 8 consecutive desktop-suite runs: 8/8 green. Full suite: **301 tests, 0 failures** (Core 186, Data 69, Desktop 46).

## 2. Phase 1 — NVR onboarding and channels

Implemented:

- **Schema v3**: `device.kind` (`camera` | `nvr`) and `camera.source`. **Schema v4** replaces the unique endpoint
  index with a plain one, because channels kept from a removed recorder legitimately share its address; duplicate
  detection moved into the import service, which can tell "the same recorder again" from "eight channels of one
  recorder".
- **`NvrChannel` / `NvrChannelPlanner`** (`Camzivo.Core`): turns a recorder's ONVIF media profiles into channels —
  channel number read from name/token, main/sub paired per channel, audio-only profiles ignored, anything unnumbered
  continues in device order.
- **`NvrDiscovery`** (`Camzivo.Data`): reach → sign in → profiles → per-profile stream URI, plus `GetCapabilities` /
  `GetServices` so PTZ and replay endpoints are known. Service detection failures are non-fatal.
- **`CameraRepository`**: channels as first-class rows, unique per (device, channel number), so re-import updates
  instead of duplicating.
- **`NvrOnboarding`**: import (all or a selected subset), duplicate reporting, channel rename, channel removal,
  recorder removal with an explicit choice — remove its channels, or keep each as a standalone camera with its own
  copy of the stored password.
- **UI**: the Add Camera dialog has an "NVR with several channels" mode that lists discovered channels with
  checkboxes, a channel count, stream availability and advertised services, plus select-all/none.
  The Devices page shows NVR rows with channel counts, a per-recorder "Channels" refresh action, and a channel table
  with reconnect and remove per channel. Removing a recorder asks Yes (remove channels) / No (keep as cameras) /
  Cancel.
- **Wall**: recorder channels are wall cameras; stream resolution uses the channel's own URIs and the recorder's
  credentials, so main/sub selection works per channel.

Verified by tests (simulated eight-channel ONVIF recorder over the transport seam): discovery lists 8 channels with
main + sub URIs; import creates 1 NVR + 8 channels; re-import reports 8 duplicates and creates none; selected-subset
import creates 2; rename preserves addresses; channel removal leaves the recorder; removal with channels deletes all 8
and the secret; removal keeping channels promotes 8 standalone cameras with independent secrets; failed discovery and
empty selection are refused with reasons.

Compatibility: channel naming and profile-per-channel assumptions are **unverified against real Dahua hardware**.

## 3. Phase 2 — PTZ and snapshots

PTZ (`PtzController`, ONVIF ver20 PTZ):

- Reads the advertised PTZ service, `GetNodes` (continuous pan/tilt/zoom capability) and `GetPresets`.
- Pan/tilt/zoom nudges via `ContinuousMove`, explicit `Stop`, `GotoPreset`.
- **Always stops**: every movement schedules an automatic `Stop` (600 ms), a new movement supersedes it, and
  `Forget(cameraId)` (stream failure, reconnect, tile change, shutdown) stops movement before clearing state.
- Unavailable devices are reported with a reason and cannot be commanded: a rejected capability clears the endpoint
  (regression covered by test).
- UI: PTZ controls appear only when the selected camera answered its PTZ service; no placeholder panel.

Snapshots (`SnapshotWriter`):

- Saves the **presented frame** (`CameraTileViewModel.LastFrame`, set only when a decoded frame is presented).
- PNG at the frame's own size with a caption strip (camera name + local timestamp) inside the picture, so the aspect
  ratio is unchanged; file name is `Camera_Name_yyyyMMdd_HHmmss.png` with Windows-invalid characters replaced.
- The Snapshot button is disabled until a frame exists; success and failure are reported in the status strip.

## 4. Bugs found and fixed

1. Unobserved stream-session exception that made tests fail at random (real reliability bug, section 1).
2. `PtzController` kept its endpoint after declaring a camera unavailable, so a later nudge was still sent.
3. Duplicate unique index on device endpoints made "keep channels as cameras" impossible; replaced with a plain
   index plus service-level duplicate detection.

## 5. Verification

- `dotnet build Camzivo.sln -warnaserror`: clean.
- `dotnet test Camzivo.sln`: 301 passed, 0 failed.
- Desktop suite repeated 8×: 8/8 green.
- Live acceptance probe: **12/12 steps**, real 640×360 frames, move/undo keep the session and keep presenting frames.
- UI screenshots: `artifacts/live-wall/` (live wall, moved tile, undo, disconnect, reconnect) and
  `artifacts/add-camera-dialog-nvr.png` (dialog with NVR mode).

Not verified here: the recorder import dialog against a live HTTP ONVIF recorder (service verified through the
transport seam with scripted XML, not through a running HTTP device), and the Devices channel table with seeded data
(rendered from real rows in tests, not screenshotted). No Dahua hardware was available.

## 6. Performance impact

No video-engine changes beyond the exception handling: PTZ and snapshots are on-demand only, the PTZ auto-stop is a
single timer per command, and nothing polls. The wall baseline from the acceleration report is therefore unchanged; it
was not re-benchmarked, and no claim of improvement is made here.

## 7. Remaining gaps

- Phase 3: recording capability detection, recording search (ONVIF Profile G / Dahua), timeline, playback, seek,
  speed, gaps, scripted recording device.
- Phase 4: export jobs with progress, cancel, interruption handling and file validation.
- Phase 5: events (offline/online, motion, recording interruptions), history, filters, acknowledgement,
  event→playback navigation, notification preferences.
- Phase 6: live audio and two-way talk, mute/volume, device selection, capability detection, microphone permission.
- Phase 7: named workspaces, saved views, multi-monitor walls, favourites, shortcuts, workspace import/export.
- Real Dahua validation for channels, PTZ and stream addresses.
