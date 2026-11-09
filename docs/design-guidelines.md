# UI design guidelines

How the WinUI frontend should look and behave. The goal is a dense
utility that looks the same in dark and light mode. Follow these rules when adding windows or controls.

## 1. One palette, defined once

All colors come from the theme dictionaries in `App.xaml`. Never hard-code
`Color.FromArgb(...)` or `#RRGGBB` in a window.

| Token | Use | Dark | Light |
|---|---|---|---|
| `InboundWindowBrush` | every window's root: transparent, so the Mica backdrop shows; solid where Mica isn't available | Transparent (fallback `#202020`) | Transparent (fallback `#F3F3F3`) |
| `InboundChromeBrush` | menu bar, toolbar, sidebar, list header, status bar: a faint layer over Mica | `#08FFFFFF` | `#80FFFFFF` |
| `InboundSurfaceBrush` | the download list and other lists, opaque for reading; popups drawn over a plot | `#181818` | `#FFFFFF` |
| `InboundCardBrush` / `InboundCardStrokeBrush` | cards, tiles and framed lists on the frosted window, with a 1px edge, radius 8 | `#0DFFFFFF` / `#14FFFFFF` | `#B3FFFFFF` / `#13000000` |
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
- New windows call `AppTheme.Track(root)`, which sets `RequestedTheme` on the root
  from the settings' Theme (System, Light or Dark) and changes it with the setting
  and with Windows, and use `InboundSurfaceBrush` for the background. Do not read
  the theme from `ui-state.json`; a test checks that only `AppTheme` does.
- Every window calls `WindowLayout.ApplyChrome(window, root)` after setting
  `Content`. It sets the app icon, a Mica backdrop (`MicaBackdrop`, Windows 11) behind a transparent root
  (`InboundWindowBrush`; a root with no background gets it), and a title bar in the solid color under Mica
  (`#202020` / `#F3F3F3`), so the caption meets the frosted body. Where Mica isn't supported (Windows 10),
  `App` makes `InboundWindowBrush` that same solid color before any window opens, so nothing is see-through.
- The look is frosted: the window is Mica, chrome is a faint translucent layer, and cards and tiles are
  translucent white (dark) or white (light) with a thin translucent edge. Only what is read closely stays
  opaque: the download list and other lists (`InboundSurfaceBrush`). Buttons and text boxes keep the WinUI
  defaults, which are already the matching translucent Fluent fills. No gradients drawn by the app, and
  no Acrylic. Text keeps 4.5:1 or better: the secondary and status colors are checked on the opaque surface,
  and Mica is at least as dark (dark) or as light (light) as the fallback color.

## 2. Status colors

Download states use the `InboundStatus*Brush` tokens, at least 4.5:1 on the list surface and on a selected row in
each theme (a test in `SelectionCommandsTests` checks this). Rows bind to the shared brushes in `StatusBrushes`, not
to a resource lookup, and those change color when the main window's theme changes.

| State | Token | Dark | Light |
|---|---|---|---|
| downloading / connecting / verifying | `InboundStatusActiveBrush` | `#6DB3F2` | `#1B65B4` |
| completed | `InboundStatusDoneBrush` | `#5DC879` | `#1B7130` |
| failed | `InboundStatusFailedBrush` | `#F58B8B` | `#B3261E` |
| paused / queued / retry waiting | `InboundStatusWaitingBrush` | `#E0A93E` | `#8A5A00` |
| anything else | `InboundStatusNeutralBrush` | `#A3A3A3` | `#5F5F5F` |

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
ticked for that line. Whether something is cached is known only after it is added, so the approval to fetch
what isn't cached comes in a second step, unticked, and closing that step loses nothing. Import JD Container
follows the same rules with the same words. Its list is virtualized, because a container can hold 5000 links, so a
link password is entered from the download list rather than from a row.

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
- The progress window (UI.R29, direction B) has a header (the file's icon on a tile, its name, where it
  comes from, a state chip in the status color) and a `SelectorBar`: Overview, Connections (n), Options.
  Overview puts a determinate 124px `ProgressRing` (blue running, amber paused, red failed, green with a check
  when done) beside four tiles, then a card holding the main window's `DownloadSpeedGraph` for this job only
  (1 minute by default, the same periods, scale, labels and hover), a thin
  6px segment map and the address, folder and resume facts. A failure opens an `InfoBar` with the reason
  first. Connections and Options start with a one-line summary (percent, bar, speed and time left).
  Completed hides the tabs and makes Open the primary button. Delete stays a labelled button. Tiles and cards use `InboundCardBrush` with an
  `InboundCardStrokeBrush` edge, radius 8.
- That graph samples the job updates the window already receives and never asks the Host for anything.
- `DownloadSpeedGraph` scrolls smoothly: between samples its line slides left at the speed time passes (one
  composition animation of a transform, at most 2 s), and the next sample redraws from there. It stops while
  the graph can't be seen and when Windows animations are off. The main window's graph does the same.
- The Connections tab keeps `SegmentMapBar` (14px, radius 2). It
  draws every segment's completed bytes at its real offset, with a divider at
  each segment start. The plain indeterminate bar appears only
  while the size is unknown.
- The segment table has the columns N. | Range | Progress (6px bar) | Downloaded |
  Speed | State. State is only what the Host reports: Receiving, Done, Paused or Waiting; per-connection
  retries aren't reported, so the download's own retry count shows on the Overview instead. Its values come from the live stream position
  (`RangeStart - Start + ReceivedBytes`), merged with the saved checkpoint and
  kept at a high-water mark, so numbers never move backwards between
  refreshes. Per-segment speeds are smoothed (EMA 0.25) because raw per-stream rates sawtooth.
- Normal text cells keep the inherited foreground. Never assign a `null`
  Foreground in code; it renders the text invisible.
- Keep the defaults: no gradients, no animated stripes, no percentage text
  drawn over the bar.
- A file's figures, segment map and segment table are one control, `JobDetailsPanel`. A file's own window and
  a package's window both use it, for that file or the selected one, so a file reads the same in both. A
  package's window adds what is about the package: overall progress, the file list and the archive password.
  A package of one file opens the file's own window, as it is an ordinary row on the list.

## 4. Layout and density

- List rows are 27px with 11–12px text. Column headers are 12px SemiBold on
  `InboundChromeBrush`, with a 1px bottom divider.
- Header and row column widths always match, because `ColumnHeaderRow` sets both from one
  `ListColumnLayout`. A new column goes in `ListColumnSets` and in the row template, at the same position.
- A list with a header sorts by a click on it, with a small chevron for the direction, and a second click
  reverses. Blank cells stay at the end either way. Remote and archive rows stay on top of the download
  list whatever the order. Dragging a header's right edge resizes its column and a double-click on that
  edge puts it back. Shift with Left or Right does the same from the keyboard, and the header is one Tab stop
  that the arrow keys move along.
- Until a column is resized, the name column takes the room left. The first resize fixes it, and the last
  column takes the room left from then on, so a column's right edge follows the pointer. A package's file
  is indented inside its icon and name cells, never with the row's padding, or its columns would leave
  their headers.
- Dragging a box with the mouse selects the rows it touches in the download list, and ticks them in a file
  tree, where a box replaces what is there and Ctrl adds to it. A tree's open folder row is passed over,
  since its files have rows of their own; a closed one stands for everything in it. The box is
  `InboundProgressFillBrush` around `InboundHighlightBrush`. A row's own buttons and checkboxes never start one.
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
4. Capture both themes. To switch, use the Theme box in Options or View > Dark
   Mode on the test instance. Both change the test Host's Theme setting, and every
   open window follows. Editing `artifacts\uiqa\data\ui-state.json` does not: its
   `theme` is only a start-up hint, and a `darkMode` there is saved into the
   Host's setting the next time the window connects.
5. Capture a window with `PrintWindow` and flag 2 (`PW_RENDERFULLCONTENT`). It needs no focus and takes in nothing
   else on the desktop, while a copy of the screen can come out black. A menu or flyout is a window of its own, with
   class `Microsoft.UI.Content.PopupWindowSiteBridge` or `Xaml_WindowedPopupClass`, so capture it by itself.
6. Use `--dest`, not `--dir`, with `dm add`. Destinations must be outside the
   user's Downloads folder during harness runs.

## 8. The web UI (D2)

The browser front end (`webui/`) follows the same rules with web tools:

- **Palette:** `webui/src/app.css` holds the `App.xaml` tokens as CSS variables, light by default and dark from the
  system or from `data-theme` on `<html>`, and maps them to Tailwind names (`bg-chrome`, `text-muted`,
  `text-icon-info`, `tone-done` and so on). Components never name a color of their own. A theme picked in the header
  is kept per browser.
- **Status colors and bars** are the desktop's: the state's color on the status text, a 6 px bar under it while a
  download is active or paused with progress, a moving bar when the size is unknown.
- **Layout:** header, sidebar of views with counts, toolbar (icon over label; icons alone below 768 px, the words kept
  for screen readers), the list as a grid that keeps the desktop's columns on a wide window and becomes two or three
  lines per download on a phone. The sidebar becomes a drawer below 768 px; dialogs fill the screen on a phone.
- **Icons:** our own outline glyphs on a 24-unit grid (`Icon.svelte`), colored with the icon tokens.
- **Verifying:** `npm run screens` (see `webui/README.md`) captures every screen at 1360 and 390 pixels wide in both
  themes into `artifacts\webui\`, and fails on a console error, a page error or a page that scrolls sideways. Look
  at the pictures, as with the desktop captures above.
