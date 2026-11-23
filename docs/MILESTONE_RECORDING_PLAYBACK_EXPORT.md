# Milestone: recording search, timeline playback, evidence export (2026-11-22)

Delivered: the investigation workflow — choose camera and date → search the recorder → see the ranges it confirmed on
a timeline → play, pause, seek and stop real recorded video → export a range to a playable MP4 — implemented against
ONVIF Profile G with a scripted recording device for reproducible acceptance.

## 1. Recording discovery (Phase 3A)

- `OnvifRecordingSoap` reads the recording, search and replay services from GetServices (GetCapabilities fallback),
  builds `FindRecordings` / `GetRecordingSearchResults` / `EndSearch` / `GetReplayUri`, and parses results defensively:
  vendor prefixes, multiple tracks, unparseable timestamps and malformed XML are handled explicitly.
- `OnvifRecordingCatalog` (`Camzivo.Data`) is the seam the page uses: it validates the day window before any network
  call, reports a device that advertises no recording services as **unsupported** (never as "no recordings"), pages
  search results (bounded at 8 pages), merges overlapping ranges, filters to the day, and caches service addresses for
  five minutes. Service failures, timeouts and malformed answers become messages, not exceptions.
- A recorder channel searches with its own `SourceToken` (`channel-N`); the service address and credentials come from
  the recorder, so the camera↔recording-source relationship survives import.
- Time handling is UTC internally; the page works in local dates through `RecordingAvailability.DayBounds`, which is
  DST-correct (tests assert 23 h on 8 March 2026 and 25 h on 1 November 2026 in US Eastern).

## 2. Timeline (Phase 3B)

- Camera selector, date picker, availability summary, hourly ruler, playback position marker, 1–24 hour zoom,
  half-window panning, recording blocks with gaps, tooltips with exact start/end, and explicit "not searched",
  "unsupported", "no recordings" and error states.
- The block list is rebuilt only when a search completes or the zoom window changes; the playhead is one rectangle
  whose `Canvas.Left` is updated. A position tick therefore never recreates the timeline visual tree.
- Blocks are computed in UTC and labelled in local time, so a recording that crosses midnight appears on both days
  with the correct clipped shape (the scripted device has one such recording).
- Selection is available with the mouse (block buttons) and the keyboard/commands (transport, seek, zoom, pan).

## 3. Replay engine (Phase 3C)

- `ReplaySession` (`Camzivo.Core`) is independent of live sessions: its own RTSP client, depacketizer and decoder.
  ONVIF replay is addressed by **clock time**: PLAY carries `Range: clock=YYYYMMDDTHHmmssZ-` and `Scale`, which the
  live path never sends (`npt=0-` would be wrong for recordings and is rejected by real replay services).
- Position comes from RTP timestamps at 90 kHz relative to the requested start, not from a frame counter.
- Pause sends PAUSE on the existing session; resume sends PLAY. **Seek reopens** the replay at the target clock time,
  which is portable across replay servers and guarantees no stale frames are attributed to the new position.
- End of recording is detected from the segment boundary and from packet silence; one automatic reconnect from the
  last position is attempted when the stream stalls mid-recording. Playback never touches the live wall: the
  acceptance run asserts `engine.ActiveCount == 0` throughout, and stopping releases the client, decoder and socket.

## 4. Scripted recording device (Phase 3D)

`tools/Camzivo.RecordingSimulator` is a real ONVIF recording device double:

- HTTP ONVIF services at `/onvif/device_service`, `/onvif/search`, `/onvif/replay`, `/onvif/recording`:
  GetServices/GetCapabilities, GetProfiles, GetStreamUri, FindRecordings, GetRecordingSearchResults, EndSearch,
  GetReplayUri, GetRecordings, GetRecordingInformation, with WS-Security username checks.
- RTSP replay on port 8556 serving **real H.264** from the fixture, with RTP timestamps mapped to the recording clock,
  PAUSE/PLAY, and a 404 for unknown tokens. A seek visibly changes both content and timestamps.
- Three channels, six stored ranges, a recording crossing midnight, a several-hour gap, a channel with no recordings
  today, plus `--empty`, `--unsupported` and `--unauthorized` failure modes.

## 5. Export (Phase 4)

- `Mp4Writer` muxes the replay's H.264 access units into ISO-BMFF: real `ftyp`/`mdat`/`moov`, `stsd`+`avcC` built from
  the stream's own SPS/PPS, sample sizes and durations from the RTP clock, sync-sample table from IDR frames, 4-byte
  NAL length prefixes (Annex-B start codes are never renamed into an MP4).
- `EvidenceExporter` runs its own replay session, writes to `<name>.mp4.part`, reports progress, supports
  cancellation, validates that samples and bytes exist, deletes the part file on failure, and only then renames the
  finished file. Ranges are bounded (max 4 hours) and clamped to the recording.

## 6. Acceptance evidence

`Camzivo.Desktop.exe --probe-playback <dir> --simulator <exe> --fixture <h264>` drives the real window:

| Step | Result |
| --- | --- |
| simulator.ready | recording simulator on http :8099, rtsp :8556 |
| search.recordings | 3 blocks · 6 h 45 m recorded · Eastern Standard Time |
| ui.selectSegment | 08:15–11:45 · 210 min |
| playback.play | frames=10, position 08:15:01, 640×360 |
| playback.pause | frames stay 10 → 10 over 1.5 s |
| playback.resume | frames 10 → 17 |
| playback.seek | position 08:15:00 → 08:35:00 (decoded frames continue) |
| playback.stop | stopped, live sessions 0 |
| export.mp4 | 1,119,147 bytes, 91 samples, 67 progress updates |

**Total 20/20 steps pass.** Screenshots in `artifacts/playback/` (`01-playback-page` … `09-exported`), logs in
`probe-log.ndjson` and `summary.txt`.

Independent verification of the exported file with FFmpeg (LGPL build in `tools/ffmpeg-dl`):

```
codec_name=h264   width=640   height=360   nb_frames=91
format_name=mov,mp4,m4a,3gp,3g2,mj2   duration=6.066667   size=1119147
ffmpeg -i evidence.mp4 -f null -   → no decode errors
```

## 7. Bugs found while proving it

1. The MP4 `ftyp` size was wrong, so boxes after it were misaligned.
2. All box fields were little-endian (BinaryWriter); replaced with a big-endian writer.
3. `mdat` had no type field — the first sample's length prefix occupied it.
4. `avcC` was written without its box header.
5. `avc1`'s 32-byte compressorname was 4 bytes, misaligning depth/avcC.

Each was caught by the independent media inspection, not by the WPF window — which is why the required FFmpeg check
matters.

## 8. Tests

- Core 192 (6 new: search parsing/merge, malformed XML, service detection, DST day bounds, window validation, MP4
  structure), Data 69, Desktop 46 — **307 passing**, build clean with `-warnaserror`.
- Existing live-wall acceptance still passes 12/12 (checked before this milestone; live code paths only gained the
  replay overload with a default and the supervisor catch-all from the previous milestone).

## 9. Limits and remaining work

- **Dahua/Profile G behaviour is unverified on real hardware.** The implementation follows ONVIF Profile G's search
  shape and replay clock semantics; vendor quirks (search tokens with long polling, per-track ranges, RTSP seeking
  via Scale/Range on a live session) are not exercised.
- The acceptance run drives the Playback page with a camera descriptor built from the simulator; the imported-camera
  mapping (database device/channel → `PlaybackCamera`) is implemented in `MainViewModel.InitializeAsync` but is not
  exercised end-to-end here because the simulator does not serve live RTSP for onboarding.
- Playback speed controls exist only as a fixed 1× path (`ReplayPlaybackOptions.Rate`); the UI exposes no speed
  selector because the scripted device does not prove rate support.
- Export is video-only, non-fragmented MP4; there is no audio, no re-encode and no hash/manifest.
- Phases 5–7 remain: events, audio, workspaces/multi-monitor.
