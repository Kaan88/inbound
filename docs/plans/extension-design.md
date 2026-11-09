# EXT.R8 implementation spec: the extension looks and reads like part of Inbound

Status: spec only. Nothing in the repo was changed. Paths are relative to the repository root.
Owner request (2026-11-09): "have a go over the design of the extension too. too disconnected from main apps. and create new screen shots."

## 0. How the proposals were judged

| Criterion | A (desktop angle) | B (web UI angle) | Taken |
|---|---|---|---|
| Feels like the desktop window | Strongest. It uses the desktop's wording ("Download file info", "Start download", "Browse…", "Options", "Retrying"), a status bar with a "This PC ▾" switcher, file-type glyphs, the SettingsWindow layout with OK/Cancel/Apply, and primary-first buttons on the right. | Good on palette and flatness. It follows the web UI's dialog order (Cancel first) and puts a pill in the header. | A's wording, status bar, options layout and button order |
| Same product as the web UI | It copies the tokens into a second file. | It uses one `tokens.css` and `ui-kit.css` shared with `webui/`, plus shared `icons.ts` and `format.ts`. | B |
| Clarity | Select-then-toolbar actions take two clicks, and the selection resets on every 1 s poll. | Per-row icon buttons, one scroll region, constant row height, and a delete dialog over the list. | B's popup mechanics |
| Maintainability | Two token sources. | One token source, but no test that it stays equal to App.xaml. | B, plus a new token-parity test |
| Browser constraints | Thorough: EXT.B4 fixed px, `html` background for Firefox, `openPopup` from 127, sender checks, CSP-safe theme boot. | Fixed px, one scroll region, no re-measure flicker. | Both |
| Accessibility | Found the 1.4.11 input-border gap. Gives keyboard, live-region and stable-DOM rules. | Found the dark-mode primary button at 2.70:1 (white on #4aa3f0). | Both |

**Base: B** (shared tokens and kit, popup mechanics). **Grafted from A:** desktop wording, a status bar with the target switcher and counts, file-type glyphs, the options page layout, button order, the Theme setting, the accessibility and browser-constraint rules, and the badge colour.
**Rejected:**
- A's selection-plus-toolbar in the popup.
- A's private token copy.
- B's header pill and header segmented switch. The status bar switcher replaces both, as in `MainWindow.xaml:511`.
- B's Cancel-first order. `docs/design-guidelines.md` §6 rules here.
- B's uppercase group headers.
- B's favorite chips. A ▾ menu costs less height.

## 1. Decisions and reasons

1. **One token source.**
   - `webui/src/tokens.css` holds today's `webui/src/app.css:10-100`, moved unchanged, plus `--font-sans`, `--font-mono`, `--radius-control`, `--radius-card`, `--radius-dialog` and `--control-stroke`.
   - The web UI imports it. The extension build copies it.
   - A test asserts that every colour equals `App.xaml`'s Light/Dark dictionaries. App.xaml stays the real source.
   - D8 puts the web UI inside the Windows app, so the three frontends then share one file.
2. **One component kit.** `webui/src/ui-kit.css` is plain CSS with `var()` only and no `@apply`. It has `.btn`, `.btn-primary`, `.btn-danger`, `.btn-icon`, `.field`, `.label`, `.hint`, `.section-title`, `.card`, `.notice`, `.progress`, `.menu`, `.dialog*`, `.segmented`, `.tone-*` and `.tabular`. The web UI imports it in `layer(components)`. The extension links it.
3. **Shared code, not copies.**
   - The extension imports `bytes`, `speed`, `duration`, `stateWord`, `statusText` and `tone` from `webui/src/lib/format.ts`, and glyph paths from the new `webui/src/lib/icons.ts`. esbuild bundles relative imports.
   - Delete `formatBytes`, `formatEta` and `STATE_LABELS` from `popup.ts`. Keep `parseEtaSeconds`.
   - Add "Merging" (`muxing`) and "Scheduled" (`scheduled`) to `stateWord`.
4. **Desktop wording wins over web wording.** The owner prefers the desktop window, so where they differ the extension uses the desktop's terms. The one formatting conflict, duration, is open question Q2.
5. **Popup = Edge-bubble shape with the desktop's parts.**
   - A 40 px header with icon buttons.
   - Notice bars only when there is something to say.
   - One scrolling list with constant 56 px rows and per-row icon actions.
   - A 24 px status bar like the desktop's: "Send to" switcher, counts, connection.
6. **Ask view (EXT.R7), right-click window and Download All use one save-form module** (`src/ui/save-form.ts`). This gives one layout, one set of labels and one folder browser (EXT.B7: folders, default and favorites come from the selected target).
7. **Dialog buttons sit bottom right, primary first, the cancel equivalent last** (guidelines §6). For a destructive confirm, focus starts on Cancel.
8. **The options page uses the SettingsWindow layout.**
   - Page list on the left; a titled page with Section and Hint headings.
   - Footer with OK / Cancel / Apply. OK and Cancel close the tab.
   - The two overlapping checkboxes become one visible box that writes both stored keys.
9. **Theme.** New setting: Match the browser (default), Light, Dark.
   - Stored in `chrome.storage.local` and mirrored to `localStorage['inbound.theme']`, the same key as `webui/public/theme.js`.
   - `theme.js` is copied as a file (MV3 CSP), so the first paint is right.
   - Following the app's theme would be an IPC allow-list change (EXT.R2). It is out of scope.
10. **Flat look.**
    - No shadows except on menus and dialogs (`0 6px 24px var(--shadow)`).
    - Radii are 4 px for controls, 6 px for cards and 8 px for dialogs. The progress bar is 6 px, rounded 3.
11. **Accessibility fixes go into the shared kit, so the web UI gets them too.**
    - `.btn-primary` text uses `--accent-text`.
    - `.field` gets a `--control-stroke` bottom border and a visible focus ring.
    - The web UI screens are re-captured to show the change (§7).
12. **Screenshots are produced by a script from fixed data**, never by hand. Store images are 24-bit RGB PNGs with no alpha, matching the current files (colour type 2).

## 2. Tokens

The colour values are identical in App.xaml (brush `Inbound*Brush`) and in `webui/src/app.css`; the test in §8 keeps them so.

| CSS var | Use in extension | Light | Dark | Source |
|---|---|---|---|---|
| `--chrome` | header, list header, group rows, status bar, dialog header/footer, options strip/page list | #f3f3f3 | #202020 | App.xaml `InboundChromeBrush`; app.css:12/43 |
| `--surface` | body, list, dialog body, fields | #ffffff | #181818 | `InboundSurfaceBrush`; :13/44 |
| `--divider` | 1 px lines | #dadada | #343434 | `InboundDividerBrush`; :14/45 |
| `--text` | primary text | #1a1a1a | #f2f2f2 | app.css:15/46 |
| `--muted` | secondary text, hints, meta | #666666 | #a3a3a3 | `InboundSecondaryTextBrush`; :16/47 |
| `--progress` / `--track` | 6 px bar | #1f6fc5 / #e2e2e2 | #4aa3f0 / #383838 | `InboundProgressFill/TrackBrush` |
| `--error` | inline errors | #c42b1c | #f07a7a | `InboundErrorTextBrush` |
| `--highlight` | notice bars | #dce9f7 | #2b3a4f | `InboundHighlightBrush` |
| `--selected` | selected segment, page-list item | #dce9f7 | #2b3a4f | app.css:36/67 |
| `--accent` / `--accent-hover` / `--accent-text` | primary button, focus, checkbox `accent-color` | #1f6fc5 / #1a5fa8 / #ffffff | #4aa3f0 / #84bdf3 / #0e1b2a | App.xaml `SystemAccentColor*`; app.css:21-23/52-54 |
| `--icon-start/pause/danger/info/neutral/media` | glyphs | #2e8b3e #b7791f #c42b1c #1f6fc5 #6a6a6a #7b4fb8 | #8ccf8c #e8b36a #e08a8a #6fa8dc #a8a8a8 #b49ad6 | `InboundIcon*Brush` |
| `--status-active/done/failed/waiting/neutral` | status word only | #1b65b4 #1b7130 #b3261e #8a5a00 #5f5f5f | #6db3f2 #5dc879 #f58b8b #e0a93e #a3a3a3 | `InboundStatus*Brush` |
| `--hover` | hover fill | rgb(0 0 0/.04) | rgb(255 255 255/.05) | app.css:35/66 |
| `--shadow` | menus and dialogs only | rgb(0 0 0/.18) | rgb(0 0 0/.5) | app.css:37/68 |
| `--font-sans` (new name) | all text | "Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,"Helvetica Neue",Arial,sans-serif | same | app.css:128 |
| `--font-mono` (new name) | URLs, paths, IDs | ui-monospace,"Cascadia Mono",Consolas,monospace | same | app.css:143 |
| `--radius-control` / `-card` / `-dialog` (new) | 4 / 6 / 8 px (.25/.375/.5rem at 14 px root) | | | Tailwind `rounded`/`-md`/`-lg` as used in app.css:148,165, Dialog.svelte:55 |
| `--control-stroke` (new) | bottom border of `.field`, `.segmented` outline | = `--muted` | = `--muted` | Fluent TextBox pattern; fixes 1.4.11 (divider on surface is 1.43:1) |

**Tone mapping.** `tone()` in `format.ts` maps states to tones:
- active: downloading, connecting, verifying
- done: completed
- failed: failed
- waiting: paused, queued, retrying
- neutral: everything else

**Theme selectors.** Light is the default. Dark applies under `@media (prefers-color-scheme: dark) :root:not([data-theme=light])` and under `:root[data-theme=dark]`.

**Type** (root `html { font-size: 14px }`, as app.css:136):

| Text | Size and weight |
|---|---|
| Body, controls | 13 px |
| Popup file name | 13 px / 400 |
| Popup line 2, labels, hints, list header | 12 px |
| Group rows | 12 px / 600 muted |
| Dialog title | 14 px / 600 |
| Options page title | 20 px / 600 (`SettingsWindow.cs:100`) |
| Section | 14 px / 600 |
| Hint | 12 px in `--muted`, not opacity |

All live numbers use `tabular-nums`.

**Fixed sizes.**

| Element | Size |
|---|---|
| Popup width | 380 |
| Popup header | 40 |
| Notice | 32 |
| Group row | 24 |
| Popup row | 56 (8 + 18 + 16 + 6 + 8), including a reserved bar slot |
| Status bar | 24 |
| Icon button | 28×28 with a 16 px glyph |
| Dialog footer | 48, gap 8, buttons min-width 92 |
| Options page list | 190 wide, items 32 tall |
| Dependent option indent | 28 |

**Glyphs.**
- Toolbar glyph colours: start = play, pause = pause, danger = trash, info = download, neutral = settings/more.
- File-kind glyphs follow `JobViewModel.cs:418-430` (document, music, video, program, image, disc, archive, file) in `--icon-info`. Add them to `icons.ts`, so the web UI gets them too.
- Brand: `icons/icon*.png`, the plane-and-tray mark.

## 3. Wireframes

### 3.1 Popup, list view: 380 × up to 600

Layout:
- `html,body{width:380px}` and `html{background:var(--surface)}`.
- `body{display:flex;flex-direction:column;max-height:600px}`.
- Header, notices and status bar are `flex:none`. The list is `flex:1;min-height:0;overflow-y:auto;scrollbar-width:thin`.
- No vw, vh or % heights.

```
|<----------------------------------- 380 ----------------------------------->|
| [icon16] Inbound Download Manager          [⤓] [⏸] [⧉] [⚙]                  | 40 --chrome; 28px btn-icon:
|                                                                             |   Download all links / Pause catching /
|                                                                             |   Open Inbound / Options (tooltips+aria)
+-----------------------------------------------------------------------------+
| ⓘ Downloads are not caught automatically.                   [Turn on]       | 32 .notice (0-2 shown), --highlight
+-----------------------------------------------------------------------------+
| This PC                                                                     | 24 group row, only when server paired
| 💿 linux-desktop-24.04-amd64.iso              5.00 GB        [⏸] [🗑]       | 18: name 13px ellipsis, size 12 muted right
|    Downloading 62%              18.4 MB/s · 1m 45s                          | 16: status (tone) · rate · time, muted
|    ██████████████████████░░░░░░░░░░░                                        |  6: bar, reserved slot always
|-----------------------------------------------------------------------------|  1: --divider
| 📄 project-assets.part2.bin                    1.00 GB        [▶] [🗑]       |
|    Paused 40%                                                               |
|    █████████░░░░░░░░░░░░░░░░                                                |
|-----------------------------------------------------------------------------|
| ▷ installer-2.4.1.exe                                         [🗑]          | unknown size: blank
|    Failed · HTTP 403 Forbidden                                [⟳]          | message muted, ellipsis + title
| home-server (server)                                                        | 24 group row
| ...                                                                         | ≤ 20 per place; list scrolls here only
+-----------------------------------------------------------------------------+
| Send to: This PC ▾ │ 4 downloads · 1 active                    Connected    | 24 --chrome, 12px muted
+-----------------------------------------------------------------------------+
```

**Rows.**
- Action buttons are always visible and come from `actionsFor()` (`job-list.ts`): ⏸ Stop when `canPause`; ▶ Resume; ⟳ Retry for failed or cancelled; 🗑 Delete. Two buttons at most, in a fixed column so names don't jump.
- `aria-label` is "Stop linux-desktop-24.04-amd64.iso" and so on.
- Double-clicking the row or pressing Enter on it opens the app (`dm.openApp`).
- The bar shows only for downloading or paused with a known size. When the size is unknown, it is the moving bar (indeterminate), and still under reduced motion it gets no animation.

**Status bar.**
- **Left, "Send to: This PC ▾":** shown only when a server is paired and the app is installed. It is a `.menu` opening upward, with "This PC" and "<server name>" and a check on the current one, and it calls `dm.setDefaultTarget`. It mirrors `ServerSwitcher`.
- **Middle:** "N downloads · A active · Q queued", leaving out zero parts (`MainWindow.xaml.cs:547`).
- **Right:** "Connected", "Server connected", or "Not connected" in `--status-failed`. The tooltip carries `connectionProblem()`.

**Pause catching.** When catching is paused, the header button becomes Resume catching (play in `--icon-start`) and the badge reads OFF.

**Delete** opens a `.dialog` over the list with backdrop rgb(0 0 0/.45). The popup height does not change.

```
| Delete download?                                        | 14/600
| Remove 'project-assets.part1.bin' from the list?        | 13
| ☐ Delete from disk: the downloaded file                 | completed only, unticked
|   (unfinished: "Partial data is always removed.")       | 12 muted
|                                  [Delete] [Cancel]      | Delete = .btn-danger; focus on Cancel
```

### 3.2 Popup states

| State | Shows |
|---|---|
| Checking (≤ 300 ms) | Rows are empty, and the status bar says "Connecting…". After 300 ms: three skeleton rows in `--hover`, static under reduced motion. |
| Empty | 140 px centred: the download glyph (32, `--icon-info`), then "No downloads yet" (13/600), then the hint "Downloads you send from this browser, and the app's own, appear here.", then the `.btn` "Download all links on this page". |
| Automatic sending off | A notice with the alert glyph in `--icon-pause`: "Downloads are not caught automatically." with [Turn on], which opens `options.html#general`. |
| Catching paused | A notice: "Catching paused, 4 min left. Downloads stay in the browser." with [Resume now]. Header button and badge as in 3.1. |
| Left in browser (≤ 30 min, ≤ 3) | One notice: "2 downloads stayed in the browser" with [Show]. Show expands lines inside the notice, each "**name** stayed in the browser: reason" with ✕ to hide it for the session. |
| App not installed, no server | The list area becomes an empty state: the plug glyph (`--icon-danger`), "Inbound Download Manager isn't installed on this PC.", "Downloads stay in the browser.", and [Get the app] as `.btn-primary`. Row and header actions other than Options are disabled. |
| App installed, not answering | Same layout: "Can't reach Inbound on this PC.", then the `connectionProblem()` text, then [Try again]. Status bar "Not connected". |
| Forbidden ID | Same layout, with the ID in `--font-mono`. |
| Server paired, both answer | Group rows and the switcher. |
| Server not answering | A notice with the cloud glyph in `--icon-danger`: "home-server isn't answering." with [Open web UI]. Its group shows one muted line, "Not answering". |
| Server only (`localMissing`) | No switcher and no groups. ⧉ opens the web UI and its tooltip is "Open web UI". The status bar right reads "Server connected" or "Server not answering". |
| Action failed | One 12 px line in `--error` above the status bar, `role="alert"`, with ✕. It clears on the next action. |
| Worker restarting | A notice in `--error`: "The extension is restarting. Close and reopen this popup." |

### 3.3 Popup, ask view (EXT.R7; the logic is on branch `ext-ask-popup`, the layout comes from here)

It replaces the list while caught downloads wait.

```
| [icon16] Download file info                          1 of 2  [‹][›] [✕]     | 40 --chrome; ✕ = Keep in browser
+-----------------------------------------------------------------------------+
| 📄 project-assets.part1.bin                                                  | 13/600, glyph 24 --icon-info
|    1.00 GB · downloads.example.com                                          | 12 muted; host only
| File name                                                                   | .label
| [project-assets.part1.bin                                               ]   | .field; name selected up to extension
| Send to   [ 🖥 This PC | ☁ home-server ]                                     | .segmented 28; only when both work
| Destination folder                                                          |
| [D:\Downloads\Compressed                                  ▾] [Browse…]      | mono; ▾ = favorites of the target
| 26.9 GB free                                                                | .hint, only if reported
| ▸ What is sent                                                              | <details>, full text inside
+-----------------------------------------------------------------------------+
|                        [Start download] [Download later] [Keep in browser]  | 48 footer --chrome
```

**Browse…**
- On This PC it calls `host.pickFolder`.
- On a server it swaps the body for the folder panel (styled as `FolderPicker.svelte`): a path bar with [↑ Up], 28 px rows with the folder glyph, a free-space hint, and [Use this folder] [Cancel].
- Switching the target refills the default folder and the favorites, and clears a folder the user did not edit (EXT.B7).

**Defaults and keys.**
- The default button follows Options > Default start (Q1).
- Enter triggers the default button. Esc = Keep in browser.
- Losing focus does not answer: the item stays pending in `storage.session`. While items wait, the badge shows their count on #1f6fc5 (white text, 5.07:1).

### 3.4 Right-click window (handoff)

`windows.create` at 520 wide, with the height from `fitWindowToContent()`. Today it is 560×680 at `background.ts:380-384,654-658,722-726`.

```
| [icon16] Download file info                                                    | 40 .dialog-header
+--------------------------------------------------------------------------------+
| Address                                                                        |
| https://downloads.example.com/files/project-assets.part1.bin           [Copy]  | plain text, mono 12 muted, no box
| File name        [project-assets.part1.bin                                  ]  |
| Send to          [ 🖥 This PC | ☁ home-server ]                                |
| Destination folder [D:\Downloads\Compressed                     ▾] [Browse…]   |
| Applies to this download only.                                                 | .hint
| ▸ What is sent: the address, the page, your user agent and this site's        |
|   cookies, to Inbound on this PC.                                              |
+--------------------------------------------------------------------------------+
|                               [Start download] [Download later] [Cancel]       | "Keep in browser" when caught
```

States, keeping today's logic:
- **Unavailable:** an `--error` notice with [Close] only.
- **Sending:** buttons disabled, and the primary reads "Sending…".
- **Success:** the window closes.
- **Error:** an alert line above the footer.
- **Folder pick open:** EXT.B2 `FormGate` disables Start.

### 3.5 Download All window: 720 × 640

```
| [icon16] Add links                         downloads.example.com · 24 links   | 40 header; 12 muted right
| [🔍 Filter by name, type or site                             ] [All types ▾]  | 40 toolbar --chrome
+--------------------------------------------------------------------------------+
| ☑ │ File Name                                  │ Type │ Site                   | 27 header --chrome, 12/600, sortable
| ☑ │ 📄 project-assets.part1.bin                  │ BIN  │ downloads.example.com  | rows 27; URL in title + aria-description
| ☐ │ 💿 linux-desktop-24.04-amd64.iso             │ ISO  │ downloads.example.com  | list flex:1, the only scroll
+--------------------------------------------------------------------------------+
| Package name [Example downloads page          ]   (2+ links; default page title)|
| Send to [🖥 This PC|☁ home-server]  Destination folder [D:\Downloads ▾][Browse…] |
| Start [Start now ▾]          ▸ What is sent                                     |
+--------------------------------------------------------------------------------+
| 4 selected                                                    [Add] [Cancel]   | 48 footer
```

- **Empty:** "No downloadable links on this page."
- **Scanning:** "Scanning the page…" with the moving bar.
- The `Start` choices use the AddLinksDialog wording: "Start now" or "Add to queue" (`AddLinksDialog.cs:106-108`).
- The context-menu item stays "Download all links…".

### 3.6 Options page (a tab)

Max width 1000, centred. Below 640 px the page list becomes a row of tabs.

```
| [icon16] Inbound Download Manager · Browser extension                            | 40 --chrome strip
+--------------+-------------------------------------------------------------------+
| ⚙ General    | General                                              20/600        |
| ⤓ Catching   | Automatic sending                                    14/600        |
| ☁ Server     | ☑ Send downloads to Inbound automatically                          |
| 🔒 Site access|     Downloads that match your rules stop in the browser and       | 28 indent, hint
| 🔌 Connection |     start in the app. Right-click and Download all links work     |
| ⓘ About      |     either way.                                                    |
|              | ☑ Show notifications                                               |
|              | When a download is sent                                             |
|              | (•) Save to the app's default folder  ( ) Ask where to save          |
|              | Default start  [Start download ▾]                                   |
|              | Theme          [Match the browser ▾]                                |
+--------------+-------------------------------------------------------------------+
| <inline error in --error, selects its page>                [OK] [Cancel] [Apply] |
```

**Pages.**
- **General:** as drawn above.
- **Catching:** file-type and site rules, [Restore the default list], and the Delete-key hint.
- **Server:** pairing, state, default place.
- **Site access:** shown only when needed.
- **Connection:** [Check connection], [Open Inbound], [Get the app].
- **About:** the version, the full privacy text at `#privacy`, and the policy link.

**Navigation and footer.**
- The URL hash selects the page.
- Links are styled `color:var(--accent)` with an underline on hover; no visited purple.
- OK saves and closes the tab (`chrome.tabs.getCurrent` then `tabs.remove`). Cancel discards and closes. Apply saves.

**Merged box.** "Send downloads to Inbound automatically" is checked when `enabled && autoIntercept`, and writing it sets both keys. `background.ts` is unchanged.

## 4. Wording

| Where | Now | New |
|---|---|---|
| Settings link/button | Settings | Options |
| List title | Downloads in Inbound / Recent downloads | (none; group rows only when paired) |
| Row meta | 3.1 GB / 5.0 GB · 18.4 MB/s · ETA 1m 45s | `Downloading 62%` · `18.4 MB/s` · `1m 45s` (size on line 1) |
| State | Waiting to retry | Retrying |
| Unknown size | 0 B / unknown size | blank |
| Status card | Connected to the desktop app / Desktop app not reachable | status bar "Connected" / "Not connected"; empty state "Can't reach Inbound on this PC." |
| Auto card | Automatic sending: on/off/paused | nothing when on; notices when off or paused |
| Button | Pause catching for 5 min | Pause catching (tooltip "Let the browser keep downloads for 5 minutes"); Resume catching |
| Button | Open app | Open Inbound (Open web UI when server only) |
| Handoff title | Send to Inbound Download Manager | Download file info |
| Handoff buttons | Download / Cancel + Start select | Start download / Download later / Cancel (Keep in browser when caught) |
| Folder | Save in / Destination folder (optional) / Choose… | Destination folder / Browse… |
| Target | Save to / Send downloads to | Send to |
| Batch | Download All / Download selected / Close / Start immediately | Add links / Add / Cancel / Start now |
| Options heading | Inbound Download Manager Bridge / INTEGRATION | strip "Inbound Download Manager · Browser extension"; page "General" (sentence case) |
| Two checkboxes | Allow automatic sending… / Automatically send downloads… | Send downloads to Inbound automatically |
| Privacy paragraphs | 5–7 lines on every form | "▸ What is sent" with the full text inside, plus the full text in About and on first-run consent; wording unchanged |
| Delete | (inline row) | Delete download? / Remove '<name>' from the list? / Delete from disk: the downloaded file / Partial data is always removed. |
| Badge | OFF on #6b6b6b | OFF on #8a5a00 (5.93:1); pending-ask count on #1f6fc5 (5.07:1) |

The manifest name, the store listing text and the data-use disclosure stay as they are. The text inside the privacy `<details>` is the current text, unchanged, and is checked against `docs/chrome-store/LISTING.md`.

## 5. Browser constraints

- **Popup size.** Chrome, Edge and Firefox cap a popup at 800×600. Use fixed px only (EXT.B4) and `html{background:var(--surface)}` so Firefox's panel colour doesn't show.
- **No popup re-measure.**
  - Row height is constant, and the bar slot is reserved.
  - Notices render in the first frame from `dm.status`.
  - Delete is an overlay, so it adds no height.
- **Polling.** The 1 s poll updates text in rows keyed by `jobId`, with no `replaceChildren`, so focus and open menus survive it.
- **Ask view.** `action.openPopup()` without a gesture needs Chrome or Edge 127+. Feature-detect it and fall back to the 520 window (minimum_chrome_version stays 109). Firefox 159 is checked in the test profile before relying on it.
- **Sender checks.** `background.ts` must accept `popup.html` for `dm.getHandoff`, `dm.confirmHandoff`, `dm.keepInBrowser` and `dm.browseServerFolders`, with tests next to `server-target.test.cjs:132`. This is owned by EXT.R7. R8 only uses it.
- **CSP.** No inline script. `theme.js` is loaded as a file in `<head>`. Colours never go in `style=` attributes; bar widths may.
- **Theme.** In Firefox, `prefers-color-scheme` follows the Firefox theme. The explicit Theme setting overrides it everywhere.

## 6. Accessibility

Contrast (WCAG 2.x, computed for this spec and asserted by the test in §8):

| Pair | Light | Dark |
|---|---|---|
| muted on surface / chrome | 5.74 / 5.17 | 7.04 / 6.46 |
| muted on highlight (notice) | 4.66 | 4.57 |
| waiting on highlight | 4.81 | 5.44 |
| active on selected | 4.78 | 5.15 |
| done on surface | 6.08 | 8.46 |
| primary text | white on #1f6fc5: 5.07 | #0e1b2a on #4aa3f0: 6.44 (today white: 2.70, fixed) |
| progress on track (non-text, ≥3) | 3.92 | 4.35 |
| field stroke (`--muted`) on surface (≥3) | 5.74 | 7.04 |
| divider on surface | | 1.43, decorative only, never the only control boundary |

`--icon-pause` (light) is 3.64 on white: glyph only, never text.

**Focus.** `:focus-visible{outline:2px solid var(--accent);outline-offset:1px}`. Inside list rows the offset is −2px. `.field:focus` adds `box-shadow:0 0 0 1px var(--accent)`. Outlines are never removed.

**Popup structure and keyboard.**
- Tab order: header buttons, notice action, row buttons in row order, status-bar switcher.
- The list is `<ul role="list">`. Each row is an `<li>` whose label is "<name>, <status>, <size>".
- The switcher has `aria-haspopup="menu"`. Arrow keys move, Esc closes, and focus returns to the button.

**Live updates.**
- The list is not a live region.
- Action results go to one hidden `aria-live="polite"` line. Errors use `role="alert"`.

**Dialogs.** The delete dialog and the folder panel trap focus, Esc cancels, and focus returns to the row's button.

**Targets and motion.**
- Hit targets are at least 24×24 (icon buttons are 28).
- `prefers-reduced-motion: reduce` turns off transitions and the moving bar's animation (it stays a static 1/3 fill at 60 % opacity).

## 7. Screenshot production plan

**New script:** `extensions/chrome/scripts/store-screens.mjs`. Run it with `npm run screens` in `extensions/chrome`, from the Bash tool or a normal terminal, never from the PowerShell tool.

**Determinism.**
- It builds `dist`, serves it from a local static server on 127.0.0.1, and opens each real page in Playwright Chromium. Use `@playwright/test` 1.65.0, the same as `webui`.
- `addInitScript` installs `tests/fixtures/chrome-stub.js`. The stub answers `chrome.runtime.sendMessage`, `storage` and `i18n` from `tests/fixtures/screens-data.json`. There is no native host, no network and no real profile.
- Fixed clock: `page.clock.setFixedTime('2026-11-09T10:00:00')`.
- `reducedMotion: 'reduce'`, `deviceScaleFactor: 1`.
- The script fails if `document.fonts.check('13px "Segoe UI Variable Text"')` is false, so it runs on Windows only.
- It also fails on any console error, any page error, or horizontal overflow, the same rules as `webui/scripts/screens.mjs`.

**Fixture data.** All invented, with no cookies or real hosts:
- `linux-desktop-24.04-amd64.iso`: 5.00 GB, downloading 62 %, 18.4 MB/s, 105 s left.
- `project-assets.part2.bin`: paused 40 %.
- `project-assets.part1.bin`: completed.
- `installer-2.4.1.exe`: failed, HTTP 403.
- Host `downloads.example.com`, server `home-server`.
- The same names are used in the desktop capture.

**QA set** (not committed; goes to `artifacts\extension-screens\`). Every state is captured in light and dark at 1:1:
- popup: list, each §3.2 state, delete dialog, switcher menu open, ask view (both targets and the folder panel)
- handoff: default, server, unavailable, error
- Download All: populated, empty, scanning
- options: each page, and the 600 px narrow layout

**Store set** (committed to `docs/chrome-store/images/`). Images are composed by `scripts/store-frame.html`:
- `--chrome` background.
- A caption on the left: 28 px/600 title and 16 px muted subline.
- The surface on the right. The popup is scaled 1.5× with a CSS transform, which stays vector-crisp. Windows are shown at 1:1 inside a dialog frame with `--divider` and `--shadow`.
- No imitation of browser chrome or any third-party branding.
- Each file is converted to 24-bit RGB PNG with no alpha by a small `zlib` re-encoder in the script, and checked as colour type 2 at the exact size.

| File | Size | Content | Theme |
|---|---|---|---|
| screenshot-1-popup.png | 1280×800 | popup list: 4 rows across states, This PC/server groups, status bar with switcher. Caption "Your downloads, one click away" | dark |
| screenshot-2-ask.png | 1280×800 | popup ask view, This PC, favorites menu closed. Caption "Choose where each download goes" (needs EXT.R7 merged; until then the handoff window) | light |
| screenshot-3-batch.png | 1280×800 | Add links with 6 links, 4 selected | dark |
| screenshot-4-options.png | 1280×800 | Options > General | light |
| screenshot-5-app.png | 1280×800 | the desktop window with the same 4 downloads | dark |
| promo-small-440x280.png | 440×280 | brand icon at 64, "Inbound Download Manager" 22/600, "Send browser downloads to your Windows download manager" 13 muted, and a crop of 2 popup rows at 1:1 | dark `--chrome` |

**Screenshot 5** is a real app capture, not a mock:
- Use `scripts\ui-capture.ps1`'s `PrintWindow` path (guidelines §7) with `DOWNLOADMANAGER_INSTANCE=uiqa` and TestServer URLs carrying the fixture names (`/slow/linux-desktop-24.04-amd64.iso?...`, a 403 URL, a small completed file).
- The window is sized to exactly 1280×800, and the Last Try Date column is hidden, so dates don't drift.
- Start it from Bash.

**Old files.** Remove `screenshot-2-handoff.png`, or replace it with the ask image. Update the captions and file list in `docs/chrome-store/LISTING.md`.

## 8. Tests and acceptance checklist

**Automated** (`extensions/chrome/tests`, `node --test`):
- [ ] `tokens-parity.test.cjs`
  - parses `App.xaml` Light/Dark `Inbound*Brush` and `SystemAccentColor*` and `webui/src/tokens.css`
  - asserts each mapped token is equal in both themes and both dark selectors
  - asserts the §6 contrast pairs meet their minimums
- [ ] `popup-render.test.cjs` (jsdom with the stub)
  - rows keyed by `jobId` keep their node and focus across two polls
  - row height is constant when the state changes
  - unknown size renders empty
  - the "Retrying" label
  - actions match `actionsFor()`
- [ ] `options-settings.test.cjs`
  - the merged box reads `enabled && autoIntercept` and writes both keys
  - the hash selects the page
  - Cancel leaves storage unchanged
  - an invalid entry selects its page and blocks OK
- [ ] `theme.test.cjs`: the Theme setting writes `storage.local` and `localStorage['inbound.theme']`, and `theme.js` sets `data-theme`.
- [ ] `firefox-build.test.cjs` extended: `tokens.css`, `ui-kit.css`, `theme.js` and `extension.css` exist in `dist` and `dist-firefox`.
- [ ] `npx tsc --noEmit`, `npm run build`, `npm run build -- --target firefox`, and all existing extension tests pass.
- [ ] `webui`: `npm run check`, `npm test`, and `npm run screens`. Diff the captures before and after: identical apart from the `.field` stroke and focus ring and the primary-button text colour.

**Manual and evidence** (record in PROGRESS.md with paths):
- [ ] `store-screens.mjs` runs twice with byte-identical PNGs, and the store files are RGB with no alpha at exact sizes.
- [ ] Chrome for Testing 156+ (`--load-extension`, test native host via `scripts\register-test-native-host.ps1`, `-Remove` afterwards):
  - the popup against the real Host: list, pause, resume, delete with and without the file, switcher, catching pause
  - right-click handoff, Download All, and Options OK/Cancel/Apply
- [ ] Edge: the popup and a caught download look the same as in Chrome.
- [ ] Firefox 159 test profile (WebDriver BiDi install of `dist-firefox`):
  - popup size correct on the first open (EXT.B4)
  - no panel colour around the content
  - theme follows the setting
- [ ] Keyboard-only pass through every surface, and an NVDA or Narrator spot check of the popup rows and the delete dialog.
- [ ] Dark mode is walked on every surface. The light walk is deferred, as the owner asked. The light store shots are still produced and looked at.
- [ ] Side by side at 100 %: popup next to the desktop window and the web UI phone list. Same greys, status colours, bar, glyphs and words.

## 9. File-by-file plan

Branch `ext-design` from `dev`. R7 on `ext-ask-popup` rebases on it for the shared form.

| File | Change |
|---|---|
| `webui/src/tokens.css` (new) | `app.css:10-100` moved, plus font, radius and `--control-stroke` tokens |
| `webui/src/ui-kit.css` (new) | plain-CSS kit (§1.2), including `.notice`, `.progress`, `.menu`, `.dialog*`, `.segmented`; `.field` stroke and focus; `a` style |
| `webui/src/app.css` | `@import "./tokens.css"`; `@import "./ui-kit.css" layer(components)` replacing 146-172; `@theme inline` unchanged |
| `webui/src/lib/icons.ts` (new) | paths from `Icon.svelte` plus file-kind glyphs, plug, retry, chevrons |
| `webui/src/components/Icon.svelte` | import paths from `icons.ts` |
| `webui/src/lib/format.ts` | `stateWord`: muxing → Merging, scheduled → Scheduled (duration per Q2) |
| `extensions/chrome/scripts/build.mjs` | copy `../../webui/src/tokens.css`, `ui-kit.css`, `../../webui/public/theme.js`; rename `styles.css` → `extension.css` in `staticFiles` |
| `extensions/chrome/styles.css` → `extension.css` | only extension-specific layout: popup grid, rows, status bar, options two-pane; no colours except tokens |
| `extensions/chrome/popup.html`, `src/popup.ts` | new structure (§3.1–3.2); keyed incremental render; shared `format.ts`; delete dialog; switcher menu; notices |
| `extensions/chrome/src/job-list.ts` | group labels "This PC" / "<name> (server)"; `actionsFor()` unchanged |
| `extensions/chrome/src/ui/icon.ts` (new) | builds `<svg viewBox="0 0 24 24" stroke-width="1.75" fill="none" stroke="currentColor">` from `icons.ts` |
| `extensions/chrome/src/ui/file-kind.ts` (new) | extension → glyph, same groups as `JobViewModel.cs:418-430` |
| `extensions/chrome/src/ui/dialog.ts`, `ui/menu.ts` (new) | focus-trapped confirm; upward menu with arrow keys and Esc |
| `extensions/chrome/src/ui/save-form.ts` (new) | file name, Send to, Destination folder + favorites + Browse…, server folder panel, What is sent; used by handoff, the ask view and batch |
| `extensions/chrome/handoff.html`, `src/handoff.ts` | §3.4 on save-form; buttons and wording |
| `extensions/chrome/batch.html`, `src/batch.ts` | §3.5; Package name; Add/Cancel |
| `extensions/chrome/options.html`, `src/options.ts` | §3.6 pages, hash routing, OK/Cancel/Apply, merged box, Theme |
| `extensions/chrome/src/background.ts` | window sizes 520 / 720×640; badge colours; no logic change (sender checks in R7) |
| `extensions/chrome/manifest.json` | version 0.6.0; name unchanged |
| `extensions/chrome/package.json` | `@playwright/test` 1.65.0 dev dependency; `"screens"` script |
| `extensions/chrome/scripts/store-screens.mjs`, `scripts/store-frame.html` (new) | §7 |
| `extensions/chrome/tests/fixtures/chrome-stub.js`, `screens-data.json` (new) | stub and fixed data |
| `extensions/chrome/tests/*.test.cjs` | §8 new and extended tests |
| `docs/chrome-store/images/*`, `docs/chrome-store/LISTING.md` | new images and captions |
| `docs/design-guidelines.md` | new §9 "The browser extension": token files, kit, popup anatomy, screenshot script |
| `ROADMAP.md`, `PROGRESS.md` | EXT.R8 status with evidence paths; R7 dependency for screenshot 2 |

## 10. Open questions for the owner

1. **Start buttons in the save forms.** "Start download" and "Download later" replace the Start dropdown, as in the app's "Download file info". The last button used is no longer remembered; the default lives in Options > Default start. Recommended: yes.
2. **Time format.** The desktop shows `1m 45s` and the web UI shows `1 min 45 s`. The extension will share the web code. Change `format.ts` to the desktop's form, so all three match? Recommended: yes.
3. **Web UI side effects.** The shared kit gives the web UI the stronger field border and focus ring and the darker text on blue buttons in dark mode. May the web UI header and favicon also use the plane-and-tray icon instead of the blue square (`webui/public/favicon.svg`, #2563eb)? Recommended: yes to both.
