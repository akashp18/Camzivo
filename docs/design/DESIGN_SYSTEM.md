# Camzivo Design System

The system is deliberately small: one token pair (dark and light), one control dictionary, one icon dictionary, and a
short set of component rules. Its purpose is consistency and legibility in a control room, not visual novelty.

Source of truth:

- `src/Camzivo.Desktop/Themes/Tokens.Dark.xaml`, `Tokens.Light.xaml`
- `src/Camzivo.Desktop/Themes/Controls.xaml`
- `src/Camzivo.Desktop/Themes/Icons.xaml`
- `src/Camzivo.Desktop/Controls/TimelineControl.cs`, `CalendarMonth.cs`, `DateField.xaml`

If this document and the code disagree, the code wins and this file is a bug.

---

## 1. Principles

1. **Footage first.** Chrome never takes space the wall could use. No page header above the wall, no card around video.
2. **State is text; colour reinforces.** Every status carries a word (`LIVE`, `QUEUED`, `OFFLINE`, `ENABLED`) so the
   interface survives colour blindness, screenshots and projectors.
3. **No decoration.** No gradients, glass, glows, oversized radii, purple, or animation that communicates nothing.
4. **Density with air.** 4 px spacing scale, 32 px controls, but two different things never touch.
5. **Silence unless it matters.** A banner only for conditions that change what the operator can trust (persistence
   unavailable, previous crash, unreadable camera list). Success is not a toast.

## 2. Colour tokens

Views use semantic names through `DynamicResource`; no view hardcodes a colour except white text over video scrims.

| Token | Dark | Light | Used for |
|---|---|---|---|
| `Brush.Canvas` | `#101419` | `#F3F5F8` | window background |
| `Brush.Surface` | `#1B222B` | `#FFFFFF` | sidebar, title bar, panels |
| `Brush.Elevated` | `#222C36` | `#FFFFFF` | raised controls, transport buttons |
| `Brush.SurfaceHover` | `#232D38` | `#EEF2F6` | hover |
| `Brush.Pressed` | `#182029` | `#E4EAF0` | pressed |
| `Brush.Selected` | `#1F3040` | `#E3EEF7` | selected nav item, selected list row |
| `Brush.SelectedHover` | `#263B4E` | `#D7E7F4` | selected + hover |
| `Brush.Border` | `#2B3642` | `#DCE2E9` | 1 px separators |
| `Brush.BorderStrong` | `#3A4756` | `#C3CCD6` | dialog frame, focused input |
| `Brush.Focus` | `#7DC0EA` | `#1B6FA8` | keyboard focus ring |
| `Brush.TextPrimary` | `#E9EFF5` | `#141A20` | primary text |
| `Brush.TextSecondary` | `#98A7B5` | `#4A5866` | labels, inactive nav |
| `Brush.TextMuted` | `#687987` | `#6E7C8A` | hints, metadata |
| `Brush.TextOnAccent` | `#FFFFFF` | `#FFFFFF` | text on accent |
| `Brush.Accent` | `#408DC4` | `#2C6E9E` | primary action, selection, playhead |
| `Brush.AccentHover` | `#56A1D5` | `#3884B8` | accent hover |
| `Brush.AccentText` | `#8FC7E8` | `#1F5F8B` | accent-coloured text |
| `Brush.AccentSoft` | `#1C3242` | `#E4F0F8` | accent chip background |
| `Brush.Success` | `#4CC9A0` | `#1E7D5E` | decoded, healthy |
| `Brush.Warning` | `#E5AE54` | `#A96A12` | degraded, retrying, queued |
| `Brush.Critical` | `#D96B70` | `#B23A3F` | offline, unauthorised, failed |
| `Brush.Info` | `#6FB6DD` | `#2C6E9E` | preview, informational |
| `Brush.VideoSurface` | `#0B0F14` | `#0B0F14` | video viewport (always dark) |
| `Brush.TileOverlay` | `#CC0B0F14` | `#CC0B0F14` | camera name/status chip over video |
| `Brush.TimelineTrack` | `#151C24` | `#E8EDF2` | empty timeline |
| `Brush.TimelineSegment` | `#2F6F96` | `#9CC6DE` | recording |
| `Brush.TimelineSegmentSelected` | `#408DC4` | `#2C6E9E` | selected recording |
| `Brush.TimelinePlayhead` | `#E9EFF5` | `#141A20` | playhead |
| `Brush.InputBackground` | `#141A21` | `#FFFFFF` | text fields |
| `Brush.InputBorder` | `#34414E` | `#C9D2DC` | field border |
| `Brush.TableHeader` | `#171E26` | `#EEF2F6` | table header strip |
| `Brush.RowSelected` | `#1C3040` | `#E3EEF7` | selected table row |
| `Brush.DialogSurface` | `#1B222B` | `#FFFFFF` | prompt/dialog |
| `Brush.TooltipBackground` | `#222C36` | `#FFFFFF` | tooltips |
| `Brush.DisabledSurface` / `Brush.DisabledText` | muted pair | muted pair | disabled fields |

Rules: accent ≤ 5 % of pixels; `Success` only for decoded frames/live health; `Critical` only for something that was
expected to work and does not (not for a missing capability, which uses muted text).

Theme switching: `App.ApplyTheme(bool)` replaces the single merged dictionary whose `Source` contains `Tokens.`;
every view uses `DynamicResource`, so the visual tree remaps without recreating pages. `TimelineControl` exposes its
brushes as dependency properties bound with `DynamicResource` for the same reason.

## 3. Type

One family, `Segoe UI Variable` with `Segoe UI` fallback (`Font.Ui`); `Font.Display` for the wordmark; `Font.Mono` only
for addresses, timestamps and byte counts.

| Role | Size | Weight | Token |
|---|---|---|---|
| Page/section title | 17 | 600 | `SectionTitle` |
| Dialog title | 14 | 600 | `Strong` |
| Body / control label | 13 | 400 | `Body` |
| Metadata, help | 12 | 400 | `Meta` |
| Category label (uppercase) | 10.5 | 600 | `Micro` |
| Table header | 10.5 | 600, uppercase | `TableHeaderText` |
| Timestamp / clock | 12 | 500, tabular | `Timestamp`, `Mono` |
| Numbers (counts, FPS) | 12 | 500, tabular | `Number` |

Tabular numerals are mandatory wherever digits change in place (playback position, durations, memory, progress).
Never uppercase a sentence; micro-caps are category labels (`APPEARANCE`, `STORAGE`, `SEGMENTS IN THIS WINDOW`).

## 4. Space, size, radius

- Spacing scale: 4, 8, 12, 16, 20, 24, 32, 40. Page padding 16; panel padding 12–16; table row 40 min height.
- Control height: 32 (buttons, inputs, combos). Compact toolbar controls 26–28. Toolbar/table header 34.
- Radii: 4 compact controls, 6 buttons/inputs, 8 panels/video, 10 maximum (cards/dialogs). Tiles use 6.
- Elevation: borders do the work. One shadow exists (dialog). No card shadows, no glass.
- Minimum window: 1024 × 640; the wall keeps at least 60 px per tile before it clips.

## 5. Shell

- **Title bar** 40 px: logo, wordmark, version, current page, context; right: engine chip, theme switch, minimise /
  maximise / close. Dragging is on the bar; buttons opt out of `WindowChrome` hit testing.
- **Sidebar** 216 px expanded / 60 px collapsed (Ctrl+B). Six items only: Live View, Playback, Devices, Events,
  Exports, Settings. Selected = `Brush.Selected` pill + 2 px accent rail + accent icon + semibold label. Collapsed =
  icon with tooltip; `AutomationProperties.Name` always carries the label.
- **Banner** full width, `Brush.WarningSoft`, one sentence, only for trust-changing conditions.
- **Status bar** 26 px: left = engine/stream truth, right = memory/threads/clock. No decorative badges.

## 6. Components (`Themes/Controls.xaml`)

| Component | Keys | States |
|---|---|---|
| Buttons | `PrimaryButton`, `SecondaryButton`, `DestructiveButton`, `GhostButton`, `IconButton`, `NavButton`, `TransportButton`, `SpeedButton`, `ChromeButton`, `CloseChromeButton` | default/hover/pressed/focus/disabled (opacity 0.45–0.55); `SpeedButton` selected = accent border + selected fill |
| Inputs | `InputTextBox`, `InputPasswordBox`, `SearchBox` | default/hover/focus/disabled, placeholder overlay |
| Combo box | implicit `ComboBox` | closed/open/hover/focus/disabled, custom popup |
| Check box / toggle / radio | implicit styles, `ToggleSwitch` | unchecked/checked/hover/focus/disabled |
| Slider / progress | implicit styles | normal/disabled, 4 px track |
| Scroll bars | implicit | thin, auto-hiding |
| Tabs | `TabControl`, `TabItem` | selected/hover |
| Lists / tables | implicit `ListBox`, `ListBoxItem`, `TableHeader`, `TableRow`, `TableHeaderText` | hover/selected/focus |
| Panels | `Panel`, `SectionCard`, `Toolbar`, `Chip`, `Hairline`, `Divider` | — |
| Text | `PageTitle`, `SectionTitle`, `Strong`, `Body`, `Meta`, `Micro`, `Mono`, `Number`, `Timestamp`, `SectionHeader`, `ErrorMessage` | — |
| Feedback | `ProgressBar`, `Chip`, `Banner`, empty states in each page | loading/error/empty |
| Dialog | `DialogWindow` style; `PromptWindow` (rename) | modal, Escape/Cancel, Enter/Save |
| Timeline | `TimelineControl` + `TimelineLayout` | bars, ticks, playhead, hover, selection, keyboard, focus ring |
| Date | `DateField` + `CalendarMonth` | popup month grid, today, selected, outside month |
| Icons | `IconPath` + `Geometry` keys (`Icon.Live` … `Icon.Calendar`) | inherits `Fill`; one 16 × 16 geometric family |

Converters (`Converters/ValueConverters.cs`): `BoolToVis`, `InverseBoolToVis`, `NullToVis` (`invert`), `EmptyToVis`
(`invert`), `CountToVis`, `ZeroToVis`, severity → brush/label, bytes → size, fraction → percent, icon key → geometry.

## 7. Camera tile

Header 24 px (name, quality chip), footer 22 px (state dot + word), video letterboxed `Uniform`. States: `Empty`,
`Preview` (sample data only — never `LIVE`), `Queued`, `Waiting`, `Live`, `Offline`, plus reconnect detail. Selected =
1 px accent border. Drop target = dashed accent inset, never a fill that hides video. Controls are contextual (hover /
selected), not a permanent overlay.

## 8. Playback

Top: camera, date, search, availability. Centre: video (always dark, scrim chips). Bottom: 1–24 h timeline, zoom (−/+),
pan (‹/›), playhead, transport, ±30 s, time. Right: recording, segments in window, export range, integrity note.
A click on a gap selects nothing and seeks nowhere. Export fields are times of day and are clamped into the selected
date, so a midnight-crossing recording never opens on a validation error.

The transport carries a six-segment **speed selector** (`1× 2× 4× 6× 8× 16×`) built from `SpeedButton` and the
same tokens as the transport. Exactly one speed is highlighted. Beside the recording timestamp the page shows the
measured active rate (`15.68× active`), or `16× requested · 1.0× actual` when the source is not accelerating. A
limitation notice uses `ErrorMessage` below the transport; it never replaces the measured number with the requested
one. See `docs/PLAYBACK_SPEED.md`.

## 9. Motion

Two motions, both ≤ 120 ms linear: theme dictionary swap (instant) and status text/colour change. No pulse, no
spinners except where elapsed time is shown. Reduced motion: the application has no decorative motion to disable.

## 10. Accessibility

1. Every interactive element has an automation name that says what it is and, where relevant, its state.
2. Focus is always visible: `Brush.Focus` ring (1 px, 2 px offset) on buttons, inputs, nav items, timeline.
3. Keyboard: Tab order follows visual order; Enter/Space activates; Escape closes dialogs and fullscreen; Ctrl+1…8
   layout presets; Ctrl+B sidebar; Ctrl+T theme; Ctrl+Z undo placement; Delete clears the selected tile.
4. Minimum hit target 24 × 24 (nav 38 × sidebar width, window buttons 38 × 40).
5. Body text contrast ≥ 4.5:1; large text ≥ 3:1. Disabled controls stay legible (opacity 0.45–0.55) and keep a reason.
6. Status never relies on colour alone.
7. Windows text scaling and 100/125/150/200 % DPI: all sizes are DIPs; wall layout uses `*` rows/columns, no fixed
   pixel wall. Mixed-DPI secondary window converts physical bounds using per-monitor DPI.

## 11. Do / don't

| Do | Don't |
|---|---|
| Put stream facts in the tile footer | Move them to a separate panel |
| Use a word for every state | Use a coloured dot alone |
| Keep the wall edge-to-edge | Wrap the wall in a card |
| Show `PREVIEW` for sample content | Show `LIVE` because the layout looks alive |
| Disable with a reason | Leave a dead button enabled |
| Use the 4 px scale | Arbitrary margins |
| 6 px radius on controls | Pill buttons, glass, gradients, purple |
