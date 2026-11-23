# Baseline Benchmarks — Camzivo vs the incumbent

Every number here was measured on this workstation, with the method stated. Numbers that were not measured are
marked **not measured** — never estimated and presented as fact.

---

## 1. Method

| Item | Detail |
|---|---|
| Host | Windows 10/11 x64, 8 logical cores, 16 GB RAM, NVMe SSD, integrated GPU |
| Defender | real-time scanning **enabled** for all runs |
| Incumbent | Dahua SmartPSS Lite `1.003.0000006.0`, launched read-only, login window only, never logged in |
| Camzivo | framework-dependent R2R publish, `0.2.0` |
| Startup | `--probe-startup` writes `ContentRendered − Process.StartTime` |
| Memory | `WorkingSet64`, `PrivateMemorySize64`, `Threads.Count` sampled at idle |
| Repetition | 5 runs where possible; first run after a build discarded (AV) |

## 2. Installation footprint

| Metric | SmartPSS Lite | Camzivo | Ratio |
|---|---|---|---|
| Installed files | 11,339 | 1 app + 3 assemblies + 1 runtime-dependent set (no installer yet) | — |
| Installed size | 383 MB | ~30–40 MB publish folder (framework-dependent) | ≈ 10× smaller |
| Processes at login window | 3 (`SmartPSSLite`, `SmartPSSLiteDaemon`, `DSMessageNotify`) | 1 | 3× |
| Writes to `Program Files` | yes (all user state) | no | — |
| Elevation required | yes for state changes | no | — |

## 3. Startup (login/first window)

| Product | Runs (ms) | Median | Notes |
|---|---|---|---|
| SmartPSS Lite | 8,693 | 8,693 | Single measured cold launch to login window; Qt5 + 3 SDK trees |
| Camzivo (earlier session) | 1,981 / 2,433 / 2,987 | **2,433** | R2R publish, warm |
| Camzivo (this session) | 2,867 / 4,562 / 3,864 | **3,864** | After a fresh publish; AV scanned new binaries on runs 1–2 |
| Camzivo cold (first run after publish) | 50,406 | — | Defender scanning; outlier recorded rather than hidden |
| Camzivo after in-place publish (single run) | 11,294 | — | New binaries again; consistent with AV behaviour |
| **Camzivo 0.3 rename build, first run after publish** | 2,292 | — | Includes the one-time profile migration (6 files) |
| **Camzivo rename build, warm runs (same binaries)** | 1,667 / 1,803 / 1,777 (best 1,315) | **1,777** | No AV rescan; logging and the preference writer are now included |

**Interpretation.** Camzivo is 2.2–3.6× faster to first frame than the incumbent in the like-for-like case (2.4 s vs
8.7 s). The 1,500 ms target is **not met**. The cost is dominated by WPF startup + JIT of the shell, not by I/O
(`AppRuntime.Create` runs one small migration check).

## 4. Idle memory and threads

| Product | Working set | Private | Threads | Processes |
|---|---|---|---|---|
| SmartPSS Lite | ≈ 160 MB | not measured | 182 | 3 |
| Camzivo (earlier) | 150.2 MB | 87.3 MB | 13 | 1 |
| Camzivo (snapshots, this session) | 120–124 MB | not sampled | 13 | 1 |

Both are inside the 200 MB idle budget. Camzivo's thread count is 14× lower, which is the structural difference:
no SDK thread pools, no message daemon, no Qt event loops.

## 5. Database and disk behaviour

| Metric | Value |
|---|---|
| `camzivo.db` size after migration | 144 KB |
| Backups before fix | 20 files in `%APPDATA%` (one per launch) |
| Backups after fix | 5 files (prune on every open) — verified on the real profile |
| Writes per launch | 1 preference row per changed setting (`ui.theme`, `ui.layout`, `ui.section`, `ui.wall`) |
| Log volume | 0 bytes (no logger yet — a gap, not an achievement) |

## 6. Not measured (explicit gaps)

- Idle CPU (needs a 60 s sampling harness; the 1 s telemetry timer is the only periodic work).
- UI input latency and frame pacing (needs an automation harness).
- Layout switch duration at 49/64 tiles.
- Any video metric: first frame, decode CPU, GPU memory, dropped frames, reconnect time, seek latency.
- Export throughput and integrity timings.
- 8 h soak, memory growth, handle counts.
- Behaviour on 4K/150 % DPI, multi-monitor, RDP.

These are the acceptance gates for M3–M6 and are tracked in `OPTIMIZATION_PLAN.md` and
`docs/planning/RELEASE_READINESS.md`. No claim of "buttery smooth" is made anywhere until they are measured.
