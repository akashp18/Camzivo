# ADR-001 — UI system, timeline rendering and operator state

Date: 2026-11-22. Status: accepted. Context: SmartPSS Lite parity work needed a premium, consistent WPF interface and
several operator features without changing the video engine, networking stack or database architecture.

## Decisions

### 1. One token pair, one control dictionary

All colours, type roles, spacing and component styles live in `Themes/Tokens.Dark.xaml`, `Tokens.Light.xaml`,
`Controls.xaml` and `Icons.xaml`. Views reference semantic keys with `DynamicResource`; `App.ApplyTheme` swaps the token
dictionary and the visual tree remaps without rebuilding pages.

Consequences: no per-page styling, a complete light theme from the same names, and a single place to fix contrast.
Two literal colours remain, both white text over a video scrim (`#F4F7FA`, `#AEB9C4`), because video is always dark.

### 2. Timeline is a `FrameworkElement`, not a `Path`

The old timeline built one `PathGeometry` in a converter from pixel coordinates. Bars disappeared when the window
resized or the theme changed because the geometry and the layout could disagree. `TimelineControl` draws in `OnRender`
from `TimelineLayout` output, where each bar is a **fraction of the visible window**. Resizing, zooming, panning and
theme changes only invalidate rendering; they never rebuild geometry.

Consequences: bar maths is unit-tested (`TimelineLayoutTests`), a click resolves by fraction (a gap selects nothing),
and the playhead is a fraction rather than a pixel. The control's brushes are dependency properties bound with
`DynamicResource` so a theme swap recolours it.

### 3. Custom `DateField` instead of the stock `DatePicker`

The stock calendar popup is a separate visual tree that does not inherit the token dictionary, which broke the dark
theme. `DateField` + `CalendarMonth` render inside the app's own popup, start weeks on Monday, keep six rows, and mark
today/selected/outside-month independently. Grid maths is unit-tested.

### 4. Operator state in schema v5, without a foreign key to `camera`

`wall_layout`, `wall_slot`, `event_entry` and `export_entry` are new tables. They intentionally do **not** reference
`camera`: a standalone camera is a `device` identity, and removing a recorder must not erase wall layouts or event
history. Deleting a device leaves historical rows readable; the wall drops missing cameras on apply and preserves the
rest by identity.

### 5. Saved layouts are local rows; the active layout is a preference

Named layouts are stored as rows plus ordered slots. The active layout name is a preference, so startup can restore the
last layout without a write. Missing cameras are skipped rather than replaced by a different camera.

### 6. A second display window shares the wall model and decoder sessions

`WallWindow` receives the same `LiveViewModel` (and therefore the same `StreamEngine`). Opening a display never starts a
second RTSP session for a camera already on the wall. The trade-off is explicit: the second window mirrors the wall, it
does not hold an independent layout. The main window may be minimised without suspending decoding while a wall window is
open. Window placement is encoded by `WindowPlacementCodec` (strict parse, on-screen check) and the display device name
is remembered; a disconnected display simply means no second window.

### 7. Evidence export is a file plus a sidecar

Every export writes `<file>.mp4`, `<file>.mp4.sha256` (sha256sum format) and `<file>.mp4.json` (camera, UTC range,
samples, bytes, time zone). The Export record in SQLite points at the same facts. Verification recomputes the digest;
tampering or a missing sidecar reports failure rather than an optimistic success.

### 8. Health is derived from the wall

`HealthViewModel` is loaded from the live tiles each time the page is opened: offline is critical, queued is a warning,
connecting is a warning, healthy tiles produce nothing. It never shows a stale outage and never shows demo rows when
running against real devices.

## Rejected alternatives

- **Restyle stock controls at page level** — would not fix the white-button defect at its source and would leave the
  light theme inconsistent.
- **Keep the geometry converter and add clipping/z-order fixes** — would preserve the fragile pixel/fraction coupling.
- **Independent per-monitor view models** — would double decoder sessions; not worth it before independent layouts are
  a real requirement.
- **Vendor SDK for PTZ/playback** — licensing and deployment weight; ONVIF/RTSP remains the integration boundary.
