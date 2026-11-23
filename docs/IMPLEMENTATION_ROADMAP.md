# Implementation Roadmap (revised)

> **Historical planning document.** The status line below is from an earlier pass. Current status: 359 tests green and
> the redesign/feature work delivered — see `docs/RELEASE_READINESS.md`.

Direction: **SmartPSS-level surveillance functionality, with better diagnosis, less weight and no fake state.**
Integration is protocol-first (ONVIF, RTSP, documented Dahua HTTP where needed); vendor binary SDKs are not bundled.

Status date: 2026-11-22. Build green, 191 tests passing (125 core, 56 data, 10 desktop).

## Principles

1. Vertical slices: a workflow is finished when it works end to end, including its diagnostics and its failure path.
2. A tile is LIVE only after a decoded frame is presented.
3. Optimistic UI is a bug: writes are verified, capabilities are `Unverified` until probed.
4. No vendor SDK binaries in the shipped product; no code or assets copied from SmartPSS.
5. Every milestone reports implemented / simulated / hardware-verified, separately.

## M3 — Device onboarding (in progress)

Done and tested:

- ONVIF WS-Discovery message + namespace-agnostic `ProbeMatches` parsing (multi-device dedupe, malformed datagrams ignored).
- WS-Security UsernameToken (PasswordDigest) and HTTP Digest with one retry — the combination most cameras need.
- SOAP device information / profiles / stream URI, DTD-prohibited parsing, main/sub profile selection.
- Four-stage connection test (reach → sign in → profiles → stream) with a real RTSP port probe and plain-language
  failure classification; sub stream URI resolved when the device exposes one.
- DPAPI credential store (per-user, write-then-replace, corrupt files degrade to "missing").
- `DeviceOnboarding`: validation, atomic registration (nothing persisted on failure), rename, remove-with-secret.

Remaining for M3:

- Add Camera dialog wired to `DeviceOnboarding`, showing the four stages.
- UDP discovery scanner using the tested message/parser.
- Device list bound to SQLite with edit/remove/rename actions.
- Real `HttpClient` ONVIF transport and TCP RTSP probe implementations (currently behind interfaces).

Exit criteria: add a camera by address and by discovery; a wrong password, a closed port and a non-ONVIF HTTP server
each produce a distinct, correct message; nothing is saved on failure.

## M4 — Video engine (the critical milestone)

1. `RtspClient`: DESCRIBE → SETUP (TCP interleaved) → PLAY → TEARDOWN; Digest and Basic auth; keep-alive; cancel-safe.
2. SDP parsing; RTP depacketization for H.264 (RFC 6184) and H.265 (RFC 7798); access-unit assembly.
3. Decoder abstraction: FFmpeg LGPL build with `d3d11va`, software fallback; Media Foundation backend for fleets that
   cannot deploy FFmpeg.
4. Renderer: D3D11 texture → `D3DImage`; no per-frame `BitmapSource`; aspect-ratio letterboxing.
5. Wire the existing `StreamScheduler` (one connection per camera) and `BoundedFrameQueue` (drop-oldest).
6. Tile lifecycle: decoder starts on demand, stops when the tile is empty/hidden, releases on layout change.
7. Reconnect with backoff; health state per stream; LIVE only on presented frame.

Exit criteria: a real camera shows video; unplugging the network recovers; 16 sub-stream tiles run within budget on
the reference machine; frame counters prove the picture is live.

## M5 — Evidence workflows

- Snapshots from the presented frame (camera + timestamp in the filename).
- Local recording via stream copy.
- Playback: ONVIF Profile G replay, Dahua CGI fallback; timeline-first UI with per-camera lanes and event ticks.
- Export as a cancellable job using stream copy, verified playable before success is reported.

## M6 — Events, health, scale

- ONVIF PullPoint subscriptions with reconnect; event store and event centre on the shared wall component.
- Health page: last contact, latency, reconnect count, last failure reason; resource panel with decode/drop counters.
- NVR channel import; bulk credential/group operations.
- Multi-monitor workspaces; PTZ (ONVIF ContinuousMove/AbsoluteMove/presets).
- Benchmarks at 1/4/9/16/25/49 cameras, recorded with hardware and stream parameters.

## M7 — Optional depth

- Audio listen; two-way talk only where a backchannel is advertised.
- Device settings subset with read-back verification.
- Config export/import (no secrets).
- Optional, user-supplied Dahua SDK adapter, excluded from the installer.

## Explicit non-goals

Access control, attendance, intercom, P2P cloud linking, bundling vendor SDKs, and any feature whose only
implementation path is bypassing a device's authentication or licensing.

## Reporting template (per milestone)

1. Features completed, and what each one actually does.
2. Implemented vs simulated vs hardware-verified (three lists, never merged).
3. Files changed.
4. Tests added and the full suite result.
5. Measurements, with hardware and stream parameters.
6. Known limitations and the exact conditions that were not tested.
7. Next milestone, and what it needs from hardware or the operator.
