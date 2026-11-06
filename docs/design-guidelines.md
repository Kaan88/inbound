# UI design guidelines

How the WinUI frontend should look and behave. The goal is a dense
utility that looks the same in dark and light mode. Follow these rules when adding windows or controls.

## 1. One palette, defined once

All colors come from the theme dictionaries in `App.xaml`. Never hard-code
`Color.FromArgb(...)` or `#RRGGBB` in a window.

| Token | Use | Dark | Light |
|---|---|---|---|
| `InboundChromeBrush` | menu bar, toolbar, sidebar, list header, status bar | `#202020` | `#F3F3F3` |
| `InboundSurfaceBrush` | window/page background, download list, dialog bodies | `#181818` | `#FFFFFF` |
| `InboundDividerBrush` | 1px borders and grid lines | `#343434` | `#DADADA` |
| `InboundSecondaryTextBrush` | secondary columns, hints, axis labels | `#A3A3A3` | `#666666` |
| `InboundProgressFillBrush` | progress fill, segment map and the speed-graph line (blue, not green, so it does not read as "completed") | `#4AA3F0` | `#1F6FC5` |
| `InboundProgressTrackBrush` | progress track | `#383838` | `#E2E2E2` |
| `InboundErrorTextBrush` | inline error messages | `#F07A7A` | `#C42B1C` |
| `InboundHighlightBrush` | informational banners and highlighted panels | `#2B3A4F` | `#DCE9F7` |
| `InboundIconStartBrush` / `Pause` / `Danger` / `Info` / `Neutral` | toolbar glyph accents | lighter tints | darker tints |

- `App.xaml` overrides `SystemAccentColor*` with Inbound blue, so checkboxes,
  radio buttons, accent buttons, toggles and list selection follow the palette
  instead of the user's Windows accent. Don't reintroduce a second accent.

- In XAML, use `{ThemeResource InboundXxxBrush}`.
- In code-behind, use `InboundTheme.Brush("InboundXxxBrush", element)`. Pass the
  window's root element. Each window sets its own `RequestedTheme`, so an
  application-level lookup can return the wrong variant.
- New windows read `UiStateStore.Load().DarkMode`, set `RequestedTheme` on the
  root, and use `InboundSurfaceBrush` for the background.
- Every window calls `WindowLayout.ApplyChrome(window, root)` after setting
  `Content`. It sets the app icon and a title bar matching `InboundChromeBrush`,
  so a dark window never sits under a white caption.

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

Remote rows, for work on a provider's side with no local download yet, sit above the local rows in All
Downloads and in their own Waiting view. They show a cloud glyph and an italic name, with a divider under
the last one. Their stage is words and never a bar. Size, Time left and Transfer rate stay blank, because
those columns are about bytes reaching this computer. A provider's own progress goes in Description,
labelled as the provider's.

In the add dialogs, each line says what it is in a word: Folder link, Magnet, Torrent file, Hoster through
the provider, Direct provider link, Ordinary link, Unsupported, Can't tell. A line that can hold a secret shows
only its host and its position in the list, never its address. A hoster link has no control that sends it
anywhere but the provider. A link nobody could classify gets a retry, and a direct download only by a box
ticked for that line. Whether something is cached is known only after it is added, so the approval that uses
the account's quota comes in a second step, unticked, and closing that step loses nothing.

Extraction has its own Extraction column beside Status, with the same status colors on its text and no bar,
so a failed extraction never reads as a failed download and never lands in the Failed view. The cell is short
and its tooltip has the Host's whole sentence, including every missing volume. An archive with no file on the
list gets a row of its own, above the local rows on All Downloads and in the Extractions view; Status stays
blank on it. Nothing about extraction opens by itself: a set that needs a password gets a key button on its
row and one line in a notice bar above the list, and the password dialog opens only when asked for. The column
and every extraction command hide when the Host doesn't answer `archive.*`. The column also stays away until there is an
archive to talk about, so the Description column, where a provider's words and a remote failure's cause go, fits at the
default window width. With one remote or archive row selected, the status bar says what Description says.

## 3. Progress bars

- Every `ProgressBar` uses `InboundProgressBarStyle`: 6px tall, rounded,
  blue fill on a neutral track. For bars created in code, wrap them with
  `InboundTheme.Styled(new ProgressBar { ... })`.
- In the list, the bar sits under the status text inside the 140px Status
  column. Show it only while a job is active or paused with progress above 0.
- The progress dialog has one main bar, `SegmentMapBar` (14px, radius 2). It
  draws every segment's completed bytes at its real offset, with a divider at
  each segment start. The plain indeterminate bar appears only
  while the size is unknown. Don't add a second overall bar.
- The segment table has the columns N. | Progress (6px bar) | Downloaded |
  Speed | Info. Its values come from the live stream position
  (`RangeStart - Start + ReceivedBytes`), merged with the saved checkpoint and
  kept at a high-water mark, so numbers never move backwards between
  refreshes. Per-segment speeds are smoothed (EMA 0.25) because raw per-stream rates sawtooth.
- Normal text cells keep the inherited foreground. Never assign a `null`
  Foreground in code; it renders the text invisible.
- Keep the defaults: no gradients, no animated stripes, no percentage text
  drawn over the bar.

## 4. Layout and density

- List rows are 27px with 11–12px text. Column headers are 12px SemiBold on
  `InboundChromeBrush`, with a 1px bottom divider.
- Header and row column widths must match exactly. Change both definitions in
  `MainWindow.xaml` together.
- Leave unknown values blank: no "0 B" for an unknown size and no empty speeds.
  Use "—" for missing live values in summaries.
- Units: binary multiples labelled `KB`/`MB`/`GB` (1024-based), the same in the
  list, the dialogs and the speed graph. Don't mix in `KiB`/`MiB`.
- The speed graph is secondary and must never take more space than the list: a
  23px bar styled like the status bar, holding the figures and the period, with
  an 85px plot that opens upward from it, so the bar stays where it was clicked.
- It opens by animating the panel's real height, so the list moves with it and
  nothing is left blank. Don't use `Expander` for bottom panels: it reserves the
  full height first and slides the content in afterwards, leaving a gap.

## 5. Toolbar

- Buttons are flat (`InboundToolButtonStyle`): 58px tall, at least 72px wide and
  sized to a single-line label, icon above label, with a transparent background
  in every state, including disabled. Disabled buttons only dim; they don't get
  a gray box.
- Groups, separated by `InboundToolSeparatorStyle`: Add URL | Resume, Stop,
  Stop All | Delete, Delete Completed | Options, Scheduler | Start Queue,
  Stop Queue.
- Icons are outline glyphs colored with the `InboundIcon*Brush` tokens: start
  green, pause/stop amber, delete red, add/scheduler blue, settings neutral.
  Stop (keeps partial data) uses the pause glyph; Stop All and Stop Queue use
  the square. Don't use hard-coded hex colors or filled icons.

## 6. Dialogs and secondary windows

- Use the same surface, divider and text tokens as the main window.
- Buttons go at the bottom right, with the primary action first and Cancel or
  Close last.
- Don't use `TabView` (browser tabs) for settings. Multi-page settings use
  `SettingsWindow`: a resizable window with a page list on the left, a titled
  scrolling page with `Section`/`Hint` headings on the right, and
  OK / Cancel / Apply in a footer. Validation errors appear inline in the footer
  and select the offending page; the window never closes on invalid input.
- Smaller multi-page windows (Scheduler, My provider) use a `SelectorBar` above the page.
- Use the right control for the value: `NumberBox` for numbers,
  `FolderField` (text box with Browse…) for folders, `TimeField`/`DaysField`/
  `DateField` (`FormFields.cs`) for schedule times, weekdays and dates,
  `RadioButtons` for exclusive modes. Don't ask users to type formats like
  `HH:mm` or comma lists.
- Dependent options are indented 28px under their parent and disabled when
  the parent is off.
- Windows that can be empty show an empty-state explanation, and every
  secondary window has a Close or OK/Cancel button at the bottom right.
- Secondary text uses `TextFillColorSecondaryBrush` in XAML or
  `InboundSecondaryTextBrush` in code. Error text uses `InboundErrorTextBrush`.

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
   `artifacts\uiqa\data\ui-state.json` and restart the UI.
5. Capture a window with `PrintWindow` and flag 2 (`PW_RENDERFULLCONTENT`). It needs no focus and takes in nothing
   else on the desktop, while a copy of the screen can come out black. A menu or flyout is a window of its own, with
   class `Microsoft.UI.Content.PopupWindowSiteBridge` or `Xaml_WindowedPopupClass`, so capture it by itself.
6. Use `--dest`, not `--dir`, with `dm add`. Destinations must be outside the
   user's Downloads folder during harness runs.
