# Architecture Review — Camzivo 0.2.0

Scope: the three assemblies as built. The question is not "is this elegant" but "what will break or slow down when
the video engine, discovery and playback arrive".

---

## 1. Current structure

```
Camzivo.Core   (no dependencies)   layout, scheduler, queues, policy, device model,
                                  ONVIF discovery parser, diagnostics, URI building, redaction
Camzivo.Data   → Core              SQLite factory, migration runner, repositories, app paths
Camzivo.Desktop→ Core, Data        WPF shell, view models, theme tokens, AppRuntime composition root
```

Dependency direction is correct (UI → Data → Core, never back). Core has no I/O and is fully unit-testable — that
is why 106 of 155 tests cover it (and 10 more now cover the shell's view models).

## 2. Findings

### A1 — `MainViewModel` owns too much (High)

**Problem:** navigation, theme application, status text, telemetry timer, preference persistence, layout commands,
page lifetimes and the persistence banner all live in one 300-line class. It cannot be constructed without a WPF
`Application`, a `Dispatcher` and a live database, so it has **zero tests**.

**Proposed:** extract `ShellState` (selected section, theme, banner) and `LayoutCommands` into Core-testable
classes; keep `MainViewModel` as a thin binder. Introduce `IUiDispatcher` with a WPF implementation so timer-driven
updates can be tested with a fake clock.

**Benefit:** first automated coverage of the shell; removes the single largest regression risk.
**Perf:** none. **Migration:** mechanical, one class at a time. **Risk:** medium (XAML bindings) → covered by
snapshots + new tests.

### A2 — No interfaces at the seams that will change (High)

**Problem:** `AppRuntime` exposes concrete `DeviceRepository`, `PreferencesRepository`, `AuditRepository`. The
coming engine (`IVideoEngine`), discovery (`IDeviceDiscovery`) and clock have no abstraction, so they will be
newed inside view models — untestable and impossible to simulate.

**Proposed:** define the ports now, in Core: `IVideoEngine` (Open/Close/State/Statistics/FrameAge), `IClock`,
`IDeviceDiscovery` (async stream of candidates), `ISecretStore` (DPAPI). `AppRuntime` exposes interfaces; a
`SampleVideoEngine` replaces the `demo` boolean currently threaded through three view models.

**Benefit:** the demo/production split disappears; 16-stream logic becomes testable with a fake engine.
**Perf:** none. **Risk:** low (additive).

### A3 — `Refresh()` is a single mutation funnel (Medium)

**Problem:** one method recomputes tiles, selection, counts, summary, filter and command states. Called from five
places. Correct today; with streams it will be called on every state change and will thrash 64 VMs (BUG-04).

**Proposed:** split into `RebuildWall()` (layout change only), `UpdateTile(slot)` (state change) and
`UpdateChrome()` (counts/summary). Tiles become long-lived.

**Benefit:** removes allocation churn, makes per-tile state updates O(1). **Risk:** medium — needs tests on slot
identity.

### A4 — Demo branch is a constructor flag (Medium)

**Problem:** `bool demo` is passed into `LiveViewModel`, `DevicesViewModel`, `HealthViewModel`, `SettingsViewModel`,
and each branches. Every new feature multiplies the matrix.

**Proposed:** `ISampleData` provider + fake engine/services, selected once in `AppRuntime.Create(demo: true)`.

**Benefit:** one seam, no per-feature branching. **Risk:** low.

### A5 — Persistence failure handling is narrow (Medium)

**Problem:** `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
or SqliteException)` in `AppRuntime`, and `catch (SqliteException)` in `Persist`. An `ObjectDisposedException` or
`FormatException` from a corrupt row escapes to an unobserved task.

**Proposed:** a single `SafeExecute` helper that logs everything, rethrows nothing on background paths, and surfaces
only user-actionable failures.

### A6 — No single-instance guard (Low)

Two instances share one WAL database. Not corrupting (SQLite handles it) but UI state diverges and both write
preferences. **Proposed:** named mutex; second instance focuses the first and exits.

### A7 — Duplicated "now" and time formatting (Low)

`DateTime.Now`, `DateTimeOffset.UtcNow` and `"O"` formatting appear in repositories and view models. Inject
`IClock`; makes retention/pruning and audit timestamps testable.

## 3. Module boundaries (target)

| Concern | Owner | Must not |
|---|---|---|
| UI composition | `Camzivo.Desktop` | contain protocol logic |
| Shell state / commands | Core (`ShellState`, `LayoutCommands`) | touch WPF types |
| Device inventory | Data repositories + Core model | perform network I/O |
| Discovery / ONVIF | new `Camzivo.Devices` (Core-only XML logic + thin network adapter) | know about WPF |
| Stream scheduling | Core (exists) | allocate decoders |
| Decode / render | new `Camzivo.Video` (FFmpeg + D3D11) | reference Data or Desktop |
| Playback / export | new `Camzivo.Playback` | touch the UI thread |
| Security | Core (`ISecretStore` port) + Data (DPAPI impl) | log secrets |
| Diagnostics | Core (wording, counters) + `AppLog` | leak credentials |

Rationale for the new assemblies: they carry native interop and different lifetime rules; keeping them out of
`Camzivo.Core` preserves Core's "no I/O, 100 % testable" property, which is the project's main safety net.

## 4. Refactors explicitly rejected

- **Rewrite to MVVM toolkit / ReactiveUI** — no measured benefit; current `ObservableObject` is 40 lines.
- **Replace WPF with WinUI 3** — the compositing model for 64 video surfaces is *worse* here, and the shell already
  meets memory/thread targets.
- **Introduce DI container** — `AppRuntime` is 40 lines and explicit; a container would hide the graph.
- **Move to a background service for recording** — recording is device-side (direct-to-disk); a service is a
  permanent cost with no requirement behind it yet.

## 5. Sequencing

1. Ports + sample services (A2, A4) — unblocks everything.
2. `Refresh` split + tile identity (A3) — required before streams.
3. `AppLog` + `SafeExecute` + single instance (A5, A6) — reliability gate.
4. `ShellState` extraction (A1) — testability milestone.
5. Device assembly (M3), then video assembly (M4).

Each step ships with tests and keeps the build green; no step is allowed to change behaviour it does not own.
