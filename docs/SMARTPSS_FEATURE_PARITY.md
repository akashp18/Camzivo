# SmartPSS Lite Feature Parity

Reference product: **SmartPSS Lite 1.003.0000006.0**, installed at `C:\Program Files\SmartPSSLite\SmartPSSLite.exe`
(resolved from the Start Menu shortcut; `FileVersion 1.003.0000006.0`). 383 MB, 11,339 files, modules in
`Solutions/`, `platforms/`, `plugins/`, `plugins/` — a Qt 5 desktop application with several bundled SDK trees.

Evidence classes used below:

- **V** — verified in the installed build or in Dahua's own documentation for this version.
- **D** — stated in the Dahua SmartPSS Lite datasheet (V1.003.0000000.0) or user manual.
- **?** — not verified here; do not build a promise on it.

Camzivo status: **✅ shipped**, **◐ partial**, **▣ scaffold/UI only**, **✖ missing**.

Progress since the previous revision (2026-11-22): the shell, Live View, Devices, Playback, Events, Exports and
Settings were rebuilt on one token/control system; the timeline is drawn by `TimelineControl` (bars are fractions, not
pixels); named camera layouts, local event history, a second display window and a complete light theme are in place;
real live video is presented in the running application (12/12 acceptance steps); and onboarding → persistence →
playback → export passes end to end against the simulator (11/11 steps, SHA-256 verified export). The Dahua NVR
compatibility pass adds advertised Media XAddr routing, HTTP Digest fallback, Profile G `IncludedSources` filtering,
persisted source-token mapping, a read-only diagnostics dialog, advanced ports and a blocked-by-default real-hardware
probe; see `docs/DAHUA_NVR_COMPATIBILITY.md`. Real Dahua hardware remains **unverified** and is marked `unknown` in
that matrix. The 49-camera
substream benchmark was re-run after accelerated playback: 12.84 FPS per camera, 42.0 % CPU, 440 MB (previous
baseline 12.9 / 39.9 % / 441 MB; the live path itself is unchanged, but the CPU figure is higher than the earlier
11.6 / 38.0 % run and is reported as measured rather than smoothed). Details: `docs/performance/ACCELERATION_REPORT.md`,
`docs/RELEASE_READINESS.md`.

## Feature matrix

| Feature | SmartPSS Lite | Camzivo | Integration method | Gap | Improvement |
|---|---|---|---|---|---|
| LAN device discovery | V — "Auto Search" on the Device Manager page, results are ticked and added with credentials | ✅ ONVIF WS-Discovery probe + namespace-agnostic `ProbeMatches` parser (`WsDiscoveryProbe`, `WsDiscoveryParser`), 4 tests | ONVIF WS-Discovery over UDP 3702 (open standard) | No UDP socket yet: the message builder/parser is tested, the scanner is not wired | Show a live, cancellable scan with per-device vendor/model before adding; dedupe multi-homed devices (implemented in parser) |
| Manual add by IP/hostname | V — IP/domain mode, name, user, password | ✅ `DeviceOnboarding.RegisterAsync` validates and persists (8 tests) | ONVIF device service over HTTP | No Add Camera dialog wired to it | Inline validation as typed; four stage rows instead of one spinner |
| Add by RTSP URL | V — the IVSS web client documents an RTSP tab with `rtsp://…/cam/realmonitor?channel=1&subtype=0` | ✅ accepts `rtsp://`, rejects credentials embedded in the URL, keeps the supplied port | RTSP (RFC 2326) | Stream is stored but nothing consumes it until M4 | One field that understands both camera and NVR channel URLs |
| NVR/DVR registration + channels | V/D — recorders (NVR, XVR) are first-class devices; channels are enumerated per device | ◐ ONVIF recorder import: channels discovered, listed with checkboxes, imported, re-imported, renamed, removed; recorder removal offers keep-as-cameras or remove-all (real Dahua channel naming unverified) | Dahua HTTP API or ONVIF profiles (per-channel `GetProfiles`) | Largest functional gap | Import all channels from one NVR in a single action, with per-channel enable |
| Camera authentication | V — per-device user/password | ✅ WS-Security UsernameToken (PasswordDigest) with one HTTP Digest retry; password never in the request object, result or log | ONVIF + RFC 7616 Digest | No RTSP Digest/Basic yet (M4) | One credential store per device, DPAPI-protected, with an explicit "test again" action |
| Credential storage | V — see the weakness analysis: plaintext `admin123` in `FtpConfig.ini` in the installed tree | ✅ DPAPI `CryptProtectData`, per-user, ciphertext file, reference only in SQLite (6 tests) | Windows DPAPI (OS) | — | No password ever appears in the database, a backup or a support bundle |
| Live monitoring | V/D — real-time monitoring, arbitrary split up to 64 channels (datasheet) | ✅ WPF wall with 1–64 slots and real Media Foundation H.264 video, presented only after a decoded frame (12/12 live acceptance steps) | RTSP/RTP + decoder + D3D11 render (M4) | Software decode only; hardware decode blocked on this host (see `docs/performance/ACCELERATION_REPORT.md`) | Tile can only read LIVE after a decoded frame is presented (enforced by state machine, already modelled) |
| Main/sub stream switching | D — stream type selectable per window; sub stream used for small tiles | ◐ ONVIF profile selection picks main and sub; tile size now chooses main (fullscreen), sub (small tile) or suspend (minimised) without restarting a stream on tile movement | ONVIF `GetStreamUri` per profile | Manual per-tile main/sub override UI not implemented; switches still reconnect | Automatic: sub stream in mosaics, main stream on fullscreen/spotlight, switch without reconnecting when possible |
| Camera layouts | V/D — arbitrary split, up to 64 preview | ✅ 1–64 presets, slot assignment, undo, layout codec (tested) | In-app (custom) | — | Drag-and-drop with a visible drop target; keyboard `1`–`9` presets |
| Saved workspaces | ◐ screens/stream layout is remembered per session; no multi-workspace concept verified | ✅ named layouts: save, rename, duplicate, delete, apply, restore last used; tile assignments persisted in `wall_layout`/`wall_slot` (v5); missing cameras are dropped, identities preserved | In-app + SQLite v5 | No independent layout per display (the second window mirrors) | Named layouts in Live View ("Gate house", "Car park"), restored in one click |
| PTZ | V/D — PTZ control on live view for capable channels | ◐ ONVIF PTZ: continuous pan/tilt/zoom nudges, stop, presets, capability detection, automatic stop on command end and on stream failure | ONVIF PTZ service (`ContinuousMove`, `GotoPreset`) — open standard; Dahua CGI as fallback | No absolute move or speed slider; real Dahua PTZ unverified | Pointer-centred zoom, speed scaled by zoom level, presets as buttons |
| Audio (listen) | V — video talk / audio on live view | ✖ | ONVIF media audio track over RTSP; decode with Media Foundation/NAudio | Missing | Per-tile mute, global mute, push-to-talk later |
| Two-way talk | V/D — video talk is a listed SmartPSS Lite function | ✖ | ONVIF audio backchannel or Dahua CGI — **device-dependent** | Missing, high complexity | Defer to M7; only offer when the device advertises a backchannel |
| Playback | V/D — playback page, 36-channel D1 playback (datasheet) | ◐ ONVIF Profile G replay: play, pause, resume, seek (reopen at clock target), stop, end detection, one stall reconnect; **1×/2×/4×/6×/8×/16× with measured source rate**, decoded frames and accurate RTP-timestamp playhead (15/15 accelerated acceptance; 12.93–15.77× measured at 16× across runs) | ONVIF Profile G Replay (clock Range + Scale); client measures delivered speed and reports ignored/rejected Scale | Real Dahua replay `Scale` support unverified; recorder that only delivers real time cannot provide >1× | Speed selector beside transport, requested vs actual rate, explicit limitation instead of a fake clock |
| Recording search | V/D — smart search and playback | ◐ Profile G `FindRecordings` → `GetRecordingSearchResults` per day and channel source, service detection, unsupported/empty/error states, UTC + DST-correct local day | Profile G `FindRecordings`/`GetRecordingSearchResults`, else Dahua CGI | No event-type filter; real Dahua search unverified | Search by time range + event type in one panel, results as timeline markers |
| Timeline | V — playback timeline with split track and zoom | ✅ 1–24 h zoom, pan, hour ruler, blocks and gaps, playhead updated without rebuilding; mouse select + keyboard/transport commands | In-app (real search results only) | Single track; no event ticks | Continuous zoom (hour→minute), event ticks, keyboard frame stepping |
| Snapshots | V/D — snapshot of the monitoring screen | ✅ saves the presented frame as PNG with camera name and timestamp; button disabled until a real frame exists | Decoder frame → PNG | — | Snapshot from the presented frame (what you see is what you save), with camera + timestamp |
| Local recording | V — local record per window, saved to the PC | ✖ | RTSP stream copy via FFmpeg (no re-encode) | Missing | Recording indicator on the tile, filename template, disk guard |
| Video export | V/D — playback export | ✅ built-in MP4 muxer (avcC from the stream, RTP-clock durations, sync table), progress, cancel, `.part` cleanup, ffprobe-verified | In-app ISO-BMFF writer (no FFmpeg dependency) | Video only, no audio, no re-encode | Export with a real progress bar, cancel, and a playable-file check |
| Events / alarms | V/D — event center with statistics, real-time events, 1/4/9 event splits | ◐ stream-engine transitions (online, offline, reconnecting, authentication failure) are written to SQLite with bounded retention and shown in Events with filters and event → playback navigation. **No** device motion/PullPoint subscription yet | In-app engine events today; ONVIF PullPoint is the next source | Device-generated motion/alarm events missing; no alert-to-tile linkage | Event list that links straight to the recorded minute |
| Alert linkage | V — event linkage video popup | ✖ | In-app rule (event → tile) | Missing | Link an event type to a tile, not a modal popup that steals focus |
| Device settings | V/D — device configuration, remote configuration of Dahua devices | ✖ | ONVIF device/media config (subset), Dahua CGI (broad) | Missing | Only expose settings that are read back and verified, never optimistic writes |
| Device health | ◐ online/offline dot | ◐ Camera health page built from the live wall (offline, queued, connecting), with the engine's own detail and next actions; connection diagnostics vocabulary | In-app engine state | No latency/reconnect counters yet; rows appear only while the wall is running | One health page that explains the fault in plain language, reachable from Settings |
| Multi-monitor | ? not verified for this build | ◐ **Display** opens the wall on a chosen monitor (per-monitor DPI converted), main window can be minimised without stopping decoding, last display is remembered and restored. The second window shares the same `LiveViewModel`/decoder sessions, so it mirrors the main wall rather than holding an independent layout | WPF window per display, shared engine | Independent layout per display missing; mixed-DPI verified by code path only, not on multi-monitor hardware | No duplicate decoder sessions; display window survives main-window minimise |
| Bulk operations | ◐ multi-select add on discovery | ✖ | In-app | Missing | Apply credentials/group/rename to a selection |
| Access control / attendance / intercom | V/D — full modules (ASIs, ASCs, ASAs, VTO/VTH) | ✖ | Out of scope | Deliberate | Not a Camzivo goal: it is a different product category |
| P2P cloud linking | V/D — add by serial number over the internet | ✖ | Dahua P2P is proprietary and account-based | Deliberate | Not implementable without Dahua cloud credentials; document as unsupported |
| Dark/light theme | ? not verified | ✅ both themes, persisted | In-app | — | Keep; SmartPSS parity does not require matching its skin |

## What this means

Parity for a *video* VMS is reachable with **ONVIF + RTSP + a decoder**. Four capabilities are genuinely
device-specific and cannot be promised generically: two-way talk, recording search/playback on devices that only
speak Dahua's proprietary playback, P2P, and deep device configuration. Everything else in the matrix above is either
already implemented in Camzivo's non-video layers or is plain application work on top of a standard protocol.

The access-control/attendance/intercom modules are **not** parity targets. They make SmartPSS Lite a
"small-business security suite"; Camzivo is a VMS. Copying them would add the weight this project exists to remove.

## Verification log

- Installed version and size: read from the executable's version resource and a directory scan (this machine).
- Feature claims marked **D**: Dahua SmartPSS Lite datasheet V1.003.0000000.0 and the SmartPSS Lite user manual
  (see `docs/SDK_AND_API_FEASIBILITY.md` for the source links).
- Camzivo statuses: checked against the repository and the test run of 2026-11-22 (**422 tests green**: Core 234,
  Data 93, Desktop 95), plus real-application acceptance runs on the same date:
  `artifacts/final-live/probe-log.ndjson` (12/12, real RTSP, onboarding dialog, fullscreen, layouts, reconnect),
  `artifacts/final-playback/summary.txt` (10/10, playback mechanics/export),
  `artifacts/final-e2e/summary.txt` (11/11, persisted onboarding → playback → export, SHA-256 verified) and
  `artifacts/speeds-final15/summary.txt` (15/15, accelerated playback 1×–16× and a previous-day block).
- Nothing in this table was inferred from a screenshot alone; unverified rows are marked **?**. Real Dahua hardware,
  device motion events, audio and mixed-DPI multi-monitor remain unverified.

## Known limitations (2026-11-22)

- **Audio and two-way talk are not implemented.** There is no audio pipeline, decoder or backchannel session. The UI
  does not offer a mute button that would do nothing; capability detection is therefore also absent.
- **Device motion/alarm events are not implemented.** Event history currently contains real engine transitions only.
- **The second display mirrors the wall.** Independent per-monitor layouts are not implemented; the window shares
  decoder sessions by design, and its placement/display name is restored on start when that display is present.
- **Hardware compatibility is unverified.** Everything above was exercised against the local recording simulator and
  a local RTSP test source, not a Dahua camera or NVR. `--probe-nvr` now performs the real workflow when a recorder is
  reachable and explicitly authorized; without that authorization it reports **BLOCKED** (exit 2).
- **Hardware-accelerated decode** remains blocked on this host; decode is Media Foundation software H.264.
- **No FFmpeg/ffprobe on this machine**, so the latest export was validated by the built-in muxer checks, the SHA-256
  sidecar and successful decode during the run, not by an independent container probe.
- **49-camera benchmark** was re-measured after the UI work: 11.6 FPS per camera, 38.0 % CPU, 445 MB on the
  substream (see `docs/RELEASE_READINESS.md` §4). The main-stream 49-tile case is heavier (5.0 FPS per camera).
