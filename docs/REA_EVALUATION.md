# REA Evaluation

Question asked at the start of this project: *can REA help us inspect SmartPSS Lite, automate its UI, or test the
replacement?* Answer below is based on installing it and running it on this workstation, not on its marketing text.

---

## 1. What REA is

| Item | Value | Source |
|---|---|---|
| Repository | `https://github.com/morluto/rea` | `git ls-remote` (HEAD `747656b995ccef27d6a9f2e8b9c75e2e3d44b696`) |
| Package | `rea-agents` (npm), also an MCP server | `package.json` / npm |
| Version evaluated | **6.10.1**, released 2026-11-20 | `CHANGELOG.md` |
| Licence | **MIT** (`Copyright (c) 2026 morluto`) | `LICENSE` |
| Runtime requirement | Node **`^22.23.4 \|\| ^24.22.1 \|\| >=26.12.1`**, npm 11.x | `package.json` → `engines` |
| Platforms claimed | macOS, Windows, Linux | `README.md`, `docs/getting-started/installation.md` |
| Shape | CLI (`rea <command>`) + MCP server for agents | `rea --help` |
| Size | ~9,270 files in the repo, 175 npm packages installed | clone + install log |

It is a **reverse-engineering toolkit**: static inspection of native binaries (PE/ELF/Mach-O), JavaScript/Electron
applications, .NET managed assemblies, web bundles, firmware containers, plus optional *dynamic* lanes
(debuggers, native UI automation, process capture) when the matching provider is installed.

## 2. Installation actually performed

The workstation has Node **20.11.1**, which the published package refuses (`engines` requires ≥22.23.4). Rather than
changing the system Node installation, a project-local toolchain was used:

```bash
# 1. Pick the newest 22.x from https://nodejs.org/dist/index.json  -> v22.23.5
curl -fsSL https://nodejs.org/dist/v22.23.5/node-v22.23.5-win-x64.zip -o tools/node22.zip
unzip -q tools/node22.zip -d tools/ && mv tools/node-v22.23.5-win-x64 tools/node22

# 2. Install REA into the workspace, no global state
export PATH="$PWD/tools/node22:$PATH"
cd tools && npm install --prefix "$PWD/npm-global" rea-agents@latest
```

Result: `node --version` → `v22.23.5`; `rea --version` → `6.10.1`; `rea --help` lists ~100 commands.

Notes on hygiene: nothing was installed globally, `PATH` was not modified persistently, no administrator rights
were requested, and nothing outside `tools/` was touched. The install is disposable — delete `tools/node22` and
`tools/npm-global` to remove it.

After evaluation, `react`, `docker` and other project tooling were **not** affected: the local Node lives under
`tools/` only.

## 3. Verified capability: installation inventory (used)

```bash
rea inspect-desktop-application "C:/Program Files/SmartPSSLite" --json
```

Produced a valid Evidence bundle (`evidence_id` `ev_b504a8d6…`, provider `rea-artifact-graph` v1) with:

- `coverage`: **11,670 entries examined**, 219 files probed, 82 KB of bytes read, `status: partial`
- PE headers for every probed module: `machine: 34404` (AMD64) for the Qt/Dahua DLLs, `machine: 332` (i386) for
  `uninst.exe`
- full path/metadata inventory, including `platforms/`, `plugins/`, `Solutions/`, `Data/`, `Conf/`
- explicit `limitations`: directory hash commits to paths + read-range digests only; runtime candidates do not
  imply loaded code; special-entry payloads unread

This is genuinely useful and it fed `SMARTPSS_ANALYSIS.md` §2 (architecture) and the PE machine finding. It is
read-only, needs no admin, and did not execute anything from the installation.

## 4. Capabilities we could *not* verify here

| Capability | Status | Reason |
|---|---|---|
| Native binary deep analysis (decompile, call graphs, strings-with-context) | **not verified** | Requires a native analysis provider (Ghidra, Hopper, IDA or Kuna). None is installed on this machine; `rea capabilities` would report the lane unavailable. Setup can optionally install Hopper, which we declined |
| .NET managed assembly inspection | not applicable | SmartPSS Lite is native Qt5/C++; Camzivo is .NET but we have the source |
| JavaScript/Electron reconstruction | not applicable | No Electron target in scope |
| Windows desktop UI automation | **not available** | `capture-desktop-application` is Linux-only; `capture-native-ui-scenario` is an accessibility-API lane built around macOS AX. Neither drives a Qt5 window on Windows |
| Process capture while the app runs | not attempted | Would require attaching to a licensed product; our research stayed static + a single launch for timing |
| Firmware/container parsing | not needed | Out of scope for a VMS front-end project |

## 5. Verdict

**Keep REA as an optional research tool; use it for what was verified; do not depend on it.**

1. ✅ **Used**: installation/artifact inventory of the incumbent, PE architecture confirmation, evidence-grade
   record of paths and sizes. Re-run the same command when a new SmartPSS build appears and diff the inventory.
2. ⚠️ **Not a UI automation or test framework.** For Windows, our UI verification is the app's own headless
   `--snapshot` renderer plus (Milestone 6) FlaUI/UIA automation. REA does not replace either.
3. ⚠️ **Native analysis lane is unproven on this machine.** Adding Ghidra (~1 GB + JDK) purely to read strings we
   already obtained would be cost without benefit. If we later need to understand a specific Dahua behaviour
   (e.g. an ONVIF quirk), the correct next step is a protocol capture against an authorized device, not
   decompilation.
4. ❌ **Never** used to bypass authentication, licensing, encryption, or anti-tampering; and never to lift
   proprietary code or assets into our source tree.
5. ❌ Not added to the product. REA is a development-time tool; Camzivo ships no REA dependency, no MCP server, and
   no Node runtime.

## 6. What this means for the build

- The replacement is implemented independently from documented protocols (ONVIF SOAP, RTSP/RTP) and public
  standards, not from disassembly of the incumbent. `DEVICE_COMPATIBILITY.md` records exactly which protocol
  surfaces we implement.
- The incumbent analysis rests on file layout, configuration, metadata and readable type/log strings — evidence a
  reviewer can reproduce with `rea inspect-desktop-application` plus a file listing, without any reverse
  engineering of protected material.

## 7. Reproducing this evaluation

```bash
export PATH="$PWD/tools/node22:$PATH"
node tools/npm-global/node_modules/rea-agents/scripts/rea.mjs --version          # 6.10.1
node tools/npm-global/node_modules/rea-agents/scripts/rea.mjs inspect-desktop-application \
  "C:/Program Files/SmartPSSLite" --json > artifacts/rea-smartpss.json
```

Expected: exit 0, an Evidence bundle with `coverage.status: partial` and >10,000 examined entries. Nothing in the
installation is modified by the command.
