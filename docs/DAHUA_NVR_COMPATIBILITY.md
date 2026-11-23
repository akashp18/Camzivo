# Dahua NVR Compatibility

Status date: **2026-11-22**. This document separates what Camzivo implements, what was exercised against the
local simulator, and what remains **unverified on real hardware** because this task deliberately stayed offline.

> **Real-hardware acceptance: NOT RUN in this task.** Hardware testing was kept offline so the production recorder
> was not contacted. Simulator results below are never presented as Dahua validation. The gated hardware workflow is
> documented in §7 and can be repeated only after separate explicit authorization.

## 1. Audit findings and fixes

The audit found six assumptions that can fail on real Dahua firmware, plus missing operational evidence:

| # | Finding | Risk on a Dahua NVR | Fix in this pass |
|---|---|---|---|
| 1 | Media/Profile/StreamUri calls were sent to `/onvif/device_service` instead of the advertised Media XAddr | Dahua may expose media on a different host/port or path; profiles and stream URIs fail while identity works | `ConnectionTester` and `NvrDiscovery` now call `GetProfiles`/`GetStreamUri` on `Services.Media`, falling back only when no XAddr is advertised |
| 2 | NVR discovery and recording catalog assumed HTTP Basic or WS-Security only | Dahua often challenges with HTTP Digest after the SOAP call | Both now retry exactly once with an HTTP Digest `Authorization` header after a 401 challenge; credentials remain in memory and are never logged |
| 3 | `FindRecordings` put the source token in `IncludedRecordings` | Profile G specifies a source filter as `IncludedSources/SourceToken`; Dahua may reject or ignore the search | Corrected SOAP shape, with a unit test on the generated request |
| 4 | Playback used a hardcoded `channel-{N}` source token | Dahua recording sources can be named differently, or profiles can be ordered differently from physical channels | Discovered source tokens are persisted (`Device.RecordingSourceToken`, `NvrChannel.SourceToken`); playback uses the persisted value and reports an unmapped source instead of inventing one |
| 5 | Profile order was assumed to equal physical channel order | Main/sub profiles from a real recorder are not guaranteed to be adjacent or ordered by channel | Planner groups by profile name/token channel number, pairs unnumbered alternating main/sub profiles, and never caps at a hardcoded channel count |
| 6 | Firmware, serial, timezone and last connection error were not persisted or shown | Support could not identify the firmware; diagnostics lost the last error after restart | Migration v6 adds these fields; onboarding, repositories and diagnostics persist them |

Also added:

- Advanced **ONVIF port / RTSP port / HTTPS** fields in Add NVR; defaults remain 80 and 554.
- A compact read-only **Diagnostics** window launched from Devices (not Live View), with per-capability rows and
  `SUPPORTED` / `UNSUPPORTED` / `UNKNOWN` / `CONNECTION FAILED` states.
- Digest retry in `NvrDiscovery` and `OnvifRecordingCatalog`; WS-Security UsernameToken remains the normal path.
- Firmware/serial/system-time/ONVIF-service persistence and a diagnostic “last error” timestamp.
- A headless `--probe-nvr` real-hardware acceptance workflow (§7).

## 2. Connection and identity

| Requirement | Implementation | Evidence |
|---|---|---|
| IP or hostname | `DeviceEndpoint`, IPv6 bracket handling in `OnvifSoap.DeviceServiceUri` | unit tests |
| Configurable ports | Add NVR advanced section: ONVIF, RTSP, HTTPS; manual `rtsp://` still supported | build + UI code review; not visually run in this pass |
| Username/password | ONVIF WS-Security UsernameToken with digest; HTTP Digest retry; RTSP Basic/Digest | Core unit tests for routing and Digest; real hardware not tested |
| Timeout/cancellation | Existing connection-test cancellation and 10 s HTTP timeout; probe passes cancellation tokens | existing tests |
| Clear errors | `ConnectionDiagnostics` stages, plain-language actions, no credential URL in text | existing unit tests |
| Manufacturer/model/firmware/serial | `GetDeviceInformation`, persisted in device row | repository round-trip test |
| Device time/timezone | `GetSystemDateAndTime`, persisted; used for display only, search uses workstation local time zone | parser + repository tests |
| ONVIF services | `GetCapabilities` → `GetServices` fallback; Media/PTZ/Replay/Recording/Search XAddrs | service-parser tests |
| Media/recording capabilities | Profiles, stream URIs, Profile G service presence | discovery tests |

Security boundaries:

- No password is stored in SQLite; only a DPAPI secret reference is stored.
- No authentication header, WS-Security password, or credential-bearing RTSP/HTTP URL is written to logs or reports.
- The acceptance probe redacts stream URIs and reads the password only from `CAMZIVO_NVR_PASSWORD`.
- Camzivo never writes NVR configuration, schedules, storage, firmware, or recordings. All acceptance operations are
  read-only toward the recorder.

## 3. Channel discovery and import

For every discovered channel Camzivo records:

- channel number derived from the profile, never from list position;
- camera/profile name;
- main and sub RTSP URIs;
- PTZ and audio when advertised;
- recording source token when Profile G exposes one;
- enabled/disabled state in the local camera row.

Import supports all channels or a selection, rename, remove, refresh/re-import, duplicate prevention by
`(device, channel number)`, and persistence across restart. A re-import updates names and URIs; it does not create a
second channel row. A source token is only mapped when the source name/token contains the same channel number; an
ambiguous or missing source remains `null` and is shown as unmapped.

**Unverified on hardware:** Dahua's exact profile ordering, channel naming, and source-token scheme. The planner is
conservative and the probe prints each channel’s source token so the first hardware run produces a mapping report.

## 4. Live video

The existing path is unchanged: RTSP/RTP over TCP → H.264 depacketizer → Media Foundation decoder → presented frame.
The state only becomes `LIVE` after a decoded frame exists. Adaptive main/sub selection remains based on tile size;
fullscreen uses main, small tiles use sub where available.

Simulator acceptance in this pass:

- Live wall: **12/12** steps (`artifacts/nvr-live/probe-log.ndjson`), including the real Add Camera dialog, 4/9
  layouts, fullscreen, tile move/undo, disconnect → `WAITING`, restart → `LIVE`, removal → 0 sessions.
- Onboarding E2E: **11/11** (`artifacts/nvr-e2e/summary.txt`), including persisted device restart, playback, pause,
  resume, stop, and export.

Not measured on a Dahua NVR: FPS, CPU, memory, decoder sessions, or connection stability across a real recorder’s
streams. The 49-camera substream benchmark (12.84 FPS/camera, 42.0 % CPU, 440 MB) is simulator evidence.

## 5. Recording search and playback

- Search uses Profile G `FindRecordings` with `IncludedSources/SourceToken` when a source token is known.
- Empty results, unsupported Profile G, and connection errors are distinct states; an unsupported search is never
  displayed as “no recordings”.
- Day bounds use `TimeZoneInfo`, so DST days are 23 or 25 hours; midnight crossings are represented as separate
  segments.
- Playback is a separate replay session from live, uses the real replay URI and segment timestamps, and never
  substitutes live video for a recording.
- Supported client speeds are 1×, 2×, 4×, 6×, 8×, 16×; the RTSP `Scale` header is sent, but the UI reports the
  measured source rate. A recorder that ignores or rejects `Scale` is shown as delivering ~1× with a compatibility
  notice, not a fake accelerated clock.

Simulator playback evidence: **10/10** mechanics steps (`artifacts/nvr-playback/summary.txt`) and **15/15** speed
steps (`artifacts/nvr-speeds2/summary.txt`):

| Requested | Measured | Decoded AUs | Presented | CPU | Working set |
|---:|---:|---:|---:|---:|---:|
| 1× | 0.97× | 60 | 61 | 12.2 % | 257 MB |
| 2× | 2.00× | 93 | 38 | 11.6 % | 248 MB |
| 4× | 3.86× | 176 | 42 | 6.8 % | 254 MB |
| 6× | 5.77× | 265 | 43 | 9.9 % | 249 MB |
| 8× | 8.01× | 369 | 44 | 9.9 % | 257 MB |
| 16× | 15.75× | 720 | 45 | 16.8 % | 259 MB |

The same run covers pause/resume at 8×, seek while fast, end-of-recording, zero remaining sessions, previous-day
16×, a Scale-ignoring recorder, and a Scale-rejecting recorder. There was one transient 6× frame-hash failure in the
first run; a clean rerun passed all 15 steps and is the report above. That flake is recorded, not hidden.

## 6. Export

The built-in ISO-BMFF writer produces H.264 MP4 with real RTP-clock durations, a `.part` file that is renamed only
after completion, cancellation cleanup, and a SHA-256 sidecar. Simulator E2E export: **1,131,180 bytes / 92 samples**,
6.15 s duration; the sidecar verified. No `ffprobe`/FFmpeg is installed on this machine, so independent container
and decode validation of the exported file was **not available**; the SHA-256 is integrity evidence, not proof of
decodability.

## 7. Real-hardware acceptance procedure (blocked here)

No NVR was reachable. Step one is the metadata-only probe, which answers "what is this recorder and what does it
advertise" without touching a stream, a recording, PTZ or configuration:

```powershell
$env:CAMZIVO_NVR_PASSWORD = "..."
Camzivo.Desktop.exe --probe-nvr-metadata artifacts/nvr-metadata `
  --nvr-host 192.168.1.108 --nvr-user admin `
  --nvr-port 80 --nvr-metadata-authorized --nvr-confirm 192.168.1.108
```

It sends only `GetDeviceInformation`, `GetCapabilities`, `GetServices`, `GetServiceCapabilities`,
`GetSystemDateAndTime`, `GetProfiles`, `GetVideoSources`, `GetVideoSourceConfigurations` and
`GetVideoEncoderConfigurations`, sequentially, with a 20-attempt budget and a 60-second limit, and writes
`nvr-metadata.json` / `nvr-metadata-shareable.json` / `summary.txt`. Dahua media typically lives at
`/onvif/media_service`; if firmware advertises that XAddr on another host or port the probe reports it as blocked
instead of following it. It exits **2** without its own `--nvr-metadata-authorized` switch. Offline it passed 9/9
requests against the simulator; **no Dahua NVR has run it.**

Step two is the full real-device workflow on a machine with hardware:

```powershell
$env:CAMZIVO_NVR_PASSWORD = "..."   # never put the password on the command line
Camzivo.Desktop.exe --probe-nvr artifacts/nvr-real `
  --nvr-host 192.168.1.108 --nvr-user admin `
  --nvr-port 80 --nvr-rtsp-port 554 `
  --nvr-authorized --nvr-confirm 192.168.1.108
```

The probe is read-only and performs:

1. connect and authenticate; 2. identity/firmware/serial/timezone; 3. ONVIF service discovery; 4. channel discovery
with main/sub and source tokens; 5. import every channel into a **temporary local SQLite profile**; 6. reopen that
profile with new repositories (restart persistence, DPAPI password); 7. live H.264 decode; 8. previous-day Profile G
search; 9. replay lookup and 1× playback; 10. 2×/4×/6×/8×/16× requested-vs-measured checks; 11. a short MP4 export.

It writes `nvr-acceptance.json` and `summary.txt`. Without `--nvr-host`, `CAMZIVO_NVR_PASSWORD`, `--nvr-authorized`
and a matching `--nvr-confirm` it exits **2** with `BLOCKED` and makes no hardware claim. The protocol allowlist and
approval rules are documented in `docs/READ_ONLY_NVR_MODE.md`. That is the current result on this machine
(`artifacts/nvr-blocked2/summary.txt`).

## 8. Compatibility matrix

Legend: **Implemented** = code + automated tests; **Simulator** = real application exercised against the local
Dahua-shaped simulator; **Dahua hardware** = actual Dahua NVR evidence (none yet).

| Capability | Implemented | Simulator | Dahua hardware |
|---|---|---|---|
| ONVIF identity, profiles, media XAddr routing | yes | pass | **unknown** |
| HTTP Digest challenge fallback | yes | unit test | **unknown** |
| WS-Security UsernameToken | yes | pass | **unknown** |
| Advanced ONVIF/RTSP/HTTPS ports | yes | code review | **unknown** |
| Channel discovery and import all/selective | yes | pass (E2E import/restart) | **unknown** |
| Duplicate prevention, rename, remove, refresh | yes | repository tests | **unknown** |
| Main/sub stream selection | yes | live pass | **unknown** |
| Live H.264 decode | yes | 12/12 | **unknown** |
| Profile G service discovery | yes | pass | **unknown** |
| Recording source token mapping | yes | pass | **unknown** |
| Previous-day / DST / midnight search | yes | 15/15 speed probe | **unknown** |
| Playback play/pause/seek/stop | yes | 10/10 | **unknown** |
| 1×/2×/4×/6×/8×/16× measured | yes | 15/15 | **unknown** |
| Scale ignored/rejected messaging | yes | pass | **unknown** |
| MP4 export + cancellation | yes | pass | **unknown** |
| Independent export decode (ffprobe) | n/a | unavailable | **unavailable** |
| Read-only diagnostics view | yes | service test | **unknown** |
| Metadata-only probe (`--probe-nvr-metadata`, 9 Get operations) | yes | simulator 9/9 | **unknown** |
| PTZ / audio | existing PTZ only; no audio pipeline | not applicable | **unknown** |

## 9. Known limitations

- **No Dahua hardware evidence.** The compatibility matrix above must not be read as “Dahua certified”. Neither probe
  has been run against a Dahua recorder; only the local simulator answered.
- Profile G `Scale` support, source-token naming, channel numbering, firmware-specific SOAP quirks, timeout
  behaviour, and digest challenge details remain unverified on Dahua firmware.
- Audio, two-way talk, device motion events, and deep device configuration are not implemented.
- No FFmpeg/ffprobe on this machine; export validation is muxer + decode-at-runtime + SHA-256, not an independent
  container probe.
- The diagnostics window and advanced Add NVR layout compile and are wired, but were not visually inspected in this
  pass.
- A transient simulator speed-probe frame-hash failure occurred once at 6×; a clean rerun passed 15/15.

### Probe dry-run (simulator, not hardware)

The probe was exercised end to end against the local simulator on `127.0.0.1:8115/8571`: discovery → import →
repository reopen → live decode → yesterday search → replay → 1×–16× → MP4 export all passed (timezone remained
`UNKNOWN` because the simulator does not return `GetSystemDateAndTime`). This validates the probe itself; it does not
change the hardware status above. Command and temporary profile are described in §7.

## 10. Read-only production safety

The NVR boundary is now enforced below the UI. `NvrAccessPolicy` binds a session to the exact host, ONVIF port and RTSP
port; `ReadOnlyNvrTransport` permits only an explicit ONVIF operation allowlist; discovery/catalog reject cross-host
service and stream addresses; recorder PTZ is disabled; UNC/device export paths are rejected; and the real-hardware
probe is blocked unless two separate authorization switches name the same exact host. See
[`READ_ONLY_NVR_MODE.md`](READ_ONLY_NVR_MODE.md) for the architecture, operation allowlist, safety tests and the list of
operations that require a separate future approval.
