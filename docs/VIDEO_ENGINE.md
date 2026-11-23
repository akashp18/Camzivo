# Video Engine

Scope: RTSP ingest, hardware decode, GPU composition, frame scheduling and lifecycle. Milestone 4 delivers the
FFmpeg/D3D11 implementation; Milestone 2 delivers the policy layer that decides *what* to decode and *at which
quality*, which is where most of the scalability actually comes from.

---

## 1. Why the policy layer comes first

On the reference GPU (Intel UHD Gen11, shared 1 GB) the decode budget is the binding constraint. 64 main streams is
not achievable; 64 tiles *is*, if most of them are substreams at reduced frame rate and off-screen tiles are not
decoded at all. So the engine is designed around three decisions taken before any packet is read:

1. **Which quality** (`StreamSelectionPolicy`)
2. **Which streams get a decoder slot right now** (`StreamScheduler`)
3. **How much buffer** each stream may hold (`BoundedFrameQueue`)

All three are pure, deterministic, and unit-tested (`tests/Camzivo.Core.Tests`) precisely because they are the part
that must not be debugged live in front of a customer.

## 2. Stream quality policy

Inputs: tile pixel area, visibility, fullscreen state, user preference, and what the device actually offers.

| User preference | Condition | Selected | Reported reason |
|---|---|---|---|
| `Main` | main available | **Main** | `UserSelectedMain` |
| `Main` | main unavailable | Sub (if any) | `MainUnavailableUsingSub` — tile shows a persistent "MAIN UNAVAILABLE" chip |
| `Sub` | sub available | **Sub** | `UserSelectedSub` |
| `Auto` | fullscreen, main available | **Main** | `AutoFullscreenMain` |
| `Auto` | tile area ≥ 480×270, main available | **Main** | `AutoLargeTileMain` |
| `Auto` | small tile, sub available | **Sub** | `AutoSmallTileSub` |
| `Auto` | only one stream exists | whichever exists | `AutoOnlyStreamAvailable` |
| any | device offline | none | `NoStream` |

Hard rule (NFR + user trust): an explicit `Main` preference is **never** silently downgraded. If the engine believes
main cannot be sustained, it reports `SustainedBandwidthInsufficient` as a *suggestion* the operator accepts — the
preference stays, the fallback is recorded.

The incumbent's equivalent is a single integer in `Settings.ini` (`AutoStreamType=1`, applied to any grid larger
than 4). Per-camera policy with an explicit reason is the differentiator.

## 3. Decoder scheduling

`StreamScheduler` is a priority allocator over a fixed decode budget (default `maxActive = 16` on this class of
hardware, configurable, auto-tuned down if `decode_ms` p95 exceeds the frame interval).

Priority (highest first):
1. fullscreen / single-tile camera
2. the tile the operator interacted with last
3. visible tiles, by grid position (top-left first, matching reading order)
4. tiles on a monitor that currently has focus
5. occluded/off-screen tiles → **suspended** (stream may stay connected but frames are not decoded or presented)

Suspended streams keep their TCP connection only if the device benefits (cheaper reconnect); otherwise they are
torn down and re-opened on reveal. The policy is a property of the scheduler, not of the tile, so 64 tiles on a
weak GPU degrade in a defined, explainable order instead of randomly.

Counters exposed per stream: `frames_received`, `frames_decoded`, `frames_presented`, `frames_dropped`,
`queue_depth`, `decode_ms_p95`, `reconnects`.

## 4. Buffering

- Per stream: `BoundedFrameQueue` with capacity 3–6 depending on latency mode (`LowLatency`: 2, `Balanced`: 4,
  `Smooth`: 6). Policy: **drop oldest** on overflow and increment `frames_dropped`; never block the network read.
- Rationale: a surveillance wall must show *now*; falling behind and catching up later is worse than dropping.
  Playback uses a different policy (bounded, no drop, backpressure) because correctness beats latency there.
- Memory: one frame at 1080p NV12 is ~3 MB. 16 streams × 4 buffers × 3 MB ≈ 192 MB of GPU/system memory if frames
  were held on the CPU — which is exactly why decoded frames stay as D3D11 textures (1080p NV12 ≈ 3 MB VRAM each)
  and why queue depth is small and enforced.

## 5. Decode path (Milestone 4)

```
avformat_open_input (rtsp, tcp|udp, stimeout/ rw_timeout set)
  avformat_find_stream_info (bounded: 3 s)
  av_hwdevice_ctx_create(D3D11VA)  ── fallback DXVA2 ── fallback software
  avcodec_open2 with hw_device_ctx
loop:
  av_read_frame  (with interrupt_callback tied to CancellationToken)
  avcodec_send_packet → avcodec_receive_frame
  if frame->format == AV_PIX_FMT_D3D11: hold texture + subresource index
  push texture ref to tile slot (no copy)
  stats update
```

Details that matter in practice:

- **Transport**: TCP default for reliability over WAN/NVR; UDP offered per camera with a visible warning that loss
  causes tearing. `?tcp` / `-rtsp_transport` equivalents handled explicitly, not by string concatenation guesswork.
- **Timeouts**: connection 5 s, `find_stream_info` 3 s, read timeout 10 s (configurable). On timeout: classify,
  back off (1 s, 2 s, 4 s, capped 30 s with jitter), retry until cancelled. No infinite tight retry loops.
- **Codec support**: H.264 baseline/main/high, HEVC main, MJPEG, AV1 decode if present. Unsupported codec →
  `StreamFailure.UnsupportedCodec` with the codec string, never a blank tile.
- **Parameter-set changes** mid-stream (common on Dahua when switching main/sub): decoder is re-opened for that
  stream only; other streams unaffected.
- **Corrupt packets**: FFmpeg logs are suppressed to error level and mapped to counters; a single bad PES never
  tears down the session.
- **Latency**: RTSP jitter buffer set to `max_delay=500ms`, `reorder_queue_size=0` for TCP; `fflags=nobuffer`,
  `flags=low_delay`. Measure, don't assume: `first_frame_ms` and `decode_ms_p95` are logged per session.

## 6. Composition (Milestone 4)

- **One** `ID3D11Device` per application, created with `D3D11_CREATE_DEVICE_VIDEO_SUPPORT` and
  `D3D11_CREATE_DEVICE_BGRA_SUPPORT`; multithread protection enabled.
- Tiles are quads in a single render target sized to the grid area. Per draw: bind NV12 SRV, run the
  NV12→sRGB pixel shader with BT.709 limited-range coefficients, letterbox to preserve aspect.
- One present per composition frame (60 Hz or display refresh), independent of stream frame rate; a 10 fps
  substream is simply re-drawn.
- Presentation to WPF via a single `D3DImage` (one shared surface). Fallback: `HwndHost` child swapchain per
  monitor if profiling shows `D3DImage` copies on target drivers.
- Digital zoom/pan is a UV transform in the shader — no re-decode, no CPU work.
- Overlays (camera name, timestamp, status) are drawn by the compositor, not by WPF, so they survive at 64 tiles
  without 64 visual subtrees; the WPF layer only draws hover toolbars for the focused tile.

## 7. GPU lifecycle and recovery

| Event | Response |
|---|---|
| `DXGI_ERROR_DEVICE_REMOVED` / `DEVICE_RESET` | stop presents, dispose device + surfaces, recreate, re-bind, log `gpu.recovered`; tiles show "RECOVERING" during the gap |
| Second failure within 2 minutes | switch to software composition for the session, banner explains why, Health shows degraded mode |
| Decoder init failure for one stream | that stream falls back to software decode; chip shows `SW` |
| Out of VRAM / allocation failure | scheduler reduces active streams to the next lower budget and reports `BudgetReduced` |
| Sleep/resume | all sessions re-validate, sockets re-open, clock offset re-checked |

## 8. Comparison with the incumbent's engine (evidence-based)

| Aspect | SmartPSS Lite | Camzivo target |
|---|---|---|
| Hardware decode | DXVA2 + D3D11 shared handles, H.264/HEVC/AV1 (`HWDec.dll`) | D3D11VA first, DXVA2 fallback, same codec set |
| Decode path coupling | per-module SDK (`dhplay.dll`, `play.dll`) + `dsengine.dll` DirectShow | single FFmpeg path, one device, no DirectShow |
| Composition | Qt/Direct2D per-widget surfaces (platform plugins `qdirect2d`, `qwindows`) | one composition surface, N quads |
| Stream policy | global `AutoStreamType`/`AutoSwitch` | per-camera policy + explicit reason |
| Off-screen handling | unknown | defined suspension in scheduler |
| Queue bounds | unknown | enforced, counted, tested |

We do not claim to beat the incumbent's decode throughput — that requires the same hardware and stream set, which
we did not have. We claim a simpler, observable, per-camera-controllable pipeline, and we will prove the rest with
the harness in `performance/BASELINE_BENCHMARKS.md` §6.

## 9. Anti-goals

- No DirectShow, no vendor play-SDKs, no per-vendor decode DLLs.
- No CPU colour conversion (`sws_scale`) in the live path.
- No per-tile `D3DImage`/`WriteableBitmap` (the classic WPF multi-camera performance cliff).
- No unbounded frame accumulation, no "catch up" playback in live mode.
- No frame-rate negotiation by dropping packets invisibly: every drop is counted and visible in Health.
