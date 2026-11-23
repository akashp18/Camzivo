# Product Requirements — Camzivo VMS

Camzivo is an original, Windows-first video management system for Dahua-compatible and standards-compliant
cameras/NVRs. It is not a clone of SmartPSS Lite: it keeps the operational vocabulary surveillance operators
already know and replaces the architecture underneath it.

- Product name: **Camzivo** (executable `Camzivo.Desktop`)
- Platform: Windows 10 1809+ / Windows 11, x64
- Baseline analysed: SmartPSS Lite 1.003.0000006.0 (see `SMARTPSS_ANALYSIS.md`)

---

## 1. Problem statement

Operators running 8–64 cameras on commodity Windows hardware need to see all of them, know instantly which are
healthy, and get to a specific recording fast. The incumbent charges 8.7 s of startup and ~160 MB of resident
memory before it shows a login box, stores per-user state inside `Program Files`, exposes one global
main/substream switch, depends on vendor cloud endpoints by default, and splits features across 28 UI modules
with three inconsistent skins.

## 2. Personas

| Persona | Context | Primary need |
|---|---|---|
| **Operator (primary)** | 24/7 desk, 2 monitors, 16–49 tiles | glanceable health, fast layout change, no modal dialogs in the way |
| **Investigator** | after an incident | find the segment, scrub precisely, export evidence with timestamps |
| **Installer** | on site, laptop, one hand on a ladder | discover devices, test credentials, verify stream, name and group |
| **Administrator** | occasionally | device inventory, capability truth, health trends, audit trail |

## 3. Goals

1. Camera imagery is the primary content; chrome recedes.
2. Zero-device shell is instant and cheap: ≤1.5 s to interactive, ≤90 MB, ≤40 threads.
3. Every tile is honest about its state: live, reconnecting, offline, unsupported codec, substream-only.
4. Standards-first device support (ONVIF + RTSP) with Dahua-specific documented extras as optional add-ons.
5. Per-camera stream policy with explicit user override; auto-policy explains itself.
6. Playback that makes "where is the gap?" obvious.
7. Failures produce actionable diagnostics, never bare codes.
8. All state per-user, secrets DPAPI-protected, zero default egress.

## 4. Non-goals (v1)

- Cloud relay/P2P traversal, mobile clients, browser client.
- Access control, attendance, intercom, POS overlay (incumbent modules; out of scope, deliberately).
- Video analytics/object detection.
- Multi-site federation / central management server.
- Recording *to* local disk as a primary NVR replacement (evidence export only).

## 5. Functional requirements

### Live view (LV)

| ID | Requirement | Acceptance |
|---|---|---|
| FR-LV-001 | Grid presets 1, 2, 4, 6, 8, 9, 12, 16, 25, 36, 49, 64 | presets produce exact grid geometry; switching preserves camera assignment where possible |
| FR-LV-002 | Custom layouts (rows × cols, 1–8 each) | geometry validated; persisted per workspace |
| FR-LV-003 | Camera tree: search, groups, favourites | filter < 16 ms for 500 cameras; favourites persist |
| FR-LV-004 | Drag-and-drop from tree to tile | drop assigns; drop on occupied tile swaps |
| FR-LV-005 | Double-click tile → fullscreen (main stream) | main stream requested on promotion; substream released |
| FR-LV-006 | Per-tile status: online/connecting/reconnecting/offline/unauthorised | state derives from stream manager events only |
| FR-LV-007 | Per-camera stream preference: Auto / Main / Sub | explicit Main never silently becomes Sub; if unavailable, tile shows "Main unavailable" and stays degraded-by-consent |
| FR-LV-008 | Snapshot to file with device timestamp + camera name | PNG written atomically; path shown in toast |
| FR-LV-009 | Layout keyboard shortcuts (Ctrl+1..8, F11, Esc) | all reachable without mouse |
| FR-LV-010 | Workspace persistence (layout + assignment + per-camera stream) | restored exactly after restart |

### Devices (DV)

| ID | Requirement | Acceptance |
|---|---|---|
| FR-DV-001 | ONVIF WS-Discovery on the local segment | discovered endpoints listed with scopes/model; no credentials sent during discovery |
| FR-DV-002 | Manual device entry (host, port, RTSP port, user, password, TLS toggle) | validation errors are field-level and specific |
| FR-DV-003 | Connection test separates TCP reachability / RTSP auth / stream open / first frame | each stage reports its own result |
| FR-DV-004 | Capability detection → matrix (media profiles, PTZ, events, replay, codecs, transports) | unknown ≠ supported; UI shows "unverified" |
| FR-DV-005 | Credentials stored via DPAPI, never in the database or logs | DB contains only a secret reference; log scan test passes |
| FR-DV-006 | Groups and ordering | drag to regroup, persists |
| FR-DV-007 | Camera enable/disable without deleting | disabled cameras never open streams |

### Playback (PB)

| ID | Requirement | Acceptance |
|---|---|---|
| FR-PB-001 | Date picker + timeline of available segments | gaps visibly distinct from empty selection |
| FR-PB-002 | Scrub, play, pause, step (where source supports) | stepping disabled with reason if unsupported |
| FR-PB-003 | Speed 0.25–8× (source-limited) | speed clamped to capability, shown |
| FR-PB-004 | Multi-camera synchronised playback | sync error surfaced per camera ("offset +1.4 s") |
| FR-PB-005 | Export range to MP4/MKV with original timestamps | progress + cancel; output verified readable |
| FR-PB-006 | Snapshot from playback at current time | metadata sidecar written |

### Events (EV), Health (HL), Settings (ST)

| ID | Requirement | Acceptance |
|---|---|---|
| FR-EV-001 | Event list from ONVIF PullPoint / device HTTP where supported | unsupported devices show "no event source" |
| FR-EV-002 | Event markers on the playback timeline | marker click seeks |
| FR-HL-001 | Health board: online/offline counts, stream interruptions, CPU/GPU/RAM/net, active streams | updates at 1 Hz without UI jank |
| FR-HL-002 | Diagnostics text per failure with remediation steps | every `StreamFailure` maps to ≥1 action |
| FR-ST-001 | Dark/light theme, persisted | switch applies < 100 ms, no restart |
| FR-ST-002 | Storage/cache location shown and changeable | config path in `%APPDATA%\Camzivo` |
| FR-ST-003 | Audit log of sensitive actions (device add, credential change, export) | entries never contain secrets |

## 6. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-001 | No network, decode, or file I/O on the UI thread; all long work cancellable |
| NFR-002 | Bounded queues everywhere; drop-oldest with counters; no unbounded `List` growth in a stream path |
| NFR-003 | Every worker owns its resources; decoder/surface released on cancel and on window close |
| NFR-004 | Failure isolation: one camera's failure never throws into the shell |
| NFR-005 | Deterministic startup: no vendor SDK load until the first device exists |
| NFR-006 | Keyboard-complete: every action reachable via keyboard; focus visible |
| NFR-007 | DPI: correct at 100–250 %, multi-monitor with mixed DPI |
| NFR-008 | Accessibility: names/roles on all controls, contrast ≥ 4.5:1 for text |
| NFR-009 | Structured telemetry with counters per stream (received, decoded, presented, dropped, reconnects) |
| NFR-010 | All user data survives upgrade; migrations transactional and reversible via backup |

## 7. Release acceptance (v1.0)

- 16 substreams stable at ≥20 fps aggregate on the reference workstation; 49 tiles stable with visibility
  scheduling.
- 24 h soak with < 5 % memory drift and zero unhandled exceptions.
- Full sign-in-free local operation; no default network egress (verified with a packet capture on first run).
- Installer/uninstaller preserve user data; upgrade from previous minor version keeps devices, groups, layouts.
- Every documented failure scenario in `testing/TEST_STRATEGY.md` reproduced and handled.
