# Feature Gap Analysis — Camzivo 0.2.0

Scoring key. **Current Camzivo:** ✅ verified (tested or measured end-to-end) · ◐ partial (logic exists, no
end-to-end path) · ▣ UI-only · ✖ missing · ⚠ defective. **SmartPSS Lite / Industry:** documented capability from
`COMPETITOR_ANALYSIS.md`; `?` = not verified in vendor documentation (never assumed). **Priority:** P0 essential
and broken/missing · P1 important workflow · P2 valuable · P3 optional. **Cost** = steady-state resource cost of
having it *enabled* (L = negligible, M = background sampling/decoding, H = continuous processing/recording).

Rows are 137 rows: every item in the brief's A–H inventory (grid sizes the brief lists separately share one row — 4/9/16/25
and 36/49/64 — which is why the row count is lower than the item count) plus two audit-surfaced rows (session
restore, honest status). Counts in §9 are computed from these rows, not estimated.

---

## 1. Camera and device management (A)

| Feature | Camzivo | SmartPSS Lite | Industry | Gap | Pri | Cost | Recommendation |
|---|---|---|---|---|---|---|---|
| Automatic ONVIF discovery | ◐ parser only | yes | all | no UDP probe/matches | P0 | L | send WS-Discovery probe, parse, dedupe, show candidates |
| Manual registration | ✖ | yes | all | no form | P0 | L | 4-field add dialog + test button |
| NVR/DVR registration | ✖ | yes (NVR/XVR) | all | channel model absent | P0 | L | device→channels model, one row per channel |
| RTSP URL support | ◐ builder only | yes | all | no UI, no validation surface | P0 | L | paste URL, parse, probe |
| Connection testing | ◐ wording only | yes | all | no probe | P0 | L | OPTIONS/DESCRIBE probe with 4 outcomes |
| Capability detection | ✖ | yes | all | no ONVIF media/PTZ query | P1 | L | cache profiles, codecs, PTZ support |
| Naming / renaming | ✖ | yes | all | field exists, no UI | P1 | L | inline rename with validation |
| Grouping | ◐ column only | yes (org/area tree) | all | no CRUD | P1 | L | groups in tree, drag not required |
| Favorites | ✖ | yes | XProtect/Nx | none | P2 | L | star + filter chip |
| Search / filter | ◐ demo list | yes | all | no persistence, reset bug | P1 | L | fix reset, add group filter |
| Bulk camera import | ✖ | ? | Nx (CSV) | none | P2 | L | CSV import with dry-run report |
| Bulk camera edit | ✖ | yes | XProtect | none | P2 | L | multi-select + set group/substream |
| Device status monitoring | ✖ | yes | all | no polling/events | P0 | M | event-driven heartbeat, not 1 Hz polling |
| Device reconnect | ▣ button disabled | yes | all | no engine | P0 | L | per-device reconnect with backoff |
| Main/substream discovery | ◐ policy only | yes | all | no ONVIF profiles | P0 | L | read profiles, map main/sub |
| Device authentication | ◐ redaction only | yes (digest/basic) | all | no digest client | P0 | L | RFC 7616 digest + basic over TLS |
| Secure credential storage | ◐ column only | plaintext INI (incumbent) | DPAPI/keystore | no cipher | P0 | L | DPAPI CurrentUser, `secret_ref` only |
| Duplicate detection | ✖ | ? | XProtect | none | P1 | L | match on host+port+serial/profile token |
| Camera time sync status | ✖ | yes (device time) | Nx | none | P2 | L | ONVIF time compare, show drift only |
| Firmware information | ✖ | yes | all | none | P2 | L | read-only display from ONVIF |

## 2. Live monitoring (B)

| Feature | Camzivo | SmartPSS Lite | Industry | Gap | Pri | Cost | Recommendation |
|---|---|---|---|---|---|---|---|
| Single-camera view | ✅ | yes | all | — | P0 | — | done |
| 2-camera view | ✖ | yes | all | no preset | P1 | L | add 2-up (side-by-side) |
| 4 / 9 / 16 / 25 grids | ✅ (layout math + render) | yes | all | no video | P0 | — | done (shell) |
| 36 / 49 / 64 grids | ✅ layout, ⚠ no video | yes (64) | Nx 64 | decode path absent | P0 | H | engine + adaptive streams |
| 6-camera grid | ✖ | yes | iVMS custom | no preset | P2 | L | 3×2 preset |
| Custom layouts | ✖ | yes | all | none | P2 | M | user-defined row spans |
| Drag-and-drop placement | ✖ | yes | all | click-to-place only | P1 | L | add DnD, keep click |
| Fullscreen | ✅ probe-verified (maximised, chrome collapsed, restored) | yes | all | none | P0 | L | done |
| Multi-monitor | ✖ | yes | all | single window | P1 | M | per-window wall, monitor picker |
| Saved views | ✖ | yes (saved views/plan) | all | layouts only | P1 | L | named wall snapshots |
| Camera rotation / tours | ✖ | yes (tour) | Nx showreel | none | P2 | M | timed tour per view |
| Main/sub switching | ◐ auto policy | yes | all | no override | P0 | L | per-tile chip toggle |
| Automatic stream selection | ✅ policy tested | yes | all | — | P0 | — | done (policy) |
| Manual stream override | ✖ | yes | all | none | P1 | L | persisted per camera |
| Digital zoom | ✖ | yes (split track) | all | none | P2 | M | wheel + pan, GPU scale |
| Snapshot | ✖ | yes | all | none | P1 | L | save PNG + path toast |
| Manual recording | ✖ | yes | all | none | P2 | H | record on demand to export root |
| Audio playback | ✖ | yes (talk) | all | none | P2 | M | opt-in, muted by default |
| PTZ controls | ✖ | yes | all | none | P1 | L | ONVIF PTZ, keyboard-safe |
| Connection indicators | ◐ enum, never offline | yes | all | no engine | P0 | L | status from frame age |
| Per-camera reconnect | ✖ | yes | all | none | P0 | L | context action |
| Aspect-ratio preservation | ✅ visual | yes | all | — | P0 | — | keep |
| Camera context menu | ✖ | yes | all | none | P1 | L | snapshot/reconnect/stream/info |
| Keyboard shortcuts | ◐ Ctrl+1..8, Ctrl+T, F11/Esc, Delete, Ctrl+Z (focus-guarded) | yes | all | no paging, no Enter-to-place | P1 | L | remaining keys in M3/M4 |
| Double-click fullscreen | ✖ | yes | all | none | P1 | L | tile double-click |
| Tile swapping | ✖ | yes | all | selection replaces only | P2 | L | drag tile onto tile |
| Grid resizing (splitter) | ✖ | yes | all | fixed ratios | P2 | M | draggable gutters |
| Stream loading indicator | ✖ | yes | all | none | P0 | L | per-tile spinner + elapsed |

## 3. Playback and investigation (C)

| Feature | Camzivo | SmartPSS Lite | Industry | Gap | Pri | Cost | Recommendation |
|---|---|---|---|---|---|---|---|
| Recording calendar | ▣ placeholder | yes | all | no query | P1 | L | month grid with availability |
| Date/time selection | ▣ | yes | all | none | P1 | L | date + time + jump |
| Availability indicators | ✖ | yes | all | none | P1 | M | range query per day, shaded |
| Visual timeline | ▣ | yes | all | none | P1 | M | canvas timeline, no per-frame retain |
| Timeline zoom | ✖ | yes | all | none | P1 | L | 1h/6h/24h |
| Speed control | ✖ | yes | all | none | P1 | L | 0.25×–8× |
| Pause / resume | ✖ | yes | all | none | P1 | L | space |
| Frame step | ✖ | ? | XProtect | none | P2 | L | device/I-frame dependent, label it |
| Fast seeking | ✖ | yes | all | none | P1 | M | keyframe seek + preroll |
| Multi-camera playback | ✖ | yes | all | none | P2 | H | 2×2 max, synced |
| Synchronised playback | ✖ | ? | XProtect/Nx | none | P2 | M | single clock, per-stream offset |
| Event markers | ✖ | yes | all | none | P1 | L | from events table |
| Motion markers | ✖ | yes | device events | none | P2 | L | device-generated only |
| Recording gaps | ✖ | ? | XProtect | none | P1 | L | derive holes from segments |
| Export time range | ▣ | yes | all | none | P1 | M | range picker + progress |
| Snapshot export | ✖ | yes | all | none | P1 | L | PNG with timestamp overlay |
| Download progress | ✖ | yes | all | none | P1 | L | determinate bar |
| Export cancellation | ✖ | yes | all | none | P1 | L | cancel token |
| Export error recovery | ✖ | ? | XProtect | none | P1 | L | partial → resume/retry |
| Original timestamps | ✖ | yes | all | none | P1 | L | preserve + sidecar JSON |
| Recording source id | ✖ | yes | all | none | P2 | L | camera + stream in filename |
| Export integrity check | ✖ | ? | XProtect | none | P2 | L | SHA-256 manifest |

## 4. Events and alerts (D)

| Feature | Camzivo | SmartPSS Lite | Industry | Gap | Pri | Cost | Recommendation |
|---|---|---|---|---|---|---|---|
| Camera offline alert | ✖ | yes | all | none | P0 | L | event from watchdog |
| Camera online recovery | ✖ | yes | all | none | P0 | L | paired event |
| Recording interruption | ✖ | yes | XProtect | none | P2 | L | needs NVR poll |
| Device connection errors | ◐ wording only | yes | all | no records | P1 | L | persist diagnostic events |
| Storage warnings | ✖ | yes | XProtect | none | P3 | L | NVR-side only |
| Motion events | ✖ | yes | device | none | P1 | L | ONVIF PullPoint |
| Tamper events | ✖ | ? | device | none | P2 | L | same subscription |
| Alarm input events | ✖ | yes | device | none | P2 | L | same subscription |
| Event history | ▣ placeholder | yes | all | no table UI | P1 | L | list + detail |
| Event filtering | ✖ | yes | all | none | P1 | L | type/camera/time |
| Event acknowledgment | ✖ | yes | XProtect | none | P2 | L | ack + operator name |
| Notification preferences | ✖ | yes (email) | XProtect | none | P3 | L | local toast only, no SMTP |
| Event → playback jump | ✖ | yes | all | none | P1 | L | deep link with timestamp |

## 5. System health (E)

| Feature | Camzivo | SmartPSS Lite | Industry | Gap | Pri | Cost | Recommendation |
|---|---|---|---|---|---|---|---|
| Connected camera count | ✖ | yes | all | demo numbers | P0 | L | derive from engine |
| Offline camera count | ✖ | yes | all | same | P0 | L | derive |
| Active stream count | ✖ | yes | Nx | none | P0 | L | derive from scheduler |
| CPU usage | ✖ | ? | all | none | P1 | L | `Process.TotalProcessorTime` delta |
| GPU usage | ✖ | ? | XProtect | none | P2 | M | optional adapter counters |
| RAM usage | ◐ status bar | yes | all | not in health | P1 | L | move to health, keep out of operator chrome |
| Network throughput | ✖ | ? | Nx | none | P2 | L | sum of stream bitrates |
| Stream bitrate | ✖ | yes (iVMS) | all | none | P1 | L | from decoder/statistics |
| Frame rate | ✖ | yes | all | none | P1 | L | presented fps vs nominal |
| Dropped frames | ✖ | ? | Nx | queue counts exist | P1 | L | surface `QueueStatistics` |
| Reconnection attempts | ✖ | ? | XProtect | none | P1 | L | engine counter |
| Device latency | ✖ | ? | — | none | P2 | L | DESCRIBE RTT |
| Recording availability | ✖ | yes | all | none | P2 | M | NVR query |
| Decoder errors | ✖ | ? | Nx | none | P1 | L | per-camera error surface |
| Application logs | ✅ rotating 5 × 2 MB, redacted, crash marker (no in-app viewer) | yes (bundle) | all | viewer + bundle export | P0 | L | open log folder from Settings (M6) |
| Diagnostic export | ✖ | yes | all | none | P2 | L | zip log + config (no secrets) |

## 6. Workspace and productivity (F)

| Feature | Camzivo | SmartPSS Lite | Industry | Gap | Pri | Cost | Recommendation |
|---|---|---|---|---|---|---|---|
| Saved views | ✖ | yes | all | none | P1 | L | name + restore wall |
| Multiple workspaces | ✖ | yes (scheme) | XProtect | none | P2 | L | tabs of saved views |
| Multi-monitor layouts | ✖ | yes | all | none | P1 | M | per-monitor wall |
| Recently used cameras | ✖ | yes | — | none | P2 | L | LRU list |
| Favorites | ✖ | yes | all | none | P2 | L | see A |
| Camera search | ◐ | yes | all | reset bug | P1 | L | fix + keyboard |
| Quick actions | ◐ disabled buttons | yes | all | no-op | P1 | L | wire or hide |
| Keyboard shortcuts | ◐ | yes | all | wall keys done, paging/Enter missing | P1 | L | finish map |
| Configurable startup view | ◐ restores last | yes | all | not configurable | P2 | L | pick section or view |
| Restore previous session | ✅ theme, layout, section and wall placements (codec tested) | yes | all | none | P1 | L | done |
| Compact mode | ✖ | ? | — | none | P3 | L | density token |
| Dark/light theme | ✅ | yes | all | — | P1 | L | done |
| Layout import/export | ✖ | yes (plan files) | XProtect | none | P3 | L | JSON |
| Settings backup/restore | ✖ | yes | XProtect | none | P2 | L | copy config + db |

## 7. Administration and security (G)

| Feature | Camzivo | SmartPSS Lite | Industry | Gap | Pri | Cost | Recommendation |
|---|---|---|---|---|---|---|---|
| Secure device credentials | ◐ column only | plaintext INI | DPAPI | **no cipher** | P0 | L | DPAPI now |
| Local application settings | ✅ | yes | all | small set | P1 | L | extend |
| Role-based access | ✖ | yes (users) | all | none | P3 | M | only if multiuser justified |
| Audit logs | ◐ repo, unused | yes | all | **nothing writes** | P1 | L | audit every mutation |
| Configuration backup | ✖ | yes | XProtect | none | P2 | L | one-click export |
| Controlled updates | ✖ | yes | all | none | P3 | M | signed update later |
| Secure exports | ✖ | ? | XProtect | none | P2 | L | hash manifest |
| Device access permissions | ✖ | yes | XProtect | none | P3 | M | follows RBAC |
| Windows authentication | ✖ | yes | XProtect | none | P3 | M | optional, later |
| Dependency security checks | ✖ | ? | all | none | P2 | L | `--vulnerable` in CI |

## 8. Optional advanced (H) — evaluated, deliberately not scheduled

| Feature | Camzivo | Industry | Decision |
|---|---|---|---|
| Smart camera maps | ✖ | XProtect, Nx Maps | P3, separate module |
| Floor plans | ✖ | XProtect, Nx | P3 |
| Motion heatmaps | ✖ | Agent DVR | P3; device/analytics cost |
| Object detection | ✖ | Blue Iris 6, Agent DVR | out of core (CPU/GPU) |
| People counting | ✖ | Agent DVR | out |
| LPR | ✖ | Blue Iris, Agent DVR | out |
| AI-assisted search | ✖ | Nx, Agent DVR | out |
| Cloud sync | ✖ | Nx, Agent DVR | out (zero egress invariant) |
| Mobile companion | ✖ | all | P3 |
| Web monitoring | ✖ | Nx, Agent DVR | P3; costs a server process |
| Central management | ✖ | XProtect | out of scope |
| Distributed recording | ✖ | XProtect, Nx | out |
| Advanced alert rules | ✖ | XProtect, Nx | P3 (event rules engine) |
| External integrations | ✖ | all | P3 |

---

## 9. Gap summary by class

**Functional.** P0: no video, no network device path, no credential protection, no playback/export, no events.
Counted across the 137 rows (verified by counting the rows themselves): **✅ 10 (7 %)**, **◐ 19 (14 %)**,
**▣ 6 (4 %)**, **⚠ 0**, **✖ 102 (74 %)**. An earlier revision claimed 148 rows with 13 verified — that was an
estimate and it was wrong. Every count here is produced by counting the matrix rows, never by estimating.

**UX.** Layout switching is good and fullscreen is now real (probe-verified); the wall still has no saved views, no session restore before this pass, dead
command-bar buttons, and a status bar that shows developer telemetry instead of stream health.

**Performance.** Shell is 2.4–3.9 s warm / 120–150 MB / 13 threads versus the incumbent's 8.7 s / 160 MB /
182 threads / 3 processes. No video path exists, so the 16/49/64-tile budgets are unproven, not met.

**Reliability.** No crash logging, no global handlers, fire-and-forget writes, no single-instance guard, no
watchdog (nothing to watch yet).

**Security.** Credential storage is a column, not an implementation; the audit trail is a repository nobody calls;
no TLS anywhere (defensible for LAN RTSP but must be surfaced).

**Accessibility.** Automation names exist on tiles/nav/theme; contrast is tokenised; keyboard coverage is partial;
status is colour **and** text (good) but there is no focus-visible style audit and no screen-reader pass.

**Testing.** 122 pure-logic tests, zero UI/network/soak. The riskiest code (UI state, engine seams) is untested.

**Documentation.** 17 pre-existing docs cover design intent well; they over-claimed drag-and-drop and fullscreen
before this audit and are corrected here. No user-facing manual, no troubleshooting guide.
