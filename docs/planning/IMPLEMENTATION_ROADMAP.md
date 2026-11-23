# Implementation Roadmap — Camzivo

Authoritative plan. Milestones are ordered by dependency, not by appeal. Each has an acceptance gate that is
**measured or tested**, and a "not done if" clause that prevents the roadmap from lying to itself.

Status legend: ✅ done · 🔄 in progress · ⏳ planned · 🚫 out of scope.

---

## M0 — Research and product definition ✅

Deliverables: incumbent analysis, technology evaluation, requirements, 17 documents.
Gate: stack chosen with evidence; no proprietary code or assets used.
Evidence: `docs/SMARTPSS_ANALYSIS.md`, `docs/TECHNOLOGY_EVALUATION.md`, measured baseline (8,693 ms, 160 MB,
182 threads, 383 MB installed).

## M1 — Domain core and data layer ✅

Layout geometry, decode-budget scheduler, bounded frame queue, stream policy, RTSP URI builder, credential
redactor, diagnostics wording, ONVIF discovery parser, device model, SQLite schema + migrations + repositories.
Gate: 155 tests green, 0 warnings.
Not done if: any of these is only a model with no tests.

## M2 — Shell and design system ✅ (with open UX defects)

Nav, command bar, status bar, camera wall 1–64, click-to-place, selection, clear tile, theme tokens (dark/light),
Settings, Devices, Health pages, preference persistence.
Gate met: build clean, tests green, snapshots reviewed, no false `LIVE` claims.
Open: fullscreen (BUG-01), wall persistence (BUG-03), tile rebuild churn (BUG-04), list reset (BUG-05), crash
logging (BUG-02) — **closed in M2.1 (this pass)** except BUG-04's streaming case.

## M3 — Devices and credentials ⏳

ONVIF discovery (WS-Discovery probe, dedupe, candidate list), manual add/edit/delete, connection test with the four
stages (reach → authenticate → describe → first frame), ONVIF media profiles → main/sub stream URIs, DPAPI secret
store, audit rows for every mutation, Duplicate detection.
Gate: on the lab LAN, a Dahua camera is added in ≤ 6 fields and appears on the wall as `CONNECTED`; a wrong password
produces the diagnostic wording; password absent from the DB (verified by `strings`/query); 24 h of 20 real cameras
with no unhandled exception.
Not done if: any credential is stored outside DPAPI, or discovery blocks the UI for > 200 ms.

## M4 — Video engine ⏳

FFmpeg (LGPL build) demux/decode → D3D11VA surfaces → single `D3DImage` composite; scheduler-driven decoder
allocation; substream-first policy with manual override; per-tile state from frame age (`CONNECTING`, `LIVE`,
`RECONNECTING n`, `OFFLINE`, `UNSUPPORTED`); reconnect with backoff; teardown soak.
Gate: 16 substreams at ≤ 20 % CPU, 49 at ≤ 45 %, UI composition ≥ 55 fps during decode, first frame ≤ 1.5 s LAN,
100 open/close cycles with adapter memory within 5 %, 8 h soak with ≤ 5 % RSS growth.
Not done if: any frame is copied through the CPU, or `LIVE` can appear without a decoded frame.

## M5 — Playback and exports ⏳

Recorder query, calendar + timeline with availability shading and gaps, play/seek/speed/step, snapshot, export with
original timestamps, progress, cancel, retry, hash manifest, export queue page.
Gate: incident walkthrough ("what happened at 14:32") in ≤ 3 actions; seek ≤ 1 s; 10-minute export verifies against
its manifest; export during live monitoring does not stutter the wall.
Not done if: a file is reported complete before flush + hash.

## M6 — Events, health and multi-monitor ⏳

ONVIF event subscription (motion, tamper, input, offline), event list/filter/ack/jump-to-playback, health counters
from the engine, log viewer + diagnostics bundle, detached wall on a second monitor, DPI-change handling.
Gate: an unplugged camera produces exactly one offline event and one recovery event; health shows fps/bitrate/drops
per camera; moving the wall between 100 % and 150 % displays does not resize content or lose selection.

## M7 — Performance hardening ⏳

Startup (composite R2R, store gate, theme cache), tile VM reuse under streaming load, allocation audit, DB writer
serialisation, VACUUM/retention, UI harness + frame pacing, soak automation.
Gate: warm start ≤ 1,500 ms p50; idle ≤ 200 MB; no frame > 32 ms in a 10-minute 16-stream run; measured before/after
for each optimisation.
Not done if: any claimed improvement lacks a number.

## M8 — Release hardening ⏳

Installer (per-user, no elevation, clean uninstall), signature, upgrade/migration tests (including the profile
rename), single-instance guard, accessibility pass with a screen reader, 24 h soak with 20 cameras, security
checklist (`SECURITY_AUDIT.md` §6), documentation for operators.
Gate: `RELEASE_READINESS.md` classification moves from Alpha to Beta.

---

## Sequencing and effort

| Milestone | Depends on | Rough effort | Biggest risk |
|---|---|---|---|
| M2.1 fixes (this pass) | M2 | 1–2 days | XAML fullscreen edge cases |
| M3 devices | M2 | 1–2 weeks | ONVIF device variance (Dahua quirks) |
| M4 engine | M3 (stream URIs), M1 scheduler | 3–4 weeks | decoder/renderer correctness, GPU resets |
| M5 playback | M4 (transport), M3 (recorder query) | 2–3 weeks | vendor recording APIs differ |
| M6 events/health | M4 | 1–2 weeks | event flood control |
| M7 perf | M4 | 1 week | measurement harness |
| M8 release | M3–M7 | 1–2 weeks | installer/signing |

## Non-negotiables carried through every milestone

No UI-thread network or decode; bounded queues only; no secret in plaintext or logs; every feature states its
resource cost; no `LIVE` without frames; no dead buttons; tests before merge; docs updated in the same change.
