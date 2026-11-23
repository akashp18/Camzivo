# SmartPSS Lite — Installation and Capability Analysis

Status: **measured on the authorized analysis workstation** (Windows 11 Home 26200, i5-1035G1, 16 GB, Intel UHD G1).
Analysis date: 2026-11-22. Method: read-only filesystem/registry inspection, binary string extraction, one launch for
startup timing and UI capture. No file in the installation was modified, moved, or deleted.

---

## 1. Confirmed installation facts

| Item | Value | Source |
|---|---|---|
| Product name | SmartPSS Lite | Start Menu shortcut |
| Publisher | Dahua Technology Co.,Ltd | Uninstall registry key |
| Version | `1.003.0000006.0` (64-bit) | `PackageVersion.ini`, EXE version resource, registry |
| Build date | 2024-05-17 (all core binaries share this timestamp) | file metadata |
| Install root | `C:\Program Files\SmartPSSLite` | shortcut target |
| Executable | `SmartPSSLite.exe` (465,976 bytes) | `SmartPSS Lite.lnk` → resolved target |
| Uninstaller | `C:\Program Files\SmartPSSLite\uninst.exe` | `Uninstall SmartPSS Lite.lnk` |
| Install footprint | **383 MB, 11,339 files** | `du -sh`, `find -type f` |
| Shortcut args | none (no elevation flag, no safe-mode flag) | resolved `.lnk` |
| Processes at idle | 3: `SmartPSSLite`, `SmartPSSLiteDaemon`, `DSMessageNotify` | process enumeration |

The shortcut points directly at the executable; there is no launcher wrapper, no update shim, and no signed
service registered at install time (no SmartPSS service exists — it is a pure client plus two helper processes).

## 2. Runtime architecture (derived from binaries, not from marketing material)

SmartPSS Lite is a **Qt 5 Widgets desktop client** with a heavily modularized Dahua SDK stack beneath it.

```
Qt5Widgets / Qt5Gui / Qt5Core                       UI shell, 5.4–7.1 MB each
  platform plugin: qwindows.dll (also qdirect2d.dll present)
  style plugin:    DSStyle.dll
  media service:   plugins/mediaservice/dsengine.dll  (DirectShow engine)
  audio:           qtaudio_wasapi.dll, qtaudio_windows.dll

Dahua SDK layer
  PlatformSDK.dll (5.4 MB)   "DPSdk::*" — device manager, config, ONVIF ability probing
  dhnetsdk.dll    (28 MB)    Dahua NetSDK — login, realplay, playback, smart events, CryptoPP crypto
  dhplay.dll (13 MB), play.dll (14 MB)   PlaySDK — decoding/rendering of Dahua streams
  avnetsdk.dll    (4.3 MB)   audio/video network SDK
  dhconfigsdk.dll (7.3 MB), SPDModel.dll, Infra.dll/Infrad.dll/Inframd.dll
  HWDec.dll                  "DhHwdec::" namespace — hardware decode
  P2PDll.dll                 P2P/cloud traversal
  DVRIPClient.dll            DVRIP (Dahua private TCP) transport, ONVIF flags, PTZ
  Playback.dll, Ptz.dll, LinkWebDll.dll, EncryptSqlite.dll, LogdataMgr.dll

Module UI layer ("Solutions", 28 DLLs)
  MainFormPlugin, RealMonitorUI, PlayBackUI, DeviceManagerUI, DeviceConfigUI,
  AlarmCenterUI, AlarmConfigUI, AlarmDisplayUI, AlarmManagerUI, LogSearchUI,
  SystemConfigUI, UserManagerUI, QuickWizardUI, VIMainControlUI, VIRecUI,
  VIntercomConfigUI, ACS*/Attendance*/PersonManagerUI, ConditionMonitorUI,
  ThirdPartyDBConfigUI, PermissionManagerUI, DeviceFunctionLogical
```

Hardware decoding is real, not incidental. `HWDec.dll` exports C++ types that name the exact pipeline:

- `DhHwdec::CDXVADecoderH264`, `CDXVADecoderHevc`, `CDXVADecoderAV1`
- `DhHwdec::D3D11SurfaceMgr`, `CDxva2`, `CDxva2LockRect`, `CDxva2OclShareHandle`, `CDxva2ShowDirect`
- `DhHwdec::CH264Parser`, `CHevcParser`, `CDXVADecoderAV1`
- log strings: `CAdapterMgr::D3D11CreateDevice pD3d11Device:%p`, `CDxva2ShowDirect CreateSurface`

So the incumbent uses **DXVA2/D3D11 with shared handles** for H.264, HEVC and AV1, and falls back to its own
parser/software path. Any replacement must match this: an RGB-only software pipeline will lose on this hardware.

`DVRIPClient.dll` string evidence for protocol support:

```
RtspPort        Onvif          DVRIPPtz       ptz.factory.instance
ptz.getCurrentProtocolCaps     ptz.moveAbsolutely / moveContinuously
SupportPTZCoordinates  SupportPTZZoom  SupportPtzPatternOSD  SupportPTZOSDMenu
Handle_Set_PTZ  Handle_Get_PTZPosition  Handle_Opr_OnvifAbility
```

`PlatformSDK.dll` additionally contains `DPSdk::DevManagerModule::HandleModifyDeviceIpByMulticast`,
`DPSdk::DevConfigExModule::Handle_Get_PTZ`, `OnvifAbility`, `Handle_Set_Connect::PARSER_ENCODE-RTSP`.

Interpretation: SmartPSS speaks RTSP/ONVIF, but through Dahua's own SDK, with ONVIF treated as a *capability
probe* (`OnvifAbility`) rather than a first-class standards stack. ONVIF recording/replay (Profile G) and ONVIF
events (Profile A/C) are not evident as separate implementations; the app relies on Dahua playback APIs
(`Playback.dll` + NetSDK) instead.

## 3. Configuration, state and data locations

**Every mutable path is inside `C:\Program Files\SmartPSSLite`.** There is no `%APPDATA%`, `%LOCALAPPDATA%`,
`%PROGRAMDATA%` or `Documents` footprint (verified: no matching directories exist).

| Path | Content |
|---|---|
| `Conf/System/{P2PServer,Easy4IPServer,Plugin,BackupFileConfig,UpgradeServerCluster}.xml` | cloud endpoints, plugin manifest, upgrade servers |
| `Data/System/Languages/LanguageDb.db` | SQLite language database |
| `Data/System/Sound/*.wav` (incl. `en/`) | per-event alarm sounds (~40 files) |
| `Data/System/Report/*.ncr` | NCReport templates (attendance/access control) |
| `Default/{Settings.ini,ModuleConfig.ini,SolutionsInfo.xml,Plugin.xml,AccessControl.db}` | shipped defaults |
| `playsdk_log/` | PlaySDK log directory (empty at rest) |
| `Solutions/*.dll` | feature modules loaded at runtime |
| `Skin/{White,DustyBlue,theme1}` | three skin trees, QSS + SVG sprite sets |
| `Languages/*.qm` | **991** Qt translation files covering 24 languages |

Consequences that directly shape our design:

1. **Elevation dependency.** Any preference change requires writing under `Program Files`; on a standard user
   account the app either fails silently or needs admin. This is a security and multi-user defect.
2. **No per-user separation.** Two Windows users share one configuration and one device database.
3. **Upgrade risk.** User data lives among binaries; uninstall/reinstall can destroy device lists.
4. **AV scanning cost.** 11,339 files in one directory slows real-time malware scanning and patching.

## 4. Shipped defaults worth flagging

`FtpConfig.ini` ships a hardcoded service account:

```ini
[FTPService]
CustomMode=1
Localpath=./VideoIntercom/ftp
UserName=admin
UserPwd=admin123
ServicePort=21
```

`GrantF.bat` (shipped, not referenced by the shortcut) enumerates local users with `Net users` and grants
`Cacls ... /t /e /c /g %%i:F` — full control of a sibling `Smart Professional Surveillance System` directory to
every local account. We do not execute it; we note it as evidence of the product's ACL model. Our design must
never ship a credential in a config file and must never widen ACLs.

`Default/Settings.ini` also documents the vendor's own streaming policy in comments:

```ini
;四分割以上请求视频默认使用码流类型 0:主码流; 1:辅码流
AutoStreamType=1     ; grids larger than 4 tiles request the substream by default
AutoSwitch=1         ; double-click switches stream (single-window = main stream)
SupportedDevVerList= / NotSupportedDevVerList=KL
P2PServer1=www.easy4ipcloud.com   P2PServer2=www.dahuap2pcloud.com
NewServerSetting/P2PServer1=p2p.dolynksecurity.com
```

Two lessons: (a) adaptive substream-by-tile-size is the right policy and we should own it explicitly with
per-camera overrides instead of one global integer; (b) the default build phones home to vendor cloud
infrastructure — our product ships with **no cloud dependency and no default egress**.

`Default/SolutionsInfo.xml` shows the hosted-module model: `MainFrame`, `Monitor`, `AccessControl`,
`Attendance`, `VideoIntercom` each with independent version/state. `ModuleConfig.ini` then maps module order per
solution (`[Management] DeviceManagerUI=0 LogSearchUI=1`, `[AccessControl] AcsMainControlUI=0 ...`). This is why
the product can appear as a CCTV client, an access-control console, or an attendance system depending on build.

## 5. Functional inventory (as evidenced)

| Area | Modules | Evidence | Notes |
|---|---|---|---|
| Live monitoring | `RealMonitorUI.dll` | module list, `AutoStreamType`/`AutoSwitch` settings | multi-split grid, main/sub switching, double-click toggle |
| Playback | `PlayBackUI.dll`, `Playback.dll`, `play.dll` | binaries | device-side recording retrieval, timeline implied |
| Devices | `DeviceManagerUI`, `DeviceConfigUI`, `DeviceFunctionLogical`, `QuickWizardUI` | binaries | wizard-based add, config passthrough |
| Alarms/events | `AlarmCenterUI`, `AlarmConfigUI`, `AlarmDisplayUI`, `AlarmManagerUI`, `DSMessageNotify.exe` | binaries + wav set | popup alarm host, per-event sounds |
| Logs | `LogSearchUI`, `LogdataMgr.dll` | binaries | local search UI |
| Users/permissions | `UserManagerUI`, `PermissionManagerUI` | binaries | local RBAC |
| System | `SystemConfigUI`, `EncryptSqlite.dll` | binaries | encrypted local DB |
| Video intercom | `VIMainControlUI`, `VIRecUI`, `VIntercomConfigUI` | binaries | door station features |
| Access control / attendance | `ACS*`, `Attendance*`, `PersonManagerUI`, `ConditionMonitorUI`, `*.ncr` | binaries + report templates | full ACS suite |
| POS overlay | settings `[POSInfoColorConfig]` | `Settings.ini` | POS text overlay on video |
| Third-party DB | `ThirdPartyDBConfigUI` | binary | external DB integration |

## 6. Observed startup and login behaviour

- Launch → login window visible in **8,693 ms** (cold, measured: process start → non-zero `MainWindowHandle`).
- Login window (captured): centered card on a pale blue decorative gradient; fields = server IP
  (`192.168.18.11` prefilled in the captured instance), user name (`admin`), password, `Remember Password`
  checkbox, `Auto login` checkbox, primary blue `Login` button, `Forgot password?` link.
- The application is unusable before authentication: no local mode, no demo mode, no offline device list.
  We did **not** attempt to authenticate, guess credentials, or bypass the gate.
- Idle footprint at the login screen (3 processes, zero cameras): see `performance/BASELINE_BENCHMARKS.md`.
- Startup spawns `SmartPSSLiteDaemon.exe` and `DSMessageNotify.exe` immediately — the alarm/notification host
  runs whether or not any device exists.

## 7. UX assessment (evidence-based, not stylistic preference)

| # | Observation | Impact | Our response |
|---|---|---|---|
| U1 | Login wall before any device visibility | cannot verify connectivity or view a stored layout without credentials | shell opens to Live View immediately; auth is per-device, not per-app |
| U2 | 8.7 s to first window on a 4-core laptop | slow perceived start | lazy device init; window < 1.5 s with no device I/O on the UI thread |
| U3 | 160 MB / 182 threads before any stream | cost paid with zero value delivered | helper processes created only when a subsystem needs them |
| U4 | Settings live in `Program Files` | admin rights for preferences; shared between users | `%LOCALAPPDATA%\Camzivo` for cache/logs, `%APPDATA%\Camzivo` for config, DPAPI for secrets |
| U5 | One global `AutoStreamType` integer for stream policy | no per-camera control; silent quality changes | per-camera explicit preference; auto policy always reports its reason |
| U6 | Cloud P2P endpoints enabled by default | uncontrolled egress from a surveillance network | zero default egress; all endpoints user-entered and visible |
| U7 | 383 MB / 11,339 files, 28 feature DLLs | heavy patching, AV scan cost, long installs | single self-contained publish, < 200 MB, no per-module DLL surface |
| U8 | Alarm popup host always resident | notification fatigue, unattributed background process | health centre aggregates; no separate always-on popup daemon |
| U9 | Three skins, 991 translation files, no documented design system | inconsistent visual language across modules | one token-driven system, dark/light, verified contrast |
| U10 | ONVIF used only as capability probe | non-Dahua devices degrade quietly | capability matrix per device with explicit "unsupported" and reasons |

## 8. What we deliberately did not do

- Did not log in, decrypt `AccessControl.db`, or parse `EncryptSqlite.dll` key material.
- Did not patch, hook, rename, or delete installation files; no writes occurred under `Program Files`.
- Did not configure, reset, or query any camera/NVR (none were provided, and none were authorized).
- Did not use UI automation to drive the app past authentication.
- Did not decompile proprietary code into our source tree; all findings above come from file layout, metadata,
  configuration files, and readable type/log strings in shipped binaries.

## 9. Conclusions feeding the architecture

1. The incumbent's decode path (DXVA2 + D3D11, H.264/HEVC/AV1) is the bar to match — not beat by hand-waving.
2. Its real weaknesses are architectural: state in `Program Files`, monolithic module zoo, login-gated shell,
   cloud defaults, global-only stream policy, heavy idle cost. These are ours to fix cheaply and visibly.
3. Standards support is shallow (ONVIF as probe). A genuine ONVIF stack + RTSP fallback is a credible differentiator.
4. Nothing about the incumbent's UI is worth copying beyond the operational vocabulary users already know
   (Live View, Playback, Devices, Alarms), which our information architecture keeps for muscle memory.
