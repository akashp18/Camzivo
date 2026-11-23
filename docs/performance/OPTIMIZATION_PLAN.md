# Optimization Plan — Camzivo

Ordered by (user-visible impact ÷ risk). Each item names the problem, the change, the expected gain, how it will be
measured, and the risk. Nothing here is described as an improvement until it has been measured; items marked
**pending** have not been implemented.

---

## 1. Startup (highest user-visible cost: 2.4–3.9 s vs 1.5 s target)

| # | Problem | Change | Expected gain | Risk | State |
|---|---|---|---|---|---|
| S1 | WPF + JIT of the shell dominates first paint | `PublishReadyToRun` already on; add **composite R2R** with the framework, or trim-safe split so the shell's hot paths are precompiled | 300–600 ms | larger publish, version coupling | pending (measure A/B) |
| S2 | `AppRuntime.Create()` opens SQLite and runs a migration check on the UI thread before the window exists | split into `CreatePaths()` (cheap) + `CreateStore()` behind a "store ready" gate; window shows immediately with `Connecting to local store…` | 100–250 ms | more startup states to test | pending |
| S3 | Theme preference read requires the DB | cache `ui.theme` in a 200-byte `theme.txt` next to the DB; DB remains the source of truth | 30–80 ms | two sources → write both atomically | pending |
| S4 | XAML templates compiled at first layout | keep `CacheMode` off but pre-instantiate the wall container in `Loaded` before cameras arrive | 50–150 ms | none | pending |
| S5 | No phase instrumentation | add `StartupTrace` marks (paths, store, vm, window, rendered) written to the log | enables all of the above | none | **this pass** |

Target after S1–S4: p50 ≤ 1,500 ms warm. If composite R2R does not pay for itself, document that and keep 2.4 s
rather than adding a native splash hack.

## 2. UI smoothness and allocation

| # | Problem | Change | Expected gain | Risk | State |
|---|---|---|---|---|---|
| U1 | `Refresh()` rebuilds up to 64 tile view models | keep one VM per slot; mutate `Assign`/`Clear`/`Update`; recreate only when the slot count grows | removes 64 allocations + full ItemContainer regen per layout/place | medium | pending (M4) |
| U2 | `FilteredCameras.Clear()` on every refresh | reconcile by identity/order | keeps focus, selection, scroll | low | **this pass** |
| U3 | `Process.GetCurrentProcess()` each second | cache the `Process` | removes a handle + object per second | low | **this pass** |
| U4 | Telemetry string rebuilt every second even when hidden | only update when the status bar is visible; 1 Hz is fine | minor | low | pending |
| U5 | Search filters on every keystroke over a list | negligible under 1,000 cameras; add 150 ms debounce when the list exceeds 200 | prevents a future cliff | low | pending |

## 3. Video pipeline (M4 design constraints, not yet measurable)

1. **One D3D11 device, one swap chain / one `D3DImage` per window**, grid composed as a single surface. Avoids N
   airspace problems and N presents.
2. **Zero-copy**: decoder writes NV12 into a shared texture; the compositor samples it. No CPU frame copy, no
   `BitmapSource` per frame.
3. **Substream by tile size**: < 960×540 → substream; fullscreen/1-up → main. Manual override always wins and is
   labelled.
4. **Bounded queues** (already implemented, tested) with drop-oldest and visible drop counters.
5. **Scheduler-driven decode**: only tiles in the active plan get a decoder; `QUEUED` tiles hold a poster frame.
6. **Reconnect with backoff** (0.5 s → 1 → 2 → 4 → 8 s, jitter, cap 15 s), attempt counter on the tile.
7. **Teardown order**: stop reader → flush queues → release decoder → release texture. Soak test 100 open/close
   cycles, assert adapter memory returns to baseline ± 5 %.

## 4. Memory

| Item | Rule |
|---|---|
| Frame buffers | bounded queue × frame size, never pooled unbounded |
| Decoder instances | one per active stream; released on suspend (`QUEUED`) after 10 s |
| Thumbnails | 256 px max, LRU 500 entries, disk cache capped at 100 MB |
| Log | rolling 5 × 2 MB, redacted |
| DB backups | 5, pruned every open (verified) |
| Leak gate | 8 h × 16 streams, RSS growth ≤ 5 % |

## 5. Network

- Discovery: one WS-Discovery probe + one repeat after 1 s, then stop. No continuous multicast.
- No polling for camera state: use RTSP/ONVIF events and connection state from the transport.
- HTTP(S) only for ONVIF device service calls, with a bounded timeout (5 s connect, 10 s total).
- Zero egress when no camera exists (testable: run with the network up, assert no outbound sockets).

## 6. Database

| Item | Current | Plan |
|---|---|---|
| Connections | new connection per operation | keep (SQLite is fast to open); pool only if profiling shows cost |
| Writes | one per preference change, fire-and-forget | **serialised latest-wins writer** (BUG-07) |
| Events table growth | n/a | cap by rows/time with a documented retention (M6) |
| VACUUM | never | monthly on idle, or when free pages > 30 % |

## 7. Measurement harness needed (blocking honest claims)

- Startup: exists (`--probe-startup`), needs 5-run aggregation.
- UI latency/frame pacing: needs an automation harness (FlaUI) + `CompositionTarget.Rendering` sampling.
- Video: engine counters + GPU memory via `IDXGIAdapter3::QueryVideoMemoryInfo`.
- Soak: scripted 8 h run with 16 synthetic streams (mediamtx + ffmpeg loop), RSS/handles/counters sampled.

Until these exist, the product must not claim 60 fps or "no memory growth" anywhere.
