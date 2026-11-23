# Feature Gap Analysis and Integration Strategy

> **Historical planning document (2026-11-22, earlier pass).** Several rows below ("video engine missing", "no Add
> Camera window") were true when written and are **no longer true**. Current verified status, evidence and limitations:
> `docs/RELEASE_READINESS.md`; feature matrix: `docs/SMARTPSS_FEATURE_PARITY.md`.

This is the planning view: what Camzivo must reach, in which order, with which implementation method. The
current-state inventory (including the row-by-row capability counts) lives in `docs/audit/FEATURE_GAP_ANALYSIS.md`;
the SmartPSS comparison lives in `docs/SMARTPSS_FEATURE_PARITY.md`.

## Where the project actually stands (verified 2026-11-22)

| Layer | State | Evidence |
|---|---|---|
| Shell, themes, navigation | Working | WPF shell, snapshots in `artifacts/`, 10 desktop tests |
| Wall, layouts, slots, undo, fullscreen | Working | 1–64 presets, `WallLayoutCodec`, `SlotAssignment`, fullscreen probe output |
| Persistence | Working | SQLite with migrations, preferences, audit, logs |
| Device onboarding (protocol) | Working, not wired to UI | `DeviceOnboarding`, `ConnectionTester`, `WsDiscoveryProbe`, DPAPI store; 33 new tests |
| Device onboarding (dialog) | Missing | No Add Camera window yet |
| Discovery scanner (UDP socket) | Missing | Message builder/parser only |
| Video engine | **Missing** | No RTSP client, no decoder, no renderer |
| Playback / events / PTZ / audio | Missing | — |
| Test suite | 191 green | 125 core + 56 data + 10 desktop |

The honest summary: Camzivo currently has a solid application skeleton and a tested protocol layer, and **no video**.
Everything below is ordered so that the video gap closes first.

## A. Parity work (must have to be a usable VMS)

| # | Capability | Method | Depends on | Exit criterion |
|---|---|---|---|---|
| A1 | Add camera dialog (4 stages) | Existing service + WPF dialog | — | Operator adds a device, sees stage results, failure persists nothing |
| A2 | UDP ONVIF discovery scan | `UdpClient` + existing builder/parser | — | Devices appear with name/address; scan is cancellable |
| A3 | RTSP client | Custom: DESCRIBE/SETUP/PLAY, TCP interleaved, Digest/Basic | — | SDP parsed, frames received from a real camera |
| A4 | H.264 decode | FFmpeg LGPL (d3d11va) with MF fallback | A3 | Decoded frame appears in a tile; LIVE only then |
| A5 | Wall rendering without per-frame allocation | D3D11 texture + `D3DImage` | A4 | No managed bitmap per frame; steady memory under 25 tiles |
| A6 | Main/sub switching | Profile policy + stream restart | A4 | Mosaic uses sub, fullscreen uses main, switch < 2 s |
| A7 | Reconnect supervisor | Backoff + health state | A3 | Pulling the cable recovers automatically; reason is shown |
| A8 | NVR channel import | ONVIF profiles per channel (or CGI channel list) | A1 | One add produces N channels with names |
| A9 | Snapshots + local record | Presented frame → PNG; stream copy → MP4 | A4 | Files play in an external player |
| A10 | Playback (Profile G first) | ONVIF Replay; CGI fallback | A3, A4 | Seek within a recording; timeline reflects real gaps |
| A11 | Event ingestion | ONVIF PullPoint | A3 | Events appear with device and time; no busy polling |

## B. Improvements written into the parity work

| # | Improvement | Built into |
|---|---|---|
| B1 | Sub stream chosen automatically by tile size | A6 |
| B2 | Four-stage, plain-language onboarding errors | A1 |
| B3 | Timeline-first playback | A10 |
| B4 | Export as a cancellable job with verification | A9/A10 |
| B5 | Any layout in any context | existing wall |
| B6 | Health history instead of an online dot | A7 |
| B7 | Keyboard-driven wall and camera selection | existing shell |

## C. Camzivo-exclusive (after parity, only if cheap)

| # | Enhancement | Why it is cheap here | Gate |
|---|---|---|---|
| C1 | Named workspaces per monitor | Layout codec + preference store already exist | after A5 |
| C2 | Decoder/queue telemetry panel | Counters already planned in the engine | after A5 |
| C3 | "Why is this tile black?" diagnostic | Stage results + frame counters | after A4 |
| C4 | Snapshot contact sheet for an incident window | Frame cache + PNG writer | after A9 |
| C5 | Config export/import without secrets | Device model + JSON | after A1 |

## Reuse decisions (do not rewrite)

| Component | Decision | Reason |
|---|---|---|
| WPF shell, themes, navigation | Keep | Working, tested, snapshots verified |
| Wall + layout codec + slot assignment + undo | Keep | Pure, tested, no video assumptions |
| SQLite layer, migrations, preferences, audit | Keep | 56 data tests including migration safety |
| `StreamScheduler`, `BoundedFrameQueue`, stream selection policy | Keep | Already the right shape for the decoder |
| `TileStatus` state machine | Keep and extend | Prevents the "fake LIVE" failure mode |
| Device/`DeviceCapabilities` model | Keep | Capability states already model "unverified" |
| Onboarding protocol layer (new) | Keep | 33 tests green; needs UI and a UDP scanner |

## Ordering rule

One vertical slice at a time, each ending in a runnable, measured state:

`A1 → A2 → A3 → A4 → A5` (a camera on screen, honestly LIVE) → `A6 → A7` (usable under real conditions) →
`A9 → A10 → A11` (evidence workflows) → `A8` (NVR scale) → B/C items.

A milestone is not "done" because the UI is attractive. It is done when the workflow above it works end to end
against a device or a scripted fixture, and the limitations are written down.
