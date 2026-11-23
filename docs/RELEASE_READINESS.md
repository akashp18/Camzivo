# Camzivo Release Readiness — 2026-11-23

This is the evidence for the redesign/feature-completion pass, updated for the Dahua NVR compatibility pass. It
separates what was **built**, what was **run**, and what remains **unverified**. No row below claims real Dahua
hardware. The dedicated audit, compatibility matrix and blocked hardware procedure are in
[`DAHUA_NVR_COMPATIBILITY.md`](DAHUA_NVR_COMPATIBILITY.md).

> **Real Dahua NVR acceptance: NOT RUN in this task.** Hardware testing was intentionally kept offline; no real NVR
> or camera was contacted. `--probe-nvr` is blocked by default and requires separate authorization plus an exact-host
> confirmation. Simulator evidence is not presented as hardware validation.

> **Metadata-only verification (2026-11-23):** `--probe-nvr-metadata` was added and exercised offline only. It has its
> own authorization flag, sends nine Get-only device/media operations, cannot reach streams, recordings, PTZ or
> configuration, and has not been run against a real recorder.

## 1. Build and automated tests

| Check | Command | Result |
|---|---|---|
| Clean build, warnings as errors | `dotnet build Camzivo.sln -warnaserror` | **pass** |
| Core tests | `dotnet test Camzivo.sln` | **261 pass** |
| Data tests (including v5 operator state, v6 NVR compatibility, read-only and metadata-only safety) | same | **102 pass** |
| Desktop tests (timeline maths, calendar, placement, evidence manifests, health mapping, live/stream, speed selection, read-only safety) | same | **96 pass** |
| Total | | **459 pass, 0 failed** |

New deterministic tests in this pass:

- `OnvifConnectivityTests` — advertised Media XAddr routing, system time and profile audio parsing.
- `RecordingSoapTests` — `IncludedSources/SourceToken` SOAP shape and Profile G source parsing.
- `NvrChannelPlannerTests` — unnumbered alternating main/sub pairing.
- `DeviceRepositoryTests` — firmware/serial/timezone/last-error/diagnostic identity round trip.
- `NvrOnboardingTests` — recorder source-token mapping and the read-only diagnostics report.
- `NvrSafetyTests` / `ReadOnlyNvrTransportTests` / `NvrDiscoverySafetyTests` / `OnvifRecordingSafetyTests` /
  `NvrSafetyDesktopTests` — operation allowlist, exact-host and port enforcement, cross-host service/stream rejection,
  redirect non-following, disabled recorder PTZ, local-only export paths, and default-blocked hardware gate.
- `NvrMetadataProbeTests` plus the metadata cases in `NvrSafetyTests` — metadata-only allowlist and its separate
  authorization gate, cross-host service reporting without contact, bounded request budget and digest retries, overall
  time limit, credential/token-free reports, host masking, and unsupported-operation reporting.
- `TimelineLayoutTests` — fractions, clipping at both window edges, gaps, hit testing, ruler ticks, seek mapping,
  compact durations.
- `CalendarMonthTests` — six-week grid, Monday start, outside-month cells, today vs selected, leap day.
- `WindowPlacementCodecTests` — round trip, culture independence, rejects off-screen/corrupt/undersized placements.
- `EvidenceManifestTests` — SHA-256 sidecar in `sha256sum` format, tamper detection, missing file/sidecar, missing
  source file.
- `HealthViewModelTests` — offline is critical, queued is a warning, healthy wall is silent.
- `PlaybackRateTests` — supported speed set, invariant RTSP `Scale`, source-time rate meter, timestamp-reset rebasing,
  requested-versus-delivered tolerance.
- `PlaybackSpeedTests` — six-speed selector and 1× default, selection before play, unsupported speed notice,
  camera-change reset.
- `OperatorStateRepositoryTests` — named layouts, event history and export records (v5 tables, no FK to camera).

## 2. Real-application acceptance runs

### 2.1 Live wall (real RTSP, real Add Camera dialog)

Command:

```
Camzivo.Desktop.exe --probe-live-wall artifacts/live \
  --test-source tools/Camzivo.TestSource/bin/Debug/net8.0/Camzivo.TestSource.exe
```

Result: **12/12 steps pass** (`artifacts/live/probe-summary.txt`), including:

- the real Add Camera dialog: fill `rtsp://127.0.0.1:8554/live` → Test connection → Save;
- first decoded frame at 640 × 360, then consecutive distinct frames;
- 4/9-slot layout switching, fullscreen enter/exit with the tile still live;
- move tile (same decoder session), undo, drag drop indicator;
- stream stop → `WAITING` with reason, source restart → `LIVE` again;
- device removal → **0 engine sessions**, tile unassigned.

Screenshots: `artifacts/live/01-devices.png` … `13-drop-indicator.png`.

### 2.2 Onboarding → persistence → playback → export (simulator)

Command:

```
Camzivo.Desktop.exe --probe-onboarding-e2e artifacts/e2e \
  --simulator tools/Camzivo.RecordingSimulator/bin/Debug/net8.0/Camzivo.RecordingSimulator.exe \
  --fixture tests/Camzivo.Core.Tests/Fixtures/h264-640x360.h264
```

Result: **11/11 steps pass** (`artifacts/e2e/summary.txt`):

1. simulator ready (ONVIF/Profile G on `:8109`, RTSP on `:8566`);
2. ONVIF device → profiles → stream URI → **decoded live frame** (5/5 stages);
3. camera saved through `DeviceOnboarding` into a temporary SQLite profile with a DPAPI secret;
4. read back from the database (`rtsp://127.0.0.1:8566/live` main and sub);
5. the store is closed and **reopened** (a real restart of the composition root), password decrypted;
6. a new `MainViewModel` reads the camera from the database and shows it in Playback;
7. Profile G search returns **3 recordings / 6 h 45 min** for today;
8. playback decodes 640 × 360 frames;
9. pause holds the frame count (12 → 12), resume advances (→ 16);
10. stop leaves no active session;
11. export writes **1.12 MB / 91 samples**; the SHA-256 sidecar verifies.

Screenshots: `artifacts/e2e/01-restarted-playback.png` … `05-live-after-restart.png`. The timeline visibly contains
the 00:00–00:25 cross-midnight block, the 08:15–11:45 block and the 12:30–15:00 block in both search and playing
states; the previously reported “blocks disappear” defect does not reproduce after the control rewrite.

### 2.3 Playback mechanics (seek, stop, progress)

`--probe-playback` (10/10 steps, `artifacts/playback/summary.txt`): three segments found, play, pause (11 → 11
frames), resume, **seek to 08:35:00 with 0.0 s drift**, stop with 0 live sessions, and a 6 s export with 66 progress
callbacks. Screenshots `artifacts/playback/01…09`.

### 2.4 Accelerated playback (1×–16×, real RTSP replay)

```
Camzivo.Desktop.exe --probe-playback-speeds artifacts/speeds-final15 \
  --simulator tools/Camzivo.RecordingSimulator/bin/Debug/net8.0/Camzivo.RecordingSimulator.exe \
  --fixture tests/Camzivo.Core.Tests/Fixtures/h264-640x360.h264
```

Result: **15/15 steps pass** (`artifacts/speeds-final15/summary.txt`, `speeds.json`). The probe records wall-clock time
and RTP source position separately, hashes the decoded picture before/after each window, and counts decoded versus
presented frames. Tolerance is ±15 % at 1× and ±25 % above 1× over 4 s / 3 s windows.

| Requested | Measured | Decoded AUs | Presented | CPU | Memory |
|---:|---:|---:|---:|---:|---:|
| 1× | 0.97× | 60 | 61 | 12.9 % | 254 MB |
| 2× | 1.93× | 88 | 37 | 12.0 % | 255 MB |
| 4× | 3.86× | 177 | 41 | 14.6 % | 253 MB |
| 6× | 5.80× | 265 | 42 | 12.2 % | 252 MB |
| 8× | 7.69× | 353 | 42 | 12.5 % | 262 MB |
| 16× | 15.77× | 722 | 52 | 22.9 % | 256 MB |

The same run proves pause at 4×, selecting 8× while paused (283 frames held, then advanced 12.0 source seconds),
seek at 8×, end-of-recording, zero remaining sessions, a recorder that **ignores** Scale (16× requested, 0.93×
measured, notice shown) and one that **rejects** it (initial failure visible; change falls back to 1× with a reason).
It also returns a **previous-day** block (2026-11-21, 14:00–15:00) and plays it at 15.55× with changed frames. A
prior run of the same probe measured 12.93× at 16×, so the 12.93–15.77× range reflects decoder throughput rather
than a different clock; both are inside the ±25 % acceptance tolerance. See `docs/PLAYBACK_SPEED.md`.

### 2.5 Visual review

`--snapshot` was run for all seven pages in both themes with the application's demo data:

```
artifacts/ui/dark-LiveView.png   … dark-Settings.png
artifacts/ui/light-LiveView.png  … light-Settings.png
```

Reviewed at 1600 × 900: typography, spacing, alignment, contrast, button chrome, empty states, table headers,
timeline and transport controls. Defects found and fixed during that review:

- ComboBox template resolved `Icon.ChevronDown` before the icon dictionary was merged (crash) → merge order fixed.
- `PopupAnimation="{x:Null}"` is not a valid value (crash in DateField/ComboBox) → removed.
- `ProgressBar.Value` bound two-way to a read-only property (crash on Playback) → `Mode=OneWay`.
- All transport/zoom icons were invisible in the first build because icons were letter tokens; every control now uses
  the shared vector `IconPath` family (no foreground patches).
- Devices showed “no devices match this search” with one device; playback duplicated “Idle”; camera/layout combo boxes
  were blank when empty; search fields lacked placeholders; settings said “0 days” of retention; health codes were raw
  enum names; the timeline header read `00:00 – 00:00`.
- Export range seeded from a midnight-crossing recording showed “end must be after start” before any edit; the range
  is now clamped to the selected day.

## 3. Feature status

| Area | Status | Evidence / limit |
|---|---|---|
| Shell, 6-item nav, collapsing sidebar, title bar, status bar | implemented | snapshots, keyboard shortcuts |
| Dark + light theme, semantic tokens | implemented | both snapshot sets; `Themes/Tokens.*` |
| Shared component library | implemented | `Themes/Controls.xaml`; DESIGN_SYSTEM §6 |
| Live View wall, 1–64 slots, drag/drop, fullscreen, snapshots, PTZ panel, saved layouts | implemented | live probe 12/12; layouts persisted (v5) |
| Devices table, search, rename, remove, reconnect, NVR import dialog | implemented | live probe; existing onboarding tests |
| Playback search, timeline, zoom/pan, playhead, transport, gaps, export range | implemented | playback/E2E runs |
| Export with progress, cancel, SHA-256 + JSON sidecar | implemented | E2E step 11 |
| Events: engine transitions, filters, severity, event → playback | implemented | repository tests; UI snapshot |
| Device motion/alarm events (ONVIF PullPoint) | **not implemented** | no subscription; documented |
| Audio listen / two-way talk | **not implemented** | no audio pipeline; no UI offered |
| Named camera layouts | implemented | v5 repository + Live View UI |
| Second display window | implemented (mirrored) | `WallWindow`; independent layouts not implemented |
| Window placement persistence | implemented | codec tests; restored when display present |
| Settings (appearance, startup, retention, storage, about) | implemented | snapshot; retention now defaults correctly |
| Camera health | implemented from live wall | health mapping tests |
| Accessibility: automation names, focus rings, keyboard, contrast | implemented | code review + XAML; no automated screen-reader run |
| High DPI | layout in DIPs; per-monitor conversion for the second window | not exercised on mixed-DPI hardware |

## 4. Performance

Measured on this machine after the UI work, with the production decode/present path and the local test source
(`--probe-benchmark`, 12 s window, 15 fps source):

| Streams | Source | Presented FPS total | FPS per camera | CPU (all cores) | Working set | Dropped |
|---|---|---|---|---|---|---|
| 9 | main (640×360) | 108.1 | 12.0 | 35.4 % | 343 MB | 0 |
| 16 | main | 176.8 | 11.1 | 45.4 % | 461 MB | 0 |
| 49 | main | 246.4 | 5.0 | 64.7 % | 909 MB | 0 |
| 16 | sub | 208.0 | 13.0 | 14.4 % | 265 MB | 0 |
| 49 | sub | 629.1 | 12.84 | 42.0 % | 440 MB | 0 |

The final 49-camera substream result after accelerated playback is **12.84 FPS / 42.0 % / 440 MB**, against the
post-redesign run of 11.6 FPS / 38.0 % / 445 MB and the pre-redesign baseline of ~12.9 / ~39.9 % / ~441 MB. The live
decode/present path is unchanged (the accelerated presentation gate is attached only to replay decoders); the CPU
figure is higher than the earlier run while FPS is also higher, so it is reported as measured rather than as a
speed-up. Raw data: `artifacts/bench-speed-final/benchmark.csv`.

## 5. Not verified / limitations

1. **Real Dahua hardware — NOT TESTED in this task.** Hardware access was intentionally not used, so Profile G,
   Dahua channel naming, source tokens, firmware quirks, ONVIF event subscription and backchannel behaviour remain
   unverified. The real-hardware probe is blocked unless `--nvr-authorized` and `--nvr-confirm <same exact host>` are
   present.
2. **Independent multi-monitor layouts** — the second window mirrors the main wall; mixed-DPI placement is
   implemented but untested on a physical multi-monitor setup.
3. **Audio and two-way talk** — absent end to end. Accelerated playback has a documented future policy: normal audio
   at 1×, muted above 1× unless time-stretching exists.
4. **Recorder-honoured Scale on real hardware** — the client is proven against honour/ignore/reject simulator modes,
   not against a Dahua/NVR replay endpoint. A recorder that delivers only real time is reported at its measured 1×;
   Camzivo does not fabricate 16× from it.
5. **Device motion events** — absent; only application-observed transitions are recorded.
6. **Independent media validation of the newest export** — no `ffprobe` on this machine. The export passed the muxer's
   own checks, decoded during the run, and its SHA-256 sidecar verifies.
7. **Screen-reader and 200 % DPI** — implementation follows the rules, but no Narrator/Inspector run was performed.
8. **49-camera benchmark** — re-run after this feature (see §4); FPS is slightly higher than the previous run but CPU
   is also higher (42.0 % vs 38.0 %), so the result is reported as measured, not as an improvement.

## 6. How to reproduce

```
dotnet build Camzivo.sln -warnaserror
dotnet test Camzivo.sln --nologo

Camzivo.Desktop.exe --snapshot artifacts/ui/live.png --demo --section LiveView
Camzivo.Desktop.exe --snapshot artifacts/ui/light-live.png --demo --light --section LiveView

Camzivo.Desktop.exe --probe-live-wall artifacts/live \
  --test-source tools/Camzivo.TestSource/bin/Debug/net8.0/Camzivo.TestSource.exe

Camzivo.Desktop.exe --probe-onboarding-e2e artifacts/e2e \
  --simulator tools/Camzivo.RecordingSimulator/bin/Debug/net8.0/Camzivo.RecordingSimulator.exe \
  --fixture tests/Camzivo.Core.Tests/Fixtures/h264-640x360.h264
```

## 7. Real Dahua NVR acceptance (current result: BLOCKED)

No NVR address or credentials were available for this pass. On a machine that can reach the recorder, run the
metadata-only probe first — it has its own authorization flag and sends only nine Get-only device/media operations:

```powershell
Camzivo.Desktop.exe --probe-nvr-metadata artifacts/nvr-metadata `
  --nvr-host 192.168.1.108 --nvr-user admin `
  --nvr-port 80 --nvr-metadata-authorized --nvr-confirm 192.168.1.108
```

Offline evidence: against the local simulator the metadata probe sent 9 requests, received 9 answers (identity,
capabilities, services, service capabilities, clock, 3 profiles, 3 video sources, 3 encoder configurations) and
exited **0** in 0.2 s; without `--nvr-metadata-authorized` it exited **2** and sent nothing. It opens no RTSP
connection and can issue no recording, PTZ or configuration request. **Not run against a real recorder.**

The full acceptance probe:

```powershell
$env:CAMZIVO_NVR_PASSWORD = "..."   # keep the password out of the command line and logs
Camzivo.Desktop.exe --probe-nvr artifacts/nvr-real `
  --nvr-host 192.168.1.108 --nvr-user admin `
  --nvr-port 80 --nvr-rtsp-port 554 `
  --nvr-authorized --nvr-confirm 192.168.1.108
```

The probe also requires `CAMZIVO_NVR_PASSWORD`. It discovers identity/services/channels, imports all channels into a temporary local profile, reopens that
profile (restart persistence), decodes live H.264, searches the previous day, plays at 1×, measures 2×–16×, and
exports a short MP4. Without a host/password it exits **2** and writes `BLOCKED`; it never presents simulator evidence
as Dahua validation. See `docs/DAHUA_NVR_COMPATIBILITY.md` §7–§9 and `docs/READ_ONLY_NVR_MODE.md` for the safety boundary,
matrix and limitations.


## 8. Read-only NVR safety verification

Implemented and documented in [`READ_ONLY_NVR_MODE.md`](READ_ONLY_NVR_MODE.md). This pass did **not** contact any real
NVR or camera and did not run the real-hardware probe.

- ONVIF calls are constrained by `NvrAccessPolicy` to one exact host/port/scheme and by
  `NvrReadOnlyOperations` to a default-deny allowlist.
- Cross-host Media/Search/Replay XAddrs and stream/replay URIs are rejected before the inner transport/probe is called.
- HTTP redirects are not followed; configuration writes and PTZ movement are blocked; recorder-owned channels hide PTZ.
- UNC/device export paths are rejected, keeping evidence on the local computer.
- The hardware probe is blocked unless `--nvr-authorized` and `--nvr-confirm <same exact host>` are both present.
- New safety tests: `NvrSafetyTests`, `ReadOnlyNvrTransportTests`, `NvrDiscoverySafetyTests`,
  `OnvifRecordingSafetyTests`, `NvrSafetyDesktopTests`.
- Metadata-only scope: `NvrOperationScope.MetadataOnly` + `NvrMetadataOperations` + `NvrMetadataAccessGate`, with no
  RTSP probe and no recording catalog; 20-attempt budget, 60-second limit, sequential requests, redirects disabled.
- Full suite: **459 pass, 0 failed**. Simulator evidence: live 12/12, onboarding/restart/export 11/11, playback 10/10,
  speeds 15/15 (`artifacts/metadata-task/*`), metadata-only 9/9 (`artifacts/nvr-metadata-sim/run`).
- Visual review of the Add NVR dialog and the diagnostics window in both themes
  (`artifacts/ui-review/*.png`) found and fixed two layout defects: the diagnostics row label was clipped, and the
  Add camera connection-test panel was squeezed to a sliver when Advanced settings was expanded. Keyboard traversal
  and DPI scaling were not exercised.
