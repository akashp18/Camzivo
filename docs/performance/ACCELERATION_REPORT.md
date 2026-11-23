# Acceleration and scalability report — 2026-11-22

Machine: Intel Core i5-1035G1, Intel UHD Graphics (driver 30.0.100.9864), 8 logical CPUs, Windows 10.0.26200,
.NET 8.0.34. Fixtures, stream counts and measurement windows are the ones used for the previous baseline:
loopback RTSP source (`tools/Camzivo.TestSource`), 640×360@15 fps main stream, 320×180@15 fps substream,
4 s settle + 12 s window per count, six counts for the main run.

Raw data: `artifacts/bench-legacy/benchmark.csv` (previous `WritePixels` presentation), `artifacts/bench-after/benchmark.csv`
(current build, main stream), `artifacts/bench-sub/benchmark.csv` (current build, substream), `artifacts/hardware/hardware.json`.

## 1. Where the time actually goes

Per-frame cost measured inside the running pipeline at 640×360 (mean over the window, 1 / 9 / 25 streams):

| Stage | 1 stream | 9 streams | 25 streams |
|---|---|---|---|
| RTSP receive + RTP depacketize | 0.083 ms/AU | 0.141 ms/AU | 0.753 ms/AU |
| H.264 decode (Media Foundation, CPU) | 1.10 ms | 1.72 ms | 5.82 ms |
| NV12 → BGRA conversion | **7.58 ms** | **12.33 ms** | **29.40 ms** |
| WPF presentation (`Lock`/`AddDirtyRect`/`Marshal.Copy`) | 0.138 ms | 0.217 ms | 0.607 ms |

Conclusion, measured rather than assumed: **pixel format conversion is the bottleneck**, not decoding, not reception,
not presentation. Conversion is 70–85 % of per-frame video CPU; presentation is under 2 %. The dispatcher is not the
ceiling either: coalescing only starts when the wall is already saturated (33 frames at 9 streams, 153 at 25).

Before the change the same profile showed conversion at 9.48 / 16.47 / 28.09 ms, so the ceiling was in the scalar
NV12→BGRA loop — and the earlier hypothesis that `WriteableBitmap.WritePixels` dominated was
wrong: replacing it with `Lock`/`AddDirtyRect`/`Marshal.Copy` moved presentation from 0.16–0.30 ms to 0.13–0.61 ms
per frame, i.e. no throughput change at all.

## 2. What was changed

1. **Conversion rewritten** (`YuvConverter`): precomputed luma/chroma tables, BT.601 fixed point, pointer writes two
   pixels at a time, no per-pixel bounds checks. Output is byte-identical to the previous formula; a golden test
   (`YuvConverterTests`) compares every pixel, plus crop and alpha, against that formula.
2. **Presentation**: `Lock`/`AddDirtyRect`/`Marshal.Copy` into the existing `WriteableBitmap` (no new bitmap per
   frame; the old `WritePixels` path stays available behind `--legacy-present` for A/B).
3. **Stream quality by tile size** (`LiveViewModel.UpdateTileSize` + `StreamSelectionPolicy`): fullscreen → main,
   small tile → sub, hidden/minimised window → decoding suspended, re-planned only when the decision changes and at
   most 4×/s. A tile move never restarts a session (covered by `StreamEngineLifecycleTests`).
4. **Decoder fallback and reset** (`H264StreamSession(decoderFactory, fallbackDecoderFactory)`): failed hardware or
   custom decoder falls back to the software decoder, with the reason surfaced in telemetry.
5. **Defect found by the new tests**: `EnsureTransform` cleared the SPS-derived visible size, so any stream whose SPS
   arrived before the transform existed was reported as Media Foundation's default 1920×1080 — i.e. every
   substream. Fixed (`Reset()` clears it now, transform rebuilds keep it) and pinned by
   `H264SpsTests` + `StreamStabilityTests`.

## 3. Before and after, same fixtures and windows

Main stream (640×360). The "before" columns are the same build with the previous presentation path
(`--legacy-present`, `WritePixels`); "after" is the current build, same fixtures, counts and windows:

| Streams | fps before | fps after | CPU before | CPU after | RAM before | RAM after | Coalesced (after) |
|---|---|---|---|---|---|---|---|
| 1 | 12.91 | 12.82 | 3.0 % | 2.6 % | 189 MB | 189 MB | 0 |
| 4 | — | 51.31 | — | 10.8 % | — | 224 MB | 0 |
| 9 | 117.35 | 110.78 | 30.6 % | 25.7 % | 292 MB | 315 MB | 33 |
| 16 | — | 184.28 | — | 41.9 % | — | 426 MB | 2 |
| 25 | 222.28 | 213.84 | 74.5 % | 51.5 % | 532 MB | 535 MB | 153 |
| 49 | 230.07 | 205.23 | 79.2 % | 49.0 % | 764 MB | 782 MB | 117 |

(4 and 16 were not part of the legacy A/B run, which was stopped after 25 to free the executable.)

Read this honestly: **throughput did not improve for full-resolution walls.** Presented frames at 25 and 49 streams
are within run-to-run variance of the previous build (222→214 and 230→205 fps), while CPU per presented frame fell by
roughly a third (79.2→49.0 % at 49 streams) and per-frame conversion fell from 9.5/16.5/28.1 ms to 7.6/12.3/29.4 ms
at 1/9/25 streams. The smaller wall gets no faster because software H.264 decode of 25–49 640×360 streams saturates
this 4-core mobile CPU; what did improve is CPU efficiency per frame and memory at 49 streams (764→782 MB, i.e. flat,
against 895 MB in the older baseline run). The honest claim is "cheaper frames", not "more main-stream frames".

Substream (320×180), same harness, current build — this is what the tile-size policy now actually selects for a
large grid:

| Streams | fps | per camera | CPU | RAM | Coalesced |
|---|---|---|---|---|---|
| 1 | 12.5 | 12.5 | 4.1 % | 186 MB | 3 |
| 9 | 117.5 | 13.1 | 8.1 % | 245 MB | 3 |
| 25 | 310.5 | 12.4 | 18.5 % | 327 MB | 204 |
| 49 | **631.3** | **12.9** | **39.9 %** | **441 MB** | 197 |

49 tiles at ~13 presented fps each, 40 % CPU, 441 MB: the large grid is smooth, and it is smooth *because* the
selection policy chose the substream rather than decoding 49 main streams. Coalescing at that size is 197 frames
(≈ 2.5 % of decoded frames) and is counted as video loss, not as free throughput.

First-frame latency stayed at 1.5–1.7 s for 1–25 streams and rose to 3.0 s (main) / 5.0 s (sub) at 49 streams.

## 4. Hardware acceleration: evidence and blocker

`Camzivo.Desktop.exe --probe-hardware artifacts/hardware` writes `hardware.json`:

```json
{
  "HardwareH264Decoders": 0,
  "SoftwareH264Decoders": 0,
  "D3D11DeviceCreated": true,
  "D3D11FeatureLevel": 45056,
  "DxgiDeviceManagerCreated": true,
  "Detail": "Media Foundation hardware enumeration returned no H.264 decoder MFT (hr=0x00000000). D3D11 device created at feature level 0xB000. Media Foundation can create a DXGI device manager.",
  "HardwareDecodeAvailable": false
}
```

Independent check with an LGPL FFmpeg build (fixture decode, `-hwaccel d3d11va -hwaccel_output_format d3d11`):
it opened a D3D11VA device and produced hardware frames (`d3d11(progressive)`, 45 frames, 38× speed), so the GPU
*can* decode H.264. Media Foundation's own hardware MFT enumeration on this driver returned nothing (S_OK, zero
items), while the software decoder MFT (CLSID `62CE7E72-…`) is what the application actually uses and works.

Conclusion for the pipeline, with the two facts separated:

- Hardware H.264 decoding exists on this GPU, but it is reachable through D3D11VA, not through the synchronous
  Media Foundation transform the application currently uses.
- Using hardware output inside Camzivo requires an asynchronous MFT or a D3D11VA decoder, GPU colour conversion
  (`VideoProcessorBlt`), and a shared-surface bridge into WPF (`D3DImage` with D3D9Ex). None of that can be verified
  in this environment without a real camera wall and a GPU-backed display session, and shipping unverified native
  interop into the live path would put the acceptance gate at risk.
- **Documented blocker**: no hardware-accelerated path is enabled in v0.9. The software path is retained, measured,
  and improved; the hardware path is scoped (FFmpeg D3D11VA, LGPL build, dynamic linking, no GPL components,
  redistribution notice) but unimplemented.

Software fallback is implemented and tested: `H264StreamSession` accepts a decoder factory and a fallback factory,
falls back on initialisation failure, and reports the reason.

## 5. Stability work

New tests (all green):

- `StreamStabilityTests` — decoder initialisation failure, fallback selection, resolution change on one session,
  dispose during decode, four-session shutdown, one stalled camera not affecting a healthy one.
- `StreamEngineLifecycleTests` — attaching the same address is a no-op, a changed address replaces exactly one
  session, detach and shutdown release everything, half the wall removed during playback keeps the rest, rapid layout
  churn (9→4→16→1→9→25→4→9) keeps one tile per slot and no duplicate camera, a tile move never starts a session,
  minimising suspends and restoring resumes, a camera without an address never opens a session.
- `YuvConverterTests`, `H264SpsTests` — pixel-exact conversion, crop, alpha, short buffers, both committed SPS.

Full suite: **255 tests, 0 failures** (Core 163, Data 56, Desktop 36). Live acceptance probe re-run after the
converter change: **12/12 PASS**, real 640×360 frames presented (pixel hash changes between consecutive frames).

## 6. Not measured here

- GPU utilisation and GPU memory: the benchmark does not sample PDH GPU counters, and with no GPU video path the
  only GPU work is desktop composition. Reported as unmeasured rather than estimated.
- Camera switching time and long-running memory growth (hours): not measured in this pass; both are in
  `docs/performance/OPTIMIZATION_PLAN.md`.
- A/B of the legacy presentation path at 49 streams: the legacy run recorded 1/9/25 before it was stopped, so the
  A/B table covers those three counts only.

## 7. Next steps, in priority order

1. Implement the FFmpeg D3D11VA decode + GPU colour conversion + `D3DImage` bridge behind a switch, keep the
   Media Foundation path as the current default until the new path passes the same 12-step acceptance probe and a
   two-hour soak. This is the only change that can push full-resolution 49-camera walls past ~200 fps.
2. Wire PDH GPU counters into the benchmark so the next comparison includes GPU utilisation and memory.
3. Manual stream override UI (per-tile main/sub/auto) — the policy and its reasons are already modelled.
4. Real Dahua camera/NVR validation; RTSP/RTP/MF behaviour against hardware is still unverified.
