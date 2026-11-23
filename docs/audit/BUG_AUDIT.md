# Bug Audit — Camzivo 0.2.0

Every entry below is either **verified by a test/reproduction** or marked **code-confirmed** (found by reading the
code and reasoned from a concrete execution path). Nothing is listed as a bug on suspicion alone; the two
categories are kept separate on purpose.

Severity: **S1** crash/data loss · **S2** wrong behaviour visible to an operator · **S3** degradation · **S4** polish.

---

## Fixed before this audit (regression-protected, from the previous review round)

| ID | Severity | Bug | Root cause | Fix | Regression test |
|---|---|---|---|---|---|
| B-01 | S3 | Database copied on every launch; 20 stale copies found in the real profile | Backup ran before the "is anything pending" check | Backup only when pending, prune newest 5 on every open | `MigrationSafetyTests` (8) |
| B-02 | S2 | A newer-schema database was opened silently | `user_version` read but never compared to the build's maximum | Refuse with a message, leave the file untouched | `RefusesADatabaseFromANewerBuild` |
| B-03 | S2 | A hidden duplicate tile could suspend the visible tile for the same camera | Dedupe ran before visibility/priority ordering | Order, then dedupe | `AHiddenDuplicateMustNotShadow…` |
| B-04 | S2 | `rtsp://user@host` leaked the account name into text | Redaction required a `:` password | Optional password group + evaluator | `RedactsUserInfoWithoutPassword` |
| B-05 | S2 | Demo tiles claimed LIVE while the status bar said "no decoder" | Demo path reused the live status | `TileStatus.Preview` | visual + snapshot |
| B-06 | S2 | Empty tiles advertised drag-and-drop that did not exist | Copy promised a future interaction | Click-to-place + honest label | `SlotAssignmentTests` |
| B-07 | S2 | Demo wall was empty | Async init cleared the camera list after ctor placement | Demo goes through `LoadCameras` | snapshot |
| B-08 | S4 | Header showed a raw enum (`Nine`) | `Preset` interpolated | friendly `R × C` label | visual |
| B-09 | S2 | Settings rows overlapped | Two `Grid` children in one cell with wrapping text | stacked rows, stretch layout | visual |

---

## Open defects found in this audit

### BUG-01 — "Fullscreen" does not fullscreen · S2 · **code-confirmed**

- **Repro:** press `Ctrl+F` or run the fullscreen command on Live View.
- **Expected:** window enters borderless fullscreen (or maximised) and video area takes the whole monitor.
- **Actual:** the wall switches to a 1-tile *layout*; the window keeps its size, title bar and margins.
- **Root cause:** `MainViewModel.ToggleFullScreenCommand` calls `Live.ApplyLayout(1|9)`. There is no
  `WindowState`/`WindowStyle` interaction anywhere in the codebase.
- **Files:** `Camzivo.Desktop/ViewModels/MainViewModel.cs`.
- **Fix:** command raises `FullScreenRequested`; `MainWindow` toggles `WindowStyle=None` + `WindowState=Maximized`,
  restoring the previous state on exit, `Esc` and `F11`; layout is left alone (a fullscreen wall may still be
  1-up). View model keeps a testable `IsFullScreen` flag.
- **Verification:** `--probe-fullscreen` drives the real transition and records the window state. Measured output:
  `before: style=None state=Normal resize=CanResize titleBar=Visible titleRow=40 caption=40 vmIsFullScreen=False` →
  `fullscreen: state=Maximized resize=NoResize titleBar=Collapsed titleRow=0 caption=0 vmIsFullScreen=True` →
  `restored: state=Normal resize=CanResize titleBar=Visible titleRow=40 caption=40 vmIsFullScreen=False`.
  Not verified: whether the maximised borderless window visually covers the taskbar on a multi-monitor setup (needs
  a human on the machine).
- **Regression risk:** low; keyboard handling is local to the window.

### BUG-02 — No global exception handling · S1 · **code-confirmed**

- **Repro:** any unhandled exception on the UI thread (e.g. a future binding converter failure or an IO error
  outside the narrow `when` filters) terminates the process.
- **Actual:** WPF shows the default crash dialog and the process exits; no log, no state, nothing to diagnose.
- **Root cause:** `App.OnStartup` registers no `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException`
  or `TaskScheduler.UnobservedTaskException`; there is no logger at all.
- **Fix:** single `AppLog` facility: synchronous append-only log under `%LOCALAPPDATA%\Camzivo\logs`, all three
  handlers installed, and a `last-crash.txt` marker read at next start to show a non-blocking banner. An unexpected
  **UI** exception is logged and then **not** marked handled — the process terminates instead of continuing in an
  unknown state (an earlier version of this fix swallowed it, which hid real faults).
- **Note:** because the process terminates, pending preference writes queued at that instant are lost; the log line
  and crash marker are already on disk because `AppLog` writes synchronously.
- **Tests:** log writer writes and rotates; redaction applies to log lines; crash marker read/cleared.
- **Regression risk:** low. Never swallow: the log must contain the exception.

### BUG-03 — Wall placement is not persisted · S2 · **code-confirmed**

- **Repro:** place cameras on specific tiles, close, reopen.
- **Actual:** cameras are re-placed in arbitrary order at startup; the operator's arrangement is gone.
- **Root cause:** only `ui.theme`, `ui.layout`, `ui.section` are stored; `_slotAssignments` is ctor state.
- **Fix:** serialise the slot array as a compact, versioned JSON document under `ui.wall`, restore after cameras
  load, ignore unknown/moved camera ids, fall back to auto-fill. Codec lives in Core and is unit-tested.
- **Regression risk:** medium (touches startup ordering) — covered by codec tests plus a snapshot.

### BUG-04 — Whole wall rebuilt on every change · S3 · **fixed**

- **Repro:** switch layout or place a camera while watching 64 tiles; observe GC and visual re-template.
- **Actual:** `Refresh()` clears `ObservableCollection<Tile>` and constructs up to 64 new view models, so the
  `ItemsControl` rebuilds every tile container, drops any focus and re-runs templates.
- **Root cause:** state is recomputed by wholesale recreation instead of reconciliation.
- **Fix (done):** one VM per slot for the wall's lifetime, mutated via `Assign`/`Clear`/`Update`; `SyncTiles`
  only adds or removes when the slot count changes.
- **Test:** `TileViewModelsSurviveARefreshSoTheWallIsNotRebuilt` asserts reference identity across selection,
  placement and a same-size layout change.
- **Regression risk:** medium; tile mutation paths raise `Name`, `Status`, `QualityChip`, `QualityHint`.

### BUG-05 — Camera list selection is destroyed by every refresh · S3 · **code-confirmed**

- **Repro:** select a camera in the tree with the keyboard, press Enter to place it.
- **Actual:** `ApplyFilter()` clears and re-adds every item, so the list loses focus/selection; arrow-key
  placement becomes impossible.
- **Root cause:** `ObservableCollection.Clear()` treats an unchanged result set as a full reset.
- **Fix:** reconcile (remove/insert only real differences, keep order), so an unchanged filter is a no-op.
- **Tests:** filtered collection identity is stable when the camera set does not change.

### BUG-06 — Renaming the product would orphan the profile · S2 · **code-confirmed (about to become real)**

- **Repro:** install build A (writes `%APPDATA%\Camzivo`), install build B that uses `%APPDATA%\Camzivo`.
- **Actual:** B starts empty: preferences, device inventory and audit trail appear lost (the old folder is simply
  ignored).
- **Fix:** `AppPaths` detects the legacy directory and performs a one-time move (copy-then-verify-then-delete,
  never a blind delete); upgrades the database file name; logs the migration. Tested with a temporary root.
- **Regression risk:** medium — file moves; the test covers an existing destination and a missing source.

### BUG-07 — Preference writes race and can persist a stale value · S2 · **code-confirmed**

- **Repro:** press `Ctrl+1` … `Ctrl+8` quickly on a slow disk.
- **Actual:** each change starts an independent `Task.Run` with its own connection; completion order is not the
  input order, so `ui.layout` can end up as an intermediate value.
- **Root cause:** fire-and-forget writes with no serialisation or coalescing.
- **Fix:** one background writer with a latest-wins queue per key; awaited at shutdown with a short timeout;
  failures logged once, not per keystroke.
- **Tests:** enqueue 100 values for one key, drain, assert last-value-wins; a failing writer does not throw into
  the caller.

### BUG-08 — `AppPaths` ignores injected roots for logs and exports · S3 · **code-confirmed**

- **Repro:** `new AppPaths(configRoot: tmp, cacheRoot: tmp)` still points `LogRoot` at the real profile.
- **Root cause:** `LogRoot`/`ExportRoot` are built from `Environment.SpecialFolder` and ignore the ctor arguments.
- **Fix:** derive both from `configRoot`/`cacheRoot` when supplied; keep defaults identical.
- **Tests:** injected roots produce paths under the injected root.
- **Regression risk:** low.

### BUG-09 — `HasCameras` never raises change notification · S4 · **code-confirmed**

- **Detail:** `LiveViewModel.HasCameras` is computed from `_cameras.Count` but `Refresh`/`LoadCameras` never call
  `Raise(nameof(HasCameras))`. It is not currently bound in XAML, so nothing breaks today; it is a trap for the
  first empty-state binding.
- **Fix:** raise it, or delete the property. Chosen: raise it and bind the camera-tree empty state to it.

### BUG-10 — Tile area was measured against a fictional frame · S4 · **resolved**

- **Detail:** in `Refresh`, `tileWidth/tileHeight` are computed from a hardcoded 1920×1080 reference frame, so on a
  4K wall the "SUB for small tiles" threshold is evaluated against fictional pixels. The policy decision is
  therefore monitor-independent, which is defensible, but undocumented and untestable as "current tile size".
- **Fix:** renamed to `referenceTileWidth`/`referenceTileHeight` with the reason in the code: the substream
  decision is deliberately monitor-independent and testable; real pixels replace it when the renderer exists.

### BUG-11 — Startup can block on a slow profile before the window is shown · S3 · **code-confirmed**

- **Detail:** `AppRuntime.Create()` runs migrations and opens SQLite on the UI thread, before the window exists.
  On a roaming profile or virus-scanned disk this delays first paint (observed cold start 50.4 s once, AV scan).
- **Fix:** keep DB work synchronous but bounded (it is small), add a startup phase trace, and show the window
  before device reads (`InitializeAsync` already runs after `Show`). Move discovery/warm-up off the UI thread in
  M3. Track cold/warm separately in the benchmark doc.

### BUG-12 — Status bar allocates a `Process` object every second · S4 · **code-confirmed**

- **Detail:** `UpdateTelemetry` calls `Process.GetCurrentProcess()` on a 1 s timer.
- **Fix:** cache the `Process` instance (it is disposable once), read `WorkingSet64` and thread count from it.

---

### BUG-13 — Production tiles claimed LIVE with no engine · S2 · **fixed**

- **Found by:** writing the first desktop tests. `LiveViewModel` returned `TileStatus.Live` for every placed tile
  whenever the app was not in demo mode — the demo path had been corrected earlier, the real path had not.
- **Impact:** a real install would have shown a green LIVE lamp over a tile with no decoder, exactly the
  contradiction the product promises never to show.
- **Fix:** added `TileStatus.Waiting` (text `WAITING`, detail `VIDEO ENGINE NOT STARTED`); only a decoded frame
  may produce `Live`.
- **Tests:** `CamerasArePlacedInOrderAndNeverClaimToBeLiveWithoutAnEngine`, `DemoContentIsLabelledPreviewRatherThanLive`.

### BUG-14 — Wall shortcuts would have fired inside the search box · S3 · **prevented**

- **Detail:** `Delete` and `Ctrl+Z` were about to be bound at window scope, which would clear a tile or undo the
  wall while the operator typed in the camera search field.
- **Fix:** both are handled in `MainWindow.OnPreviewKeyDown` with a focused-`TextBox` guard; `F11` is handled once
  (the duplicate XAML binding was removed so the toggle cannot run twice).
- **Status:** no automated window-level test exists (no UI harness); the guard is code-reviewed, and the rule is
  recorded in `docs/design/INTERACTION_PATTERNS.md` §4.

## Video / device bug classes that cannot be confirmed yet

These are the categories the brief asks about. They are **blocked, not absent**: there is no decoder or network
path in the product, so black tiles, frozen frames, A/V sync, GPU leaks, reconnection loops and codec failures
have no execution path. The engine milestone inherits them as acceptance criteria:

- black tile on first frame (surface never presented) — acceptance: first frame ≤ 1.5 s local, 3 s WAN;
- frozen frame without status change — watchdog on frame age, must flip tile to `RECONNECTING` within 2 s;
- stream switch leaving the old surface bound — teardown order test;
- decoder init failure on unsupported profile (H.265 Main10) — must surface `UnsupportedCodec`, never a black tile;
- GPU memory growth on repeated open/close — 100-cycle soak with adapter memory bound;
- reconnect storm — exponential backoff with jitter, capped, visible attempt counter.

## Test coverage of the defect surface

| Area | Cases | Notes |
|---|---|---|
| Migration/backup/refusal | 8 | includes real-profile pruning verified |
| Device repository | 6 | round trip, delete, ordering |
| CRUD + audit repository | 5 | |
| Preferences | 4 | |
| Scheduler/queue/policy/URI/redaction/diagnostics/layout/slot | 99 total core | pure logic only |
| UI (WPF) | **0** | no automation harness yet |
| Network/RTSP/ONVIF | **0** | no client yet |
| Performance/soak | **0** | only manual startup probes |

## Verification log for this pass

| Fix | Test | Result |
|---|---|---|
| BUG-01 fullscreen | `--probe-fullscreen` (real window state) | verified: maximised, chrome collapsed, restored |
| BUG-02 crash honesty | `AppLogTests` + handler inspection | log/marker tested; termination is WPF behaviour, not unit-tested |
| BUG-02 crash logging | `AppLogTests` (write, redact, rotate, crash marker) | added, passing |
| BUG-03 wall persistence | `WallLayoutCodecTests` | added, passing |
| BUG-05 list reset | `CameraListReconciliationTests` | added, passing |
| BUG-06 profile rename | `AppPathsMigrationTests` | added, passing |
| BUG-07 write ordering | `PreferenceWriterTests` | added, passing |
| BUG-08 injected roots | `AppPathsTests` | added, passing |
| BUG-12 telemetry allocation | code change, inspected | passing build |
| BUG-13 LIVE without engine | `CamerasArePlacedInOrderAndNeverClaimToBeLiveWithoutAnEngine` | passing |
| BUG-04 tile reuse | `TileViewModelsSurviveARefreshSoTheWallIsNotRebuilt` | passing |
| BUG-14 shortcut focus guard | no window harness | code-reviewed, recorded as a rule |
