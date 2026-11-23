# API Contracts

These are the in-process contracts between modules plus the external protocol surfaces Camzivo speaks. They are the
authority for signature shape; the milestone that implements each is noted.

---

## 1. Internal contracts (`Camzivo.Core`)

### 1.1 Streaming

```csharp
public enum StreamQuality { Auto = 0, Main = 1, Sub = 2 }
public enum RtspTransport { Tcp = 0, Udp = 1 }
public enum StreamState { Idle, Connecting, Live, Reconnecting, Suspended, Faulted }

public sealed record StreamRequest(
    Guid CameraId,
    StreamQuality Quality,
    RtspTransport Transport,
    TimeSpan ConnectTimeout,
    int MaxQueueDepth,
    StreamUriSet Uris);

public sealed record StreamUriSet(string? Main, string? Sub)
{
    public bool HasMain => !string.IsNullOrWhiteSpace(Main);
    public bool HasSub  => !string.IsNullOrWhiteSpace(Sub);
}

public interface IStreamEngine : IAsyncDisposable
{
    Task<IStreamSession> OpenAsync(StreamRequest request, CancellationToken ct);
    IReadOnlyCollection<StreamSnapshot> Snapshot();
    event EventHandler<StreamFaultEventArgs>? Faulted;
}

public interface IStreamSession : IAsyncDisposable
{
    Guid CameraId { get; }
    StreamState State { get; }
    StreamMetrics Metrics { get; }
    Task ReconnectAsync(CancellationToken ct);
}
```

Contract rules:
- `OpenAsync` returns as soon as the pipeline is *established* (socket + SDP + decoder), not on first frame;
  first-frame timing is reported through `Metrics.FirstFrame`.
- Every failure arrives as a `StreamFault` with a classified `StreamFailure` and a redacted detail string.
- `IStreamSession.DisposeAsync` is idempotent and must release decoder + surfaces even if the socket is stuck.

### 1.2 Quality policy (implemented, M2)

```csharp
public static class StreamSelectionPolicy
{
    public static StreamSelection Select(StreamSelectionContext context);
}

public sealed record StreamSelectionContext(
    StreamQuality Preference, bool IsFullScreen, int TileWidth, int TileHeight,
    bool IsVisible, StreamUriSet Uris);

public sealed record StreamSelection(StreamQuality Quality, StreamSelectionReason Reason);
```

Deterministic, allocation-free, unit-tested. `Reason` is surfaced in the tile chip and Health so a downgrade is
never silent.

### 1.3 Scheduling (implemented, M2)

```csharp
public static class StreamScheduler
{
    public static SchedulingPlan Plan(IReadOnlyList<TileDemand> tiles, int decodeBudget);
}

public sealed record TileDemand(Guid CameraId, bool IsVisible, bool IsFullScreen, int TileArea, long LastInteractedTicks);

public sealed record SchedulingPlan(IReadOnlyList<Guid> Active, IReadOnlyList<Guid> Suspended);
```

Invariants: `Active.Count <= decodeBudget`; fullscreen always active if visible; stable ordering for equal
priorities (tie-break by `CameraId`) so tests and UI do not flicker.

### 1.4 Diagnostics (implemented, M2)

```csharp
public enum StreamFailure
{
    None, DnsFailure, ConnectionRefused, Timeout, Unauthorized, Forbidden,
    RtspMethodNotAllowed, NotFound, UnsupportedCodec, DecoderInitFailed,
    GpuDeviceLost, BandwidthInsufficient, NvrPermissionDenied, ProtocolViolation, Unknown
}

public sealed record Diagnostic(string Title, string Explanation, IReadOnlyList<string> Actions, StreamFailure Code);

public static class ConnectionDiagnostics
{
    public static Diagnostic Explain(StreamFailure failure, string? redactedDetail = null);
}
```

Every code maps to a title, an explanation of *why*, and 1–4 concrete actions. `Unknown` is a bug signal, not a
user-facing cop-out: it still offers "copy diagnostics" and "retry with TCP".

### 1.5 Devices

```csharp
public enum CapabilityState { Unverified = 0, Supported = 1, Unsupported = 2 }

public sealed record DeviceEndpoint(string Host, int OnvifPort, int RtspPort, bool UseTls);

public sealed record DeviceCapabilities(
    CapabilityState Ptz, CapabilityState Events, CapabilityState Replay, CapabilityState Snapshot,
    IReadOnlyList<MediaProfile> Profiles, IReadOnlySet<string> Encodings, IReadOnlySet<RtspTransport> Transports,
    DateTimeOffset? ProbedUtc);

public interface IDeviceProbe
{
    Task<ProbeOutcome> ProbeAsync(DeviceEndpoint endpoint, DeviceCredentials credentials, CancellationToken ct);
}
```

### 1.6 Security

```csharp
public interface ISecretStore
{
    ValueTask<string?> ReadAsync(string key, CancellationToken ct = default);
    ValueTask WriteAsync(string key, string secret, CancellationToken ct = default);
    ValueTask DeleteAsync(string key, CancellationToken ct = default);
}

public static class CredentialRedactor
{
    public static string Sanitize(string text);         // rtsp://user:***@host, ?password=***
    public static string RedactUri(Uri uri);
}
```

`Sanitize` is applied at the logging boundary (single choke point), and tested with real credential shapes.

## 2. ONVIF SOAP surface (M3)

| Operation | Request | Notes |
|---|---|---|
| `GetDeviceInformation` | `tds:GetDeviceInformation` | manufacturer, model, firmware, serial |
| `GetCapabilities` (all categories) | `tds:GetCapabilities` | gates everything else |
| `GetProfiles` | `trt:GetProfiles` | profile tokens + resolution + encoding |
| `GetStreamUri` | `trt:GetStreamUri` with `StreamSetup/Transport=RTSP`, `Stream=RTP-Unicast` | stored per profile |
| `GetSnapshotUri` | `trt:GetSnapshotUri` | optional |
| `GetNodes`, `GetNode`, `ContinuousMove`, `Stop`, `GotoPreset` | `tptz:*` | PTZ, M5 |
| `CreatePullPointSubscription`, `PullMessages` | `tev:*` | events, M6 |
| `FindRecordings`, `GetReplayUri` | `trp:*` | replay, M5 |

Transport: HTTP POST to `XAddr`, `Content-Type: application/soap+xml; charset=utf-8`, WS-Security
`UsernameToken` with `PasswordDigest` = `Base64(SHA1(nonce + created + password))`. TLS validation is on by
default; a per-device "allow self-signed certificate" toggle exists and is explicit (logged in audit).

## 3. RTSP surface (M3/M4)

| Method | Purpose |
|---|---|
| `OPTIONS` | capability probe (is it RTSP? which methods?) |
| `DESCRIBE` | SDP: codec, `sprop-parameter-sets`, `fmtp` |
| `SETUP` (RTP/AVP/TCP interleaved, or UDP) | transport selection |
| `PLAY` | start; `Range: npt=now-` |
| `TEARDOWN` | release server resources on every teardown path |

Auth: `Basic` and `Digest (MD5)`, attempted in that order per the server's `WWW-Authenticate`. Credentials come
from `ISecretStore`, are never placed in a URL, and never logged (the `Authorization` header is redacted).

URI builder contract (`RtspUriBuilder`, implemented M2): `rtsp://host:port/path?transport=tcp` with correct port
defaulting (554), no embedded credentials, and idempotent query composition. Tested for IPv6 literals and paths
with existing query strings.

## 4. Persistence contract (`Camzivo.Data`)

```csharp
public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct);
    Task<Device> UpsertAsync(Device device, CancellationToken ct);   // returns stored instance
    Task DeleteAsync(Guid id, CancellationToken ct);
}
```

Rules: all SQL parameterized (`AddWithValue`/named parameters only), no string concatenation of user data, every
method takes a `CancellationToken`, and all writes are transactional. Repository methods never touch secrets.

## 5. Error and telemetry envelope

Every surfaced error uses one shape:

```json
{
  "code": "RtspUnauthorized",
  "title": "Camera refused the stream credentials",
  "explanation": "The camera answered the RTSP request with 401 twice. The web login works but ONVIF/RTSP may use a different account.",
  "actions": ["Enable ONVIF in the camera's network settings", "Re-enter the device password", "Retry with TCP"],
  "correlationId": "c-7F3A...",
  "detail": "DESCRIBE rtsp://admin:***@192.168.18.11:554/... -> 401"
}
```

`detail` is always redacted. `correlationId` ties the UI message to log lines.

## 6. Versioning

- Internal contracts are source-compatible within a minor version; breaking changes bump the assembly minor and
  are recorded in `CHANGELOG`.
- Persisted JSON (`payload_json`, `profiles_json`, `config.json`) carries a `schema` integer; readers accept older
  schemas and upgrade on write.
- The database schema version is authoritative in `PRAGMA user_version`; the app refuses to open a database from a
  *newer* version rather than guessing.
