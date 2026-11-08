<!-- D2c, owner 2026-11-09. Six area reviewers compared the code of both sides; each finding was then checked against the code by a second agent (174 kept, 1 refuted). Not yet checked on screen. -->

# D2c review: web UI against the desktop window

Reviewed 2026-11-09. The desktop window is the reference. The web UI keeps its look and should follow the desktop's flows, wording, defaults and features wherever a browser allows.

## 1. Summary

- The web list has no row menu. Most of the desktop's per-download commands can't be reached at all: queue, priority, package commands, Redownload, Move/Rename, Change Category and Properties.
- Package rows behave differently. Resume and Pause act on each file and bring cancelled files back. The package status uses its own maths. There is no package window and no Cancel Package.
- Adding is a different flow. There is one multi-line dialog, with no single-address Add URL, no file-info check and no per-line ticks. Uncached provider items need a second approval that the desktop doesn't ask for.
- Some web actions lose data the desktop keeps:
  - Delete Completed can delete files; on the desktop it only clears the list.
  - Saving one Settings page silently drops unsaved edits on the other pages.
  - Changing a site login's site or user name drops its saved password.
- Settings save page by page, or at once, where the desktop has one OK/Cancel/Apply. Category folders and custom categories can't be edited.
- Queues: queued files can't be reordered, moved or removed. Deleting a queue doesn't ask and fails for any queue that holds downloads. "Start again every" really means "re-check finished files".
- My TorBox: Download works only for cached items, with no file choice, and it reports success even when nothing started.
- Keyboard: Delete, Shift+Delete, Ctrl+F, F3, the arrow keys and Ctrl+A do nothing.

## 2. Differences by area

**Key:**
- **Kind:** MW = missing in the web, WD = works differently, BUG = broken.
- **BL (browser limit):** the desktop feature only makes sense on the Host's own PC. "What to do" then gives the browser equivalent; otherwise it means "follow the desktop".
- **Effort:** S = small, M = medium, L = large.

File references for every # are in section 5.

### L. Download list (ListScreen, sidebar, toolbar, status bar)

| # | Difference | Kind | What to do | Effort |
|---|---|---|---|---|
| L1 | No context menu on rows | MW | Add a right-click menu, plus long-press or a "⋯" button on touch. Use the desktop's items, order and wording, and dim items with the same FileMenuRules reasons. Drop Open, Open With and Open Folder, or swap in the L29 equivalents. | L |
| L2 | Queue membership and priority can't be changed anywhere | MW | Add "Add to Queue ▸" (queues in order), "Remove from Queue" and "Priority ▸ High/Normal/Low" to the row menu and to details. Disable Priority for finished downloads. Send package rows as group.action, as RunOnSelectionAsync does. | M |
| L3 | No package commands: Group into Package, Remove from Package, Rename Package, Cancel Package | MW | Add all four, plus the conditional Cancel Package toolbar button, using group.action. Keep the desktop's rules: 2+ files to group, a 100-character name, the name suggestion, and "Cancel Package / Keep Downloading" with Keep as the default. | M |
| L4 | Resume/Pause on a package row acts on each file and revives cancelled files; Delete on a package covers different files | WD | Send group.action resume/pause, show the leftCancelled note, and enable the buttons by PackageRules.ResumeTargets/PauseTargets. Pick one rule for what Delete on a package row covers and use it on both sides; the web's "only the files in this view" may be the safer rule. | M |
| L5 | Package status maths differs from DownloadGroupSummary: rate summed over every file, queued checked before paused, a completed+cancelled mix shown as paused, done/count used as progress | BUG | Port DownloadGroupSummary exactly, or have the server send it. Show "<state> · done/count", the package's time left and its size. | S |
| L6 | A package sits where its first visible file lands; its files follow the list sort | WD | Sort package rows by their combined values. List their files in natural name order under the default sort. | S |
| L7 | Delete and Shift+Delete do nothing | MW | Handle them on the focused list (not in text fields). Shift+Delete opens the dialog with "Delete from disk" ticked. | S |
| L8 | No Ctrl+N, Ctrl+F, F3, Ctrl+M, Ctrl+Shift+S, Ctrl+A or arrow keys; every row is its own tab stop | MW | Add a document-level key handler. Make the grid one tab stop with roving focus: arrows move, Shift+arrows extend, Ctrl+A selects all. Ctrl+N is taken by browsers, so pick another key for it. | M |
| L9 | Delete dialog: different title and question, the file name never shown, the delete-file box always shown, first focus on the checkbox, Delete placed before Cancel | WD | Use "Delete download?" / "Delete N downloads?" and "Remove '<name>' from the list?". Show "Delete from disk: the downloaded files and any partial data" only when a completed file is selected. Add the removed-after-extraction case. Put first focus on Cancel. | S |
| L10 | Delete Completed offers to delete the files | WD | Make it list-only, with the desktop's title, text ("…The downloaded files will stay on disk.") and a "Remove from list" button. | S |
| L11 | Only 4 sortable columns, no reverse, sort lost after leaving the list; status sorts by raw state key | WD | Make every header sortable, with the desktop's starting direction, click-to-reverse and an up/down marker. Remember the sort in localStorage. Sort status by the words shown. | S |
| L12 | No Q column; "Added" in place of "Last Try Date" | WD | Add Q: queue number + 1, queue name as tooltip, empty when not queued, sortable, may hide on phones. Show Last Try Date (completedAt ?? updatedAt) under the desktop's title. | S |
| L13 | Cell text differs: status words, size blank when the total is unknown, no rate or time left while retrying, duration format, Description = failure OR note OR description | WD | Port ToDisplayState/JobNotes and DurationText.TimeLeft. Match the size/rate/ETA conditions. Build Description as "note · (description or page URL)" and move the failure to the status tooltip. | S |
| L14 | No category entries in the sidebar (Compressed, Documents, Music, Programs, Video, Other, custom) | MW | Add them under All Downloads and filter by categoryId, falling back to FileCategories.For(fileName) as the desktop does. | S |
| L15 | Sidebar order and labels differ ("All downloads", "Waiting at …", flat list, "Downloads" heading) | WD | Use "All Downloads", a "Categories" heading, Active/Queued/Failed indented under Unfinished, then Finished, Extractions, Queues, "Waiting for <provider> (n)" and "My <provider>". The extra Settings link can stay. | S |
| L16 | No speed graph | MW | Add the collapsible bottom graph: 1-second samples including retrying jobs, gaps while disconnected, shown or hidden as remembered in localStorage. | M |
| L17 | Status bar wording and counts differ | WD | Show "N downloads · X active · Y queued · Z retrying" and "(N selected)", with active meaning connecting, downloading or verifying. The total speed can stay. | S |
| L18 | Live header search instead of Find/F3; fewer fields searched | WD | Keep the live box. Ctrl+F focuses it, F3 moves to the next row. Search the desktop's fields for every row kind (detail and extraction text, remote status and description). | S |
| L19 | Toolbar set, labels and enablement differ; no Options or Scheduler buttons; no tooltips | WD | Use the desktop order: Add URL \| Resume, Stop, Stop All, [Cancel Package] \| Delete, Delete Completed \| Options, Scheduler \| Start Queue, Stop Queue. Options links to #/settings and Scheduler to the #/queues schedule section. Dim Stop All when nothing can pause. Add title tooltips. | S |
| L20 | Start/Stop Queue always use the default queue | WD | Use the selected download's queue, then the package's shared queue, then the default. Add a split button listing "Start <name> download queue" / "Stop …" for every queue. | S |
| L21 | Resume is offered for permanent failures (DRM, live stream); the job is re-queued and fails again | BUG | Enable Resume by job.canResume only. A permanent failure needs Redownload. | S |
| L22 | Double-click/Enter: a job always opens details; an archive row reacts only when it needs a password; a remote row opens the dialog | WD, BL | Opening details for a completed file is the right browser equivalent. An archive row should run its primary action. Enter on a remote row should run RemoteRules.EnterFor's action directly. | S |
| L23 | Remote and archive rows highlight on click, but no action uses that selection (no checkbox; Delete, Enter, F5 and bulk actions ignore them) | WD | Give them checkboxes. Delete removes them (with confirmation, leaving out sets that are extracting). Enter and F5 work on remote rows, plus bulk Approve and "Download All Files". | M |
| L24 | The remote row's Actions button is hidden between 768 and 1023 px | BUG | Move it to the name or status cell, or rely on the row menu (L1). | S |
| L25 | Extraction records without a file on the list are missing from All Downloads; Extractions sorts by updatedAt, searches names only, is always in the sidebar, and the column shows only when a set has jobs | WD | Show those records above the files on All Downloads. Sort by createdAt and search stateText. Gate the sidebar entry on archive.list being answered: hello has no "archive" capability. Show the column whenever sets exist. | S |
| L26 | Empty-list text is generic | WD | Use the desktop text per view (default, waiting for provider, Extractions). Keep the search message and the Add button. | S |
| L27 | Shift-click adds a range instead of replacing the selection; expanded packages are forgotten | WD | Shift-click replaces, Ctrl+Shift adds. Remember expanded packages in localStorage. A drag box is optional. | S |
| L28 | No "Clean Up Leftover Partial Files…" and no quick limit menus (simultaneous downloads, connections, speed) | MW | Add a toolbar overflow menu with these, applied at once, as in the Downloads menu. | M |
| L29 | No Open, Open With or Open Folder | MW, BL | Showing the path is enough. Add "Copy path" and "Copy folder path". An optional "Save to this device" link needs a file-serving endpoint. | M |

### A. Adding downloads (AddLinks, My provider)

| # | Difference | Kind | What to do | Effort |
|---|---|---|---|---|
| A1 | Uncached provider items need a second "Fetch the ticked ones" approval; on the desktop the Add itself is the approval | WD | Send approveUncached: true on every remote.add. Drop the "Added" approval screen and close after a clean add. | S |
| A2 | No single-address Add URL → "Download file info" flow | MW | Add the two-step dialog: "Enter new address to download", then the file-info panel with the desktop's fields, order and "Start download" / "Download later" buttons. Keep "Add links" as the separate multi-line entry. | L |
| A3 | No metadata check (size, type, server file name) | MW | In the file-info step, call file.metadata when the step opens. Show the same line and "Checking the server… You can start without waiting." Add a "Check file info" button and use the result for the free-space check. | M |
| A4 | No file-name override | MW | Add "File name (optional)", filled from the metadata unless the user edited it. | S |
| A5 | No category choice or "Create a category"; the category-folder setting never applies to web adds | MW | Add the Category combo ("Automatic (from file type)", built-in, custom) and the "Create a category" expander to the single-address step. Move the destination to the category's folder. | M |
| A6 | The default folder is always sent, which skips category folders and overwrites the last-folder memory (the desktop's Add links does the same) | WD | Single-address step: category folder, else default, with a "Remember this folder for manual downloads" tick sent as rememberDestination. For multi-line adds, fix the overwrite on both sides or leave it. | S |
| A7 | No description when adding | MW | Add "Description (optional)" to the single-address step. | S |
| A8 | No per-download user name and password | MW | Add the "Authentication (optional)" expander with the desktop's checks. Send the header with job.add and file.metadata. Clear the password after sending. | S |
| A9 | No "If the file exists" choice | MW | Add Rename / Overwrite / Fail to both dialogs, sent as conflictPolicy. | S |
| A10 | No duplicate check | MW | Mark lines already on the list ("· already in the list", unticked). Add "Skip URLs that are already in the list" (on by default). For a single address, show "Download already exists" with "Download again", "Select existing" and Cancel. | M |
| A11 | Lines can't be ticked; no "Select all"; a hoster reported down is still added | WD | Give each line a checkbox, with "Select all" for 2+ lines. Use the desktop's default ticks (unusable hosters and listed direct links start unticked). The Add count follows the ticks. | M |
| A12 | Text is split into whole lines; links aren't extracted from free text | WD | Port LinkExtractor.Inputs (links and magnets in text, trailing punctuation trimmed, de-duplicated). Header: "Paste links or any text containing links". | S |
| A13 | Lines are checked only on blur or with "Check links" | WD | Classify as the user types, with a 400 ms debounce. Remove "Check links" and keep Retry on each line. | S |
| A14 | Start options: Queue before Start, "Now / When the queue gets to it / Later, by hand", "Save to (a folder on the server)" | WD | Use "Destination folder", then Start ("Start now" / "Download later") beside Queue. The extra queue mode: see W7. | S |
| A15 | No "Keep as one package"; magnets count toward packaging; a name box shows even when every line is a magnet | WD | Add "Keep as one package" (on). Count direct links only and name split archives as the desktop does. If the name box stays, show it only for 2+ direct links. | S |
| A16 | The package archive password is dropped for direct links and sent with magnets, folder links and torrents | BUG | Show it only when archives are supported. For direct links, set it with archive.setPassword on the new package. For provider lines, send it with hoster links only. | S |
| A17 | One shared site and archive password for all lines | WD | Move them onto each hoster line behind "It needs a password", with "Use it as the site password too". | M |
| A18 | No file choice before adding cached magnets or torrents; no "Let me choose files when … is done" | MW | Classify with withFiles. After Add, show "Choose files: <name>" with Add and "Skip this one". Add the choose-later tick and send selection "ask" for folder links. | M |
| A19 | .torrent import takes one file, in its own tab with its own Add button | WD, BL | Accept several files (input multiple and drag-and-drop). Each upload becomes a line in the same list, sharing selection and options. | M |
| A20 | No JD container import (.dlc, .ccf, .rsdf) | MW | Add a container upload endpoint next to the torrent one, then the desktop's preview, select and add steps. | L |
| A21 | No export or import of the download list | MW, BL | Export as a browser download, import as an upload, then the desktop's "Import downloads?" confirmation and review. | M |
| A22 | No recent-address history | MW | Offer settings.recentUrls (last 20) as a dropdown under the address box. | S |
| A23 | Nothing is filled from the clipboard | MW, BL | Add a "Paste" button using navigator.clipboard.readText(). Never read the clipboard unasked. | S |
| A24 | Destination box has no free-space line, no "Not enough space", no mounted-volume warning and no Recent folders | WD | Add the free-space line under FolderField (folders.space) with the same warnings, and a "Recent" section in the picker. | M |
| A25 | After an add: toasts showing at most 3 failures, an "Added" screen, nothing selected | WD | Close on success with a short status. List every problem in one message. For a single provider item with no direct download, select and scroll to its row. | S |
| A26 | After a partial failure that included a provider add, the failed lines can't be retried (only the "Added" screen with Fetch/Close shows) | BUG | Show "Added" only when nothing failed, or drop it (A1). | S |
| A27 | One "Add links" button; no Ctrl+N; Enter doesn't submit | WD | Give the Add button a menu: Add URL, Add links, Add magnet, Import torrent, Import container, Import list. Use a browser-safe key for Add URL. Submit on Enter in single-line fields. | S |
| A28 | Web adds send source "web", which isn't a DownloadSource and falls back to Api; this would open progress windows on the Host's desktop once D8 ships | BUG | Add a Web source (or use "batch" for several links) and leave it out of DownloadWindows.OpensWindows before D8. | S |
| A29 | The web accepts ftp:// links; the desktop pre-fills them from the clipboard, then refuses them | BUG (desktop) | Accept ftp:// in desktop validation, HttpLinks and AddOrdinaryLinksAsync. Keep the web as it is. | S |

### D. Download details and packages (JobDetails, package window)

| # | Difference | Kind | What to do | Effort |
|---|---|---|---|---|
| D1 | No per-download speed limiter | MW | Add the "Speed limiter" section with the desktop's controls, checks and notices. Send job.update speedLimitBytesPerSecond (0 = off) and disable it by canChangeSpeedLimit. Add both fields to the Job type. | S |
| D2 | No segment map or "Segment details" table | MW | Use a segmented bar when the size is known and a collapsible table (N., Progress, Downloaded, Speed, Info). Poll job.segments and job.connections every second while open and stop at terminal states. | M |
| D3 | Detail rows differ in content, order and when they show | WD | Address, then Status, File size, Downloaded with %, Transfer rate, Time left, Resume capability. Show them always, with "—" for no value and "Complete" once finished. Add resumeSupported. | S |
| D4 | No Cancel download | MW | Add Cancel when canCancel: "Cancel download?" / "Cancel this transfer and delete its partial file?", buttons "Cancel download" and "Keep downloading" (Keep as the default). Send job.cancel deleteFile + onlyIfUnfinished. | S |
| D5 | No Delete in the download's own dialog | MW | Add "Delete…" with the desktop's question. It deletes the file (respecting FileRemovedAfterExtraction), then closes the dialog. | S |
| D6 | No Move/Rename for finished files | MW | Add "Move/Rename…" for completed jobs, using FolderField. Preselect the name without its extension. Send job.update newFilePath. | M |
| D7 | Description and category can't be edited (no Properties, no Change Category…) | MW | Add a Properties section (Description, Category with the desktop's choices, Save via job.update), and "Change Category…" for one or more selected rows. | M |
| D8 | No per-download completion options | MW | Add the JobCompletionEditor block. The program is a text path on the server, checked by the Host. Disable it for completed jobs. | M |
| D9 | No "Show the completion window for this download" or "Don't show this dialog again" | MW | Offer them when the server is the Windows app (D8). Hide them or explain them on Docker. | S |
| D10 | No notice when a download finishes or fails | MW, BL | Show a toast "Download complete: <name>" with Show, following notifyOnCompletion, and an error toast on failure. Use the Notification API while the tab is hidden. | S |
| D11 | A finished download keeps the progress layout | WD | Show the desktop summary: "Download complete", the exact byte count, and copyable address and saved-as fields. | S |
| D12 | No Redownload | MW | Add it to details and the row menu, with the desktop's refusals (active states, remote:// jobs, masked addresses) and the provider route. | S |
| D13 | Refreshing the address: inline "Replace an expired address", empty box, upfront "direct" checkbox, hidden for completed jobs | WD | Use "Refresh download address…" in a dialog prefilled with the address, where an empty submit on a hidden address retries the saved one. Ask "Try again / Download directly" only when the Host can't tell. Offer it for completed jobs too. | M |
| D14 | A provider-account file shows its internal remote:// address and offers a refresh the Host refuses | BUG | For remote jobs, hide Address and the refresh form and show "Source: <provider>". Use "Site" for hidden addresses. | S |
| D15 | No package progress window; a package row only expands | WD | Add a package dialog from "Progress Details…": summary line, overall bar, files sorted by name, the selected file's details, and Pause all, Resume all, Cancel package and archive password. | M |
| D16 | Archive password per archive set only, never per package | MW | Add "Archive password…" to package rows and the package dialog, via archive.setPassword packageId, with Save Password / Clear Password, the hint, and the PackageArchiveSection.Show rule. | S |
| D17 | "Retry" for failed jobs, sent as job.retry; buttons appear and disappear | WD | Use "Resume" (job.resume). Keep Pause and Resume in fixed places, disabled rather than removed. | S |
| D18 | The last error is hidden while retrying | WD | Show the failure whenever job.failure is set ("Last error" when not failed), plus the desktop's state hint. | S |
| D19 | An idle download of unknown size shows a pulsing busy bar | BUG | In JobDetails, pass null only for connecting, downloading or verifying; otherwise 0. | S |
| D20 | No Statistics | MW | Add a Statistics page under Manage with the same sections, polling statistics.get every 2 s while the page is visible. | S |
| D21 | A download removed while its dialog is open just vanishes | WD | Keep the dialog open with "Download removed" until the user closes it. | S |

### Q. Queues and scheduler (QueuesScreen)

| # | Difference | Kind | What to do | Effort |
|---|---|---|---|---|
| Q1 | No Files view: queued downloads can't be seen, reordered, moved or removed | MW | Add a "Files" panel per queue in host order (priority, JobOrder, CreatedAt). Move up/down works only within one priority. Add Remove from queue and Move to queue. Save with job.moveToQueue first, then queue.action "reorderfiles". Show priority in the list. | M |
| Q2 | Deleting a queue doesn't ask and fails for any queue holding downloads or schedules | WD | Use "Delete {name}?" with "Move downloads and schedules to" (default queue preselected) and "Delete queue" / "Cancel" (Cancel as the default). Send moveContentsToQueueId. | S |
| Q3 | "Start again every (minutes)" actually re-checks finished files and downloads them again | WD | Use the checkbox "Periodically check completed files for changes" with minutes (1–1440, default 60), disabled while timed start is off, plus the desktop's explanation. Summary: "synchronize every N min". | S |
| Q4 | No "Limit retries after a file fails" | MW | Add it (unchecked = unlimited, 0–100, default 3) with the explanation, saved as retryLimit. | S |
| Q5 | Schedules that can never fire are saved: cleared start time, "Once" without a date, empty name | BUG | Check name (1–80 characters), start time and date the way the desktop does, with its messages. Better, follow the desktop and replace the radio with an optional "One-time date". | S |
| Q6 | Both triggers can be off and repeat stays enabled; only the Host's combined error comes back | WD | Check in the browser with the desktop's messages. Disable repeat without timed start. Block saving with no trigger. | S |
| Q7 | Each schedule saves and removes at once, from an old copy of the settings, with no conflict check | WD | At least confirm Remove. Then mirror Apply/Close, or rebase on the latest settings and refuse a schedule changed elsewhere, with the desktop's wording. | M |
| Q8 | Clicking Edit on another schedule drops the unsaved draft | WD | Keep drafts while switching, or ask first. Show the editor under the schedule being edited. | S |
| Q9 | New schedule defaults: "Nightly", 02:00–07:00, default queue | WD | "New schedule", 09:00, no stop time, every day, the queue the user came from. | S |
| Q10 | Form wording and field order differ | WD | Queue, Schedule name, Enable schedule, Start time / Stop time (optional), Days + "Every day", One-time date (optional), "When the queue finishes", Queue run options. | S |
| Q11 | Schedules form one flat list, not grouped by queue | WD | Show them under their queue card, sorted by name. Disable the Queue select when there is one queue. | S |
| Q12 | Unticking every day shows no days (summary ends in ", ") while the Host runs it every day | BUG | Treat an empty selection as all seven days, as the desktop does. Add "Every day". | S |
| Q13 | isQueued is ignored; card counts include downloads removed from the queue | WD | Add isQueued to the Job type and use it for counts, Q, Files and Remove enablement. | S |
| Q14 | Queue editor wording differs; quota is whole MB only (0.5 MB becomes 1 MB on any save) | WD | Use the Queues dialog's wording and ranges (max 10,485,760 MB, two decimals) and its messages. Add the note that the used quota stays unchanged. | S |
| Q15 | Quota numbers are sent while the limit is off, so an empty or tiny MB value blocks a rename | BUG | Send quotaBytes and quotaWindowHours only when the limit is on. | S |
| Q16 | New queue: name only, 3 downloads at a time | WD | Open the full editor prefilled with "Queue {n+1}", 1 at a time, and "Create" / "Cancel". | S |
| Q17 | Move up/down stay enabled at the ends; "Queue started." without the queue's name | WD | Disable at the ends. Use "{name} started now." / "{name} stopped." | S |
| Q18 | End-of-queue actions aren't offered | MW, BL | The server can't run them. Keep the explanation, use the heading "When the queue finishes", and keep the existing flags when saving (already done). | S |
| Q19 | The desktop Scheduler says "Queue policyd." after a rename | BUG (desktop) | Use fixed texts: "Queue created.", "Queue renamed.", "Queue deleted." | S |

### O. Options / Settings (SettingsScreen)

| # | Difference | Kind | What to do | Effort |
|---|---|---|---|---|
| O1 | Each page has its own Save; favorites and logins save at once. The desktop has one OK/Cancel/Apply for the whole window | WD | Keep one draft for all pages with a sticky OK / Cancel / Apply footer (Escape cancels). Stage favorite and login changes. Show a check failure on its page. Warn before leaving with unsaved changes. Keep the desktop's exceptions that act at once: the provider-key buttons and the favorites star. | M |
| O2 | Saving one page silently throws away unsaved edits on the other pages; "Undo changes" undoes every page | BUG | Fixed by O1. Until then, reset only the fields of the page that was saved. | S |
| O3 | Page names and order differ; the screen is "Settings", not "Options"; Extraction shows even without archive support | WD | Use General, Save to, Downloads, Connection, Proxy, Site logins, Updates, <Provider> account, Extraction, with server-only pages last. Call it "Options". Show Extraction only when archives are supported. | M |
| O4 | No per-category folder boxes | MW | Add a FolderField per category under the checkbox on "Save to", greyed out while it is off. Refresh the folders from the save result. | M |
| O5 | Custom categories can't be edited | MW | Add a "Custom categories" section (Name, "File extensions (space separated)", folder, Remove / Undo removal), with removals staged and sent as deleteCategoryIds. | M |
| O6 | No per-site limits (pattern=max); a "Connections per site" box with max 32 while the Host allows 64 | WD | Add the per-site limits box with the line check. Connections per site: see W9. | S |
| O7 | Favorite folders can only be added or removed | WD | Make each favorite an editable row (name, folder, up, down, remove) in the draft, with the hint "A name left empty becomes the folder's own name." | M |
| O8 | Changing a login's site or user name silently drops its saved password | BUG | Refuse with "Re-enter the password for a login whose site or user name changed." and keep the form open. | S |
| O9 | Logins are edited one at a time; "Site" label; Remove saves at once with no undo | WD | Label it "Site or URL prefix" with the desktop placeholder and hint, "Add site login". Stage removals. | S |
| O10 | Proxy address is one unchecked free-text field | WD | Add a Type select (HTTP / HTTPS, SOCKS4, SOCKS4a, SOCKS5) and a host:port box. Port ProxyAddress Compose/Split and refuse a bad address with the desktop message. | S |
| O11 | Proxy labels differ; the address stays stored when the proxy is off; a typed password beats "forget" | WD | Use the desktop labels ("system" for "Windows"). Clear the address when the mode isn't manual. Grey out the password box while forget is ticked. | S |
| O12 | Download field labels differ | WD | Use the desktop labels word for word ("Default download folder", "Maximum simultaneous downloads", …), with "the server" for "Inbound" where the meaning differs. | S |
| O13 | An emptied number box is sent as null; the save fails with a raw JSON error | BUG | Make the fields required and check their ranges with the desktop messages before sending. | S |
| O14 | Extraction page: different labels, no sections, hints or 7-Zip notice; a dropdown and a checkbox where the desktop has radios; the folder field hidden, not greyed; no folder check; whole object sent; a failed read leaves "Loading…" with no Try again | WD | Use ArchiveForm labels, sections, hints and the LGPL notice, with radio pairs. Grey out the folder field. Check the folder in the browser and send only changes. Show the error with Try again. | S |
| O15 | The provider key lives on "My <provider>"; only accounts[0] is shown; a typed key is lost on leaving | WD | Add a "<Provider> account" Options page per account (keep My provider for items and hosters). Warn about a typed key that isn't saved. | M |
| O16 | Save validates by itself; "Remove the key" doesn't ask; different button names; buttons hidden rather than greyed | WD | Use "Save key", "Validate", "Clear key…" with the "Clear the <name> API key?" confirmation (Cancel as the default). Owner decides on validate-on-save (W10). | S |
| O17 | Key and account status wording differs; "Premium until" stays after the date passes | WD | Use RemoteText's sentences, including "Premium ended on", the cooldown text, and the hint on where to find the key ("server" for "Windows account"). | S |
| O18 | Theme is only a header toggle | WD | Also add a Theme select on General ("Same as system" / "Light" / "Dark"), stored per browser. | S |
| O19 | No "When a download finishes" settings | MW, BL | Add a per-browser "Show a notification when a download finishes", asking permission when turned on. | M |
| O20 | No Sounds page | MW, BL | Leave it out. Optionally play a sound in the browser with the O19 notification. | S |
| O21 | No start at sign-in, close-ends-window or clipboard watching | MW, BL | Nothing to do; whole-object saves keep the values. | S |
| O22 | Updates page shows the feed and signing key, which the desktop keeps hidden on purpose | WD, BL | Drop those two rows. Keep the "update the image" line. | S |
| O23 | Post-download program is read-only, on a different page | WD, BL | The read-only value is right. Show it on Downloads under "When a download finishes", with the lock note. | S |

### P. Provider (My TorBox, remote rows) and extractions

| # | Difference | Kind | What to do | Effort |
|---|---|---|---|---|
| P1 | Extraction records open only when they need a password, so failed, cancelled or finished records can't be retried, cancelled or removed | BUG | Let every archive row open its dialog on click or Enter, as the desktop's primary action does. (Selection: L23.) | S |
| P2 | My TorBox: Download disabled unless the item is cached (and allowed for a failed cached item) | BUG | Allow any item that hasn't failed, and say it starts once the provider has the files. | S |
| P3 | My TorBox: no file tree; one whole-item Download | MW | Add an item detail with remote.item.get, a file tree with all/none, and "Download selected" / "Download all" (selection "chosen" with fileIds). | M |
| P4 | Download on an item already on the list sends remote.add again and always toasts "Downloading …" | WD | For an item with a live job, send remote.select and lock the folder with the "already on the download list" hint. Show result.note or the failure when the add didn't start. | S |
| P5 | Items don't show the status of downloads already started | MW | Match items to remoteJobs and local jobs, and show the desktop's linked status ("Downloading locally, 2 of 5 done"). | M |
| P6 | Items from the old key stay listed after the key is saved or removed | BUG | Reset items and paging after setKey or clearKey. | S |
| P7 | Items list: no columns, sort, per-kind tabs or count; "Show more" | WD | Keep the cards. Add a sort control, the "N items so far. There may be more." line, kind tabs named like the desktop's, and "Load more". | M |
| P8 | No "Delete from TorBox…" for an item | MW | Add it when a job covers the item, with the desktop's confirmation. | S |
| P9 | No provider-wide Check Now | MW | Add "Check now" to My TorBox and the Waiting view (remote.checkNow with the provider), enabled only when something can be checked. | S |
| P10 | Destination starts empty, isn't checked and isn't remembered | WD | Prefill lastDestinationDirectory, else the default. Require a full path. Save it after the add. | S |
| P11 | My provider and Waiting show without a usable key or catalog; kinds guessed when none are reported; accounts[0] only | WD | Gate the items tab on CanBrowse and the Waiting entry on the desktop rule. Use itemKinds only. Handle every account. Keep key setup reachable. | S |
| P12 | Hosters: no "Down only", no "N of M hosts up", different words | WD | Add both. Use "Up"/"Down", the desktop's limit wording, and "Reload" with the cache hint. | S |
| P13 | "Start at provider" uses a looser test; no "Remove from TorBox's queue" | WD | Use IsInProviderQueue for "Start Now on TorBox". Add "Remove from TorBox's Queue…" with confirmation. | S |
| P14 | Check now and Delete at provider are always offered; Retry isn't offered for "blocked" | WD | Port RemoteRules.Actions and CanDeleteFromProvider so buttons show only when the Host takes them. | S |
| P15 | Remove from list doesn't ask | WD | Ask with the desktop's RemoveQuestion text. | S |
| P16 | Files can't be chosen early; no "Download all when ready"; an earlier choice isn't pre-ticked; flat list | MW | Use the CanChooseEarly rule, add "Download all when ready", pre-tick the earlier choice, and show a folder tree. | M |
| P17 | Link password retry: site password field on every failed job; no "Use it as the archive password too" | MW | For linkPasswordRequired, show "Enter the link's password" with the TB.S2 checkbox and "Retry with this password". Plain Retry for other failures. | S |
| P18 | Remote row status and description use raw Host text and stage tokens; "Waiting at TorBox"; name-only search | WD | Port ShortStatus, ProviderProgress and NextCheck. Use "Waiting for TorBox". Search status and description too. | S |
| P19 | Archive password dialog: a reveal toggle, an empty submit clears the password but says "Password saved", the form shows while extracting | WD | Use the desktop titles and buttons ("Extract with This Password" / "Save Password", a separate "Forget Saved Password"), a message for each case, and no form while extracting. | S |
| P20 | Run again and Remove skip the desktop's checks and confirmations | WD | Port CanRetry and the Remove rule. Use "Extract again…" / "Retry extraction" and "Remove extraction record…" with confirmations. | S |
| P21 | The password notice misses wrong-password sets, isn't oldest first, can't be dismissed, has no package password and no completion notice | WD | Filter on needsPassword, order by updatedAt, use the desktop sentences, add dismiss and NoticeTarget. Add a completion notice whose action opens the record. | S |
| P22 | Extraction labels are generic; package rows' extraction cell is empty | WD | Port ArchiveText.Label, PackageLabel and the tooltip lines, and fill the package cell. | S |
| P23 | Extract is offered only for a file that already has a set; no multi-select or package extract; no password before a set exists | WD | Offer Extract on the selection by LooksLikeArchive, packages included, with the disabled reason. Allow "Archive password…" ahead of time. | M |
| P24 | No "Extract Archive…" from disk | MW, BL | Let the server's folder browser list archive files, then send archive.extract filePath and show ExtractSummary's wording. | M |

## 3. Order of work

1. **List screen: actions and correctness.** Start with O2 and O8, two small data-loss fixes on the Settings screen that should not wait. Then L10 and L9 (delete dialogs), L7 and L8 (keys), L1 (row menu), L2 (queue and priority), L3 and L4 (package commands through group.action), L5 (summary port), L21, L20 and L23. This is the screen used most, and it opens the way to the commands the later batches add.
2. **Add dialog.** A1 and A26 first (approval step and the dead-end state), then A2–A10 (single-address Add URL with file info, file name, category, folder, description, auth, conflict, duplicates), A11–A18 (multi-line ticks, extraction, live classification, start wording, packaging, passwords, file choice), and A19–A29.
3. **Download details and package dialog.** D4, D1, D6 and D7 (cancel, speed limit, Move/Rename, Properties), D3, D11, D17 and D18 (rows and wording), D12–D14 (Redownload, address refresh, remote address), D15 and D16 (package dialog and package archive password), D2, D8–D10, D19–D21 and L29.
4. **Provider and Extractions.** P1–P6 (unreachable records, the cached-only Download, file tree, false success, linked status, stale items), then P13–P21 (remote row actions and archive dialogs), P7–P12, P22–P24, and O15–O17 on the provider account.
5. **Options, Queues and list polish.**
   - Options: O1 (one draft with OK/Cancel/Apply), O13, O3–O7, O9–O14 and O18–O23.
   - Queues: Q2, Q15, Q5, Q12, Q3, Q1 and Q4, then the rest of Q.
   - List polish: L11–L19, L22 and L24–L28, including the speed graph and the sidebar categories.

## 4. Web-only things worth keeping or bringing to the desktop

| # | Web-only item | Suggestion |
|---|---|---|
| W1 | Live search box in the header | Keep it. The desktop could get the same box beside Find. |
| W2 | A count beside each sidebar view | Owner decides: add counts to the desktop, or drop them on the web. |
| W3 | Details show "Connections: N of M" and the Added date | Keep. Worth adding to the desktop progress window. |
| W4 | Queue cards show "(default)" and the quota used; schedules show On/Off, the last run and its error | Bring to the desktop Scheduler's Limits tab and schedule picker. |
| W5 | Account "Refresh" (remote.account.get refresh) | Fold into Validate on the web, or keep it as is. Only Validate re-reads on the desktop. |
| W6 | "Allow downloads from addresses on the local network" setting; FFmpeg path shown | A real Host setting with no desktop control. Owner decides whether the desktop Connection page gets it. |
| W7 | Start mode "When the queue gets to it" (StartMode.Queue) | Valid in the Host. Either add it to the desktop or keep it on the web with the desktop's wording around it. |
| W8 | Package name box when adding 2+ direct links | Could be added to the desktop's Add links dialog. |
| W9 | "Connections per site" (MaxConnectionsPerHost) | No desktop control. Owner decides; if it stays, set its max to the Host's 64. |
| W10 | The key is validated straight after saving | Arguably better. Owner decides whether both sides do it. |
| W11 | Sign-in, password change and browser pairing | Server only. Keep it as the last Settings page. |
| W12 | Folder hint "New downloads go here unless you choose another folder." | Could go on the desktop's Save to page. |

Desktop fixes found along the way: Q19 ("Queue policyd."), A29 (ftp:// pre-filled from the clipboard, then refused), and the dead ShowSchedulerAsync/ShowScheduleEditorAsync path in MainWindow.xaml.cs:3560-3711.

## 5. References

Desktop (**D**) paths are under `src/DownloadManager.Desktop.WinUI/` unless they start with `src/`. Web (**W**) paths are under `webui/src/`.

Short names:
- MW = MainWindow.xaml.cs; MWx = MainWindow.xaml
- LS = screens/ListScreen.svelte; AL = screens/AddLinks.svelte; JD = screens/JobDetails.svelte; QS = screens/QueuesScreen.svelte; SS = screens/SettingsScreen.svelte; TB = screens/TorBoxScreen.svelte; RA = screens/RemoteActions.svelte; AP = screens/ArchivePassword.svelte
- DMS = src/DownloadManager.Runtime/DownloadManagerService.cs; HCD = src/DownloadManager.Runtime/Ipc/HostCommandDispatcher.cs

**List**
- **L1** D: MW:938-999, 1030-1093. W: LS:183-193; no oncontextmenu in webui/src.
- **L2** D: MW:971-981, 1068-1093, 1118-1140, 1167-1200; SchedulerWindow.cs:503-505. W: lib/types.ts:36; JD:43, 69-75; no job.moveToQueue or priority call.
- **L3** D: MW:468-469, 991-993, 1050-1063, 1142-1163, 1575-1634; MWx:171-178; PackageWindow.cs:65, 528-546; src/DownloadManager.Contracts/PackageRules.cs:53-55. W: LS:145-159; lib/model.ts:74-76; no group.action.
- **L4** D: MW:429-433, 456-458, 1169-1200, 4785-4795; PackageRules.cs:35-51; PackageWindow.cs:514-526; JobViewModel.cs:230-231. W: lib/views.ts:150-162; LS:30, 111-118, 148-149.
- **L5** D: src/DownloadManager.Contracts/DownloadGroupSummary.cs:38-82, 97-99; JobViewModel.cs:219-229. W: lib/views.ts:48-72; LS:237, 239, 241, 245.
- **L6** D: MW:456-458, 627, 637; JobViewModel.cs:222, 242-243; MainWindow.Lists.cs:55-67. W: lib/views.ts:127-143.
- **L7** D: MW:836-846; MainWindow.Remote.cs:460-463; MainWindow.Archives.cs:340-343. W: LS:192; components/Dialog.svelte:24-29.
- **L8** D: MW:140-159; MWx:376; MainWindow.Remote.cs:456-459. W: Dialog.svelte:24-29; LS:170, 189, 192.
- **L9** D: MW:4820-4852; SelectionCommands.cs:89-91. W: LS:291-307; Dialog.svelte:19-20.
- **L10** D: MW:2006-2047 (2038). W: LS:126, 153, 291-306.
- **L11** D: MainWindow.Lists.cs:28-46, 55-67; ListColumns.cs:179-184; MWx:104-115; MW:161-163. W: LS:19, 171-177; lib/views.ts:76-83; App.svelte:115-123.
- **L12** D: ListColumns.cs:23-38, 184; JobViewModel.cs:150-153, 237-241; MWx:430-432; MainWindow.Lists.cs:58. W: LS:169-179, 273; lib/views.ts:74; lib/format.ts:47-52; lib/types.ts:24.
- **L13** D: JobViewModel.cs:108-118, 133, 154-155; SelectionCommands.cs:76-86; DurationText.cs:6-11; JobAddress.cs:24-29. W: lib/format.ts:36-45, 100-106; LS:133-141, 256, 258, 272, 274.
- **L14** D: MWx:272-313; MW:583, 586, 871-893; JobViewModel.cs:147. W: lib/views.ts:8-17; App.svelte:16-23, 100-105.
- **L15** D: MWx:261, 272-350; MainWindow.Archives.cs:175-179; MainWindow.Remote.cs:160-166, 179-186. W: App.svelte:98-110; lib/views.ts:10-17.
- **L16** D: MWx:102, 498; MW:58-77. W: App.svelte:127-133.
- **L17** D: MW:527-558; MWx:500-515. W: App.svelte:82-85, 127-133, 138-146.
- **L18** D: MW:560-573, 1389-1404, 2113-2139; MainWindow.Remote.cs:115-119. W: App.svelte:73-77; lib/views.ts:85-91, 105, 112.
- **L19** D: MWx:96, 142-227 (203-210); MW:460-481. W: LS:144-159; App.svelte:107.
- **L20** D: MW:1406-1433, 4752-4783; MWx:88-89, 212-226. W: LS:40, 154-158; QS:98-101.
- **L21** D: Models.cs:231-242; MW:467, 4791-4795. W: LS:30, 114, 148; HCD:259-265; DMS:839-900.
- **L22** D: MW:895-921; MainWindow.Archives.cs:353-359; MainWindow.Remote.cs:472-482. W: LS:104-109; AP:43-45.
- **L23** D: MW:470-473, 4813-4818; MainWindow.Remote.cs:346-405, 436-480; MainWindow.Archives.cs:114-127, 334-359. W: LS:59-83, 152, 192, 194-198, 214; lib/views.ts:150-161; RA:121; AP:45.
- **L24** D: MainWindow.Remote.cs:436-469. W: LS:212-215, 393.
- **L25** D: MainWindow.Archives.cs:135-142, 162-185, 316-332; MainWindow.Remote.cs:115-119; Remote/RemoteClient.cs:592-603. W: lib/views.ts:110-116; LS:38; App.svelte:105; lib/types.ts:299; HCD:65-69.
- **L26** D: MWx:473-476; MainWindow.Remote.cs:20, 121-127; MainWindow.Archives.cs:21. W: LS:277-287.
- **L27** D: MWx:376; MainWindow.Lists.cs:25, 69-105; MW:820-826. W: LS:18, 64-72.
- **L28** D: MWx:71-98; MW:2049-2111; MainWindow.Limits.cs:20-40. W: SS:217-220.
- **L29** D: MW:915-920, 942-944, 1102-1116, 1202-1225, 4949-4998; DownloadWindow.xaml:105-107; DownloadWindow.xaml.cs:219-220, 446-470; PackageWindow.cs:66. W: JD:41-42, 68-76; LS:104-109; src/DownloadManager.Server/ServerApp.cs:303-349.

**Add**
- **A1** D: Remote/AddLines.cs:841-853, 869-881; MainWindow.AddFlows.cs:13-15. W: AL:121-125, 185-189, 199-214, 318; RA:56-59.
- **A2** D: MW:2422-2900 (2474, 2764-2786, 2792-2795); MainWindow.AddFlows.cs:116-230; MWx:45, 142. W: App.svelte:99, 136; LS:146, 285.
- **A3** D: MW:2681-2762, 2801. W: lib/store.svelte.ts:25-28; no file.metadata call.
- **A4** D: MW:2524-2528, 2861. W: AL:111-114.
- **A5** D: MW:2574-2645, 2779, 2862, 2937-2941; AddLinksDialog.cs:105. W: AL:17, 112; SS:216.
- **A6** D: MW:2583-2599, 2658-2661, 2866, 2941, 3095-3141; AddLinksDialog.cs:105; ContainerImportDialog.cs:72-74; MyProviderWindow.cs:254. W: AL:17, 112; DMS:715-733, 4247-4252.
- **A7** D: MW:2573, 2865. W: AL:111-114.
- **A8** D: MW:2646-2657, 2706-2712, 2781, 2809-2820, 2880-2885. W: AL (no field).
- **A9** D: MW:2675-2679, 2786, 2867-2872, 2959-2963, 3135-3140; AddLinksDialog.cs:121-125. W: AL:111-114.
- **A10** D: MW:2827-2856, 2964-2968, 3012-3023; AddLinksDialog.cs:126-127; AddLines.cs:357, 582. W: AL:107-133.
- **A11** D: AddLinksDialog.cs:66, 137-141, ~372-374, 403-404; AddLines.cs:349-365; MW:3006-3053. W: AL:44, 107, 229-249; lib/addlines.ts:50.
- **A12** D: AddLines.cs:38-90; AddLinksDialog.cs:85; MW:2917. W: lib/addlines.ts:19-30; AL:78-82, 222.
- **A13** D: AddLines.cs:159, 212-226; AddLinksDialog.cs ~370. W: AL:223, 225, 321.
- **A14** D: AddLinksDialog.cs:106-119, 248, 282; MW:2783-2795, 2940, 2943-2946, 3066; DMS:513, 4351-4352. W: AL:276-291; src/DownloadManager.Core/Models.cs:65-70.
- **A15** D: AddLinksDialog.cs:69; MW:2937, 3099, 3102-3110. W: AL:100-102, 292-297.
- **A16** D: AddLinksDialog.cs:70-71, ~380, 322-325, 458-459; MainWindow.AddFlows.cs:537-549. W: AL:111-114, 124, 178, 298-306.
- **A17** D: AddLinksDialog.cs:322-325, 405-432. W: AL:21-24, 298-312.
- **A18** D: AddLinksDialog.cs:67, 95; AddLines.cs:785-807, 859-872; MainWindow.AddFlows.cs:242, 481-512. W: AL:67, 118-125, 255-271; RA:63.
- **A19** D: MainWindow.AddFlows.cs:286-300; MainWindow.Remote.cs:274; AddLines.cs:45-46. W: AL:148-168, 218, 250-273, 324.
- **A20** D: MainWindow.AddFlows.cs:307-348; ContainerImportDialog.cs; MainWindow.Remote.cs:284. W: lib/store.svelte.ts:27-28; src/DownloadManager.Server/ServerApp.cs:167-171, 305.
- **A21** D: MWx:52-53; MW:1775, 1813-1950. W: none.
- **A22** D: MW:2431-2447; DMS:738-748. W: AL:222-223; lib/types.ts:140.
- **A23** D: MW:2449-2464, 2924-2936; MainWindow.AddFlows.cs:269-284. W: AL:15.
- **A24** D: FolderChooser.cs:31-87, 113-130. W: components/FolderField.svelte:24-31; components/FolderPicker.svelte:24-31, 113, 139-140; lib/types.ts:327.
- **A25** D: MainWindow.AddFlows.cs:415-435, 459-474, 518-534; Remote/AutoOpen.cs:73-74. W: AL:142-145, 199-214.
- **A26** D: MainWindow.AddFlows.cs:350-409. W: AL:135, 137, 143, 199-200, 317-319.
- **A27** D: MWx:45-50; MainWindow.Remote.cs:273-284; MW:2478; AddLinksDialog.cs:175. W: App.svelte:99; LS:146, 285; Dialog.svelte:24-29.
- **A28** D: MW:2873, 3141; src/DownloadManager.Runtime/DownloadWindows.cs:43; src/DownloadManager.Host/Program.cs:303. W: AL:112; src/DownloadManager.Core/Models.cs:45-63; DMS:514, 4351-4352.
- **A29** D: MW:2455-2458, 2511-2516, 3121-3125; AddLines.cs:49-57, 87-88; MainWindow.AddFlows.cs:216-218. W: AL:78-82.

**Details**
- **D1** D: DownloadWindow.xaml:45-65; DownloadWindow.xaml.cs:184-199, 332-377; HostConnection.cs:181-184. W: JD:24-77; lib/types.ts:11-45.
- **D2** D: JobDetailsPanel.xaml:39-44; JobDetailsPanel.xaml.cs:14, 38, 69, 165-275; DownloadWindow.xaml:95; PackageWindow.cs:56. W: JD:25-31.
- **D3** D: JobDetailsPanel.xaml:21-37; JobDetailsPanel.xaml.cs:102-117; DownloadWindow.xaml:37-40. W: JD:26-40; lib/types.ts:11-45.
- **D4** D: DownloadWindow.xaml:103; DownloadWindow.xaml.cs:212, 215, 316-330; HostConnection.cs:89-91. W: JD:68-76; LS:145-159.
- **D5** D: DownloadWindow.xaml:104; DownloadWindow.xaml.cs:286-314. W: JD:68-76; LS:152, 291-307.
- **D6** D: MWx:61; MW:946-963, 1227-1316 (1282-1285); HostConnection.cs:194-196. W: JD:68-76; LS:145-159.
- **D7** D: MW:994-995, 1636-1774 (1641, 1648, 1757). W: LS:138-141, 274; JD (no fields).
- **D8** D: JobCompletionEditor.cs:14-19, 32, 46-52, 91, 108; MW:1718-1748. W: lib/types.ts:11-45; SS:333.
- **D9** D: DownloadWindow.xaml:67-79, 111-112; DownloadWindow.xaml.cs:379-444. W: lib/types.ts:118-145.
- **D10** D: src/DownloadManager.Host/Program.cs:268-291; src/DownloadManager.Runtime/DownloadWindows.cs:45-62; Dtos.cs:218. W: lib/model.ts:62-72; lib/store.svelte.ts:66-69.
- **D11** D: DownloadWindow.xaml:82-91; DownloadWindow.xaml.cs:149-176, 203. W: JD:24-55.
- **D12** D: MWx:66; MW:968, 1318-1387. W: none.
- **D13** D: MW:969, 1045, 1054-1057, 1463-1521; JobAddress.cs:31-32; MainWindow.AddFlows.cs:49-81. W: JD:10-21, 57-66.
- **D14** D: DownloadWindow.xaml.cs:163-166; JobAddress.cs:13-14; MW:1054-1057, 1470-1474, 1684; src/DownloadManager.Core/Providers/ShownAddress.cs:28-29. W: JD:39-40, 57; DMS:2690-2697, 4423.
- **D15** D: PackageWindow.cs:50-135, 186, 207-212, 432, 467-471, 505-512; MW:1523-1544; DownloadWindows.cs:56-58. W: LS:104-109, 229-248.
- **D16** D: PackageWindow.cs:68-73, 272-304, 350-358, 391-419; PackageArchiveSection.cs:20-25; MainWindow.Archives.cs:361-369; src/DownloadManager.Contracts/ArchiveDtos.cs:200-205. W: AP:14-19, 30-40; LS:229-248; RA:101-108.
- **D17** D: DownloadWindow.xaml:101-102, 108; DownloadWindow.xaml.cs:203, 207-215, 280; MW:965. W: JD:69-70.
- **D18** D: DownloadWindow.xaml.cs:172-176. W: JD:47; LS:258.
- **D19** D: SelectionCommands.cs:97-102; JobDetailsPanel.xaml.cs:86-99. W: JD:14, 30; lib/format.ts:90-97, 109-112; components/ProgressBar.svelte:15-16; src/DownloadManager.Core/Models.cs:245.
- **D20** D: MWx:101; StatisticsWindow.cs:13, 39-54, 159-200. W: App.svelte:14-23, 106-109.
- **D21** D: DownloadWindow.xaml.cs:118-129; PackageWindow.cs:255-261. W: LS:309-311; lib/model.ts:65-67.

**Queues**
- **Q1** D: SchedulerWindow.cs:284-330, 496-570 (534-535), 1019-1064 (1056); MW:3233-3240, 4498-4626. W: QS:90-127.
- **Q2** D: SchedulerWindow.cs:834-859; MW:3326-3354. W: QS:118-120; DMS:1551-1553.
- **Q3** D: SchedulerRunOptionsEditor.cs:12-20, 69-73, 129-130; src/DownloadManager.Runtime/SchedulerAutomation.cs:48-60, 159-200. W: QS:83, 176.
- **Q4** D: SchedulerRunOptionsEditor.cs:21-29, 75, 105-119; SchedulerWindow.cs:765-774, 933-940. W: lib/types.ts:61.
- **Q5** D: SchedulerWindow.cs:28, 715-731, 747. W: QS:154, 161-162, 171, 174; DMS:1229-1233; SchedulerAutomation.cs:35, 51, 105-106.
- **Q6** D: SchedulerRunOptionsEditor.cs:99-106, 122-123, 129-130. W: QS:173, 176-177; DMS:1229-1233.
- **Q7** D: SchedulerWindow.cs:280, 627, 690, 780-796, 908-1017 (957-958). W: QS:56-70, 144; lib/store.svelte.ts:128-134.
- **Q8** D: SchedulerWindow.cs:89-100, 259-273, 280. W: QS:143, 150, 152-189.
- **Q9** D: SchedulerWindow.cs:665-693 (682); SchedulerRunOptionsEditor.cs:36-37. W: QS:47-54.
- **Q10** D: SchedulerWindow.cs:27-39, 238-258; SchedulerRunOptionsEditor.cs:10-11; FormFields.cs:61-63. W: QS:154-187.
- **Q11** D: SchedulerWindow.cs:572-590, 601. W: QS:11, 134-148, 157.
- **Q12** D: FormFields.cs:61-83; SchedulerWindow.cs:749. W: QS:72-83, 167; DMS:2794-2799.
- **Q13** D: JobViewModel.cs:151; SchedulerWindow.cs:499; MW:3200, 3213, 4503. W: lib/types.ts:34-36; QS:12-16, 96; src/DownloadManager.Runtime/QueueMembership.cs:23-25.
- **Q14** D: MW:3389-3415, 3465-3490; SchedulerWindow.cs:53-56, 341. W: QS:28, 37, 112-116.
- **Q15** D: MW:3523-3533. W: QS:37, 115; DMS:1568, 1691-1696.
- **Q16** D: MW:3376-3455; SchedulerWindow.cs:811-816. W: QS:42-45, 129-132.
- **Q17** D: MW:3208, 3242-3277; SchedulerWindow.cs:21, 903. W: QS:98-104.
- **Q18** D: SchedulerWindow.cs:34-39, 252-257, 804-809. W: QS:62, 179-183; src/DownloadManager.Server/ServerPlatform.cs.
- **Q19** D: SchedulerWindow.cs:826-831, 889. W: QS:38.

**Options**
- **O1** D: SettingsWindow.cs:23-25, 78-85, 175-197; MW:4201-4202, 4354-4486; ProviderAccountPage.cs:76. W: SS:59, 70-75, 224, 234, 254, 290, 317; lib/store.svelte.ts:128-134.
- **O2** D: SettingsWindow.cs:175-197. W: SS:24-29, 49, 80-81, 85-92, 224, 290.
- **O3** D: MW:4284-4344 (4340); SettingsWindow.cs:34; MWx:97, 195-200. W: SS:10-18; App.svelte:109.
- **O4** D: MW:4191-4199, 4289-4300, 4450-4452, 4480-4482. W: SS:216; lib/types.ts:125.
- **O5** D: MW:4250-4282, 4301-4305, 4453-4462. W: none (lib/types.ts:16, 125).
- **O6** D: MW:4038-4047, 4313-4316, 4392-4403, 4649. W: SS:219; lib/types.ts:127; DMS:1291.
- **O7** D: MW:4201-4249, 4306-4309, 4444-4449. W: SS:226-244; components/FolderPicker.svelte:75-78.
- **O8** D: MW:4189, 4369-4372. W: SS:66-77, 261-266; DMS:4655-4661.
- **O9** D: MW:4153-4189, 4365-4368. W: SS:245-269.
- **O10** D: MW:4088-4110, 4385-4390; src/DownloadManager.Contracts/ProxyAddress.cs:11-78. W: SS:279-283; DMS:4602-4614.
- **O11** D: MW:4089-4094, 4113, 4119-4128, 4145, 4384-4390, 4407-4408. W: SS:86-87, 274-288; DMS:1365-1372.
- **O12** D: MW:4031-4036, 4050, 4054, 4057, 4315. W: SS:215-223.
- **O13** D: MW:4374-4381. W: SS:41-43, 217-219; src/DownloadManager.Contracts/Dtos.cs:200-210; Protocol.cs:138.
- **O14** D: ExtractionOptionsPage.cs:18, 25-27, 61-80, 120-144, 176-183; Remote/ArchiveForm.cs:23-36, 46-113; MW:4361-4362; src/DownloadManager.Runtime/Archives/ArchiveCoordinator.Run.cs:407-408. W: SS:100-119, 297-321; src/DownloadManager.Runtime/Archives/ArchiveCoordinator.Commands.cs:75-79, 84, 96.
- **O15** D: MW:4325-4336, 4356-4360; ProviderAccountPage.cs:69-80. W: TB:12, 114-141; App.svelte:108.
- **O16** D: ProviderAccountPage.cs:20-22, 124-125, 128-198. W: TB:26-31, 135-139.
- **O17** D: Remote/RemoteText.cs:58-126. W: TB:116-129.
- **O18** D: MW:4058-4072, 4433. W: App.svelte:51-57, 86; lib/store.svelte.ts:150-159.
- **O19** D: MW:4051-4053, 4311-4312. W: SS (none).
- **O20** D: SoundOptionsEditor.cs:14-19, 95-162; MW:4322-4323. W: SS (none).
- **O21** D: MW:4048-4049, 4055-4056. W: SS:213-225; lib/store.svelte.ts:130.
- **O22** D: MW:4079-4086, 4319-4321, 4380-4381. W: SS:322-334.
- **O23** D: MW:4073-4078, 4312. W: SS:324-329; HCD:441-447, 554-560.

**Provider and extractions**
- **P1** D: MainWindow.Archives.cs:114-127, 334-359. W: LS:59-83, 104-109, 192, 194-198; lib/views.ts:154-161; AP:42-46; JD:71-74.
- **P2** D: Remote/RemoteRows.cs:402; MyProviderWindow.cs:759-763. W: TB:171; src/DownloadManager.Contracts/RemoteDtos.cs:257.
- **P3** D: MyProviderWindow.cs:179, 184-185, 345-367, 699-767, 856-911. W: TB:61-66, 157-176.
- **P4** D: MyProviderWindow.cs:809-828, 867-879, 906-909. W: TB:61-66; lib/store.svelte.ts:116-125; RemoteJobCoordinator.cs:707-710.
- **P5** D: Remote/RemoteRows.cs:357-405; MyProviderWindow.cs:996-1005. W: TB:163-169.
- **P6** D: MyProviderWindow.cs:1007-1054. W: TB:26-31, 57-59, 139.
- **P7** D: MyProviderWindow.cs:95-116, 174, 208-215, 245-251, 413-444, 555-559. W: TB:14, 146-177.
- **P8** D: MyProviderWindow.cs:804, 926-957; Remote/RemoteRows.cs:183. W: TB:157-176; RA:110-122.
- **P9** D: MyProviderWindow.cs:797-801, 913-924; MainWindow.Remote.cs:287-295. W: RA:119; RemoteCommands.cs:467-479.
- **P10** D: MyProviderWindow.cs:253-255, 880-887, 902-904; MainWindow.Remote.cs:326-342. W: TB:39, 61-66, 154.
- **P11** D: MainWindow.Remote.cs:151-166; Remote/RemoteRows.cs:109-111; ProviderAccountPage.cs:69-80. W: App.svelte:104, 108; TB:12-13.
- **P12** D: Remote/HosterWindow.cs:21, 55, 99, 117, 134-176. W: TB:92-95, 180-204.
- **P13** D: Remote/RemoteRows.cs:186-187; MainWindow.Remote.cs:389-401, 622-656. W: RA:120.
- **P14** D: Remote/RemoteRows.cs:113-119, 183. W: RA:91, 119, 122.
- **P15** D: MainWindow.Remote.cs:658-662; Remote/RemoteRows.cs:487-493. W: RA:121.
- **P16** D: Remote/RemoteRows.cs:172-173; MainWindow.Remote.cs:512-538; RemoteFileSelectionDialog.cs:110-128. W: RA:63-89.
- **P17** D: MainWindow.Remote.cs:560-592. W: RA:91-99.
- **P18** D: Remote/RemoteRows.cs:246-311; MainWindow.Remote.cs:115-119, 162. W: LS:207, 213; App.svelte:104; lib/views.ts:105; RA:48.
- **P19** D: MainWindow.Archives.cs:479-527; Remote/ArchiveRows.cs:200-214, 387-391. W: AP:16, 30-39.
- **P20** D: MainWindow.Archives.cs:529-548; Remote/ArchiveRows.cs:331, 410-417. W: AP:43, 45.
- **P21** D: Remote/ArchiveRows.cs:359-364, 464-506; MainWindow.Archives.cs:393-418. W: LS:39, 161-167, 265.
- **P22** D: Remote/ArchiveRows.cs:70-161; MW:613; MainWindow.Archives.cs:314. W: lib/format.ts:140-165; LS:244.
- **P23** D: Remote/ArchiveRows.cs:295-338, 393-408; MainWindow.Archives.cs:422-444. W: JD:71-74; LS:145-159.
- **P24** D: MainWindow.Archives.cs:117, 446-476. W: JD:73; components/FolderPicker.svelte.

**Web-only**
- **W1** D: MW:2113-2139. W: App.svelte:73-77.
- **W2** D: MWx:272-350; MainWindow.Remote.cs:162. W: App.svelte:60-65, 102-105; lib/views.ts:164-172.
- **W3** D: JobDetailsPanel.xaml:21-37; MW:1680-1688. W: JD:37, 43-45.
- **W4** D: SchedulerWindow.cs:332-345, 572-583; MW:3213-3215, 3548, 3560-3711. W: QS:94, 108, 139-141.
- **W5** D: ProviderAccountPage.cs:20-22, 145-151; RemoteClient.cs:103. W: TB:138.
- **W6** D: MW:4712, 4715; SchedulerWindow.cs:39, 977-979; MW:3804-3808, 3983. W: SS:222, 330, 333.
- **W7** D: MW:2943-2946, 3066; AddLinksDialog.cs:106-109, 248. W: AL:285-290; src/DownloadManager.Core/Models.cs:65-70.
- **W8** D: MW:2937, 3099-3110. W: AL:292-297.
- **W9** D: MW:4649. W: SS:219; DMS:1291.
- **W10** D: ProviderAccountPage.cs:128-143. W: TB:26-31.
- **W11** D: MW:4284-4344. W: SS:121-194, 335-379.
- **W12** D: MW:4031. W: SS:215.
