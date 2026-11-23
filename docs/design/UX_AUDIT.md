# UX Audit — Camzivo

Method: heuristic evaluation of the five implemented surfaces against real operator tasks, using the rendered
build (`artifacts/shell-*.png`) and the source. Findings are numbered and tied to a concrete consequence, not a
taste preference.

---

## 1. What the operator sees today

- **Live View** — nav rail, camera tree with search, layout chips 1–64, wall of tiles with name/quality/state,
  status bar with memory and thread count.
- **Devices** — demo inventory table, counters, a disabled "Add device".
- **Events / Playback / Recordings & Exports** — single-card placeholders naming the milestone.
- **System Health** — three diagnostic cards with severity bar, plain-language cause and three next steps.
- **Settings** — appearance, storage paths, decode budget, credential policy, version.

## 2. Findings

| # | Finding | Consequence for an operator | Severity | Fix |
|---|---|---|---|---|
| U1 | `Ctrl+F` / fullscreen changes the grid to 1-up but not the window | Operator cannot get an unobstructed view for a wall monitor | High | **Fixed** — real fullscreen, `F11`, `Esc`, layout untouched; verified by `--probe-fullscreen` |
| U2 | "Clear slot" is a tooltip-free text button next to layout chips | Ambiguous: does it clear all, or the selected? | Medium | **Fixed** — "Clear tile", tooltip names the selection and `Ctrl+Z` |
| U3 | Placement is not remembered between sessions | Re-arranging 16 cameras every morning | High | BUG-03 wall persistence |
| U4 | Command bar has two dead buttons ("Add device", "Reconnect all") | Teaches the operator that buttons do nothing | Medium | disable with reason in tooltip, enable in M3 |
| U5 | Status bar shows `MEM 123 MB · 13 THR` | Developer telemetry in the operator's field of view; also competes with stream health | Medium | **Fixed** — numbers appear only in sample/design mode; a real run shows the clock (stream facts replace it in M4) |
| U6 | Camera tree items have no explicit "place" affordance | Clicking a name to inspect it accidentally places it | Medium | **Decision, not defect** — a click placing the camera is one gesture instead of two; the row tooltip says "Place this camera on the wall". A future detail panel will evidence a separate select gesture (M3) |
| U7 | Search has no result feedback | Typing a wrong name looks like an empty tree | Low | **Fixed** — "No cameras match this search." (`HasNoMatches`, tested) |
| U8 | Empty wall state is per-tile only | A brand-new install shows 9 identical hints and no onboarding | High | **Fixed** — wall-level onboarding panel (`ShowOnboarding`, `LiveViewModel.ShowOnboarding`) |
| U9 | Playback/Events/Exports placeholders look clickable but aren't | Wasted clicks; uncertainty about what is broken | Low | **Resolved by copy** — the page carries a milestone chip and a "PLANNED" list, so it does not present as a broken feature |
| U10 | No visible keyboard map | The fastest operator path is undiscoverable | Medium | `?` overlay listing shortcuts; cheat-sheet in Settings |
| U11 | Theme toggle is a text chip in the command bar | Fine, but no discoverability from Settings (Settings has a *button* that duplicates it) | Low | Settings row shows a segmented Dark/Light control bound to the same command |
| U12 | Error/warning colour is the only difference between severity bars in a screenshot | Colour-blind operators lose the distinction | Medium | **Fixed** — severity word (`Critical`/`Warning`) under the camera name, plus the status code |
| U13 | Tile header shows `MAIN` but never explains the choice | Operator can't tell why a tile is soft | Medium | **Partly fixed** — `QualityHint` tooltip explains main vs sub; clicking to switch is M4 |
| U14 | Devices table is demo data with no path to real onboarding | The screen's primary job is missing | High | M3 |
| U15 | No indication of how long a tile has been in its state | Cannot tell a fresh reconnect from a stuck one | Medium | **Blocked on M4** — needs engine timestamps; recorded as a tile-state acceptance criterion |

## 3. Task walkthroughs

**T1 — Watch 16 cameras.** Open → Ctrl+3? No: 16 is `Ctrl+4`. Click 16 cameras. *Friction:* 16 clicks per session
because placement is not saved (U3); no "add all" action; no keyboard path (U6/U10). **Verdict: workable, not
effortless.** Fix U3 + U6 + saved views.

**T2 — A camera looks frozen.** Look at tile → see `PREVIEW`/`LIVE`/`QUEUED`. Today every tile is demo, so the
operator cannot learn the real states. **Verdict: blocked on M4**, but the state vocabulary is already decided.

**T3 — "Something happened at 14:32."** Playback is a placeholder. **Verdict: not possible.** Highest-value
missing workflow in the product.

**T4 — A camera is offline.** Health shows reasons, but from synthetic data; the tile never goes offline.
**Verdict: blocked on M4**, wording ready.

**T5 — Shift handover.** No way to leave a note, save a view, or hand over the wall. **Verdict: missing.**
Recommend saved views (P1) + "session summary" (P2, no new service: a read-only page of current wall + offline
cameras).

**T6 — Mistake recovery.** Clearing a tile is immediate and unconfirmed; there is no undo. Placing a camera on the
wrong tile silently replaces the selected one. **Verdict: needs undo (`Ctrl+Z` restores last wall mutation).**

## 4. Information architecture

Current IA is right in shape and should not be restructured: **Live View · Playback · Cameras & Devices · Events ·
Recordings & Exports · Camera Health · Settings**. Two renames are recommended in
`INTERACTION_PATTERNS.md` §7: "Devices" → "Cameras & Devices", "System Health" → "Camera Health" (both requested
by the brief, both clearer about content). Keep "Playback" and "Recordings & Exports" distinct: playback is
investigation, exports are evidence.

Do **not** add: a dashboard page, an analytics page, a map page, a separate "streams" page. Each would duplicate
Live View's job.

## 5. Density and hierarchy

- 56 px nav rail + 34 px command bar + 28 px status bar is appropriate for a control room.
- The wall must dominate: no header card, no page title above it. Current build satisfies this.
- Typography: 11 px labels, 10 px micro-caps, 15 px tile titles — legible at 100 % and 150 % scaling (checked on
  the rendered 1600×900 capture only; 4K not verified).

## 6. Accessibility

| Check | State |
|---|---|
| Automation names on tiles, nav, theme | present (`name, state, quality`) |
| Colour-independent status | dot + text label (good); Health severity bar is colour-only (U12) |
| Contrast | tokens chosen for ≥ 4.5:1 on text; not measured programmatically — **not verified** |
| Keyboard reachability | nav and wall reachable; no visible focus ring on tiles — **not verified** |
| High DPI | renders at 96 dpi; 150 %/200 % not verified |
| Motion | no animations to disable; future transitions must honour reduced motion |

## 7. Prioritised UX work

1. Real fullscreen, Esc to exit (U1) — trivial, high value.
2. Persist the wall (U3) — removes the largest daily friction.
3. Wall-level onboarding empty state (U8) — first-run comprehension.
4. Click selects / double-click or Enter places (U6) + shortcut overlay (U10).
5. Rename Clear slot, add undo (U2, T6).
6. Health severity icon + label (U12); stream-fact tooltips (U13); state age (U15).
7. Move telemetry to Settings (U5), fix search feedback (U7).
