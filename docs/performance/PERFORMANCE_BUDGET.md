# Performance Budget — Camzivo

Philosophy: the pipeline is Device → Network → Demux → Decode → Render → UI. Every stage has a cost ceiling, and a
feature that cannot fit its ceiling is redesigned or dropped. Budgets are **targets to be validated**, not
guarantees; `BASELINE_BENCHMARKS.md` records what has actually been measured.

---

## 1. Reference workstation (measurement host)

| Item | Value |
|---|---|
| OS | Windows 10/11 x64 (this machine: Windows, 16 GB class) |
| CPU | 8 logical cores (measurement host) |
| GPU | Integrated, D3D11 capable with H.264/HEVC decode |
| Disk | NVMe SSD, Defender real-time scanning enabled (deliberately not disabled — real user condition) |
| Network | 1 Gbit LAN |
| Build | `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true` |

Scanning is **not** disabled for measurement. A product that only starts fast with AV exclusions is not fast for
users.

## 2. Budgets

### 2.1 Shell (implemented surface)

| Metric | Target | Current measured | Verdict |
|---|---|---|---|
| Warm start → first rendered frame | ≤ 1,500 ms p50 | 2,433 ms p50 (earlier build); **1,777 ms median** on the rename build (3 warm runs 1,667/1,803/1,777, best 1,315) | **missed (improved 27 %)** |
| Cold start (first run after install, AV scanning) | ≤ 5,000 ms | 50,406 ms once (Defender scan) | **missed, environmental** |
| Idle working set | ≤ 200 MB | 120–150 MB | met |
| Idle private bytes | ≤ 120 MB | 87.3 MB | met |
| Threads at idle | ≤ 40 | 13 | met |
| UI input feedback | ≤ 100 ms | not measured (no harness) | unverified |
| Layout switch (64 tiles, shell only) | ≤ 100 ms | not measured | unverified |
| Install footprint | ≤ 80 MB | publish output ~30–40 MB (framework-dependent, R2R) | met |
| Background processes | 1 (the app) | 1 | met |

### 2.2 Video (M4, targets)

| Metric | Target | Rationale |
|---|---|---|
| First frame, LAN, 1080p H.264 | ≤ 1,500 ms | Operators judge responsiveness by this number |
| First frame, substream | ≤ 800 ms | Small tiles must appear fast |
| Steady-state CPU, 16 substreams @ 640×360 | ≤ 20 % of 8 logical cores | Leaves headroom for recording/UI |
| Steady-state CPU, 49 substreams | ≤ 45 % | Above this the UI competes with decode |
| UI frame pacing | 60 fps composition, no frame > 32 ms | Wall must never feel like a slideshow during decode |
| Dropped frames per stream | ≤ 0.5 % over 10 min | Drop-to-live is acceptable; *unreported* drops are not |
| Decoder memory | ≤ 25 MB per active 1080p stream | Caps 16-stream cost at ~400 MB |
| GPU memory | ≤ 64 MB per 1080p surface set | Zero-copy target; no per-frame upload |
| Reconnect time after link loss | ≤ 5 s p50, ≤ 15 s p95, exponential backoff | Operators tolerate a gap, not a loop |
| Seek to first frame (playback) | ≤ 1,000 ms | Investigation speed |
| Long-run growth | ≤ 5 % RSS over 8 h with 16 streams | Leak gate |

### 2.3 Network

| Metric | Target |
|---|---|
| Discovery burst | ≤ 20 datagrams, ≤ 3 s, no retry storm |
| Steady bandwidth, 16 substreams | ≤ 16 × 1.5 Mbit ≈ 24 Mbit |
| Steady bandwidth, 49 substreams | ≤ 49 × 1.5 Mbit ≈ 74 Mbit |
| Idle (no engine) | 0 bytes out |

## 3. Measurement method (reproducible)

1. Build with the documented publish line; never measure Debug.
2. Startup: `--probe-startup <file>` writes `ContentRendered − Process.StartTime`. Run 5×, discard the first
   (AV scan), report median and range. Cold and warm runs are reported separately.
3. Memory: `Process.WorkingSet64`, `PrivateMemorySize64`, `Threads.Count` after 60 s idle, sampled every 10 s.
4. Video: per-stream counters from the engine (frames presented, dropped, queue depth) plus an ETW/GPU capture for
   the wall — never inferred from CPU totals.
5. CPU: `TotalProcessorTime` delta over 60 s, normalised by logical cores.
6. Any claim in documentation must name the build, the run count and the host.

## 4. Cost rules for features

| Rule | Consequence |
|---|---|
| No background work without an owner and a stop condition | a feature that polls must justify the poll |
| No per-frame copying between decode and render | fail code review |
| Bounded queues only | `BoundedFrameQueue` is the only queue type for frames |
| Hidden tiles decode nothing | scheduler contract |
| Small tiles use substreams by default | policy contract |
| Analytics/AI off the core path | device events; optional module at most |
| Theme/media caches capped | 5 backups, bounded LRU for thumbnails |

## 5. Regression gates

A release candidate must satisfy: warm start ≤ 1,500 ms p50 (or a documented, justified exception), idle ≤ 200 MB,
16-stream CPU ≤ 20 %, no growth > 5 % over 8 h, zero unhandled exceptions in a 24 h soak. Failing a gate blocks the
release label, not the merge.
