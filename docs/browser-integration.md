# Browser integration strategy

## What Chrome actually permits (MV3)

These constraints drive the design; they must be verified against the installed
Chrome version during Phase 4.

1. **No arbitrary request replay.** A service worker cannot read a request body
   and construct an equivalent authenticated request for a different client in
   the general case. POST bodies, multipart uploads and short-lived signed URLs
   may be unavailable, and the extension cannot tell when they are in play. See
   the table below.
2. **Interception point is `downloads.onDeterminingFilename`.** Chromium adds a
   download to its UI (the Chrome/Edge download bubble) only after the target
   name is decided. The listener returns `true` to hold the decision, pauses,
   and asks the Host. After a durable ACK the download is cancelled and erased
   without ever answering, so no bubble opens. When the browser keeps the
   download (filtered, bypassed, rejected, Host unreachable), the listener calls
   `suggest()` once and resumes. `downloads.setUiOptions` is not needed and is not used.
   Firefox has no `onDeterminingFilename`; there the same code runs from
   `downloads.onCreated`, pausing the download and then cancelling or resuming it.
   With no name to hold, the file keeps arriving meanwhile, so on Firefox the download is paused
   as soon as the rules say to catch it, before the slower Delete-gesture lookup. If it will not
   pause, or has finished, it stays in the browser with a note, never sent while it still arrives.
   If the browser cannot continue a download it gave back, the popup notes that too.
   Firefox also asks where to save ("Always ask you where to save files") before `onCreated`, so a
   download caught there has been through that dialog for nothing (EXT.B5). Firefox therefore looks at the
   response first: see Firefox below.
3. **`webRequest` is observational in Chrome's MV3.** Blocking requires a
   policy-installed extension, so the Chrome and Edge build does not use it and does not
   request the permission. Firefox keeps blocking `webRequest` in MV3, and its build uses it (EXT.B5).
4. **Cookies are readable with the `cookies` permission + host permissions.**
   `chrome.cookies.getAll({url})` gives the cookie set for the target; this is
   the main way to transfer a browser session. For an automatic catch the
   extension reads the cookies of the download's address and, when the final
   address is on the same origin, of that one too, merged without duplicates
   (a cookie scoped to the final path is otherwise missed). A final address on
   another origin adds none: the app starts again from the first address and
   sends cookies only to that origin, so another site's cookies would go to the
   wrong site and be useless there.
5. **Headers may be partially hidden.** `extraHeaders` may be needed for
   `Cookie`/`Authorization`; we treat auth headers as sensitive and optional.
   Never log them.
6. **Redirects change credential scope.** A cookie for the original host must
   not be sent to an unrelated redirect target. The host re-evaluates scope and
   only supplies cookies for the current origin, matching same-site boundaries.
7. **Native messaging has a size limit** (keep frames well under 1 MB) and the
   native host's stdout is reserved for 32-bit length-prefixed frames. Logs go
   to stderr/a file.
8. **Extension identity is fixed by the manifest `key`.** The native-host
   manifest must list exactly our extension origins. The Chrome Web Store refuses
   a first upload that has a `key` and assigns its own ID, so the store zip
   (`scripts\package-extension-store.ps1`) has none. The native side trusts three IDs:
   Chrome's store (`olhbcdbjdnfoiinllnlpandggadfabfm`), Edge's store (`ifoagddcenmjpbklkhghbdmgplldcdci`) and the unpacked build's (`pknddmneejadnnngffojnoecojhdfalc`).
   See `docs/chrome-store/LISTING.md`.

## Edge

Edge is a supported browser (owner decision 2026-11-06). It runs the same extension and the same native host.

- **Install route.** Edge has its own listing on Microsoft Edge Add-ons (store ID `0RDCKGXFWJ1N`, extension ID
  `ifoagddcenmjpbklkhghbdmgplldcdci`, which Edge assigned; it differs from Chrome's). Setup's last page and `INSTALL.txt` link it
  beside the Chrome one. Edge can also install the Chrome Web Store listing (it may ask to allow extensions from other stores;
  not checked in Edge), whose ID `olhbcdbjdnfoiinllnlpandggadfabfm` is also accepted. The native-host manifest lists both store
  origins, and a manifest without the Edge one counts as broken until Repair or a Setup upgrade rewrites it.
- **Registration.** Edge looks the native host up under its own key,
  `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.kaank.downloadmanager`, beside Chrome's under
  `Google\Chrome`. Setup and `register-native-host.ps1` (used by install, repair and the in-app updater) write
  the same manifest path under both, whether or not the browser is installed, so installing Edge later
  works. Edge's key is optional: failing to write it gives a warning (Setup: no error) and does not stop an
  install, repair or update, while failing to write Chrome's still does. The in-app updater also replaces the
  `repair.ps1` and `register-native-host.ps1` that Setup put beside `app\`. The paths are constants in
  `BrowserBridge`.
- **Uninstall.** Setup's uninstaller and `uninstall.ps1` follow one rule: a key is removed only when it points
  at this install's manifest or at a file that no longer exists, so another program's working registration
  stays. Writing is not guarded this way: Setup, install, repair and the updater replace what is there. An
  uninstall log already written by 0.4.0 cannot change, which leaves two exceptions:
  - An install made by 0.4.0 Setup and then updated in the app keeps the 0.4.0 uninstaller, which knows only
    Chrome's key. Uninstalling it leaves Edge's key behind, pointing at a deleted manifest (the same after an
    update rolled back to 0.4.0). `register-native-host.ps1` does not treat such a key as a conflict and the
    Host replaces it. Setup rewrites the uninstaller, so a first Edge version installed through Setup does not
    have this leftover.
  - A 0.4.0 install upgraded through a later Setup is expected to keep its old log entry for Chrome's key, which
    removes that key without the check.
- **Registered state.** Each key is absent, usable or broken. Usable means it points at a manifest that lists
  the store origin and names a program that exists. The bridge is registered when either key is usable, and
  the status (tray, status bar, CLI `status` and `doctor`) names a browser that cannot reach the host:
  `on; the extension can't reach Inbound in Chrome` (its key is missing or broken),
  `on; the extension can't reach Inbound in Edge` (review 5:F2). Chrome with no Edge key stays plain `on`, because Edge may
  not be installed and an install from 0.4.0 has no Edge key until the Host adds it. The word is `on`, not
  `connected`: a registration means a browser can reach the app, not that one is running (U-WL-9). When a key is
  broken or missing, the window's status bar offers Repair, which runs the `register-native-host.ps1` that Setup
  put beside `app\` for this install's `app` folder, without `-Force`, so another program's registration is never
  replaced. A test or portable instance (`DOWNLOADMANAGER_DATA` or `DOWNLOADMANAGER_INSTANCE` set) and a build that
  isn't installed are told why instead, and nothing is written.
- **Repair at Host start.** Once per start, best effort, when one key is usable and the other is missing or
  points at a file that is gone, the Host writes the working key's value into the other one. It logs a failure
  rather than showing one, skips isolated-data runs, and never replaces a registration whose manifest exists,
  so a broken one that still has its file stays named in the status until `repair.ps1` runs.
- Evidence and what is still unverified are in `PROGRESS.md`.

## Firefox

Firefox is required (owner decision 2026-11-06, ROADMAP EXT.E1). The same sources build it.

- **Build.** `npm run build:firefox` (`scripts/build.mjs --target firefox`) writes `dist-firefox`. Its
  manifest is the Chrome one with `browser_specific_settings.gecko.id` `inbound-bridge@kaan88`,
  `background.scripts` instead of a service worker, and no `key` or `minimum_chrome_version`.
  `scripts\package-extension-firefox.ps1` checks it and zips it for addons.mozilla.org (AMO) into
  `artifacts\firefox-addon`. Release Firefox runs only signed add-ons.
- **Self-distributed (EXT.S4, owner 2026-11-08).** The add-on stays unlisted on AMO, which signs it but gives it
  no public page and no updates. `scripts\sign-extension-firefox.ps1 -ReleaseTag v<app version>` builds the
  package, has AMO sign it as an unlisted version (`web-ext sign --channel unlisted`, with the owner's AMO API
  key pair from a file outside the repository, passed to web-ext only through its environment), or takes an XPI
  signed by hand with `-SignedXpi`. It checks that the XPI carries AMO's signature and exactly the files built,
  writes `artifacts\firefox-addon\inbound-bridge-<version>-firefox.xpi`, and writes
  `extensions/firefox-updates.json` with that version, its release download address and its SHA-256.
  The Firefox manifest's `update_url` is that file on `main`
  (`https://raw.githubusercontent.com/Kaan88/inbound/main/extensions/firefox-updates.json`), so Firefox finds an
  update once the release squash puts the file there, and the XPI must be on that release by then. A version
  number can be signed only once.
- **In Setup.** `build-installer.ps1` requires the signed XPI for the extension's version (`-NoFirefoxXpi` for
  a test build) and Setup installs it as `{app}\firefox\inbound-bridge.xpi`. Its last page offers "Add the
  extension to Firefox", ticked, when Firefox is installed (App Paths) and no profile under
  `%APPDATA%\Mozilla\Firefox\Profiles` already has `inbound-bridge@kaan88.xpi`. It starts Firefox with the XPI,
  and Firefox asks the user to add it. Firefox no longer installs add-ons put in place by other programs
  (registry or folder sideloading), and enterprise policy would mark the browser as managed, so the user's
  click is the step left. `INSTALL.txt` says how to add the file by hand. A copy older than 0.5.3 has no
  `update_url`, so Firefox asks addons.mozilla.org, which serves only listed versions: Setup then offers the
  same entry unticked, to add it again once. Gaps: Firefox from the Microsoft Store has no App Paths entry and
  keeps its profiles elsewhere, so Setup offers nothing there; a profile outside the usual folder gets the
  ticked entry. The in-app update installs only the app and the Chrome extension folder, so a Firefox user
  updated that way gets no XPI and no prompt; the release page has the XPI.
- **Caught before the Save dialog (EXT.B5).** Only the Firefox manifest adds `webRequest` and
  `webRequestBlocking`. A blocking `webRequest.onHeadersReceived` listener sees each page and frame load's
  response. `response-download.ts` says whether Firefox would save it: a GET answered 200 or 206 that is an
  attachment, or has a type Firefox can't show. A missing type, or one it shows (pages, text, images it draws,
  PDF, JSON, the audio and video it plays), is answered at once with nothing. A download gets the same checks
  as one from `onCreated` (on, paused, the rules, site access, Delete held). Its name comes from
  `Content-Disposition` (`filename*` first; a name only, never a path), its cookies from the request's own
  cookie store (`cookieStoreId`), and its referrer from the page that started it (`originUrl`). The request
  is cancelled only after the app or server accepts the handoff, so Firefox never starts the download or asks
  where to save it. A cancelled typed address leaves the tab on the page it was on. Refused or unreachable,
  Firefox goes on as before, and the `onCreated` that follows leaves it alone for a minute, since the user was
  already told. With "ask each time" the toolbar popup asks (EXT.R7, below; the folder window when the popup
  can't open) and the request goes. Keep in browser, or closing the popup or window without a choice, starts
  the download again in Firefox (`downloads.download`), and that one is let through. A
  form POST is never caught this way, since it can't be sent again; it still reaches `onCreated`.
- **Differences handled in code.** No `downloads.onDeterminingFilename` (see above);
  `cookies.getPartitionKey` is optional already. A caught download's cookies come from its own cookie store
  (`DownloadItem.cookieStoreId`, a container tab's container), on the automatic and the "ask" paths alike;
  Chromium has no such field and reads the default store. Firefox may install the extension without access to sites;
  then Settings shows "Allow access to all sites", which asks with `permissions.request` from the click.
  Meanwhile a download from a site without access (`permissions.contains` for its origin) is not caught: it
  stays in the browser, unpaused, with a note, since its cookies could not be read. Chromium is not asked.
- **Registration.** Firefox lists add-on IDs (`allowed_extensions`) where Chrome lists origins, so it has a
  manifest of its own, `native-host\com.kaank.downloadmanager.firefox.json`, under its own key
  `HKCU\Software\Mozilla\NativeMessagingHosts\com.kaank.downloadmanager`. Setup and
  `register-native-host.ps1` (install, repair, updater) write both; the key is optional like Edge's, and
  uninstall removes it only when it is this install's. The Host does not yet report or repair the Firefox
  key (only Chrome's and Edge's).
- **No upgrade to https (EXT.B6).** Firefox's default CSP for MV3 extension pages adds
  `upgrade-insecure-requests`, so the background's fetch to a server at `http://<LAN address>` went to `https://`
  instead and failed with a bare network error (loopback addresses are exempt, and Chromium's default has no
  upgrade, so Edge paired with the same server and code). The Firefox manifest sets
  `content_security_policy.extension_pages` to `script-src 'self'`: the same default without the upgrade.
  Reproduced and checked in headless Firefox 159 against a test server on a LAN address of this PC (2026-11-09).
- **Checked in Firefox 159** (2026-11-08, a test profile with "Always ask" on): automatic catching (EXT.B3), and
  catching before the Save dialog for a clicked link and a typed address (EXT.B5). **Not verified:** Firefox's
  install prompt for host permissions.

## Which downloads are caught (EXT.B1)

- **Rules.** Every file type is caught except a short, editable exclude list: web pages and their parts
  (`html`, `php`, `js`, `css`, `json`, `xml`, …) and images (`jpg`, `png`, `gif`, `webp`, …), which "Save image
  as" produces all the time. Before rules version 2 a fixed allow-list was used, which missed `flac`, `mp4`,
  `pdf`, `epub` and others. A copy whose list is still exactly the old default moves to the new rules once;
  an edited list is kept. The move adds the new default exclusions to any excludes the user already had, so
  pages and images, never caught under the old allow-list, are not caught after it either. Settings still offers "catch only these types" for anyone who wants an allow-list.
- **Left in the browser, and why.** Each download the extension leaves to the browser, except `blob:` and
  `data:` downloads made by the page itself, is noted with the reason; the popup shows the recent ones.
  A notification says so too for a rule the user set (an excluded site or type, a type missing from their
  own allow-list), but not for the default exclusions, a pause or a held Delete, which are expected.
- **On or off at a glance.** Automatic sending starts on for a fresh install (owner decision, 2026-11-08;
  0.4.0 started it off), and Settings opens on install beside the explanation of what it shares; an
  update keeps the user's saved choice. While it is off or paused the toolbar badge reads OFF, the popup and Settings say so in
  words with a way to turn it on, and the first download of a browser session that stays in the browser
  because of it raises one notification.

## Choosing a folder (EXT.R1) and the popup's list (EXT.R2)

- **When sending a download:** "Use the default folder" (default; the app's, or the server's when downloads
  go to the server) or "Ask each time". Asking holds the caught download, paused, while the toolbar popup
  (EXT.R7, below) or, as a fallback, `handoff.html` shows the default folder (from
  `settings.downloadFolders`, which returns only that and the favorite folders), the favorites and
  Choose… (the app's folder picker). Download sends it and removes the browser's copy; Keep in browser, or
  closing the window, gives it back to the browser. The window holds a port open to the worker: its pings
  keep the worker (and the browser's file-name callback, which lives only in memory) alive, and the port
  closing is how a closed window is seen, so no `windows.onRemoved` listener wakes the worker for every
  browser window. Closing the window while the request is on its way waits for the app's answer. If the
  worker restarts anyway, the pending entry (in session storage) still works, and since the file-name
  callback is gone, Keep in browser cancels the stuck download and starts it again in the browser, once. A folder chosen there is for that
  download only; the next window starts at the default folder again (UI.R3). The right-click menu asks there
  only with "Ask where to save"; otherwise it sends straight to the app (EXT.R12). With a server paired the window also offers **This PC or Server** (below).
- **Asking in the toolbar popup (EXT.R7).** An automatically caught download that needs a folder waits in a
  queue (`askQueue`, also in session storage), and the worker opens the extension's toolbar popup with
  `action.openPopup({ windowId })` for the normal window in front, so the question appears under the
  extension's icon like the browser's own download bubble. The popup shows the oldest waiting download
  (`ask-panel.ts`: file name, This PC or Server, Save in with Choose…, favorites, free space on the server,
  Start, Download and Keep in browser), "1 of 3" and the names of the others; when nothing waits it is the
  usual popup with its download list, which then shows the download just sent. Several downloads at once open
  the popup once and queue in it: from the `openPopup` call until the popup's port connects (or the 5-second
  wait runs out) the popup counts as opening, `openPopup` is not called again (Firefox refuses a second call
  while the first popup loads), and downloads caught meanwhile join the queue. A download caught while the popup
  is open joins the queue. The popup holds a port (`dm.ask`) open to the worker while it is open: its pings keep
  the worker alive, and the port closing means the popup closed, so whatever it showed and still waits goes back
  to the browser, exactly as when the folder window is closed without a choice (one being sent waits for the
  answer). The popup also says it is closing on `pagehide` (`dm.askClosing`), because Firefox reports the port
  closed about a second after the popup has gone. A popup closes when it loses the focus (a click on the page,
  another window, or a tab switch), so that too leaves the download to the browser. A download counts as shown
  once it was in an open popup for 500 ms; one caught as the popup was already closing (it goes within about a
  tenth of a second of losing the focus) was never seen, so the popup opens again for it, once. Choose… for the
  server lists its folders inside the popup. Choose… for This PC would open the desktop app's folder dialog,
  which takes the focus and so closes the popup: instead the waiting downloads move to folder windows
  (`dm.askInWindow`), the chosen one opening its dialog at once (`handoff.html?pick=1`) and starting from what
  was set in the popup (target, file name, folder and start mode). Right-click downloads always use the window.
  API facts relied on:
  - Chrome: `action.openPopup()` is available to every extension from Chrome 127; from 118 to 126 only to
    policy-installed extensions ([chrome.action](https://developer.chrome.com/docs/extensions/reference/api/action#method-openPopup),
    [MDN compatibility data](https://github.com/mdn/browser-compat-data/blob/main/webextensions/api/action.json)).
    The manifest minimum is 109, so older browsers have no usable call. Edge follows Chromium.
  - Chrome, and Firefox from 149: a `windowId` naming a window that is not focused is refused
    ([MDN action.openPopup](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/action/openPopup)).
    A download item names no window, so the worker looks for the focused window of type `normal` in
    `windows.getAll({ windowTypes: ["normal"] })` and does not call `openPopup` when there is none. It checks
    the type itself: in Firefox 159, `windows.getLastFocused({ windowTypes: ["normal"] })` returned a focused
    folder window (type `popup`), and `openPopup` then succeeded there with nothing shown (seen on a test
    profile, 2026-11-09).
  - Firefox: `action.openPopup()` from 109, but before 149 only from a user gesture
    ([MDN compatibility data](https://github.com/mdn/browser-compat-data/blob/main/webextensions/api/action.json);
    [Mozilla add-ons blog, Firefox 149-152](https://blog.mozilla.org/addons/2026/04/23/webextensions-api-changes-firefox-149-152/),
    Desktop only). A caught download is not a gesture, so Firefox 140 to 148 refuses and the window asks. From
    150, uplifted to 149, it also refuses while a context menu, doorhanger or notification is open
    ([bug 2022281](https://bugzilla.mozilla.org/show_bug.cgi?id=2022281)).
  - The call resolves when the popup opens and rejects when it does not (MDN). Seen on test instances
    (2026-11-09): Chromium 158 resolves only once the popup page has loaded and connected, and rejects with
    "Failed to open popup." when the popup was closed while loading; Firefox 159 resolves before the page
    connects.

  **Fallback to the window:** when `action.openPopup` is missing, when no normal browser window is focused,
  when the call is refused, or when the popup has not connected within 5 seconds of the call (also when the
  call never answers), every waiting download gets its own folder window, as before EXT.R7. The exception is a
  popup the user closed before it could ask: if the popup page was there while `openPopup` was pending
  (`runtime.getContexts` in Chromium, `extension.getViews` in Firefox) and the call was then refused, or the
  page is gone when the 5 seconds run out, no choice was made and the browser keeps the downloads. A folder
  window that can't be opened, or does not connect within 20 seconds, gives its download back to the browser.

  Checked on 2026-11-09 with test instances only (a test Host, a test server on 127.0.0.1 and a local file
  server; downloads to test folders): Chrome for Testing 158 with the unpacked build and a server-only pairing
  (the popup opened by itself; three downloads queued, one kept, one sent to the server, the third back to the
  browser when the popup closed; a minimised browser got the folder window instead), and Firefox 159 with
  `dist-firefox` installed through WebDriver BiDi, paired with the server and the installed native host
  reaching the test Host (the popup opened by itself with This PC and Server; switching target, the server's
  folder list, a send to a server folder; closing the popup started the download again in Firefox; with the
  window not in front, three folder windows, closed, gave the three back to Firefox). Edge was not run.

  Checked again later on 2026-11-09 after the fixes for a download caught while the popup closes, several
  downloads while it loads, a focused folder window in Firefox and Choose… carrying the popup's choices (test
  Host, test server and file server as above; the test native host registered under the Chromium key and
  removed afterwards): Playwright's Chromium 158, headed, and Firefox 159, headed over BiDi. In both, a
  download caught as the popup lost the focus opened the popup again and asked (Chromium 3 of 3, Firefox 4 of
  4, with the final build; a temporary trace in earlier Chromium runs showed the reopening 8 times); three downloads at once opened one popup with "1 of 3" (in Chromium also 100 and
  250 ms apart, on a build before the last two changes); a popup shown for over a second and closed gave its
  downloads to the browser without opening again. In Firefox, with
  a popup-type window focused, `openPopup` was not called and a folder window asked. Choose… for This PC after
  switching from Server, renaming and choosing Start later opened a window on This PC with that name and start
  mode, and the test Host's folder dialog. A minimised Chromium window got the folder window. Not reproduced:
  a popup closed before its page connects. In Chromium that leaves only a few milliseconds (the page connects
  before `openPopup` answers); a close forced in that gap was refused with "Failed to open popup." before the
  page was seen, so the folder window asked. In Firefox the page connected even when the popup was closed
  while loading, so the popup opened again once. The closed-before-connecting rules are covered by the npm tests.
- **Popup:** the newest 20 of all the app's downloads, whatever started them, with Pause, Resume (Retry for
  a failed one) and Delete, which asks first. For a finished download it has "Delete the file too", unticked;
  an unfinished one's partial data is always removed, as in the app's window and the web UI, and never a final file it did not
  write (UI.B19). Each action goes on a
  native connection of its own, as the folder picker does: the Host answers one command at a time per
  connection, and a slow delete must not hold up an automatic handoff past its time limit.
- **Row menu and folder button (EXT.R11, GitHub #4):** a right-click on a row, the Menu key or Shift+F10 opens
  Open containing folder (This PC only), Copy download link (off for a provider's file or a hidden key), Redownload
  (finished, failed or stopped downloads; it saves a new copy and keeps the existing file), Rename… (finished
  downloads; the app checks the name and never replaces another file) and Delete… (the Delete button's question).
  A finished This PC download has a folder button left of Delete, which opens Explorer with the file selected, or
  its folder when the file is gone, or says "The file and its folder are gone." Open file is the row's double-click
  (or Enter) on a finished This PC download (EXT.R13, `job.open`): the app opens the file with its program, a
  program included (owner, 2026-11-13); Windows shows its own security warning first for a file with the mark of the
  web. Other rows open the app. The link is copied by the popup
  itself and asks nothing of the app. An app older than these commands refuses them, and the popup then says
  "Update Inbound to use this."; a rename counts as done only when the app sends back the new name.

## Like the browser's own downloads (EXT.R12, extension 0.6.3)

- **The browser keeps the focus.** Every handoff carries `quietWindows` (true unless Settings > General > "Open the
  app's windows for browser downloads" is on, which is off by default). The Host stores it on the job
  (`JobDto.QuietWindows`) and `DownloadWindows.KeptQuiet` then opens no progress window as it starts and no finished
  window (a package with such a file opens none either); the desktop window's own completion rule and torrent file
  choice follow it. The one exception is a link that turns out to be a `.torrent` file (T10): the app still opens its
  torrent dialog for it (T10.B, owner 2026-11-12), as for any other finished `.torrent` download, when torrents are on
  or a provider account takes them; otherwise it stays an ordinary finished file with Open as Torrent. The app's own "Download complete" notification and its sound stay away too
  (`DownloadNotices`), for a lone download and for a package with such a file: the browser shows the finish. A
  failure is still told by the app, and so is what extraction says. An older Host ignores the field and opens its
  windows as before.
- **The toolbar icon** follows the downloads this browser sent (Host sources `chromeAuto` and `chromeContextMenu`),
  from the app's `job.event`s on the shared native connection and from the popup's list. While one runs, a green
  ring fills with their combined progress around a smaller copy of the extension's icon; orange while one is paused,
  red when one failed (both until the popup is opened, or it runs again); the plain icon otherwise. The ring is drawn
  with OffscreenCanvas into `chrome.action.setIcon` only when its state or one of 24 steps changes; the tooltip says
  the same in words. Nothing polls or runs on a timer: with no events nothing happens. After a browser restart the
  icon catches up once the popup is opened (the connection, and with it the events, starts with the first request).
  A new worker draws the plain icon once at start, since the browser keeps an icon an earlier worker set. When the
  native connection closes or the app sends `host.exiting`, the downloads still running leave the icon (no event
  would say how they end); failures and pauses already shown stay until the popup is opened.
  The followed downloads are this PC's only; a paired server's are not shown on the icon.
- **When one finishes**, Settings > "When one finishes" picks: flash the icon (three blinks, the default), open the
  popup (`action.openPopup`; Chrome and Edge 127+; where the browser refuses without a click, as in Firefox, the icon
  flashes instead), or nothing.
- **Show in app** (the popup's row menu, this PC's downloads only; a server's has its web page instead) sends
  `host.openUi` with the optional `jobId`. The Host checks the download is on its list and starts the window with
  `--show-job <id>`; a running window gets the id through a small request file next to its queued torrent opens and
  its activation event. A window showing another server switches to This PC first. The main window switches to All
  Downloads if needed, opens the package holding it, selects,
  scrolls to and focuses the row, and highlights it for 2 s (one one-shot timer). An older Host opens the main
  window only.
- **The right-click item** ("Download with Inbound Download Manager") sends the link straight to where automatic
  downloads go (This PC unless the popup's switch says the server), with the page's cookies and the default folder,
  and opens nothing; a notification says how it went. It sends no file name: the Host takes the site's own name
  (Content-Disposition) and falls back to the address, so `get.php?id=5` is not saved as `get.php`. With "Ask where
  to save" it still asks in its window. The worker loads the settings before acting on a click that woke it.

## Sending to the user's own server (EXT.R1 transport)

`src/transport.ts` has two transports with one interface (`send` for a reply payload, `request` for a whole
envelope on a connection of its own):

- **`NativeTransport`**, this PC: the native-messaging code that used to live in `background.ts`, unchanged in
  behaviour (one shared connection with one reconnect, one-off connections for the folder picker and the
  popup's actions, `job.event` passed on).
- **`ServerTransport`**, the server: one `POST <address>/api/ipc` per envelope with
  `Authorization: Bearer <token>`, `credentials: "omit"` (never the browser's cookies for the server),
  `redirect: "error"` (the token goes to the paired address only) and a time limit. A 401 means the pairing
  was removed (`ServerUnpairedError`, "Pair it again"); a 403 refusal of the paired role comes back as its
  envelope; no answer is said in the server's own words, never as the desktop app missing: "Could not connect
  to the Inbound server at <address> (the browser said: <fetch error>)", plus a word on the certificate for an
  https:// address. A time-out says so instead.

**Pairing** (Settings, Inbound server): the user types the address (a missing scheme means http; a path is
dropped; a user name or password in it is refused) and the 8-character code from the web UI (Settings,
Sign-in and browsers). On the click the page asks the browser for access to that host only
(`permissions.request`, origin pattern without the port); Chrome and Edge already have it through the
all-sites host permission, Firefox may ask. The background then calls `POST /api/pair` with the code and a
name such as "Chrome on Windows", checks the token with one `host.ping`, and only then stores
`{address, token, id, name, serverName, pairedAt}` under `server` in `chrome.storage.local` (`name` is this
browser's name on the server, `serverName` the server's own name from the pairing answer, EXT.R9). No page gets the
token: `dm.status` and the other replies carry the address, names and state only.

**The server's name** (EXT.R9, GitHub #2): Send to, the popup and Settings call the server by its shown name: the
user's own name from Settings, Server, Name (`settings.serverCustomName`, kept apart from the pairing, so it can be
set before pairing and pairing again never replaces it), else the server's `serverName`, else the host of its
address. Send to shows the host after a name, smaller: "Home NAS (192.168.1.150)". `dm.status` carries
`server.serverName` and `server.displayName`; the folder windows' targets carry `name`. One custom name is kept, as
only one server can be paired. On Chrome and Edge the background
closes `storage.local` to content scripts (`setAccessLevel`, `TRUSTED_CONTEXTS`); Firefox has no such call,
so there content scripts can read `storage.local`, token included, and `dm.status.storageClosedToPages` is
false. The extension's own content script never reads storage. Unpair deletes it and sets
automatic downloads back to this PC, and asks the server to remove the token too: `DELETE /api/pair/self`
with the same `Authorization: Bearer <token>` as `/api/ipc` (no body, `credentials: "omit"`,
`redirect: "error"`, a 4-second limit; the token is forgotten here first, whatever the answer). On 204
Settings says the server removed the pairing too; on anything else (no answer, an older server without the
call, a token already removed) it says to remove this browser from the web UI's Paired apps list.
Only `options.html` may pair or unpair. A plain-http address that is not loopback gets a one-line
note that the token and cookies cross the network unencrypted.

**Which target:**

- **Automatic sending** uses "Automatic downloads go to" (`settings.defaultTarget`, This PC by default; the
  server only while one is paired). There is no fallback: if that target refuses or does not answer, the
  download resumes in the browser, as before, and is never sent to the other one, so the cookies only go
  where the user chose.
- **The folder window** (Ask each time, and the right-click menu) and **Download All** offer the targets that
  can work: the server once paired, and This PC unless the server is paired and the desktop app is not
  installed here (the native host is "not found"). With one target no choice is shown. Each target brings its
  own default folder and favorites (`settings.downloadFolders` from each). Choose… opens the app's folder
  dialog for This PC, and for the server a list of its folders one level at a time (`folders.browse` through
  `dm.browseServerFolders`, which only `handoff.html`, `batch.html` and the popup may use; the server limits it
  to its roots). The window starts on the default target. A folder chosen there is for that download only.
  EXT.R10: Settings can set a default folder for browser downloads on This PC (automatic sends carry it, and the
  forms send it as it is, since the app does not know it) and where the forms start: Default folder (the default),
  Last used folder (per target, kept in `chrome.storage.local` only while chosen) or Custom folder (This PC). A
  start folder is used only when known to exist: `settings.downloadFolders` with `candidates` answers
  `existingCandidates` for local paths on the user's own Host (never a share, a device path or a drive letter
  mapped to a share), and the server is asked with `folders.browse`. A browser folder that was deleted is not
  checked: automatic sends still carry it and the Host creates it again. Settings takes a drive path or a share
  on Windows (not `\Downloads`, `/Downloads` or `\\?\`), and a path from `/` in a browser on Linux.
  Both windows fill the folder field (Save in, in Download All) with the chosen target's default folder and
  show the other target's default on a switch; Download All gets them from `dm.targets`, which only
  `batch.html` and the popup may use and which asks the desktop app for its folders even with no server
  paired. The folder is sent only when it was changed, so an untouched field still lets the app or the server
  decide.
- **Folders come from the place the download goes (EXT.B7).** In server mode, and whenever Server is the
  chosen target, every folder the extension shows is the server's: the default folder, the favorites, the
  folder list behind Choose… and the free space (the server's `folders.browse` answer for the folder in Save
  in, shown as "… free on the server"; This PC has no such request through the native host, so nothing is
  shown for it rather than another machine's figure). Recent folders are not offered for either target.
  Download All's Browse and Download now wait for the target list, as the folder window's already did: before,
  a click while the list was on its way opened this PC's folder dialog even in server mode. Before the list
  arrives the windows say "The default folder" rather than "The app's default folder". With a server paired,
  the popup says where downloads are saved ("Saved in /downloads on the server"), from `dm.targets`, and
  follows the This PC / Server switch. Settings says "Use the default folder", the app's or the server's.
- **The popup** lists the newest 20 of each place, labelled This PC or Server, and sends Pause, Resume, Retry
  and Delete to the place the download is in. Without the desktop app here, its card is hidden and the
  server's card shows the state. One place not answering leaves the other's list.

The server's side (pairing, the token, the extension role with `folders.browse`, CORS for our extension ids
and any `moz-extension://` origin) is in `docs/ipc-protocol.md` (Sign-in and paired apps).
`scripts/server-smoke.mjs` runs `transport.ts` against a running test server (pair, folders, handoff,
duplicate, list, delete, a refused command, a removed token).

## Handoff flow (durable)

```
Chrome UI / context menu
        │
        ▼
Extension: build HandoffRequest
  requestId (UUID), url, finalUrl, pageUrl, referrer, userAgent,
  cookies for url, optional headers, suggested filename, size, mime,
  startMode (auto | ask | later), source (context-menu | auto-intercept)
        │  connectNative(our host)
        ▼
NativeHost: validate frame, ensure host running, send `handoff.request`
        │  named pipe
        ▼
Host: validate schema/version, dedupe requestId, resolve destination,
      durably write job + requestId record, then ACK {jobId, accepted}
        │
        ▼
Extension: on ACK → cancel Chrome download (auto) or leave browser download
           on any error → resume/leave Chrome download and show actionable error
```

A job is never considered accepted before the durable write completes. If the
extension times out, it retries the same `requestId`; the host returns the
existing job instead of creating a duplicate. Dedupe records expire after a
configurable window but remain keyed by `(extensionId, requestId)`.

## Supported / unsupported request classes

| Class | Initial decision | Reason |
| --- | --- | --- |
| Ordinary GET (link, file, attachment) | **Supported** | URL + cookies + referrer are sufficient |
| GET with query/signed URL | **Supported while valid** | Replayable; refresh flow needed when expired |
| GET after cross-origin redirect | **Supported with scope checks** | Cookies selected per origin; auth headers dropped cross-origin |
| Cookie-authenticated GET | **Supported** | Cookies for the original address go with it, and only to that site; not logged |
| Basic/Bearer-auth GET | **Conditional** | Only if header is visible and user enables header transfer |
| POST form/file upload | **Cannot be detected** | Chrome's download API does not show the method, so it is treated like any other download. If the app accepts it, Chrome's copy is gone and the app's GET may fail. Pause sending first |
| `blob:` / `data:` generated downloads | **Unsupported → Chrome** | Not addressable by the native client |
| Streaming playlists (HLS `.m3u8`, DASH `.mpd`) | **Unsupported → browser** | Stream downloading was cut in app 0.7.2. The extension leaves a playlist download in the browser ("Streaming playlists stay in the browser."); one sent to the app another way fails at once with "Streaming playlists aren't downloaded; use a video downloader." |
| Chrome Web Store / browser-protected URLs | **Unsupported → Chrome** | Browser policy |
| Incognito | **Unsupported → Chrome** | Session boundary; the manifest sets `"incognito": "not_allowed"` |
| YouTube | **Unsupported → Chrome** | Chrome Web Store policy; `src/unsupported-sites.ts` |
| Very short-lived URL | **Ask / later** | May need refresh-address workflow |

The fallback is explicit: if we cannot accept, the extension resumes or never
cancels Chrome's download, and tells the user why. A failed handoff must not
silently lose a download.

## Our protocol

Our extension and host use a versioned JSON protocol. Native Messaging frames are 4-byte little-endian length + UTF-8 JSON.
The native host wraps messages in:

```json
{ "v": 1, "type": "handoff.request", "id": "<uuid>", "payload": { ... } }
```

The host replies with `handoff.ack` / `handoff.error`, then streams
`job.event` messages on the same port. The extension may re-query with
`jobs.snapshot` after reconnecting. Full schema lives in
`src/DownloadManager.Contracts`.

## Permissions requested by our extension

`downloads`, `contextMenus`, `storage`, `cookies`, `nativeMessaging` and
`notifications`, with host permissions for `http://*/*` and `https://*/*`, since a
download can start from any site. Pairing a server asks for its host again
(`permissions.request`), which matters in Firefox, where host access is the user's
choice; no `optional_host_permissions` entry is added, since the required host
permissions already cover every http and https address. `docs/chrome-store/LISTING.md` gives the reason
for each. What the extension does with data is in
`docs/chrome-extension-privacy.md`; keep both true when behaviour changes.

Without access to a download's site its cookies can't be read, so such a download stays in the browser,
with a note saying why. That holds in every browser: Firefox may install without site access, and in
Chrome and Edge the user can withdraw it for a site or set it to "on click".

Automatic catching is on from a fresh install; Settings opens on first install and explains beside the
checkbox what it shares, so it can be turned off before the first download.

## Security boundaries

- Cookies and Authorization values are never written to diagnostic logs.
- The native host accepts only our own extension IDs (`BrowserBridge.IsTrustedExtensionId`): the Chrome Web
  Store build, the unpacked development build, the Edge Add-ons build and the Firefox add-on (validated by the
  browser's invocation and by `allowed_origins`, or `allowed_extensions` for Firefox). Any other ID is refused, including a store package unpacked
  by hand, whose ID comes from its folder. Chrome reports that as `forbidden`, and the extension says
  which ID was refused. To test a store package unpacked: `docs/chrome-store/LISTING.md`.
- The native host checks its own command line as well (`BrowserBridge.TrustedCaller`): a trusted
  `chrome-extension://` origin (Chrome, Edge) or, as the last argument after the manifest path, the trusted
  Firefox ID. Anything else, including no caller at all, exits with code 2 before the Host is started or a
  frame is read. Until 2026-11-08 a command line without a `chrome-extension://` origin skipped this check,
  so a browser naming another extension as the caller was let through. This checks what the browser says,
  not who started the process: a local program can write the same command line, and can reach the Host's
  pipe directly anyway (its ACL admits the whole user account). The boundary it adds is against other
  extensions, not against local programs.
- What crosses the bridge is an allow-list in both directions (`NativeBridgePolicy`, per `BridgeRole`):
  from the extension `handoff.request`, `host.ping`, `host.openUi`, `host.pickFolder`, `jobs.query`,
  `job.pause/resume/retry/cancel/remove`, `settings.downloadFolders` and the popup row menu's `job.reveal`,
  `job.update` and `job.redownload` (EXT.R11); to it only the replies and events it reads. A job action is
  checked by payload too: it names exactly one download (`jobId`), carries no `jobIds` list, and asks for
  `deleteFile` only on `job.remove`, so a compromised extension cannot remove every download and its file in
  one request. The menu's commands carry exactly `jobId`, and `job.update` exactly `jobId` and `newFileName`: from
  the browser it only renames a finished file in its own folder, never moves it, sets a completion program or
  changes anything else. `host.openUi` opens only a page the window knows (`settings`); any other text opens
  the main window and never reaches its command line (report 3, L2). `job.reveal` opens the file's folder on this PC for the job's own path only. A snapshot loses its settings on the way. Provider (`remote.*`) and
  archive messages never cross. A download the app manages on its own (a torrent job; `NativeBridgePolicy.ForBridge`,
  in snapshots, `job.event`, `job.actionResult` and `handoff.ack` alike, for both roles) reaches the browser with the
  short magnet the window shows as `url` (no trackers), no `finalUrl`, `appManaged: true`, and `canResume: false`
  while it is paused waiting for its file choice (a cancelled one keeps Retry, which brings it back to that wait). Its
  `torrent` details and `statusNote` are dropped, its `failure.message` and an action's `error` become neutral text
  (`kind` and the retry flags stay), since the Host words them for the app. The popup then offers "Open in app" for it
  on this PC (nothing for a server's, whose note says it waits on the server), and no rename, redownload or link copy;
  a finished row offers Delete and the folder only. The extension's own source
  and build still say nothing of torrents (roadmap rule 5).
- To a paired server the extension is the `BridgeRole.ServerExtension` role: the same allow-list without
  `job.reveal` (the server's screen is not the user's) plus `folders.browse`, checked by the server on every `POST /api/ipc` with the token, replies included. The
  extension sends `handoff.request` with its own extension id, which the server checks as the desktop app does.
- Inside the extension, content scripts can send only the messages they need
  (`CONTENT_SCRIPT_MESSAGES`), since they run alongside untrusted pages.
- The host validates every field and enforces size/depth limits. Toward the browser, a frame over the browsers'
  1 MB native-message limit is never sent: a reply becomes `handoff.error` with `errorCode: "message_too_large"`
  for the same request, and an event is dropped (`docs/ipc-protocol.md`, The browser extension). The popup's
  `jobs.query` asks for `{ "limit": 20 }`, the newest 20.
- The extension may not request arbitrary local file destinations (report 3, M1, 2026-11-12). On the
  desktop Host a `handoff.request` folder is used only when it is the default folder, a favorite, a
  category folder, a recent folder, or a folder inside one of those, or a folder this Host's own folder
  dialog (`host.pickFolder`, Choose… in EXT.R1/R10) returned. The Host keeps the picked folders (the
  newest 64) in `picked-folders.json` in its state folder, so the extension's saved browser folder still
  works after a restart. Any other folder is replaced by the default folder, the Host logs a warning, and
  `HandoffAck.note` says so; the extension shows that note as a notification even with notifications
  off, and "Last used folder" then remembers the default folder. A folder typed in the extension's
  Options (This folder, Custom folder) is confirmed in the desktop app's folder dialog on Apply.
  The headless server keeps its own roots check (web-session folder limit) instead.
- Credentials are scoped to the destination origin and dropped on cross-origin
  redirect (another host, port or scheme, so https to http too). Saved site logins follow the
  same rule: one is sent only to the download's own origin, never on a hop that leaves it. A
  site login saved without a scheme (`example.com`) is sent over https only, to exactly that
  host; `*.example.com` takes in its subdomains; one saved as `http://…` may also go over http
  (SEC.L1).
