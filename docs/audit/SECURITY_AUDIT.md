# Security Audit — Camzivo 0.2.0

Scope: the repository as it exists today. Where a control is *designed but not built*, it is marked as such —
a schema column is not a security control.

---

## 1. What exists, verified

| Control | State | Evidence |
|---|---|---|
| Per-user data location, no elevation required | **Implemented** | `AppPaths` uses `%APPDATA%`/`%LOCALAPPDATA%`; nothing writes to `Program Files` |
| SQL parameterization everywhere | **Implemented** | every repository statement uses `$name` parameters; the only interpolation is `PRAGMA user_version = {int}` from our own migration table |
| Credential redaction in text | **Implemented, tested** | `CredentialRedactor` handles `user:pass@`, `user@`, and does not mangle `a@b` paths |
| Secrets excluded from logs | **Vacuously true** — there are no logs yet | see §4 |
| No secrets committed in the repo | **Verified by inspection** | no `.env`, no `secrets` content, `artifacts/` holds PNGs and build output only |
| Dependency surface | Small: `Microsoft.Data.Sqlite` 8.x + xUnit | `Directory.Packages.props` / csproj |
| Device secret storage | **Not implemented** | `device.secret_ref` column and `secrets/` folder exist; no DPAPI cipher, no writer, no reader |

## 2. Incumbent comparison (context for design decisions)

Measured during the SmartPSS Lite discovery phase of this project (read-only inspection):

- Installation keeps **all user state inside `C:\Program Files\SmartPSSLite`**, which forces elevated writes for
  ordinary operations.
- `FtpConfig.ini` ships **`admin`/`admin123`** in plain text; `GrantF.bat` **widens ACLs** on the install tree.
- The client loads three proprietary SDK trees (`dhnetsdk`, `PlatformSDK`, `dhplay`, `HWDec.dll`).

Camzivo's rule set follows directly: **no plaintext device passwords, no widened ACLs, no elevation, no
proprietary binaries.** These are non-negotiable and are checked in review.

## 3. Threat model (desktop VMS, single operator, LAN cameras)

| # | Threat | Impact | Mitigation today | Required |
|---|---|---|---|---|
| T1 | Device password readable at rest | Full camera compromise | none | DPAPI `CurrentUser` scope, ciphertext in `secrets/<ref>.bin`, only `secret_ref` in SQLite; refuse to store if DPAPI fails |
| T2 | Password leaks into logs/UI/crash dump | Credential disclosure | `CredentialRedactor` + redaction tests | route **every** log write through the redactor; add a test that a synthetic `rtsp://user:pass@` line is masked |
| T3 | Local DB read by another user on a shared PC | Inventory disclosure | per-user ACLs of `%APPDATA%` | document the boundary; option to encrypt at rest is post-1.0 |
| T4 | Malicious camera answers discovery | Crash / SSRF-ish probing | none | treat all ONVIF XML as untrusted: size cap, no DTD/entity expansion, scheme/host allow-list, timeouts |
| T5 | Downgrade to a newer/older schema | Data corruption | newer-schema refusal (tested) | same guard for a *future* migration path; back up before migrating (done) |
| T6 | Tampered export evidence | False evidence | none | hash manifest (SHA-256) + original timestamps in the export folder, documented in `Recordings & Exports` |
| T7 | Credential prompt spoofing | Password capture | none | single modal owned by the app; no embedded browser, no HTML |
| T8 | Unprotected RTSP (plaintext on LAN) | Eavesdropping on video | none | support `rtsps` where the device offers it, surface "unencrypted stream" in health |

## 4. Findings

**SEC-01 (High, blocked on M4): no credential protection exists.** `secret_ref` is a placeholder. Until DPAPI
lands, the product must not accept a device password at all — and it currently has no UI to do so, which is the
only reason this is not exploitable today. The first device-onboarding commit must include DPAPI, not a follow-up.

**SEC-02 (Medium): no audit trail is written.** `AuditRepository` exists and is tested, but no code calls it.
Sensitive actions (add/edit/delete device, export, clear wall, change settings) must append entries in the same
transaction as the change. Acceptance: a `/audit`-style query returns rows after each action in the device
milestone.

**SEC-03 (Medium): unhandled exceptions can dump memory/stack to a UI dialog** and are not captured. Crash logs
will contain exception text (never frame data, never credentials — redactor applied). Do not attach frame buffers
to exception messages.

**SEC-04 (Low): no dependency vulnerability gate.** The surface is tiny, but there is no `dotnet list package
--vulnerable` check in the build. Add it as a documented pre-release step; do not add a scanner daemon.

**SEC-05 (Low): export location is under the user's Videos folder** with default ACLs. Acceptable, but exports
must be written with an explicit "completed" marker and a hash manifest so a partially written file is never
mistaken for evidence.

**SEC-06 (Informational): no TLS, no cloud, no telemetry egress in the product.** This is a feature: the app makes
no outbound connections at all today. Keep a "zero egress by default" invariant and test it (a network-deny test
once networking exists).

## 5. Least privilege and packaging

- No service, no driver, no scheduled task, no firewall rule, no registry write. Uninstall = delete folder.
- Manifest requests `asInvoker`; no `requireAdministrator`.
- No auto-update channel yet; when added it must verify a signature and never run elevated.

## 6. Security acceptance criteria for the device milestone

1. Password stored only via DPAPI; DB contains `secret_ref` only.
2. Log redaction test covers URI, headers and ONVIF SOAP bodies.
3. Every device mutation + export writes an audit row.
4. ONVIF XML parsed with entity expansion disabled and a 1 MiB cap.
5. Connection timeouts bounded (< 10 s) and cancellable; no unbounded retry.
6. Foreign-origin/tampered responses rejected by schema checks, not by string matching.
