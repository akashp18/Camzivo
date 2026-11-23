# Test Strategy — Camzivo

Goal: every claim in the docs is backed by a test, a measurement or a rendered artefact. Where none exists, the
claim says "not verified".

---

## 1. Current state (measured)

| Suite | Cases | What it covers |
|---|---|---|
| `Camzivo.Core.Tests` | 234 | layout geometry, slot assignment (incl. move-not-duplicate), wall codec, scheduler, bounded queue, stream policy, RTSP URI, redaction, ONVIF parser, diagnostics wording, playback speeds/rate meter |
| `Camzivo.Data.Tests` | 93 | migrations (v5), backup/prune (incl. pre-rename names), newer-schema refusal, device/camera/wall-layout/event/export repositories, app paths + profile migration, app log, coalescing preference writer |
| `Camzivo.Desktop.Tests` | 95 | wall view model, timeline maths (`TimelineLayout`), calendar grid, window placement codec, evidence manifests, health mapping, stream lifecycle, snapshots/PTZ, speed selection |
| **Total** | **422** | logic + persistence + view models, including read-only NVR safety |
| Window automation | 4 probe harnesses | `--probe-live-wall`, `--probe-playback`, `--probe-onboarding-e2e`, `--probe-playback-speeds`; no UIA harness for arbitrary clicking |
| Network/device | simulator + RTSP test source | recording simulator (ONVIF Profile G, live RTSP) and local H.264 source; **no Dahua hardware** |
| Performance/soak | benchmark probe | 9/16/49 streams, main and sub; 12 s windows |
| Visual regression | 14 snapshots | 7 pages × dark/light, compared by human eye |

TDD is the rule for new work: failing test → smallest fix → green → lint/build → docs. The 422 cases are the
regression floor and must never be deleted to make a change pass. Accelerated playback adds deterministic tests for
the supported speed set, invariant `Scale`, measured-rate maths, timestamp-reset rebasing and speed selection, plus
the real `--probe-playback-speeds` run (requested vs measured, decoded vs presented, pause/resume, seek, end,
ignored and rejected `Scale`).

## 2. Test pyramid for this product

| Layer | Proportion | Examples |
|---|---|---|
| Unit (Core) | 60 % | scheduler ordering, auth digest, wall codec, diagnostics |
| Component (Data) | 15 % | migration, repositories, retention |
| Contract (protocol) | 15 % | RTSP/DESCRIBE against a scripted fake server, ONVIF XML fixtures |
| UI automation | 5 % | place/clear/restore, fullscreen toggle, layout persistence |
| End-to-end (device lab) | 5 % | real camera + NVR: add, live, reconnect, playback, export |

Rationale: the logic that decides *what appears on screen* must be testable without a screen; the engine must be
testable without a camera.

## 3. Mandatory regression scenarios (brief §10) and where they live

| # | Scenario | Owner test | State |
|---|---|---|---|
| R1 | Start/stop streams repeatedly | engine soak 100 cycles | M4 |
| R2 | Switch grid layouts | UI automation + scheduler unit | UI part pending |
| R3 | Switch main/substreams | policy unit + engine integration | unit done |
| R4 | Disconnect/reconnect cameras | fake-socket integration | M4 |
| R5 | Restart NVR connections | device lab | M4 |
| R6 | Resize window during playback | UI automation | M5 |
| R7 | Move window between monitors | manual protocol + DPI matrix | M6 |
| R8 | 49-camera monitoring | soak with synthetic streams | M4 |
| R9 | Export while monitoring live | concurrent integration | M5 |
| R10 | Close app during active streams | shutdown test, assert no hang ≤ 3 s | M4 |
| R11 | Restore saved workspaces | wall codec + UI automation | **being added** |
| R12 | Recover after crash | kill process, restart, expect banner + intact DB | **being added** |

## 4. Test infrastructure to build

1. **Fake RTSP server** (in-process, scripted): OPTIONS/DESCRIBE/SETUP/PLAY, digest challenge, 401-before-auth,
   malformed SDP, mid-stream disconnect. Gives deterministic auth/reconnect tests with no hardware.
2. **ONVIF fixtures**: captured XML for discovery, device info, media profiles, PTZ — plus hostile cases (huge
   payload, entity expansion, wrong namespace).
3. **Synthetic video**: `mediamtx` + `ffmpeg` loop files (1080p H.264, 1080p H.265, low-bitrate substream) so
   decode/render can be measured and soaked without cameras.
4. **UI harness** (FlaUI or WPF UIA): drives the real window; asserts tile states, keyboard paths, persistence.
5. **Performance harness**: startup 5-run aggregation, RSS/handle sampling, `CompositionTarget.Rendering` frame
   intervals, engine counters.
6. **Window-state probes**: `--probe-fullscreen` exercises the real transition and records `WindowStyle`,
   `WindowState`, `ResizeMode`, title-bar visibility/row height, caption height and the view-model flag before,
   during and after. It proves state changes without a human, but not what the operator sees (taskbar coverage,
   multi-monitor) — that stays a manual check. `--probe-startup` records cold/warm first-frame latency.

## 5. Test data rules

- Never modify a real camera or NVR configuration during a test.
- No test may touch the operator's real `%APPDATA%` profile: every Data/UI test uses a temp root (this is why
  `AppPaths` must accept injected roots — BUG-08).
- Secrets in tests are synthetic; logs are asserted to be redacted.

## 6. Definition of done for a change

1. A test fails before, passes after (or a written reason why the case cannot be automated yet).
2. `dotnet build Camzivo.sln -warnaserror` clean.
3. `dotnet test` green; no test deleted or weakened.
4. If performance-sensitive, a before/after number with method.
5. Docs updated in the same change (screen spec, budget, or gap matrix row).
6. No new dead UI: a control either works, is disabled with a reason, or is not shipped.

## 7. Flaky-test policy

Timing-dependent assertions are banned unless they assert an invariant. Example already applied: the concurrent
queue test asserts `Enqueued − Dequeued − Dropped == Count` and `Count ≤ Capacity` instead of "the consumer saw at
least one item", which failed once under scheduling variance. A test that fails for timing reasons is rewritten, not
retried.

## 8. Coverage targets

| Area | Target |
|---|---|
| Core logic | ≥ 90 % line coverage (currently unmeasured; enable coverlet in CI) |
| Data/migrations | 100 % of paths incl. failure branches (currently 23 cases) |
| Protocol parsing | every hostile fixture has a test |
| UI | every keyboard path and every persisted state has one automation test |
| Reality | the numbers above are targets; the release doc records actuals |
