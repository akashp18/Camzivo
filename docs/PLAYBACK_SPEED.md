# Accelerated recorded playback (1×–16×)

Camzivo asks an ONVIF Profile G replay endpoint for one of **1×, 2×, 4×, 6×, 8×, 16×** with the RTSP `Scale`
header. A recorder that honours it accelerates delivery; a recorder that ignores it is measured, not trusted, and the
operator is told the actual rate. There is no timeline-only fast-forward.

## 1. Why the request is not the result

`PLAY` returning `200 OK` and echoing `Scale: 16.000` does not prove that the recorder accelerated anything. Camzivo
measures effective speed continuously:

- **source position** comes from the 90 kHz RTP timestamp of each access unit;
- **wall clock** comes from a monotonic `Stopwatch`;
- `PlaybackRateMeter` reports source-seconds per wall-second over a one-second window;
- the page displays the measured rate (`15.77× active`) and raises a notice when it differs from the request by
  more than the tolerance.

`Range: clock=…` still addresses the recording by absolute UTC time. `Scale` is sent on the initial `PLAY`, on
resume, and on every rate change.

## 2. Speed policy

| Event | Selected speed |
|---|---|
| First open | 1× |
| Pause / resume | preserved |
| Seek, jump to start/end, ±30 s | preserved |
| Stop, then play the same recording | preserved |
| Starting a different recording after one has played | reset to 1× |
| Switching camera | reset to 1×, measurement cleared |
| Recorder rejects the speed | previous working speed shown and named |
| Recorder ignores the speed | requested speed stays selected; actual rate is shown |

Changing speed while playing or paused reopens the replay at the current source position with the new
`Range`/`Scale`. Reopening is deliberate: reusing `PLAY` on a live session is not portable, and a fresh session cannot
mix old-speed frames with the new position. A paused session is re-PAUSEd before any frame is presented.

## 3. Decoding versus presentation

At 16× a 15 fps recording produces 240 access units per second. Camzivo:

1. **decodes every access unit** so SPS/PPS and reference-frame dependencies remain valid;
2. lets `MediaFoundationH264Decoder` reject the NV12→BGRA conversion for pictures that will not be presented
   (`IPresentationFilteredDecoder`);
3. caps presentation at 30 fps up to 4× and 20 fps above 4×;
4. keeps only the newest pending frame and one queued WPF dispatcher callback;
5. takes the playhead from RTP timestamps, never from the number of presented frames.

The result at 16× in the final acceptance run: **722 access units decoded, 52 frames presented in 3 s**. Queue depth
is bounded by construction; presentation drops are visible as the difference between decoded and presented. Across
repeat runs the same 640 × 360 source measured 12.9–15.8× at a 16× request; the variation is decoder throughput, not
a different clock. The final run also opened a previous-day block (2026-11-21, 14:00–15:00) and measured 15.55×.

A timestamp is rejected as a discontinuity when it moves backwards or further than the requested speed could
plausibly have advanced the recording (three times the expected source advance plus ten seconds). The session then
rebases at its current position instead of throwing the playhead to the end of the segment.

## 4. Recorder capability handling

| Recorder behaviour | Camzivo behaviour |
|---|---|
| Honours `Scale` | Source time advances at the requested rate; measured rate is shown. |
| Answers 200 but delivers real time | Measured rate is ~1×; the page keeps `16× requested` but shows `1.0× actual` and a notice that the source has no accelerated replay. |
| Rejects `Scale` (e.g. 551) | Initial play fails with a visible error. A change during playback reopens the previous speed, keeps playing, and says the recorder rejected the new speed. |
| Stalls | Existing watchdog reconnects once at the current position; the measured rate is reset. |
| Ends | Watchdog reports `End of recording`; the page leaves playing/paused state. |

Camzivo cannot manufacture 16× from a recorder that only delivers real time. A local player cannot receive 240 source
seconds without the recorder delivering 240 source seconds. The honest result is the measured rate and a named
limitation, not a relabelled clock.

## 5. Audio policy

Audio is not implemented. The speed architecture reserves the policy:

- at 1×, audio would play normally when a decoder is added;
- at 2× and above, audio would be muted unless a time-stretching implementation is added;
- no audio control is shown today, because there is no audio path to control.

## 6. Tests

Deterministic:

- `PlaybackRateTests` — exact supported set, normalization, invariant `Scale` header, meter maths, timestamp-reset
  rebasing, requested-versus-delivered tolerance.
- `PlaybackSpeedTests` — six speeds and 1× default, selection before play, unsupported rate notice, camera-change
  reset.

Real application:

```
Camzivo.Desktop.exe --probe-playback-speeds artifacts/speeds \
  --simulator tools/Camzivo.RecordingSimulator/bin/Debug/net8.0/Camzivo.RecordingSimulator.exe \
  --fixture tests/Camzivo.Core.Tests/Fixtures/h264-640x360.h264
```

The simulator supports `--scale-mode honor|ignore|reject`, plus `--stall-after`, `--stall-ms` and
`--timestamp-reset-after` for discontinuity runs. The probe measures a 4 s window at 1× and 3 s at 2×–16×, hashes
the frame before and after, and records decoded/presented counts, CPU and working set. Acceptance tolerance is ±15 %
at 1× and ±25 % above 1×.

Measured on 2026-11-22 (`artifacts/speeds-final15/speeds.json`):

| Requested | Measured | Window | Decoded | Presented | CPU | Working set |
|---:|---:|---:|---:|---:|---:|---:|
| 1× | 0.97× | 4.0 s | 60 | 61 | 12.9 % | 254 MB |
| 2× | 1.93× | 3.0 s | 88 | 37 | 12.0 % | 255 MB |
| 4× | 3.86× | 3.0 s | 177 | 41 | 14.6 % | 253 MB |
| 6× | 5.80× | 3.0 s | 265 | 42 | 12.2 % | 252 MB |
| 8× | 7.69× | 3.0 s | 353 | 42 | 12.5 % | 262 MB |
| 16× | 15.77× | 3.0 s | 722 | 52 | 22.9 % | 256 MB |

Pause at 4× held position and frames; selecting 8× while paused kept it paused with 283 frames held, and resume
advanced 12.0 source seconds. Seeking at 8× landed 4.8 s after the requested target (playback had already resumed).
End-of-recording, session release, a previous-day recording at 15.55×, and the ignored/rejected recorder modes all
passed in the same run. A previous run of the same probe measured 12.93× at 16×, which is why the report gives the
range rather than a single best value.

**Not verified:** a real Dahua/NVR replay endpoint honouring or rejecting `Scale`. The simulator proves the client
against all three documented behaviours; it does not prove a specific device implements Profile G accelerated replay.
