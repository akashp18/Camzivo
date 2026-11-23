# Current Implementation Audit — Camzivo

**Audit date:** 2026-11-22 · **Build audited:** `0.2.0` (solution `Camzivo.sln`, product name migrating to Camzivo)
**Method:** every claim below was checked against source, a test run, a process measurement, or a rendered
screenshot. Nothing is marked implemented because a button or a table exists.

---

## 1. Actual technology stack (verified, not assumed)

| Layer | Reality | Evidence |
|---|---|---|
| Language / runtime | C# 12, .NET 8 (`net8.0`, `net8.0-windows`), nullable enabled, warnings as errors | `Directory.Build.props`, `src/*/*.csproj` |
| UI | WPF on Windows, single `MainWindow`, `DynamicResource` token swap for theme | `src/Camzivo.Desktop/Shell/MainWindow.xaml`, `App.xaml.cs` |
| Video | **None.** No decoder, no renderer, no FFmpeg binding, no D3D interop | no match for `FFmpeg`/`D3DImage` in `src` |
| Networking | **None.** No `Socket`, `UdpClient`, `HttpClient`, or RTSP client in the product | `grep` over `src` returns nothing |
| Persistence | SQLite via `Microsoft.Data.Sqlite`, WAL, FK on per connection, hand-rolled migration runner | `Camzivo.Data/Sqlite/*` |
| DI | None; `AppRuntime.Create()` is the composition root | `Camzivo.Desktop/Services/AppRuntime.cs` |
| Logging | **None.** `AppPaths.LogRoot` is created and never written to | no logger type exists |
| Tests | xUnit, 145 cases (106 core, 39 data), 0 UI, 0 network, 0 performance | `dotnet test` output |
| Packaging | `dotnet publish -r win-x64 --self-contained false -p:PublishReadyToRun=true` | `artifacts/publish` |
| Toolchain | Project-local SDK 8.0.428 in `tools/dotnet`, Node 22 in `tools/node22` | `global.json`, `tools/` |

The solution is **three assemblies**: `Camzivo.Core` (pure logic, 1,036 lines), `Camzivo.Data` (701 lines),
`Camzivo.Desktop` (13 C# files + 5 XAML, 2,386 lines). Total ~4,100 lines of source. There is no server, no
service, no background worker other than one `DispatcherTimer`.

## 2. What the product actually is today

A **shell and design system with a validated data/domain core**. It opens in ~2.4 s warm, shows a 1–64 tile
camera wall, persists theme/layout/last-section, and renders a truthful "no decoder" state. It cannot connect to a
camera. It cannot play or record video. Playback, Events and Recordings are static placeholder pages.

Stated plainly: this is an **Alpha-quality front end with Beta-quality foundations**, not a working VMS.

---

## 3. Feature-by-feature audit

Status vocabulary: **Verified** (end-to-end, tested/measured) · **Defects** (works but wrong) · **Partial** ·
**UI-only** · **Untested** · **Missing** · **Blocked** (needs hardware/protocol).

### 3.1 Shell and navigation

| Feature | Intended | Actual | Files / symbols | Deps | Tests | Defects | Perf | UX | Recommendation |
|---|---|---|---|---|---|---|---|---|---|
| Custom window chrome | Frameless window, drag, min/max/close | **Verified** — renders correctly in both themes | `MainWindow.xaml` `WindowChrome` | WPF | Visual only | none observed | negligible | — | keep |
| Nav rail, 7 sections | Switch page, remember selection | **Verified**; selection persisted to `preference` | `MainViewModel.Select`, `NavItemViewModel` | Data | none directly | back/forward absent | rebuilds page VM on each switch? no | fine | add keyboard section cycling (P2) |
| Command bar | Context actions | **UI-only** — "Add device" and "Reconnect all" are `IsEnabled=False` | `MainWindow.xaml` | — | none | dead affordances until M3 | — | acceptable if tooltip says why | enable in M3 |
| Theme switch | Dark/light swap at runtime | **Verified**; persisted; `DynamicResource` swap | `App.ApplyTheme`, `Tokens.*.xaml` | — | none | token dictionary lookup is by filename substring | one dictionary swap | — | keep |
| Layout presets 1–64 + Ctrl+1..8 | Re-tile wall | **Verified** by `LayoutGrid` tests + rendered 9/25 | `LiveViewModel.ApplyLayout`, `LayoutGrid` | Core | yes (policy) | no 2-up or 6-up preset | rebuilds all tiles | — | add 2-up/6-up (P2) |
| Fullscreen | Fullscreen the wall | **Verified** by `--probe-fullscreen`: borderless, maximised, title bar collapsed (row 40 → 0), caption 0, wall layout untouched | `MainWindow.ApplyFullScreen` | WPF | probe (not unit) | visual coverage of a taskbar not checked | — | good | keep |
| Status bar telemetry | MEM/threads/clock | **Partial** — `MEM {n} MB · {n} THR · clock`; no CPU, no GPU, no stream stats | `MainViewModel.UpdateTelemetry` | — | none | allocates a `Process` each second | ~µs | noise for operators; useful for dev | move behind Settings → Diagnostics (P2) |
| Banner for persistence failure | Explain degraded mode | **Verified** — set when `HasPersistence == false` | `MainViewModel` ctor | Data | none | only covers open failure, not runtime failures | — | good | extend to engine failures |

### 3.2 Camera wall

| Feature | Intended | Actual | Symbols | Tests | Status |
|---|---|---|---|---|---|
| Grid geometry 1/4/9/16/25/36/49/64 | Compute rows/cols | Correct integer geometry | `LayoutGrid.FromPreset` | 6 cases | **Verified** |
| Click-to-place | First empty slot, else selected | Implemented via `SlotAssignment` | `LiveViewModel.Assign` | 4 cases | **Verified** |
| Clear slot | Empty the selected tile | Implemented, `CanExecute` gated | `ClearSelected` | rule tested via `SlotAssignment` | **Verified** (rule only; never clicked by a human) |
| Selection model | One selected tile | Accent border, drives replacement target | `SelectedSlot` | none | **Verified** visually |
| Decode budget | Suspended tiles beyond 16 | `StreamScheduler` marks them `QUEUED` | `StreamScheduler.Plan` | 5 cases | **Partial** — no engine consumes the plan, and demo has 9 cameras so `QUEUED` never renders |
| Stream quality pick | Main vs sub by tile size | `StreamSelectionPolicy` returns MAIN/SUB | `StreamSelectionPolicy` | 5 cases | **Partial** — no ONVIF profiles, no manual override |
| Placement persistence | Restore wall after restart | **Missing** — scheme is in memory only | — | — | **Missing** (BUG-03) |
| Tile reuse | Update in place | **Verified** — one VM per slot, mutated in place; `SyncTiles` only resizes | `LiveViewModel.Refresh` | `TileViewModelsSurviveARefresh…` | **Fixed** (BUG-04) |
| Aspect ratio | Letterbox, never stretch | `Stretch=Uniform` on the surface host | `MainWindow.xaml` | visual | **Verified** (still image) |
| Camera list search | Filter by name/group | Works, but clears the list on every refresh, losing selection | `ApplyFilter` | none | **Defects** (BUG-05) |
| Empty state | Guide the operator | `CLICK A CAMERA TO ASSIGN` per tile | `EmptySlotHint` | — | **Verified** |
| Video rendering | Decode + paint | none | — | — | **Missing** (M4) |

### 3.3 Devices

| Feature | Actual | Status |
|---|---|---|
| Device model + schema (host, ports, TLS, vendor, model, secret reference) | `Device`, `V001_InitialSchema` | **Verified** (schema + repository tests) |
| Repository CRUD | Parameterized upsert/get/delete | **Verified** (round-trip tests) |
| Devices page | Demo table + counters | **UI-only** — no add/edit/delete, no network |
| ONVIF discovery | `WsDiscoveryParser` parses a device response only | **Partial** — no UDP probe, no match/scope filtering, no dedupe |
| ONVIF device/media/PTZ services | none | **Missing** |
| RTSP URI building + redaction | `RtspUriBuilder`, `CredentialRedactor` | **Verified** (unit) |
| Connection diagnostics wording | 9 failure classes with operator actions | **Verified** (unit) — but no engine emits them |
| Credential storage | `secret_ref` column, `secrets/` folder | **Partial** — **no DPAPI implementation exists** |
| Duplicate detection | none | **Missing** |

### 3.4 Playback, Events, Recordings & Exports

All three are `PlaceholderViewModel` instances with hardcoded copy. They have **no** data source, timeline,
calendar, query, export, or file handling. Status: **UI-only placeholder**. This is the largest product gap and is
tracked honestly in the roadmap, not disguised as partial.

### 3.5 Health

Diagnostics render from `ConnectionDiagnostics` for three synthetic cameras. Severity colouring exists. There is
no measurement source: no CPU/GPU sampling, no stream statistics, no reconnect counters, no log viewer, no
diagnostic bundle export. **Partial (presentation only)**.

### 3.6 Settings

Real page: appearance, storage paths, decode budget, credential policy, version. Shown paths come from
`AppPaths`; the theme button is live; storage paths are read-only. **Verified** for content; no editable
preferences beyond theme. Demo mode states that nothing is written.

### 3.7 Persistence and upgrades

| Item | Status |
|---|---|
| Migration runner, transactional per step, `user_version` | **Verified** (7 tests incl. newer-schema refusal) |
| Backup only when pending | **Verified**; pruning on every open, newest 5 kept (8 tests) |
| Preference store (get/set/get-all) | **Verified** (4 tests) |
| Upgrade from a renamed profile (`%APPDATA%\Camzivo` → `Camzivo`) | **Missing** — will be added with the rename (BUG-06) |
| Crash-safe write patterns | **Partial** — WAL, but no graceful shutdown flush; preference writes are fire-and-forget |

### 3.8 Reliability and observability

| Item | Status |
|---|---|
| Global exception handlers (`DispatcherUnhandledException`, `AppDomain`, `TaskScheduler`) | **Implemented** — the UI exception is logged and the process is allowed to terminate (no swallowing); unobserved task exceptions are logged and observed |
| Application log file | **Implemented** — rotating 5 × 2 MB under `%LOCALAPPDATA%\Camzivo\logs`, redacted, with a crash marker |
| Unobserved task exceptions | **Implemented** — `TaskScheduler.UnobservedTaskException` logs; `Persist`/`InitializeAsync` no longer discard failures silently |
| Crash recovery / restart | **Missing** |
| Single-instance behaviour | **Missing** — two instances share one SQLite file (WAL tolerates it, but UI state diverges) |

---

## 4. Verification evidence collected for this audit

| Check | Result |
|---|---|
| `dotnet build Camzivo.sln -warnaserror` | succeeded, 0 warnings |
| `dotnet test Camzivo.sln` | 155 passed, 0 failed (106 core + 39 data + 10 view-model) |
| Warm startup, published R2R, 3 runs | 2,867 / 4,562 / 3,864 ms (median 3,864 ms this session; earlier session median 2,433 ms) |
| Working set at idle (snapshots) | 120–124 MB, 13 threads |
| Profile backups after fix | 20 → 5 verified on the real `%APPDATA%` profile |
| Rendered screens | Live (dark/light), 25-tile, Settings, Health |

## 5. Structural findings

1. **`MainViewModel` is a god object**: it owns navigation, theme, status, telemetry, persistence, layout commands
   and page lifetimes. It is untestable because it needs a `Dispatcher`, a `Window` and a live database.
2. **No interfaces at the seams that will change**: `AppRuntime` exposes concrete repositories; there is no
   `IVideoEngine`, `IClock`, `IDeviceDiscovery`. The M4 engine will otherwise be wired directly into the view
   model.
3. **UI state is derived in one big `Refresh()`** that mutates tiles, counts, selection and the filtered list, and
   is called from five places. Correct today, a regression magnet once streams arrive.
4. **The demo/production split is a constructor flag** threaded through three view models. It works, but it makes
   every new feature carry a demo branch; a `SampleData` provider behind the same interfaces would remove it.

## 6. Definition of done for the current milestone

The shell milestone is done when: build clean, tests green, theme/layout/section restored, no false "LIVE"
claims, and every dead control either disabled with a reason or removed. All but the last item are true today.
