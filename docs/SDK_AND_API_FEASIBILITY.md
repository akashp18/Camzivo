# SDK and API Feasibility

The question this document answers: for each capability, what is the *supported* way to implement it, and what may
legally ship inside Camzivo?

## Position on Dahua's binary SDKs

Dahua's NetSDK / Device Network SDK is a **binary redistribution question, not a download question**. The libraries
are published for integration partners, and integration guidance for Hikvision's equivalent states the guides require
a signed materials licence; Dahua's SDK bundles carry their own agreements. Downloadable does not mean
redistributable, and a VMS that bundles vendor SDK binaries inherits the vendor's terms, update cadence and
architecture (32-bit DLLs, per-vendor copies).

**Decision:** Camzivo ships **no Dahua SDK binaries**. If a customer already holds a Dahua SDK licence and wants the
deeper integration, an optional, user-supplied adapter can be added later behind an interface — the user places the
DLLs, Camzivo never redistributes them. That keeps the default product licence-clean and lightweight.

This is not a functional loss for the core workflows: live view, main/sub stream, PTZ, snapshots, playback for
Profile G devices, and events are all reachable over documented protocols.

## Options evaluated

| Option | What it gives | Licence / redistribution | Verdict |
|---|---|---|---|
| **ONVIF (Profile S/T/G)** | Discovery, device info, media profiles, stream URIs, PTZ, events, replay | Royalty-free specifications published by ONVIF; implemented by us | **Primary** |
| **RTSP / RTP / SDP** (RFC 2326/3550/4566) | The stream itself, TCP interleaved or UDP | Open IETF standards | **Primary** |
| **Dahua RTSP URL convention** | `/cam/realmonitor?channel=N&subtype=0\|1` | Published in Dahua device manuals and the HTTP API document | **Supported convenience**, documented, no code copied |
| **Dahua HTTP/CGI API** | Recording search, snapshots, some config, events | Documented in Dahua's HTTP API manual; the manual is Dahua's document, not redistributable as our content | **Fallback adapter**, implemented from the public command descriptions |
| **Dahua NetSDK** | Deep config, proprietary playback, alarms, P2P | Vendor binary SDK + agreement; redistribution restricted | **Not bundled**; optional user-supplied adapter only |
| **FFmpeg (LGPL build)** | H.264/H.265 decode, demux, mux, stream copy for export | LGPL 2.1+ when built without `--enable-gpl`/`--enable-nonfree`, dynamically linked, notices + source offer; H.264/H.265 **patent** licensing is separate from copyright | **Primary decoder/exporter** |
| **Windows Media Foundation** | H.264 decode always present; HEVC MFT present but its availability depends on the installed HEVC extension | OS component, no redistribution | **Fallback decoder**, and the safer default on locked-down fleets |
| **NAudio / Media Foundation audio** | Audio playback for the listen path | NAudio is MIT | Secondary |

Sources: FFmpeg legal page (`ffmpeg.org/legal.html`); Microsoft Media Foundation codec documentation
(`learn.microsoft.com/windows/win32/medfound/h-264-video-decoder`, `.../h-265---hevc-video-decoder`); ONVIF Media2
and Streaming specifications (`onvif.org/specs/...`); Dahua HTTP API for IPC (CGI stream syntax); Dahua SmartPSS Lite
datasheet and user manual (`dahuawiki.com`); Dahua RTSP guidance for NVR channel/subtype.

## Capability-by-capability feasibility

| Capability | Official SDK? | Open standard? | Existing library? | Custom work | Device-dependent? | Licence risk | Performance note |
|---|---|---|---|---|---|---|---|
| Discovery | NetSDK broadcast search (not bundled) | WS-Discovery | — | UDP probe + parser (done) | Low | None | One multicast probe; reply parsing is CPU-trivial |
| Add/authenticate | NetSDK login | ONVIF WS-Security + HTTP Digest | — | Implemented | Medium (some devices need both auth layers) | None | Two HTTP round trips typical |
| Live view | NetSDK realplay | RTSP/RTP + SDP | FFmpeg or MF for decode | RTSP client, depacketizer, decoder, renderer | Medium | FFmpeg LGPL obligations | Dominant cost is decode; see the benchmark plan |
| Main/sub switch | SDK stream selection | ONVIF profiles / RTSP `subtype` | — | Profile selection (done), stream restart (M4) | Low | None | Sub stream cuts decode cost by 4–8× at 1080p→D1 |
| PTZ | NetSDK PTZ | ONVIF PTZ | — | SOAP commands + UI | Medium | None | Negligible bandwidth |
| Audio listen | NetSDK audio | ONVIF audio / RTSP track | MF/NAudio | Track selection + playback | High | None | Small |
| Two-way talk | NetSDK talk | ONVIF backchannel (rarely implemented) | — | Full-duplex audio path | **Very high** | None | Not a parity blocker; M7 |
| Playback | NetSDK playback | ONVIF Profile G Replay | FFmpeg demux | Replay session, timeline data | **High** | None | Seeking over RTSP is latency-bound, not CPU-bound |
| Recording search | NetSDK find-file | Profile G `FindRecordings` | — | Search session + paging | **High** | None | Polling cadence matters; cap it |
| Export | NetSDK download | Replay/RTSP + mux | FFmpeg `-c copy` | Job queue, progress, cancel | Medium | LGPL only | Stream copy is I/O-bound, no re-encode |
| Snapshots | SDK capture | Decoder frame | — | PNG encode of the presented frame | Low | None | One-off |
| Events | NetSDK alarm | ONVIF PullPoint | — | Subscription, reconnect, mapping | **High** | None | Long-poll, no busy loop |
| Device settings | NetSDK config | ONVIF device/media config (subset) | — | Read-back UI | **High** | None | Write-then-verify only |
| Health | — | RTSP `OPTIONS`, ONVIF `GetSystemDateAndTime` | — | Scheduler (exists) | Low | None | Backoff, never polling per tile |

## Recommended architecture

1. **Protocol-first core.** `IOnvifTransport`, `IRtspEndpointProbe`, and the future `IRtspClient`/`IDecoder` are seams.
   Everything above them (onboarding, diagnostics, wall state) is testable without hardware — which is how the current
   359-test suite covers protocol handling (see `docs/RELEASE_READINESS.md`).
2. **Decoder strategy.** FFmpeg LGPL build (dynamic link, `d3d11va` hardware acceleration, software fallback) as the
   primary; Media Foundation as a fallback for fleets where deploying FFmpeg DLLs is not permitted. The decoder
   interface hides the choice; the UI never knows which one produced the frame.
3. **No vendor SDK in the default build.** A `DahuaSdk` adapter directory, loaded only if the user supplies the DLLs,
   and excluded from the shipped installer.
4. **Export via stream copy.** Never re-encode evidence footage.
5. **Patent note.** H.264/H.265 patent licensing is separate from FFmpeg's copyright licence; a commercial
   distribution needs its own assessment. This is recorded as a product decision, not a code decision.

## What feasibility does *not* tell us

Feasibility is not compatibility. Whether a given Dahua NVR answers Profile G, whether its playback is reachable by
CGI in the installed firmware, and whether sub streams are enabled on a customer's channels are **per-device facts**.
They are detected at runtime and reported honestly (capability state `Unverified` until a probe says otherwise).
