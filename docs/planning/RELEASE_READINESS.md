# Release Readiness — Camzivo

**Classification: ALPHA (pre-video).** Not a beta, not a release candidate, and explicitly not production-ready.

---

## 1. Classification evidence

| Dimension | Requirement for Beta | Actual | Verdict |
|---|---|---|---|
| Shows live video | yes | no decoder, no network path | ✗ blocker |
| Onboards a real camera | yes | no UI, no ONVIF probe, no auth client | ✗ blocker |
| Playback / export | yes | placeholder pages | ✗ blocker |
| Crash safety | log + fail fast + report on next start | `AppLog`, three handlers, crash marker; UI exceptions terminate after logging | ✓ (log/marker tested; termination is framework behaviour) |
| Session restore | wall + settings | settings only (fixing now) | ◐ improving |
| Tests | unit + integration + UI | 155 unit/component, including 10 view-model tests; 0 window automation, 0 network | ◐ |
| Performance | measured budgets | shell measured; video unmeasured | ◐ |
| Security | DPAPI + audit | neither implemented | ✗ blocker |
| Installer / upgrade | signed installer | publish folder only | ✗ |
| Documentation | user + operator | design/audit only | ✗ |

Two of nine dimensions pass partially; **zero** of the product-defining ones pass. Hence Alpha.

## 2. What is genuinely usable today

- Launch the shell and see a fast, correct, theme-aware camera wall (with honest sample content).
- Configure layout, selection, placement of demo cameras; preferences persist.
- Read the Health page's diagnostic vocabulary and the Settings page's storage facts.
- Use it as a design/architecture base with 155 tests and a clean build.

## 3. What is not usable (stated so nobody is misled)

- No camera can be added or viewed. No video, no audio, no PTZ.
- No recordings, playback, events or exports.
- No credential storage; do not enter a real password into any future build until DPAPI lands.
- No installer, no auto-update, no uninstaller, no license.

## 4. Gates to reach each label

**Alpha → Beta** (all required)
1. M3 complete: discovery + manual add + connection test + DPAPI + audit rows.
2. M4 complete: live video for 16 cameras on the reference host inside the CPU/UI budgets; reconnect proven.
3. M5 minimum: one-camera playback with timeline and one export with manifest.
4. Crash logging + graceful shutdown + wall restore verified.
5. 8 h soak with 8 real cameras: no leak, no unhandled exception, no dead UI.
6. Security checklist §6 of `SECURITY_AUDIT.md` green.
7. Installer installs/uninstalls per-user with no elevation and no residue outside `%APPDATA%`/`%LOCALAPPDATA%`.

**Beta → RC**
1. 24 h soak with 20 cameras including 3 forced network drops.
2. All P0/P1 rows of `FEATURE_PRIORITY_MATRIX.md` implemented or explicitly deferred in writing.
3. UI automation suite covering the 12 mandatory regression scenarios.
4. Memory/CPU budgets met with published numbers on the reference host.
5. Accessibility pass: keyboard-only operation of every workflow, screen-reader labels, 150 % DPI verified.
6. No open S1/S2 defect.

**RC → Production**
1. 30-day field trial with two operators, defect rate < 1 S2/week.
2. Upgrade path tested from every prior schema (including the profile rename).
3. Support documentation and a diagnostics bundle that a non-developer can produce.
4. Signed binaries and a reproducible build.

## 5. Known limitations to publish with any build

- Sample content is labelled `PREVIEW`; it is not camera footage.
- An unexpected UI exception terminates the application by design, after writing the log and crash marker; the next
  start reports it once in the banner. Continuing after an unknown fault would risk showing a wall that is no longer
  truthful.
- Status bar shows memory/threads only in developer builds (moving to Settings).
- Single instance is not enforced yet (being added).
- No TLS on RTSP; stream encryption depends on the device (`rtsps` support planned).
- Health page data is synthetic until M4.
- English only; no localisation infrastructure.

## 6. Risk register (top 5)

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| GPU decode variance across drivers | High | High | capability probe at startup, CPU fallback with visible label |
| Vendor ONVIF quirks (Dahua) | High | Medium | capture real responses as fixtures; per-vendor overrides |
| Recording query differences (NVR vs SD card) | Medium | High | start with one documented device family, label others unsupported |
| Scope creep from analytics requests | High | Medium | P3 rule: no user request, no build |
| Perf claims outpacing measurement | Medium | High | budgets doc + harness required before any "smooth" claim |
