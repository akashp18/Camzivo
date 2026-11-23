# Competitor Analysis — Camzivo

Sources are official vendor documentation, datasheets and comparison charts (linked). Capability claims are
attributed; licensing differences are stated where they matter. No proprietary code, assets or interface designs
are copied — only publicly documented behaviour is used as a requirement input.

---

## 1. The set

| Product | Position | Documented facts used here |
|---|---|---|
| **Dahua SmartPSS Lite** | The incumbent this product replaces; free SMB client | Manages up to **64 channels**; H.264/H.265; live view + video wall with layout/scheme/tour; smart search & playback; event centre; access-control/attendance modules; Windows 7–11 64-bit ([datasheet](https://alarmtec.ee/images/uploaded/Datasheet_SmartPSSLite_Final.pdf), [wiki](https://dahuawiki.com/SmartPSS_Lite)) |
| **Hikvision iVMS-4200** | Direct competitor, device-vendor client | **64-window** division (48 wide), custom divisions, per-window stream info (bitrate/fps/resolution), fisheye modes, recording, search/playback, alarms ([datasheet](https://www.hikvision.com/content/dam/hikvision/en/support/download/vms/ivms4200-series/software-download/v3-14-2-4/iVMS-4200-Client-Software-V3.14.2-Datasheet_20261010.pdf)) |
| **Milestone XProtect** | Professional/enterprise reference | Tiered: Essential+ **free, 8 devices, single server**; Express+ **48 devices**; higher tiers per recording server. Smart Client, Web Client, Mobile; **view layouts, 100 items/view, Smart Wall, maps, bookmarks, adaptive streaming, rules engine, failover** ([comparison chart](https://download.milestonesys.com/MTSKB/KB000034241/XProtect%20VMS%202026%20R1%20Comparison%20Chart.pdf), [product page](http://www.milestonesys.com/products/software/xprotect)) |
| **Nx Witness (Network Optix)** | Modern, hardware-agnostic VMS | **Up to 64 live streams per layout**, 2,560 resources/site, 256 dual-recording streams per server, adaptive playback, **bookmarks**, event rules, **video wall mode**, showreel, 20+ languages; client spec recommends i5/8 GB for 64 devices on one monitor, i9/32 GB for quad ([tech spec](https://www.networkoptix.com/hubfs/Generation%206%20Release/Nx%20Witness%20-%20Full%20Tech%20Spec%20Brochure%20-%20US%20Letter%20-%20Digital.pdf), [client hardware](https://support.networkoptix.com/hc/en-us/articles/115015815928-Nx-Witness-Desktop-Client-Hardware-Specs)) |
| **Blue Iris 6** | Windows-only prosumer NVR, closest architectural peer | **128 cameras**, **dual streaming** (main for record/audio, sub for display and motion), **direct-to-disk** recording, built-in AI object/face/ALPR, many formats ([release notes](https://blueirissoftware.com/?page_id=344), [manual](https://blueirissoftware.com/blueiris.pdf)) |
| **Agent DVR** | Free/cross-platform NVR, browser UI | **Unlimited cameras**, ONVIF/RTSP/USB, **records the raw stream (near-zero CPU)**, motion zones/tripwires/heatmaps, pre/post buffers, runs as a service, remote relay via vendor cloud ([features](https://www.ispyconnect.com/features), [pricing](https://www.ispyconnect.com/buy)) |

## 2. What the industry treats as table stakes

1. **Device onboarding**: discovery + manual add + a connection test that names the failure (auth, timeout, codec).
2. **Live wall** with 1/4/9/16/25/36/49/64-style divisions, custom divisions, drag-and-drop, saved views.
3. **Per-window stream facts**: resolution, fps, bitrate — iVMS exposes these in the tile.
4. **Main/substream strategy**, either automatic (Blue Iris: sub for display, main for record) or operator-chosen.
5. **Playback**: calendar or timeline, record-availability shading, speed control, frame step, event markers,
   multi-camera sync where the device allows it.
6. **Export** with original timestamps and a completion signal.
7. **Events** from the device (motion, tamper, input, offline) with jump-to-playback.
8. **Health**: who is offline, why, since when, and retry counts.
9. **Evidence discipline**: bookmarks (Nx, XProtect) so an operator can return to the incident.

## 3. Patterns worth stealing (behaviour, not design)

| Pattern | Source | Why it works | Camzivo decision |
|---|---|---|---|
| Substream for display, main for record | Blue Iris | Cuts decode cost by an order of magnitude at 16+ tiles | Adopt as the default policy, with an explicit per-tile `SUB/MAIN` chip and a manual override |
| Per-window stream info | iVMS-4200 | Lets an operator diagnose a bad camera without leaving the wall | Adopt as a one-line tile overlay, off by default |
| Bookmarks | XProtect, Nx | Turns "I saw something at ~3pm" into a durable object | Adopt in Playback (P1), device-independent |
| Adaptive streaming tied to tile size | XProtect | Keeps large walls usable on modest hardware | Already the shape of `StreamSelectionPolicy`; finish it |
| Failover / redundancy | XProtect, Nx | Recording must not stop | **Not** in scope for a single-workstation client; state it in the release notes rather than half-build it |
| Maps / GIS | XProtect, Nx Maps | Spatial recall for large sites | P3, optional; never in the lightweight core |
| Raw-stream record | Agent DVR | Near-zero CPU for archive | Adopt where the device allows direct-to-disk passthrough |

## 4. Where competitors are weak (Camzivo's opening)

1. **Startup and footprint.** The incumbent measured **8,693 ms** to its login window, ≈160 MB idle, 182 threads,
   3 processes, 383 MB / 11,339 files installed. Camzivo measured **2.4–3.9 s** warm, 120–150 MB, 13 threads, one
   process, and no service. That gap is the product thesis.
2. **Truthful state.** Operators waste time on tiles that look live but are frozen. Camzivo's rule — no `LIVE`
   without decoded frames, `PREVIEW` for sample data, `QUEUED` beyond budget — is a differentiator that costs
   almost nothing.
3. **Error vocabulary.** Competitors surface error codes. Camzivo's `ConnectionDiagnostics` maps each failure to
   *what it means* and *what to try*, in the operator's words (`Camera rejected the credentials`, with three
   concrete next steps).
4. **No cloud dependency and no bundled FTP/plaintext config.** The incumbent ships plaintext admin credentials in
   an INI file and widens ACLs; Camzivo stores nothing in `Program Files` and will use DPAPI.
5. **Keyboard-first operation.** Ctrl+1..8 layouts exist in both worlds, but Camzivo can make placement, paging and
   search fully keyboard-driven without a mouse — a real control-room win.

## 5. Where competitors are ahead (and it matters)

| Capability | Who has it | Camzivo today | Consequence |
|---|---|---|---|
| Video decoding and rendering | all | none | the product cannot show a frame; M4 is the gate |
| Playback + timeline + export | all | placeholder pages | cannot investigate an incident |
| Saved views / tours | SmartPSS, iVMS, XProtect, Nx | layouts only | operator must rebuild the wall daily |
| Bookmarks / evidence markers | XProtect, Nx | none | weak evidence workflow |
| Maps | XProtect, Nx | none | acceptable for v1 |
| AI analytics | Blue Iris 6, Agent DVR | none | deliberately out of core; device events instead |
| Multi-server/failover | XProtect, Nx | none | out of scope, stated openly |

## 6. Requirements Camzivo adopts from this study

Adopt (P0/P1): device onboarding with named failures; automatic main/sub with visible override; per-tile stream
facts; saved views; playback timeline with availability shading; export with timestamps + completion marker;
bookmarks; offline/online events with jump-to-playback; health with reason and retry count.

Defer (P2/P3): maps, tours/showreel, AI analytics, mobile/web clients, multi-server, cloud relay, failover.

Reject for the core: bundled web server, always-on analytics pipeline, embedded browser, per-vendor SDK binaries.
Each of those is a permanent resource and maintenance cost that the product thesis (fast, light, honest) does not
justify.
