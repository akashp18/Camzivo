# SmartPSS Lite Weakness Analysis

Every finding below is either **measured on this machine** or **observed in the installed files**. Where a claim could
not be measured, it is marked as such and no number is given. Measurements are single-run samples, not a benchmark
suite: treat them as orders of magnitude, not as statistically robust figures.

Environment: the same Windows desktop that runs Camzivo. SmartPSS Lite was not modified; inspection was read-only
(process counters, shortcut resolution, directory scan, configuration file review).

## Measured baseline

| Metric | SmartPSS Lite 1.003.0000006.0 | How it was measured |
|---|---|---|
| Install size | 383 MB | `du -sh` over `C:\Program Files\SmartPSSLite` |
| File count | 11,339 | `find -type f \| wc -l` |
| Process count at idle | 3 (`SmartPSSLite.exe`, `DeviceWeb.exe`, `DSMessageNotify.exe`) | Process list while the app was open |
| Working set at idle | ~160 MB | `WorkingSet64` of the tree |
| Thread count at idle | 182 | Sum of `Threads.Count` |
| Startup to interactive | **8,693 ms** | Stopwatch from process start to first responsive window |
| Camzivo startup (reference) | 1,315–1,803 ms warm (median ~1,777 ms) | Existing startup measurements, same machine |

For scale: Camzivo's start is roughly **one fifth** of SmartPSS Lite's on identical hardware, from a single process.

## W1 — Slow cold start (measured)

- **Behavior:** ~8.7 s from launch to a usable window; the splash covers most of it.
- **Reproduction:** close the app fully (including `DSMessageNotify.exe`), start from the Start Menu shortcut, time
  until the device list is clickable.
- **Impact:** an operator opening the app during an incident waits through the whole splash.
- **Baseline:** 8,693 ms.
- **Proposed improvement:** Camzivo keeps startup work to: open SQLite, read one preference row set, build the wall.
  Device I/O is lazy and off the UI thread. Target: interactive under 1.5 s warm, under 2.5 s cold.
- **Expected benefit:** ~5–7 s saved per launch.
- **Complexity:** low (already achieved); the risk is regression, so startup stays a tested budget.

## W2 — Process and thread sprawl (measured)

- **Behavior:** three processes and 182 threads before a single stream is opened; `DSMessageNotify.exe` is a separate
  notification daemon.
- **Reproduction:** launch, open Task Manager / `Get-Process`, do not add a device.
- **Impact:** more memory, more failure modes, more places for a crash to leave state behind.
- **Baseline:** 3 processes / 182 threads / ~160 MB.
- **Proposed improvement:** one process, a bounded worker pool (one decode worker per active stream, capped), no
  always-on daemon.
- **Expected benefit:** simpler shutdown, lower idle footprint, no orphan process after a crash.
- **Complexity:** architectural, already the Camzivo design.

## W3 — Credentials stored in a plaintext configuration file (observed)

- **Behavior:** the installed tree contains `FtpConfig.ini` **and** `GetUsersName.txt`; the FTP configuration holds a
  literal password (`admin123`) next to the FTP account name, readable by any local user with file access.
- **Reproduction:** inspect `C:\Program Files\SmartPSSLite\FtpConfig.ini` (do not modify it).
- **Impact:** a shared workstation or a copied install directory leaks a credential.
- **Baseline:** plaintext in a world-readable install path.
- **Proposed improvement:** Camzivo never writes a device password to disk in readable form: DPAPI ciphertext under
  `%LOCALAPPDATA%\Camzivo\...\secrets`, reference only in SQLite (implemented, 6 tests).
- **Expected benefit:** a stolen database or backup yields no usable credential.
- **Complexity:** done.

## W4 — Installer/updater needs elevated write access to its own directory (observed)

- **Behavior:** `GrantF.bat` widens ACLs on the install tree; the updater (`Upgrade/`, 33 MB) writes inside
  `C:\Program Files`. State therefore lives in a location that normally requires elevation.
- **Reproduction:** read `GrantF.bat`; note the updater directory and its contents.
- **Impact:** installs and updates are privileged operations; a failed update can leave the tree inconsistent;
  per-user settings are mixed with program files.
- **Baseline:** program files and mutable state share one directory.
- **Proposed improvement:** Camzivo separates them: binaries in Program Files (or portable), all state under
  `%LOCALAPPDATA%\Camzivo` (`AppPaths` with injected roots, tested), no elevation after install.
- **Expected benefit:** no-admin updates, clean uninstall, predictable backup scope.
- **Complexity:** done.

## W5 — Module sprawl in navigation (observed)

- **Behavior:** 37 MB of `Solutions/`, 46 MB of `Languages/`, 52 MB of `Skin/`; the left bar mixes video, access
  control, attendance and intercom, each with its own guide and settings tree.
- **Reproduction:** launch, walk the left navigation; count distinct configuration areas before reaching playback.
- **Impact:** a video operator sees concepts they never use; the common tasks are not the shortest path.
- **Baseline:** ~10 top-level areas, four product categories in one shell.
- **Proposed improvement:** Camzivo exposes four sections only — Live, Playback, Devices, Health — with device
  settings inside the device, not in a separate module.
- **Expected benefit:** fewer navigation decisions; the two frequent tasks (see a camera, see a recording) are one
  click from the shell.
- **Complexity:** low (shell already exists).

## W6 — Fixed event-center split sizes (documented)

- **Behavior:** the event center supports only 1, 4 and 9 splits (user manual, section 8.2). Live view supports
  arbitrary splits up to 64; the event view does not.
- **Reproduction:** open the event center and try to select a 16-way layout.
- **Impact:** an operator watching 16 event-linked cameras cannot see them together in the event context.
- **Baseline:** 3 layouts in the event center vs "arbitrary" in live view.
- **Proposed improvement:** Camzivo uses one wall component everywhere, so any layout (1–64) is valid in live,
  playback and event contexts.
- **Expected benefit:** consistent behavior; no relearning per page.
- **Complexity:** low (WallLayoutCodec already supports 1–64).

## W7 — One error vocabulary for very different failures (observed pattern, not fully measured)

- **Behavior:** connection problems surface as a generic online/offline dot plus a message; the operator cannot tell
  wrong password from closed RTSP port from a device that answered with HTML instead of SOAP.
- **Reproduction:** add a device with a wrong password, then with the RTSP port blocked; compare the messages.
- **Impact:** the first support call is spent identifying which layer failed.
- **Baseline:** one status indicator per device.
- **Proposed improvement:** Camzivo reports the four stages separately (reach, sign in, profiles, stream) with a
  plain-language reason per stage; the diagnostics vocabulary already distinguishes unauthorized, refused, DNS,
  timeout and protocol violation (tested).
- **Expected benefit:** the operator fixes the actual problem without a support call.
- **Complexity:** done in the core; the dialog wiring is the remaining piece.

## W8 — Playback search is a modal, form-heavy workflow (observed in manual flow, not timed)

- **Behavior:** recording search is a dialog with device/channel/type/date fields before any timeline appears.
- **Reproduction:** Playback → search → fill the form.
- **Impact:** "what happened at 21:40?" takes several decisions before showing data.
- **Baseline:** not timed (deliberately: no recording source available here).
- **Proposed improvement:** timeline-first playback: pick camera, the timeline loads the day, drag to seek, event
  markers visible immediately; the search form is an advanced filter.
- **Expected benefit:** fewer clicks per investigation; the common case needs one drag.
- **Complexity:** medium (depends on M4 playback integration).

## W9 — High-DPI and multi-monitor behavior unverified

- **Behavior:** not measured. The installed skin set is fixed-size artwork (`Skin/`, 52 MB), which is a known
  constraint for per-monitor DPI scaling, but it was not tested at 150%/200%.
- **Impact:** unknown; do not claim a defect.
- **Proposed improvement:** Camzivo is vector/template based (WPF), declares per-monitor DPI awareness, and stores
  layout per monitor. Verify at 100/150/200% before claiming anything.
- **Complexity:** low-medium; must be measured, not assumed.

## W10 — Video performance comparison not yet possible

- **Behavior:** no cameras or NVR are attached to this machine, so first-frame latency, decode CPU, GPU usage, tile
  switching and 49-camera stability **cannot be measured for either product**.
- **Impact:** any statement that Camzivo renders faster would be fabricated.
- **Proposed improvement:** the benchmark protocol in `docs/PERFORMANCE_COMPARISON.md`; results are recorded only
  when real streams are available, with the hardware and stream parameters written next to the numbers.

## How these weaknesses shape the roadmap

1. Keep the startup and footprint advantage (W1, W2) — it is the one measured win, and it is easy to lose.
2. Never regress credential handling (W3).
3. Fix the error vocabulary everywhere, because it is the cheapest reliability win (W7).
4. Do not chase module parity (W5); chase the video workflow.
5. Publish no performance claim until W10 is measured.
