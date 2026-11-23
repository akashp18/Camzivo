# Feature Priority Matrix — Camzivo

Scoring: **Impact** on operator value (1–5) · **Effort** engineer-days (S ≤ 2, M ≤ 5, L > 5) · **Risk** of
regression/uncertainty (L/M/H) · **Perf** steady-state cost (L/M/H). Priority is impact-first, then dependency
order; cost is a tie-breaker, never the driver.

---

## P0 — Core reliability (must exist before this can be called a VMS)

| Feature | Impact | Effort | Risk | Perf | Depends on | Acceptance |
|---|---|---|---|---|---|---|
| Real fullscreen + Esc/F11 | 4 | S | L | L | — | window truly fullscreen, layout unchanged |
| Crash logging + global handlers + crash banner | 5 | S | L | L | — | an induced exception leaves a log and a next-start banner |
| Wall persistence (session restore) | 5 | S | M | L | — | exact wall after restart |
| Device onboarding UI (add/edit/delete + test) | 5 | L | M | L | schema (done) | add a camera in ≤ 6 fields; failure names the cause |
| ONVIF discovery (probe + parse + dedupe) | 5 | M | M | L | onboarding | finds a camera on the LAN in ≤ 3 s |
| Digest/Basic auth client | 5 | M | M | L | — | 401→digest→200 against the fake server |
| DPAPI secret store | 5 | M | M | L | — | password never in SQLite plaintext |
| RTSP session + reconnect with backoff | 5 | L | H | M | auth | 30 min with 3 forced drops, auto-recovery |
| Video decode (DXVA) + render (D3DImage) | 5 | L | H | H | RTSP | first frame ≤ 1.5 s; no UI freeze |
| Decoder budget enforcement + `QUEUED` | 4 | M | M | L | decode | 64-tile wall stays responsive |
| Honest tile states from frame age | 4 | S | L | L | decode | no `LIVE` without frames; `RECONNECTING` ≤ 2 s |
| Application log + log viewer | 4 | S | L | L | — | log exists, redacted, openable from Health |
| Per-camera + per-stream health facts | 4 | M | L | L | decode | fps, bitrate, drops, attempts per camera |
| Playback of device recordings | 5 | L | H | M | RTSP | timeline + play + seek locally |
| Export with timestamps + manifest | 5 | M | M | M | playback | evidence file + hash + completion marker |

## P1 — Essential usability

| Feature | Impact | Effort | Risk | Perf | Notes |
|---|---|---|---|---|---|
| Saved views (named wall snapshots) | 5 | S | L | L | reuses wall codec |
| 2-up and 6-up layouts | 3 | S | L | L | geometry only |
| Keyboard-first placement (Enter/arrows/Delete/undo) | 4 | S | L | L | needs U6 in UX audit |
| Search feedback + group filter | 3 | S | L | L | fix reset bug too |
| Main/sub manual override per camera | 4 | S | L | L | persisted |
| Camera context menu (snapshot, reconnect, info) | 3 | M | L | L | |
| PTZ pad (ONVIF) | 3 | M | M | L | only on devices that support it |
| Multi-monitor wall support | 4 | L | M | M | control-room requirement |
| Event list + jump to playback | 4 | M | M | L | after events table |
| Motion/online/offline events from device | 4 | M | M | L | PullPoint subscription |
| Timeline with availability shading + gaps | 5 | M | M | M | needs recorder query |
| Export queue with cancel/retry | 4 | M | L | M | |
| Undo for wall changes | 3 | S | L | L | |

## P2 — Professional enhancements

| Feature | Impact | Effort | Risk | Perf | Notes |
|---|---|---|---|---|---|
| Map view with camera pins | 3 | L | M | M | optional module; never core |
| Camera groups CRUD + bulk edit | 3 | M | L | L | |
| CSV import with dry-run | 3 | M | M | L | |
| Bookmarks in playback | 4 | S | L | L | evidence workflow |
| Multi-camera synchronised playback (2×2) | 4 | M | H | H | per-stream offsets |
| Audit trail UI | 2 | S | L | L | repository exists |
| Diagnostics bundle export (zip, redacted) | 3 | S | L | L | |
| Clock offset per camera | 3 | S | M | L | matters in exports |
| Configurable startup view | 2 | S | L | L | |
| Compact density mode | 2 | S | L | L | token only |
| Background-shape wallpaper-free wall mode | 2 | S | L | L | |

## P3 — Optional expansion (evaluated, not scheduled)

Maps/floor plans beyond P2, heatmaps, object detection, people counting, LPR, AI search, cloud sync, mobile app,
web interface, central management, distributed recording, external integrations, RBAC/multi-user, Windows
authentication.

**Rule:** none of these are built until a user request exists. Each one brings a permanent cost (CPU, background
service, attack surface, support burden) that the product thesis — fast, light, honest — actively resists.
Analytics in particular must never run as an always-on pipeline in the client process; if ever added, it is a device
feature or a separate opt-in module.

## Explicitly out of scope (with reason)

| Item | Reason |
|---|---|
| Bundled web/FTP server | attack surface and process cost; the incumbent's plaintext FTP config is the cautionary tale |
| Exported proprietary SDK binaries | licensing and size; standard protocols only |
| Cloud relay | zero-egress invariant |
| Driver / kernel components | no requirement justifies the risk |
| Always-on AI | violates the CPU budget on ordinary workstations |
