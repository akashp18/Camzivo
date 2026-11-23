# Camzivo

A Windows video-management system for ONVIF and RTSP cameras, built as a WPF application on .NET 8. Camzivo
connects to IP cameras and network video recorders, imports their channels, and puts live video on a wall without
requiring the vendor's desktop client.

Version 0.2.0 · .NET 8 (WPF) · Windows x64 · 493 tests

---

## What works

| Area | Capability |
|---|---|
| Devices | WS-Discovery and manual add, ONVIF connection test in stages (reach → authenticate → describe → first frame), HTTP Digest and WS-Security UsernameToken, per-device diagnostics window |
| NVR | Channel import from ONVIF media profiles, channel identity read from the recorder's own `?channel=&subtype=` stream URL, stale-channel pruning, firmware/serial/service persistence, read-only mode |
| Video | RTSP over TCP with digest authentication, RTP depacketization, H.264 decode through Media Foundation (hardware where the GPU provides it), live wall from 1 to 64 tiles |
| Stream policy | Per-tile main/sub selection from tile size, visibility and fullscreen state, with an explicit reason for every choice; an explicit main-stream preference is never silently downgraded |
| Scheduling | Decode-budget scheduler, bounded frame queue, off-screen and minimised tiles stop decoding |
| Operations | Cameras, live view, events, playback, exports, snapshots, health, settings, SQLite store with versioned migrations, audit rows for mutations, structured log |
| Credentials | Passwords live in Windows DPAPI only; no credential appears in the database, the logs, or an RTSP URL that is written to disk |

## Requirements

- Windows 10 or 11, x64
- .NET 8 SDK (`global.json` pins 8.0.100 with `rollForward: latestFeature`)
- A camera or recorder reachable on the LAN over ONVIF (HTTP) and RTSP

## Build and test

```bash
dotnet build Camzivo.sln -warnaserror
dotnet test  Camzivo.sln --nologo --no-build
```

The build treats warnings as errors. Run the desktop app with:

```bash
dotnet run --project src/Camzivo.Desktop
```

## Repository layout

```
src/Camzivo.Core      protocol and policy, no UI: ONVIF, RTSP, SDP, depacketizer, decoders, layout, scheduling
src/Camzivo.Data      SQLite store, migrations, repositories, DPAPI secret store, logging, device onboarding
src/Camzivo.Desktop   WPF shell, view models, views, themes, streaming engine and probes
tests/                493 tests against Core, Data and Desktop (including a loopback RTSP server)
docs/                 product, architecture, design, audit, performance and planning documents
```

## Known limitations

- **H.265 (HEVC) is not decoded.** The decoder handles H.264. A recorder whose channels encode H.265 will connect and
  negotiate successfully, then report `The video track uses H265, which this build cannot decode.` Set the channel's
  encode mode to H.264 on the recorder, or add an HEVC decoding path. The session tries the channel's other streams
  (main → sub → third) before giving up, in case one of them is H.264.
- Session budget: a recorder has a finite number of RTSP sessions. A wall of tiles against a small NVR can reach that
  limit, which the recorder reports as `503` or as a `DESCRIBE` timeout.
- Recording, playback search and export are read-only toward the recorder; Camzivo never writes device
  configuration, schedules or storage settings.

## Interoperability notes

Camzivo sends the **absolute URL** in the RTSP request line (`DESCRIBE rtsp://host:554/cam/realmonitor?channel=1&subtype=0
RTSP/1.0`), which is what ffmpeg, VLC and OpenCV do. A Dahua recorder answers `503` and closes the socket to the
origin-form path (`DESCRIBE /cam/realmonitor?...`), so this is not a detail worth "optimising" later — see
`docs/DAHUA_NVR_COMPATIBILITY.md`.

Every RTSP exchange is traced to the application log as method, path and status line (`DESCRIBE -> 401`,
`DESCRIBE -> connection closed by the device`). Authorization headers and credentials never reach the trace, so a
log can be shared safely.

## Documentation

| Document | Contents |
|---|---|
| [docs/PRODUCT_REQUIREMENTS.md](docs/PRODUCT_REQUIREMENTS.md) | Requirements and non-functional constraints |
| [docs/architecture/SYSTEM_ARCHITECTURE.md](docs/architecture/SYSTEM_ARCHITECTURE.md) | Layers, boundaries, data flow |
| [docs/architecture/SECURITY_MODEL.md](docs/architecture/SECURITY_MODEL.md) | Credential handling, trust boundaries |
| [docs/architecture/ADR-001-ui-and-operator-state.md](docs/architecture/ADR-001-ui-and-operator-state.md) | UI and operator-state decision |
| [docs/VIDEO_ENGINE.md](docs/VIDEO_ENGINE.md) | Stream policy, decoder scheduling, frame lifecycle |
| [docs/API_CONTRACTS.md](docs/API_CONTRACTS.md) | Internal contracts between layers |
| [docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md) | Tables, migrations, invariants |
| [docs/DAHUA_NVR_COMPATIBILITY.md](docs/DAHUA_NVR_COMPATIBILITY.md) | Dahua behaviour, what is verified and what is not |
| [docs/DEVICE_COMPATIBILITY.md](docs/DEVICE_COMPATIBILITY.md) | Device matrix and quirks |
| [docs/READ_ONLY_NVR_MODE.md](docs/READ_ONLY_NVR_MODE.md) | The read-only guarantee toward recorders |
| [docs/USER_FLOWS.md](docs/USER_FLOWS.md) | Operator journeys |
| [docs/design/DESIGN_SYSTEM.md](docs/design/DESIGN_SYSTEM.md) | Tokens, typography, components |
| [docs/testing/TEST_STRATEGY.md](docs/testing/TEST_STRATEGY.md) | What is tested and how |

### Plans

| Document | Contents |
|---|---|
| [docs/planning/IMPLEMENTATION_ROADMAP.md](docs/planning/IMPLEMENTATION_ROADMAP.md) | Milestones with acceptance gates |
| [docs/planning/FEATURE_PRIORITY_MATRIX.md](docs/planning/FEATURE_PRIORITY_MATRIX.md) | Features by value and cost |
| [docs/planning/RELEASE_READINESS.md](docs/planning/RELEASE_READINESS.md) | What blocks a release |
| [docs/IMPLEMENTATION_ROADMAP.md](docs/IMPLEMENTATION_ROADMAP.md) | Earlier roadmap revision, kept for history |
| [docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md) | Earlier readiness assessment |
| [docs/FEATURE_GAP_ANALYSIS.md](docs/FEATURE_GAP_ANALYSIS.md) | Gaps against the incumbent |
| [docs/performance/OPTIMIZATION_PLAN.md](docs/performance/OPTIMIZATION_PLAN.md) | Performance work and budget |
| [docs/performance/BASELINE_BENCHMARKS.md](docs/performance/BASELINE_BENCHMARKS.md) | Measured baseline |
| [docs/audit/BUG_AUDIT.md](docs/audit/BUG_AUDIT.md) | Defect audit and fixes |
| [docs/audit/SECURITY_AUDIT.md](docs/audit/SECURITY_AUDIT.md) | Security review |
| [docs/CAMZIVO_IMPROVEMENTS.md](docs/CAMZIVO_IMPROVEMENTS.md) | Improvement backlog |

### Research

| Document | Contents |
|---|---|
| [docs/SMARTPSS_ANALYSIS.md](docs/SMARTPSS_ANALYSIS.md) | Incumbent analysis |
| [docs/SMARTPSS_FEATURE_PARITY.md](docs/SMARTPSS_FEATURE_PARITY.md) | Feature-by-feature comparison |
| [docs/SMARTPSS_WEAKNESS_ANALYSIS.md](docs/SMARTPSS_WEAKNESS_ANALYSIS.md) | Where the incumbent fails operators |
| [docs/REA_EVALUATION.md](docs/REA_EVALUATION.md) | Evaluation of an open-source VMS as a reference |
| [docs/TECHNOLOGY_EVALUATION.md](docs/TECHNOLOGY_EVALUATION.md) | Stack choices and why |
| [docs/SDK_AND_API_FEASIBILITY.md](docs/SDK_AND_API_FEASIBILITY.md) | Vendor SDK assessment |
| [docs/PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md) | Measured comparison |
| [docs/PLAYBACK_SPEED.md](docs/PLAYBACK_SPEED.md) | Playback performance |
| [docs/MILESTONE_NVR_PTZ_SNAPSHOTS.md](docs/MILESTONE_NVR_PTZ_SNAPSHOTS.md) | NVR, PTZ and snapshot milestone |
| [docs/MILESTONE_RECORDING_PLAYBACK_EXPORT.md](docs/MILESTONE_RECORDING_PLAYBACK_EXPORT.md) | Recording, playback and export milestone |
| [docs/audit/CURRENT_IMPLEMENTATION.md](docs/audit/CURRENT_IMPLEMENTATION.md) | Implementation snapshot |
| [docs/audit/COMPETITOR_ANALYSIS.md](docs/audit/COMPETITOR_ANALYSIS.md) | Competitor review |
| [docs/audit/FEATURE_GAP_ANALYSIS.md](docs/audit/FEATURE_GAP_ANALYSIS.md) | Gap analysis, audit pass |
| [docs/architecture/ARCHITECTURE_REVIEW.md](docs/architecture/ARCHITECTURE_REVIEW.md) | Architecture review |
| [docs/design/UX_AUDIT.md](docs/design/UX_AUDIT.md) | UX audit |
| [docs/design/INTERACTION_PATTERNS.md](docs/design/INTERACTION_PATTERNS.md) | Interaction patterns |
| [docs/design/SCREEN_SPECIFICATIONS.md](docs/design/SCREEN_SPECIFICATIONS.md) | Screen specifications |
| [docs/performance/PERFORMANCE_BUDGET.md](docs/performance/PERFORMANCE_BUDGET.md) | Budgets |
| [docs/performance/ACCELERATION_REPORT.md](docs/performance/ACCELERATION_REPORT.md) | Acceleration measurements |
