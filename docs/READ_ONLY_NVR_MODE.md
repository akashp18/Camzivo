# Read-Only NVR Mode

Status: **enforced by default**. This document covers the production-recorder safety boundary added to Camzivo.

> No real NVR or camera was contacted during this implementation. Every check below used the local recording
> simulator, scripted ONVIF responses, or unit tests. Real-hardware testing remains a separate, explicitly authorized
> task.

## 1. Architecture

Read-only behavior is enforced in three layers, not by hiding buttons:

1. **Core policy** — `NvrAccessPolicy`, `NvrReadOnlyOperations`, `NvrSafety`, and `NvrHardwareAccessGate`.
2. **Protocol transport** — `ReadOnlyNvrTransport` wraps every NVR ONVIF call; `ReadOnlyNvrRtspProbe` guards the
   connection test's RTSP check. Both reject before the inner transport/probe is called.
3. **Workflow** — `NvrDiscovery` validates returned service XAddrs and stream URIs; `OnvifRecordingCatalog` validates
   Profile G services and replay URIs; recorder-owned PTZ is disabled; the real-hardware probe is blocked by default.

`HttpOnvifTransport` already sets `AllowAutoRedirect = false` and `UseCookies = false`, so an HTTP redirect cannot move
a request to another host.

## 2. Exact network boundary

An NVR session is bound to one `DeviceEndpoint`:

- exact host string (a hostname and its resolved IP are not interchangeable);
- scheme and exact ONVIF port;
- exact RTSP port for stream/replay addresses.

Anything else is denied and reported. This includes:

- a Media, Recording, Search or Replay XAddr on another host;
- a different port on the same host;
- a stream or replay URI on a camera IP;
- an HTTP redirect (redirects are never followed);
- a URL containing credentials (redacted in messages and reports).

No subnet scan, port sweep, camera discovery, or unrelated service connection is performed by the NVR workflow.

## 3. ONVIF operation allowlist

Only these operations can be sent by a read-only recorder session:

**Device/media observation:** `GetDeviceInformation`, `GetCapabilities`, `GetServices`,
`GetServiceCapabilities`, `GetSystemDateAndTime`, `GetProfiles`, `GetStreamUri`, `GetSnapshotUri`,
`GetVideoSources`, `GetVideoSourceConfigurations`, `GetVideoEncoderConfigurations`,
`GetVideoEncoderConfigurationOptions`.

**PTZ metadata:** `GetNodes`, `GetPresets`, `GetStatus`, `GetConfigurationOptions`.

**Profile G observation:** `GetRecordings`, `GetRecordingInformation`, `FindRecordings`,
`GetRecordingSearchResults`, `EndSearch`, `GetReplayConfiguration`, `GetReplayUri`.

Everything else is denied by default, including:

- `Set*` (configuration)
- `Create*` / `Delete*` (users, recordings, presets)
- `ContinuousMove`, `GotoPreset`, `SetPreset`, `Stop` (PTZ movement/control)
- recording schedule/start/stop controls
- relay/alarm/output operations
- unknown future operations

`FindRecordings`/`GetRecordingSearchResults` create a temporary search session; `EndSearch` releases it. Neither
changes persistent device configuration.

## 4. Hardware-access gate

`NvrAcceptanceProbe` remains blocked unless **all** of these are true:

1. an exact `--nvr-host` is supplied;
2. valid ONVIF and RTSP ports are supplied;
3. `--nvr-authorized` is present;
4. `--nvr-confirm` names the **same exact host**;
5. `CAMZIVO_NVR_PASSWORD` is present in the environment.

A saved credential, a reachable address, or the presence of a password is not authorization. The probe prints the
intended host, ports and operation allowlist before contacting anything. It uses a temporary local SQLite profile and
does not persist the recorder password into the operator's normal profile.

Correct future real-hardware invocation (only after explicit approval):

```powershell
$env:CAMZIVO_NVR_PASSWORD = "..."   # never put this on the command line
Camzivo.Desktop.exe --probe-nvr artifacts/nvr-real `
  --nvr-host 192.168.1.108 --nvr-user admin `
  --nvr-port 80 --nvr-rtsp-port 554 `
  --nvr-authorized --nvr-confirm 192.168.1.108
```

Without both authorization switches the probe exits **2** with `BLOCKED`. This task did not run it.

### 4.1 Metadata-only verification (`--probe-nvr-metadata`)

Before the full workflow, a recorder can be identified with a strictly smaller probe. It has its own authorization
gate, so a full-NVR authorization (`--nvr-authorized`) does **not** enable it:

```powershell
$env:CAMZIVO_NVR_PASSWORD = "..."   # never on the command line
Camzivo.Desktop.exe --probe-nvr-metadata artifacts/nvr-metadata `
  --nvr-host 192.168.1.108 --nvr-user admin `
  --nvr-port 80 --nvr-metadata-authorized --nvr-confirm 192.168.1.108
```

`--nvr-tls` (or `--nvr-scheme https`) selects HTTPS; the ONVIF port is `--nvr-port`. No RTSP port is used and no RTSP
connection is made.

It may send only these nine operations, all against the exact approved host and ONVIF port:
`GetDeviceInformation`, `GetCapabilities`, `GetServices`, `GetServiceCapabilities`, `GetSystemDateAndTime`,
`GetProfiles`, `GetVideoSources`, `GetVideoSourceConfigurations`, `GetVideoEncoderConfigurations`.

`ReadOnlyNvrTransport` is constructed with `NvrOperationScope.MetadataOnly`, so even a future caller cannot widen this
run: `GetStreamUri`, `GetReplayUri`, `FindRecordings`, `GetRecordingSearchResults`, `GetRecordings`,
`GetRecordingInformation`, every RTSP method, PTZ movement and every `Set*`/`Create*`/`Delete*` are denied below it.
There is no `IRtspEndpointProbe` and no recording catalog in this path at all.

Limits: requests are strictly sequential, at most **20 HTTP attempts** (one digest retry per request), redirects are
disabled, and the whole run stops after **60 seconds**. A Media/PTZ/Replay/Recording/Search XAddr on another host or
port is reported as blocked and never contacted; a cross-host Media service stops the media part of the run.

Output is local only: `nvr-metadata.json`, `nvr-metadata-shareable.json` (host masked, device tokens replaced with
positional references) and `summary.txt`. The report contains identity, services, clock/timezone, profile/source/
encoder counts, main/sub relationships, capabilities, unsupported operations, blocked endpoints, per-request outcome
and duration. It never contains the password, an authorization header, a credential URL or the serial number, and the
probe exits **2** (`BLOCKED`), **1** (`failed`) or **0** (`passed`) without doing anything after collection.

## 5. Live, playback, and export

- Live RTSP is read-only and uses the existing decoder/session lifecycle. A recorder channel whose stored URI is
  outside the exact NVR host/RTSP port is not opened; the UI reports the blocked endpoint.
- Recording search and replay use the same guarded transport. Cross-host search/replay XAddrs and replay URIs are
  reported without connection.
- Replay supports 1×, 2×, 4×, 6×, 8× and 16× with measured source rate; a recorder that ignores/rejects `Scale` is
  reported honestly.
- Export writes only to a local absolute path. UNC and device paths are rejected by `LocalExportPolicy` before a
  replay session starts. No NVR-side files, retention, schedules or storage are touched.
- PTZ controls are disabled for recorder-owned channels, before any PTZ request is made. Standalone cameras retain
  their existing PTZ behavior.

## 6. Safety tests added

- `NvrSafetyTests` — allowlist positive/negative cases, malformed SOAP, exact host/port/scheme, hostname-vs-IP
  substitution, hardware gate default/block/confirmation/ports, URI credential redaction.
- `ReadOnlyNvrTransportTests` — approved operation forwarding; cross-host, wrong-port, wrong-scheme and
  configuration-write rejection before the inner transport; unknown-operation default deny; RTSP camera-address
  rejection.
- `NvrDiscoverySafetyTests` — cross-host Media XAddr is not contacted; cross-host stream URI is not opened.
- `OnvifRecordingSafetyTests` — cross-host Profile G search/replay services are not contacted; cross-host replay URI
  is rejected.
- `NvrSafetyDesktopTests` — recorder PTZ is disabled before any request; UNC/device export paths are rejected; the
  diagnostics view displays `READ-ONLY NVR MODE`.
- `NvrMetadataProbeTests` — only the nine metadata operations are sent (in order); no `GetStreamUri`/recording/PTZ
  request exists in the result; cross-host Media is reported and not contacted; digest retries and the request budget
  are finite; the overall time limit stops the session; reports contain no password or credential URL.
- Metadata gate/mask cases in `NvrSafetyTests` — the metadata flag is required independently of full-probe
  authorization, a matching exact-host confirmation is required, and the masked host hides the network address.

## 7. Operations requiring separate future approval

These are **not** implemented and must not be attempted against the production recorder without a new, explicit
authorization:

- contacting an ONVIF service XAddr on a different host or port;
- opening a stream/replay URI on a camera IP or a non-approved RTSP port;
- enabling ONVIF, RTSP, HTTPS or changing NVR ports/services;
- changing camera names, channel assignments, encoding, recording schedules, retention or storage;
- creating/deleting users or changing passwords;
- PTZ movement, presets, relays, alarms or any physical action;
- firmware update, reboot, reset or factory reset;
- vulnerability scanning, subnet scanning, port sweeps or brute-force authentication;
- writing any file to the NVR or deleting any recording.

If a desired feature requires one of these, Camzivo reports the limitation and stops. The NVR stays as it is.

## 8. Verification (offline only)

- Build: `dotnet build Camzivo.sln -warnaserror` — pass, 0 warnings.
- Tests: **459 passed, 0 failed** (Core 261, Data 102, Desktop 96).
- Simulator acceptance: live wall 12/12; onboarding → restart → playback → export 11/11; playback mechanics 10/10;
  1×–16× speed probe 15/15.
- Metadata-only probe against the local simulator (`127.0.0.1:8115`): 9 requests, 9 answered, 3 profiles, 3 video
  sources, 3 encoder configurations, main/sub/other relationships, `passed` in 0.2 s; the same command without
  `--nvr-metadata-authorized` exited **2** and sent nothing.
- No real-NVR probe execution. No network discovery against a production environment.
