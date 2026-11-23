# Performance Comparison

## Measured today (application-level only)

Both products were measured on the same Windows machine, same user session, no cameras attached.

| Metric | SmartPSS Lite 1.003.0000006.0 | Camzivo (current build) | Ratio |
|---|---|---|---|
| Startup to interactive | 8,693 ms | 1,777 ms median (1,315 best) | ~4.9× faster |
| Idle processes | 3 | 1 | — |
| Idle threads | 182 | 13 (measured in an earlier session on this machine) | — |
| Idle working set | ~160 MB | 120–150 MB (earlier session) | comparable |
| Application output | 383 MB installed | 2.4 MB Release output (framework-dependent; .NET 8 desktop runtime required) | 383 MB vs 2.4 MB of product code |
| Files installed | 11,339 | a few hundred | — |

**What these numbers do not cover:** decode load, GPU utilization, first-frame latency, stream switching, playback
seeking, or stability at 25/49 tiles. Neither product has been run with a real stream on this machine, so **no video
performance claim is made**. The startup and footprint numbers are real; the rest is unknown.

## Benchmark protocol (to run when hardware exists)

Fixed inputs, recorded with every result:

- Hardware: CPU model, GPU model, RAM, OS build, driver version.
- Streams: codec, resolution, fps, bitrate, GOP, transport (TCP/UDP), main vs sub.
- Layouts: 1, 4, 9, 16, 25, 49 tiles (Camzivo supports all; SmartPSS Lite claims arbitrary split up to 64).
- Duration: 10 minutes steady state after a 2-minute warm-up, sampled every 5 s.

Metrics per run:

| Metric | Instrument | Why it matters |
|---|---|---|
| Startup to interactive | Stopwatch, process start → first responsive frame | The one metric already won |
| Time to first decoded frame | RTSP PLAY → first presented frame | The number an operator feels |
| Tile switch latency | Layout change → all tiles presenting new content | Wall usability |
| CPU (total, per-process) | Performance counters | Decode efficiency |
| GPU utilization + video engine | GPU counters | Whether hardware decode is actually engaged |
| Working set + private bytes | Process counters | 25/49-tile viability |
| Frames decoded / dropped | Engine counters | Honest quality metric, per stream |
| Reconnect time | Cable pull → LIVE again | Field reliability |
| Playback seek latency | Seek request → first frame of the new position | Investigation speed |
| Crash/hang count over 8 h | Soak run | Stability |

Reporting rules:

1. A number is only published with its hardware and stream parameters next to it.
2. Software-decode results are labelled as such; they are not compared to hardware-decode results.
3. If a layout cannot run without drops, it is reported as unsupported on that hardware, not smoothed over.
4. No synthesized or "expected" figures. An empty cell stays empty.

## Design targets (not results)

| Target | Value | Rationale |
|---|---|---|
| Startup | < 1.5 s warm, < 2.5 s cold | Keep the measured advantage |
| First frame | < 1.5 s on LAN, sub stream | Below the "is it broken?" threshold |
| Tile switch | < 300 ms within the same layout | Perceived as instant |
| 16 tiles @ D1 sub | < 25% CPU on a 4-core desktop | Typical SMB wall |
| 49 tiles | sub stream only, documented as hardware-dependent | Honest limit |

These are budgets to design against and to test, not claims about current behavior.

## Architectural decisions that protect the numbers

- One connection per camera (`StreamScheduler`), so tiles never multiply NVR sessions.
- Sub stream for mosaics, main stream for fullscreen, with debounced promotion.
- Hardware decode by default (FFmpeg `d3d11va`), software fallback declared in diagnostics.
- Bounded frame queues with drop-oldest policy (tested), so a slow tile cannot stall a stream.
- No per-frame WPF bitmap allocation: D3D11 texture presented through `D3DImage`.
- Lazy device I/O; nothing on the UI thread blocks on the network.
