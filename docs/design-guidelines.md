# UI design guidelines

How the WinUI frontend should look and behave. The goal is a dense, reference app-style
utility (reference captures in `reference-screens/`) that looks the same in dark
and light mode. Follow these rules when adding windows or controls.

## 1. One palette, defined once

All colors come from the theme dictionaries in `App.xaml`. Never hard-code
`Color.FromArgb(...)` or `#RRGGBB` in a window.

| Token | Use | Dark | Light |
|---|---|---|---|
| `RillChromeBrush` | menu bar, toolbar, sidebar, list header, status bar | `#202020` | `#F3F3F3` |
| `RillSurfaceBrush` | window/page background, download list, dialog bodies | `#181818` | `#FFFFFF` |
| `RillDividerBrush` | 1px borders and grid lines | `#343434` | `#DADADA` |
| `RillSecondaryTextBrush` | secondary columns, hints, axis labels | `#A3A3A3` | `#666666` |
| `RillProgressFillBrush` | progress fill, segment map and the speed-graph line (blue, not green, so it does not read as "completed") | `#4AA3F0` | `#1F6FC5` |
| `RillProgressTrackBrush` | progress track | `#383838` | `#E2E2E2` |
| `RillErrorTextBrush` | inline error messages | `#F07A7A` | `#C42B1C` |
| `RillHighlightBrush` | informational banners and highlighted panels | `#2B3A4F` | `#DCE9F7` |

- In XAML, use `{ThemeResource RillXxxBrush}`.
- In code-behind, use `RillTheme.Brush("RillXxxBrush", element)`. Pass the
  window's root element. Each window sets its own `RequestedTheme`, so an
  application-level lookup can return the wrong variant.
- New windows read `UiStateStore.Load().DarkMode`, set `RequestedTheme` on the
  root, and use `RillSurfaceBrush` for the background.
- The title bar colors in `MainWindow.ApplyTitleBarTheme` match
  `RillChromeBrush`. Keep them in sync if the chrome color changes.

## 2. Status colors

Download states use fixed mid-tone colors (`JobViewModel.StatusBrush`) that stay
readable on both list surfaces:

| State | Color |
|---|---|
| downloading / connecting / verifying | blue `#3D8FE0` |
| completed | green `#389E4C` |
| failed | red `#DE4E4E` |
| paused / queued / retry waiting | amber `#C98C20` |
| anything else | gray `#8A8A8A` |

Color only the status text. Don't tint whole rows.

## 3. Progress bars

- Every `ProgressBar` uses `RillProgressBarStyle`: 6px tall, rounded,
  blue fill on a neutral track. For bars created in code, wrap them with
  `RillTheme.Styled(new ProgressBar { ... })`.
- In the list, the bar sits under the status text inside the 140px Status
  column. Show it only while a job is active or paused with progress above 0.
- The progress dialog has one main bar, `SegmentMapBar` (14px, radius 2). It
  draws every segment's completed bytes at its real offset, with a divider at
  each segment start, as reference app does. The plain indeterminate bar appears only
  while the size is unknown. Don't add a second overall bar.
- The segment table has the columns N. | Progress (6px bar) | Downloaded |
  Speed | Info. Its values come from the live stream position
  (`RangeStart - Start + ReceivedBytes`), merged with the saved checkpoint and
  kept at a high-water mark, so numbers never move backwards between
  refreshes. Per-segment speeds are smoothed (EMA 0.25) because raw per-stream rates sawtooth.
- Normal text cells keep the inherited foreground. Never assign a `null`
  Foreground in code; it renders the text invisible.
- The bottom Expander (speed graph) uses `ExpandDirection="Up"`, so its header
  stays at the bottom edge and the panel opens upward.
- Keep the defaults: no gradients, no animated stripes, no percentage text
  drawn over the bar.

## 4. Layout and density

- List rows are 27px with 11–12px text. Column headers are 12px SemiBold on
  `RillChromeBrush`, with a 1px bottom divider.
- Header and row column widths must match exactly. Change both definitions in
  `MainWindow.xaml` together.
- Leave unknown values blank: no "0 B" for an unknown size and no empty speeds.
  Use "—" for missing live values in summaries.
- Units: binary multiples labelled `KB`/`MB`/`GB` (1024-based), the same in the
  list, the dialogs and the speed graph. Don't mix in `KiB`/`MiB`.
- The speed graph is secondary: 130px, collapsible, and it must never take more
  space than the list.

## 5. Toolbar

- Buttons are flat (`InboundToolButtonStyle`): 86×58, icon above label, with a
  transparent background in every state, including disabled. Disabled buttons
  only dim; they don't get a gray box.
- Icons are outline glyphs with one functional accent each (green start,
  amber stop, red delete or stop, blue settings). Don't use filled or
  multicolored icons.

## 6. Dialogs and secondary windows

- Use the same surface, divider and text tokens as the main window.
- Buttons go at the bottom right, with the primary action first and Cancel or
  Close last.
- Use `TabView` or `Pivot` tabs for multi-page settings, like reference app's Options.
  Each page scrolls on its own inside fixed outer bounds.
- Secondary text uses `TextFillColorSecondaryBrush` in XAML or
  `RillSecondaryTextBrush` in code. Error text uses `RillErrorTextBrush`.

## 7. Verifying a UI change

1. Build and publish with `scripts\publish.ps1`, then run from `artifacts\app`
   with an isolated profile: set `DOWNLOADMANAGER_INSTANCE=uiqa` and
   `DOWNLOADMANAGER_DATA=artifacts\uiqa\data`.
2. Start processes with `UseShellExecute=false`, not `Start-Process` or
   double-click, so Windows doesn't show the unsigned-file security prompt. Run
   `Unblock-File` on anything that was downloaded.
3. Drive realistic states with TestServer throttling, for example
   `/slow/name.iso?size=80000000&bps=400000`. Add a failing URL and a small
   file that completes so every status color is on screen.
4. Capture both themes. To switch, set `"DarkMode": false|true` in
   `artifacts\uiqa\data\ui-state.json` and restart the UI. Compare against
   `reference-screens/`.
5. Use `--dest`, not `--dir`, with `dm add`. Destinations must be outside the
   user's Downloads folder during harness runs.
