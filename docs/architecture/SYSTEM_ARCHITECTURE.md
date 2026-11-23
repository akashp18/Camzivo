# System Architecture

Three assemblies, one direction of dependency. Everything video-heavy sits behind interfaces so the shell can be
replaced (WPF today, WinUI 3 later) without touching device or persistence logic.

```
Camzivo.Desktop (WPF, net8.0-windows)
   views, view-models, theme tokens, input, composition host
        |
        v
Camzivo.Data (net8.0)
   SQLite, migrations, repositories, audit log
        |
        v
Camzivo.Core (net8.0, zero UI dependencies, zero I/O except abstractions)
   domain model, layout geometry, stream policy, scheduler, ONVIF/RTSP protocol logic,
   diagnostics, redaction, interfaces for device/stream/credential services
```

Dependency rule: `Core` references nothing; `Data` references `Core`; `Desktop` references both. Any temptation to
put a WPF type in `Data`, or a `SqliteConnection` in `Core`, is a review failure.

## 1. Assemblies and folders (as built)

```
src/Camzivo.Core/
  Devices/            Camera, Device, DeviceGroup, DeviceCapabilities, CapabilityState
  Devices/Onvif/      WsDiscoveryParser, OnvifScopes, SoapEnvelope (M3)
  Streaming/          BoundedFrameQueue, StreamSelectionPolicy, StreamScheduler,
                      StreamState, StreamRequest, IStreamEngine, FrameMetrics
  Diagnostics/        StreamFailure, ConnectionDiagnostics, HealthSnapshot
  Security/           CredentialRedactor, ISecretStore, IAuditLog
  Layout/             LayoutPreset, LayoutGrid, Workspace
  Abstractions/       IClock, IDeviceRepository (contract only)

src/Camzivo.Data/
  Sqlite/SqliteConnectionFactory, MigrationRunner, Migrations/V001_InitialSchema
  Repositories/       DeviceRepository, CameraRepository, PreferencesRepository, AuditRepository
  Storage/            AppPaths (per-user config/cache/log roots)

src/Camzivo.Desktop/
  App.xaml(.cs)       startup, theme application, snapshot mode
  Shell/              MainWindow, NavRail, CommandBar, StatusBar
  Views/              LiveView, DevicesView, PlaybackView, EventsView, ExportsView, HealthView, SettingsView
  ViewModels/         MainViewModel, LiveViewModel, CameraTileViewModel, ...
  Themes/             Tokens.Dark.xaml, Tokens.Light.xaml, Controls.xaml
  Rendering/          (M4) D3D11 composition host, D3DImage presenter
```

## 2. Layering of runtime concerns

| Layer | Owns | Never owns |
|---|---|---|
| Shell (UI thread) | visual tree, input, view-model state, commands | sockets, decoders, file copies |
| Application services | orchestration, cancellation tokens, retry policy | rendering, SQL text |
| Device services | ONVIF/RTSP session, capability negotiation | UI state |
| Stream engine (M4) | demux, decode, surface pool, pacing | knowledge of tiles |
| Renderer (M4) | D3D11 device, shaders, composition | knowledge of cameras |
| Persistence | schema, migrations, transactions | secrets in plaintext |

## 3. Threading model

- **UI thread**: WPF dispatcher only. Every command is `async` and marshals results back via `Dispatcher`.
- **Device/network**: `HttpClient`/`TcpClient` async; one `SemaphoreSlim` per device to avoid connection storms.
- **Discovery**: UDP multicast listener on a dedicated long-running task with its own cancellation, results
  pushed through `Channel<DiscoveredDevice>` to the UI.
- **Decode (M4)**: fixed pool of `N` decoder workers (`N = clamp(2, logicalCores-1, 8)`), each owning at most one
  active decode loop; per-stream `CancellationTokenSource`; frames into a bounded queue per stream.
- **Render (M4)**: one render thread at display refresh; pulls the newest frame per visible tile; never waits on
  a decoder.
- **Exports (M5)**: separate worker with its own bounded queue; progress published via `IProgress<T>`.

Rule of thumb enforced by code review and a dispatcher watchdog: no operation that can exceed 16 ms runs inline.

## 4. Key contracts

```csharp
// Streaming
public interface IStreamEngine : IAsyncDisposable
{
    Task<StreamSession> OpenAsync(StreamRequest request, CancellationToken ct);
    event EventHandler<StreamFault>? Faulted;
}

public sealed record StreamRequest(
    Guid CameraId, StreamQuality Quality, RtspTransport Transport,
    TimeSpan ConnectTimeout, int MaxQueueDepth);

// Devices
public interface IDeviceProbe
{
    Task<ProbeResult> ProbeAsync(DeviceEndpoint endpoint, CancellationToken ct);
}

// Security
public interface ISecretStore
{
    ValueTask<string?> ReadAsync(string key, CancellationToken ct);
    ValueTask WriteAsync(string key, string secret, CancellationToken ct);
    ValueTask DeleteAsync(string key, CancellationToken ct);
}

// Time (testability)
public interface IClock { DateTimeOffset UtcNow { get; } }
```

`StreamRequest` carries timeouts and queue depth so policy is data, not hardcoded constants — this is what makes
the failure tests in `testing/TEST_STRATEGY.md` deterministic.

## 5. A frame's journey (target, M4)

```
RTSP/TCP ── FFmpeg av_read_frame (async read loop, per stream)
        ── avcodec_send_packet → D3D11VA surface (GPU, no system-memory copy)
        ── texture handle → tile slot in the compositor's frame table
        ── render thread: one D3D11 device draws N quads (NV12→RGB shader), letterbox
        ── single backbuffer present → D3DImage → WPF visual
        ── stats: received/decoded/presented/dropped, queue depth, decode time
```

Nothing on the UI thread touches a pixel. Off-screen or collapsed tiles are scheduled at 0 fps (scheduler) and
their surfaces are returned to the pool; a tile that becomes visible requests the policy-selected quality again.

## 6. Failure isolation

- Each stream lives in its own supervised scope: exceptions become `StreamFault` events with a classified
  `StreamFailure` and never propagate to the shell.
- Each device probe is independent; a hung NVR cannot block discovery results from other devices (per-device
  timeout, not per-batch).
- Renderer device-loss (`DXGI_ERROR_DEVICE_REMOVED`) tears down and recreates the device, re-attaching live
  textures; if recreation fails twice, the app switches to software composition and states why in Health.
- Migration failure rolls back the transaction, restores the pre-migration backup, and reports the failing
  migration id — the app opens read-only rather than on a half-migrated schema.

## 7. Observability

- Structured JSON logs in `%LOCALAPPDATA%\Camzivo\logs`, 7-day retention, 10 MB × 5 rolling files.
- Counters per stream: `frames_received`, `frames_decoded`, `frames_presented`, `frames_dropped`,
  `reconnects`, `last_error`.
- `HealthSnapshot` is a plain immutable record built at 1 Hz from counters — the Health page and the status bar
  read the same snapshot, so the numbers can never disagree.
- Sensitive data path: every log statement passes through `CredentialRedactor.Sanitize` in the logging wrapper
  (unit-tested against `rtsp://user:pass@host`, `?password=`, `Authorization:`).

## 8. Configuration and state

| Kind | Location | Format |
|---|---|---|
| User config | `%APPDATA%\Camzivo\config.json` | JSON, schema-versioned, atomic write (temp + replace) |
| Database | `%APPDATA%\Camzivo\camzivo.db` | SQLite, WAL, `user_version` migration |
| Secrets | Windows DPAPI, per-user scope | binary blobs in `%APPDATA%\Camzivo\secrets\<id>.bin` |
| Cache (snapshots, thumbs) | `%LOCALAPPDATA%\Camzivo\cache` | disposable, may be cleared |
| Logs | `%LOCALAPPDATA%\Camzivo\logs` | JSON lines |
| Exports | user-chosen folder, default `%USERPROFILE%\Videos\Camzivo` | MP4 + JSON sidecar |

No file is ever written under `Program Files`. This is the direct correction of the incumbent's worst
architectural decision.

## 9. Extensibility without plugins

Vendor-specific behaviour is expressed as data + small strategies, not loaded assemblies:

- `DeviceCapabilities` describes what a device can do (negotiated, persisted, versioned by schema).
- `IVendorQuirk` implementations are compiled in and selected by capability fingerprint (e.g. Dahua HTTP
  `/cgi-bin/snapshot.cgi` when ONVIF snapshot is missing).
- No runtime plugin loading: it is a security surface with no current payoff.

## 10. Development topology

- `dotnet build` / `dotnet test` / `dotnet publish` from the repo root; pinned SDK via `global.json`.
- Tests: `tests/Camzivo.Core.Tests`, `tests/Camzivo.Data.Tests` (xUnit, no device or network required).
- `dotnet run --project src/Camzivo.Desktop -- --snapshot out.png` renders the shell headlessly to a PNG for design
  review and CI visual regressions — the same path used to verify this milestone.
