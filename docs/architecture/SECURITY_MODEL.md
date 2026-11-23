# Security Model

Threat model: a Windows workstation on a customer LAN, possibly shared between shifts, holding credentials for
cameras and NVRs, with evidence exports that may become legal material. Camzivo is read-only toward devices in v1.

---

## 1. Assets

| Asset | Sensitivity | Where it lives |
|---|---|---|
| Device credentials | high — grants full camera/NVR access | DPAPI-protected blobs, `%APPDATA%\Camzivo\secrets` |
| Live/recorded video | high — personal data, often regulated | memory (GPU textures), export folder |
| Exported evidence | high | user-chosen folder + JSON metadata sidecar |
| Device inventory, layouts | medium | SQLite `camzivo.db` |
| Audit trail | medium-high (integrity matters) | SQLite, append-only table |
| Logs | low-medium (must not contain secrets) | `%LOCALAPPDATA%\Camzivo\logs` |

## 2. Threat table

| # | Threat | Vector | Control |
|---|---|---|---|
| T1 | Credential theft at rest | copy of `camzivo.db` or config | secrets never in DB; DPAPI `CurrentUser` scope (per-user, per-machine) |
| T2 | Credential leak in logs | log statement includes RTSP URL or header | single logging choke point applying `CredentialRedactor`; unit test scans a corpus of credential shapes |
| T3 | Credential leak in memory dumps / crash reports | unhandled exception with request URI | exceptions are wrapped; `detail` redacted before storage or display |
| T4 | Man-in-the-middle on ONVIF | plain HTTP on port 80 | TLS supported and preferred; explicit "allow self-signed" toggle, per device, audited; no silent downgrade |
| T5 | RTSP credential interception | Basic auth over cleartext | `Digest` preferred; Basic allowed because most cameras only offer it, but the UI states "credentials sent with weak protection" for that device |
| T6 | Unauthorized local user reads config | shared workstation | OS file ACLs (user profile), DPAPI; optional app PIN is documented as *not* a security boundary |
| T7 | Malicious/hostile device response | crafted SOAP/SDP/RTSP | bounded parsers (`XDocument` with DTD prohibited, size caps), no eval, timeouts, no path traversal from device-supplied names |
| T8 | Log injection | device string with newlines | structured JSON logs, control characters stripped |
| T9 | Malicious update | tampered installer | Authenticode-signed releases; update check only informs, never auto-executes |
| T10 | Supply chain | NuGet/FFmpeg binaries | pinned versions, `dotnet list package --vulnerable`, FFmpeg from a pinned LGPL build with checksum |
| T11 | Privilege escalation | app run as admin | manifest `asInvoker`; app never writes outside the user profile; no service, no driver |
| T12 | Data exfiltration by design | cloud defaults | zero default egress; no telemetry; a first-run check with `netstat`/pktmon is part of release verification |
| T13 | Device configuration damage | accidental writes | v1 has no device-write API at all; PTZ is the only actuation and is explicit |
| T14 | Evidence tampering after export | modification of MP4/sidecar | SHA-256 of the export recorded in the sidecar and in `audit_entry` |
| T15 | Replay of stale credentials | token reuse | WS-Security nonce + created timestamp per request, fresh nonce each time |

## 3. Credential handling rules (enforced, not aspirational)

1. A password exists as a `string` only for the duration of one auth header construction; it is never stored in a
   model, view-model, or log.
2. `DeviceCredentials` is not `ToString()`-able into anything useful (override returns `***`).
3. The database stores `secret_ref` (a GUID key), never a password, hash, or reversible blob.
4. `ISecretStore` is the only way in or out; `DpapiSecretStore` uses `ProtectedData.Protect` with
   `DataProtectionScope.CurrentUser` and an app-specific entropy value.
5. Deleting a device deletes its secret in the same user action (both or neither).
6. Restoring a database on another Windows account cannot decrypt secrets — the UI states this and requests
   re-entry; it never retries silently.
7. UI password fields use `PasswordBox` with `AutomationProperties.IsPassword=true`; clipboard copy is not offered.

## 4. Network posture

- Outbound connections only to operator-configured device addresses. No discovery beacon to the internet, no P2P,
  no cloud DNS names (the incumbent ships `easy4ipcloud`, `dahuap2pcloud`, `dolynksecurity` — we ship none).
- WS-Discovery listens on the local multicast group only; it does not unicast to arbitrary hosts.
- All sockets set explicit connect/read timeouts; no infinite waits (also a DoS control).
- TLS: `SslStream`/`HttpClient` with default validation; a per-device exception requires a checkbox and is written
  to the audit log with device id + thumbprint.
- No listening TCP ports. The app is not a server.

## 5. Input validation

| Input | Validation |
|---|---|
| Host | IPv4/IPv6 or DNS label; reject schemes, paths, whitespace, > 253 chars |
| Ports | 1–65535, defaults 80/554 |
| Username | ≤ 64 chars, no control chars |
| Password | ≤ 128 chars, no control chars, never trimmed silently |
| Device-supplied names | sanitised for control chars + length, displayed as data, never used as a file name |
| Export path | canonicalised, must be writable, no `..` traversal, never `%WINDIR%` |
| Layout dimensions | rows/cols 1–8, product ≤ 64 |
| SDP/XML | max body 512 KB, max XML depth 32, DTD prohibited |

## 6. Audit trail

Recorded: device added/edited/deleted, credential set/cleared, TLS exception accepted, export created/finished,
database backup/restore, migration applied, PTZ session started/stopped. Each entry: UTC timestamp, Windows user,
action, subject id, short detail. Never contains secrets or full URLs.

Audit rows are append-only from the application's perspective (no update/delete API). We do not claim tamper-proof
storage; that requires an external sink, documented as a follow-up for regulated deployments.

## 7. Least privilege and deployment

- `requestedExecutionLevel = asInvoker`; no admin, no service, no scheduled task, no driver, no firewall rule.
- Installs to `%LOCALAPPDATA%\Programs\Camzivo` (per-user) or `Program Files` only under an explicit machine-wide
  install; user data always stays in the user profile either way.
- Single-file/self-contained publish; no global assemblies registered.
- Code signing (Authenticode) applied when a certificate is available; `planning/RELEASE_READINESS.md` records it as a release
  gate rather than a nice-to-have.

## 8. What we explicitly refuse to do

- No bypassing authentication, licensing, or encryption of any device or of SmartPSS Lite.
- No private protocol reverse engineering to reach features the vendor gates.
- No stored default credentials (contrast: incumbent's `FtpConfig.ini` ships `admin`/`admin123`).
- No privilege widening (contrast: incumbent's `GrantF.bat` grants `Everyone:F` via `Cacls /t /e /g`).
- No telemetry or crash upload without explicit opt-in, and never with device data.

## 9. Verification in this milestone

Implemented and unit-tested: `CredentialRedactor` (RTSP userinfo, `?password=`, `Authorization:` headers, embedded
`user:pass@` in arbitrary text), `DpapiSecretStore` design, schema with no plaintext secret columns, audit table,
input validation helpers for host/port. Not yet verified: end-to-end TLS behaviour against real devices, signing,
and the `pktmon` no-egress check (all require devices/certificates, tracked in `planning/RELEASE_READINESS.md`).
