# Device Compatibility

Principle: **negotiate, never assume.** Every capability is a tri-state (`Supported`, `Unsupported`, `Unverified`)
so the UI can be honest instead of optimistic. `Unverified` is the default for anything we have not proven on a
given device.

---

## 1. Protocol layers, in order of preference

| Layer | Use | Auth | Transport |
|---|---|---|---|
| ONVIF WS-Discovery | find devices on the segment | none (discovery is unauthenticated by design) | UDP multicast `239.255.255.250:3702` |
| ONVIF Device/Media (Profile S/T) | identity, media profiles, stream URIs | WS-Security UsernameToken (digest, nonce+timestamp) | HTTP/SOAP 80/443 |
| ONVIF PTZ (Profile S) | move/stop/preset/status | same | SOAP |
| ONVIF Events (Profile A/C, PullPoint) | alerts, motion | same | SOAP |
| ONVIF Replay (Profile G) | recording search + replay | same | RTSP + SOAP |
| RTSP/RTP | video/audio ingest | Basic/Digest | TCP (default) or UDP |
| Vendor HTTP (Dahua documented) | snapshot, PTZ fallback, recording list | Digest | HTTP |

No private/DVRI P encapsulation, no P2P cloud traversal, no undocumented authentication paths.

## 2. Discovery

- Send `Probe` with `wsd:Types` = `dn:NetworkVideoTransmitter` (and optionally `dn:Device`), receive `ProbeMatch`.
- Parse `EndpointReference/Address` (device-service XAddr), `Scopes`, `Types` with namespace awareness — never
  string-split on `onvif://`.
- Extract from scopes: `name/`, `hardware/`, `location/`, `profile/` (e.g. `profile/S`), and MAC when present.
- Deduplicate by XAddr + endpoint reference; a device answering twice (multi-NIC) appears once with both addresses.
- Discovery never sends credentials and never writes to the device.

Implemented in this milestone: `Camzivo.Core.Devices.Onvif.WsDiscoveryParser` (unit-tested with real-world SOAP
fixtures, including a Dahua-style response with `Scopes` containing multiple values and a Hikvision-style response
with a namespace prefix on `d:ProbeMatch`).

## 3. Capability matrix (model, per device)

| Field | Values | Source |
|---|---|---|
| `Profiles` | list of `{token, name, width, height, fps, encoding, bitrate}` | `GetProfiles` |
| `StreamUri` | per-profile RTSP URI | `GetStreamUri` (RTP-Unicast/RTSP) |
| `PtzSupported` | Supported / Unsupported / Unverified | `GetCapabilities` → `PTZ` + `GetNodes` |
| `EventsSupported` | tri-state | `GetCapabilities` → `Events` |
| `ReplaySupported` | tri-state | `GetCapabilities` → `Replay` |
| `SnapshotUri` | URI or null | `GetSnapshotUri` |
| `Transports` | {RTSP/TCP, RTSP/UDP, HTTP} | probe |
| `Encodings` | {H264, H265, MJPEG, AV1} | profile `Encoding` values |
| `AudioSupported` | tri-state | profile audio encoder presence |
| `MaxProfiles` | int | profile count |

Negotiation order: `GetDeviceInformation` → `GetCapabilities` → `GetProfiles` → `GetStreamUri` (first profile) →
optional `GetSnapshotUri`. Any step failing downgrades only the dependent capabilities to `Unverified`/`Unsupported`;
the device remains usable for what did work.

## 4. Known device behaviours we design for

| Behaviour | Consequence | Handling |
|---|---|---|
| Dahua ONVIF requires the ONVIF user to be created separately from the web user | 401 with correct web credentials | diagnostic: "ONVIF access is disabled or uses a different account; enable ONVIF in the camera's network settings" |
| Dahua main/sub profiles differ in resolution and GOP | stream switch changes parameter sets | decoder re-open per stream; no cross-stream state |
| Some NVRs expose only channels via `/cam/realmonitor?channel=N&subtype=0` | ONVIF media may be absent entirely | vendor HTTP/RTSP template fallback, flagged as vendor-specific |
| Hikvision ONVIF often omits `GetSnapshotUri` | no ONVIF snapshot | fall back to vendor snapshot if configured, else disable snapshot with reason |
| Clock drift between cameras and workstation | timeline offsets, wrong export timestamps | per-camera offset measured at connect, surfaced in playback ("offset +1.4 s") |
| Devices reboot or rotate streams daily | unexpected EOF | supervised reconnect with backoff; session id increments |
| Some cameras send HEVC in profile `H265` but label it `H264` | decode mismatch | trust the SDP/`fmtp` and decoder result, not the label |
| Multicast-only streams | no unicast fallback | user must configure; we state it as unsupported rather than hanging |

## 5. Vendor-specific documented extras (optional, capability-gated)

| Feature | Mechanism | Gate |
|---|---|---|
| Snapshot | HTTP GET `/cgi-bin/snapshot.cgi` (Dahua) | only when ONVIF snapshot is absent; requires explicit device type = Dahua |
| Recording list | HTTP `recordManager.cgi?action=factory.create` (Dahua) | Profile G absent; read-only |
| PTZ | ONVIF PTZ preferred; HTTP PTZ fallback | capability-gated, logged |
| Device time | `global.cgi?action=getCurrentTime` | read-only in v1 |

Vendor paths are only attempted when the device was explicitly marked as that vendor by the operator or matched a
documented fingerprint. We never probe proprietary endpoints speculatively.

## 6. Test device matrix (target, not yet populated)

| # | Device class | ONVIF | RTSP | Replay | Status |
|---|---|---|---|---|---|
| 1 | Dahua IPC (ONVIF profile S) | ✓ | ✓ | n/a | **not tested — no device available** |
| 2 | Dahua NVR (channels via ONVIF) | ✓ | ✓ | ? | **not tested** |
| 3 | Generic ONVIF camera | ✓ | ✓ | ✗ | **not tested** |
| 4 | Non-ONVIF RTSP camera | ✗ | ✓ | ✗ | **not tested** |
| 5 | Synthetic (mediamtx) | n/a | ✓ | n/a | planned harness |
| 6 | Corrupt/partial SDP source | n/a | ✓ | n/a | planned harness |

Anything marked "not tested" is not claimed as supported. The parser and URI builder are unit-tested against
captured XML/URI *formats*; end-to-end device behaviour remains unverified until hardware is provided.

## 7. Unsupported, by decision

- Dahua private DVRIP binary protocol, P2P/cloud relay, `P2PDll`-style traversal.
- Proprietary authentication (e.g. Dahua's `LoginV3` hash with device-specific keys) — we require ONVIF or standard
  RTSP digest/basic.
- Firmware update, device configuration writes, user management on the device. Camzivo is read-only toward devices
  in v1: it will never change a camera's configuration, satisfying "do not modify device configuration without
  explicit authorization" by construction.

## 8. Dahua NVR compatibility pass (2026-11-22)

The dedicated audit, compatibility matrix and blocked hardware procedure are in
[`DAHUA_NVR_COMPATIBILITY.md`](DAHUA_NVR_COMPATIBILITY.md). Summary:

- ONVIF Media/Profiles/StreamUri now use the **advertised media XAddr**, not a hardcoded device-service path.
- NVR discovery and the recording catalog retry HTTP **Digest** after a 401, in addition to WS-Security.
- Profile G source filtering uses `IncludedSources/SourceToken`; source tokens are discovered with
  `GetRecordings` → `GetRecordingInformation` and are persisted per device/channel.
- Playback no longer invents `channel-N`; an unmapped source is reported instead.
- Firmware, serial, device timezone, last connection error and diagnostic time are persisted (migration v6).
- Add NVR has optional ONVIF/RTSP/HTTPS configuration; defaults remain 80/554.
- Devices has a read-only, compact Diagnostics window with Supported/Unsupported/Unknown/Connection-failed rows.
- `--probe-nvr` runs the real-hardware acceptance workflow and reports **BLOCKED** unless `--nvr-authorized` and a matching `--nvr-confirm <host>` are supplied.

**Status:** implemented and covered by 422 automated tests plus simulator acceptance. **Real Dahua hardware remains
untested and is not claimed as supported.** The matrix currently marks every Dahua-hardware cell `unknown`.
