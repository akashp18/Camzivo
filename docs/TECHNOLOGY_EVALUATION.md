# Technology Evaluation and Decision

Requirement: Windows-first, 16-camera smooth on integrated graphics, 49 tiles via adaptive substreams, no UI-thread
blocking, native GPU decode, single-installer deployment, maintainable for years.

The evaluation below is grounded in what is actually installed and measurable on the target workstation, not in
framework popularity.

---

## 1. Measured environment constraints

| Fact | Value | Consequence |
|---|---|---|
| MSVC toolchain | Visual Studio 2022 Community present | C++/Rust native builds possible |
| Qt | **not installed** (`C:\Qt` absent) | Qt 6 route needs ~1.5 GB installer + account |
| Rust | **not installed**, no `cargo` | Rust route needs rustup + first-build cost |
| .NET SDK | **not installed**; no `dotnet --list-sdks` | installable project-locally, no admin |
| Node.js | 20.11.1 | fine for tooling; REA needs ≥22.23 |
| GPU | Intel UHD Gen11, 1 GB shared | decode budget is the binding constraint |
| Windows | 11 build 26200 | WinUI 3 / WebView2 / DirectComposition all available |

## 2. The three candidates, assessed

### Option A — C++20 / Qt 6 Widgets + FFmpeg + D3D11 + SQLite

| Criterion | Assessment |
|---|---|
| Video rendering | Best in class: `QOpenGLWidget`/`QRhiWidget` or raw HWND + D3D11 swapchain, zero-copy DXVA surfaces, no compositor tax |
| Multi-camera scale | Best; direct control of decoder pools, surface reuse, frame pacing |
| GPU integration | Direct (same process, same device, shared handles) |
| UI productivity | Lowest of the three; QSS styling is limited, Qt Widgets looks dated without heavy custom painting |
| Memory | Lowest baseline (~60–80 MB for a comparable shell) |
| Windows fit | Good, but HiDPI + multi-monitor + accessibility work is manual |
| Deployment | Qt runtime must be shipped (licensing: LGPLv3 with dynamic linking, or commercial); ~150 MB |
| Debugging/profiling | Excellent (VS, PIX, ETW) — but only with a working Qt build |
| Dependency maintenance | Qt major upgrades are expensive; LGPL obligations need care |
| Long-term extensibility | Strong for the core, weak for rapid UI iteration |
| **Cost to reach Milestone 2 here** | **Highest**: no Qt installed, 1.5 GB install, CMake+vcpkg FFmpeg build, weeks before a window appears |

### Option B — Rust + Tauri 2 + native video surface + SQLite

| Criterion | Assessment |
|---|---|
| Video rendering | Viable but awkward: the webview owns presentation, so video must be composited either as a child HWND, a shared D3D11 texture via `SurfaceView`, or by rendering the grid offscreen and shipping frames — every path adds a copy or a z-order problem |
| Multi-camera scale | Core logic excellent (fearless concurrency, bounded channels); presentation is the ceiling |
| GPU integration | Needs `windows-rs` + D3D11/DXVA2 bindings written by hand; FFmpeg via `ffmpeg-next`/`rsmpeg` with MSVC |
| UI productivity | High for chrome (HTML/CSS), low for the hard part (video) |
| Memory | WebView2 processes add ~80–120 MB before any video; total is *higher* than a native shell, not lower |
| Windows fit | WebView2 is present; still a second runtime to keep current |
| Deployment | 8–15 MB installer + WebView2 bootstrapper |
| Debugging/profiling | Rust debuggability is fine; cross-boundary video bugs are the worst class to debug |
| Dependency maintenance | Young ecosystem churn in the video/compositor area |
| Long-term extensibility | Good, if the team is Rust-fluent |
| **Cost to reach Milestone 2 here** | **High**: no Rust toolchain, MSVC linker config, and the video path is research-grade work |

### Option C — C# / .NET 8 + WPF + FFmpeg + D3D11 + SQLite  ← **selected**

| Criterion | Assessment |
|---|---|
| Video rendering | Strong when done right: **one** D3D11 device renders the entire grid into a single swapchain/backbuffer, presented through a single `D3DImage` (or a `SwapChainPanel` for WinUI). Per-tile `D3DImage` is the anti-pattern we avoid |
| Multi-camera scale | Good: `async`/`Task` I/O, bounded `Channel<T>` queues, `Interlocked` counters, LOH-free frame buffers via `ArrayPool`/`IMemoryOwner` |
| GPU integration | FFmpeg `AVHWDeviceType.D3D11VA` → `AV_PIX_FMT_D3D11` textures, then a shader converts NV12→RGB on the GPU; same device as the compositor |
| UI productivity | Highest: XAML templating, full restyling with tokens, animations, accessibility, virtualization, keyboard/DPI all built in |
| Memory | Shell baseline ~70–110 MB (measured target ≤90 MB); GC is not an issue if frame buffers are unmanaged |
| Windows fit | Native Windows API surface, DPAPI, Credential Manager, WMI/ETW, MSIX/self-contained publish |
| Deployment | `dotnet publish -r win-x64 --self-contained` → single folder/trimmed; no runtime install for the user |
| Debugging/profiling | Best in class: VS debugger, `dotnet-counters`, `dotnet-trace`, PerfView, PIX for the D3D side |
| Dependency maintenance | LTS .NET (8 → 10) with a stable API; FFmpeg shipped as versioned binaries |
| Long-term extensibility | Excellent: interfaces in a pure `Core` assembly, UI replaceable |
| Security | DPAPI-protected secrets, no eval surface, signed assemblies |
| **Cost to reach Milestone 2 here** | **Lowest**: SDK installs into the workspace without admin, templates included, tests run in seconds |

## 3. Decision

**Option C: C# / .NET 8, WPF shell, D3D11 + FFmpeg video engine, SQLite persistence.**

Rationale, in priority order:

1. **The video problem is solved once, in native code, behind an interface.** WPF hosts a single D3D11
   composition surface; all 64 tiles are textured quads in one render pass. That removes the webview/multi-surface
   ceilings of Option B and keeps Option A's GPU advantage without its UI cost.
2. **Time to a verifiable foundation.** The workspace already has MSVC, but no Qt and no Rust. A project-local
   .NET SDK produced a building, testable shell in the same session; the same milestone in Qt or Tauri is days of
   toolchain work with nothing to show.
3. **Long-term maintenance.** The modular `Core`/`Data`/`Desktop` split survives a UI rewrite (WinUI 3 or a
   different shell) without touching device, streaming or persistence logic. Qt locks UI and domain together;
   Tauri ties the domain to a young bridge layer.
4. **Measured incumbent weaknesses are cheap to fix here**: startup (remove eager SDK init), idle footprint
   (no helper processes), per-user config (DPAPI + AppData), stream policy (per-camera model).

### Rejected, with reasons that could change

- **Qt 6** would be reconsidered if (a) a commercial licence or a settled LGPL policy exists, and (b) the team is
  C++-first and needs frame-accurate multi-monitor presentation with exotic GPU features. It is not wrong; it is
  disproportionately expensive *for this team, today*.
- **Rust/Tauri** would be reconsidered if the video surface were solved and verified (child HWND or
  `SwapChainPanel` equivalent) *before* committing the UI, i.e. as a spike. Until then it trades a known-good
  compositor for an unproven one.
- **WinUI 3 instead of WPF** is a real option for a future shell: `SwapChainPanel` gives a cleaner video surface
  than `D3DImage`. It is rejected *now* because WinUI 3 needs Windows App SDK packaging, has thinner
  third-party/designer tooling, and no measurable win for a dense grid UI. The domain layers are UI-agnostic so
  this stays open.

## 4. Video stack decision

| Concern | Choice | Why |
|---|---|---|
| Demux/decode | FFmpeg (LGPL build, `n7.x` line) via `FFmpeg.AutoGen` P/Invoke | One library for RTSP/RTP/MP4/PS, H.264/HEVC/AV1/MJPEG, TCP+UDP, `AVHWDeviceType.D3D11VA` and `DXVA2` |
| Hardware decode | D3D11VA first, DXVA2 fallback, software last | Matches incumbent's DXVA2 path; D3D11VA gives shared textures directly |
| Colour conversion | Pixel shader NV12/P010→RGB on the same D3D11 device | Keeps frames on the GPU; no `sws_scale` to system memory |
| Composition | Single D3D11 render target, per-tile viewport/scissor, letterboxed | One present per UI frame, not 64 |
| Presentation | `D3DImage` (WPF) with one shared surface; `SwapChainPanel` if we migrate to WinUI | Avoids per-tile interop surfaces |
| Audio | WASAPI via `NAudio` (explicit user action only) | Same as incumbent's `qtaudio_wasapi` choice |
| ONVIF | Hand-written SOAP client over `HttpClient` + WS-Discovery over UDP multicast | Removes SDK dependency; fully testable offline |
| Persistence | `Microsoft.Data.Sqlite` + hand-written migrations | Deterministic, testable, no ORM surprises |

FFmpeg licensing note: use a build configured without `--enable-gpl`/`--enable-nonfree` (no x264/x265 encoders),
ship `LICENSE`/`COPYING.LGPLv2.1`, and provide the exact build configuration + relink instructions in
`THIRD_PARTY_NOTICES`. HEVC decode in FFmpeg's native decoder is not covered by the MPEG-LA pool the way an
encoder is, but deployment in a commercial product still needs legal review — recorded as a release blocker in
`planning/RELEASE_READINESS.md`, not silently ignored.

## 5. Consequences accepted

- WPF's `D3DImage` requires `RenderCapability.Tier >= 2` for hardware acceleration; we detect tier at startup and
  fall back to a software-composited path with a visible, explained status (never a silent quality drop).
- `D3DImage` presents with a copy on some driver versions; if measurement shows this on target hardware, the
  planned mitigation is a `HwndHost` child swapchain per **monitor** (not per tile), keeping one device.
- .NET GC pauses are avoided by keeping frames in unmanaged FFmpeg buffers and never allocating per frame in the
  render loop; enforced by a `dotnet-counters` check in the soak test.
- We ship our own toolchain pin (`global.json`) so a machine with a newer SDK does not silently change behaviour.
