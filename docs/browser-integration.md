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
3. **`webRequest` is observational in MV3.** Blocking requires a
   policy-installed extension, so the extension does not use it and does not
   request the permission.
4. **Cookies are readable with the `cookies` permission + host permissions.**
   `chrome.cookies.getAll({url})` gives the cookie set for the target; this is
   the main way to transfer a browser session.
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
   (`scripts\package-extension-store.ps1`) has none. The native side trusts two IDs:
   the store's (`olhbcdbjdnfoiinllnlpandggadfabfm`) and the unpacked build's (`pknddmneejadnnngffojnoecojhdfalc`).
   See `docs/chrome-store/LISTING.md`.

## Edge

Edge is a supported browser (owner decision 2026-11-06). It runs the same extension and the same native host.

- **Install route.** Edge users install the extension from the Chrome Web Store. Edge may ask to allow
  extensions from other stores; that prompt has not been checked in Edge. The ID is therefore the store's, `olhbcdbjdnfoiinllnlpandggadfabfm`, and
  needs no new `allowed_origins` entry. A separate Edge Add-ons listing is parked (ROADMAP EXT.S1) because
  the owner cannot enroll in the Edge program.
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
  `connected; not registered for Chrome`, `connected; Chrome registration is broken`,
  `connected; Edge registration is broken`. Chrome with no Edge key stays plain `connected`, because Edge may
  not be installed and an install from 0.4.0 has no Edge key until the Host adds it.
- **Repair at Host start.** Once per start, best effort, when one key is usable and the other is missing or
  points at a file that is gone, the Host writes the working key's value into the other one. It logs a failure
  rather than showing one, skips isolated-data runs, and never replaces a registration whose manifest exists,
  so a broken one that still has its file stays named in the status until `repair.ps1` runs.
- Evidence and what is still unverified are in `PROGRESS.md`.

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
| POST form/file upload | **Cannot be detected** | Chrome's download API does not show the method, so it is treated like any other download. If the app accepts it, Chrome's copy is gone and the app's GET may fail. Pause catching first |
| `blob:` / `data:` generated downloads | **Unsupported → Chrome** | Not addressable by the native client |
| DRM-protected media segments | **Unsupported** | Detection only; no decryption |
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
download can start from any site. `docs/chrome-store/LISTING.md` gives the reason
for each. What the extension does with data is in
`docs/chrome-extension-privacy.md`; keep both true when behaviour changes.

Automatic catching is off until the user turns it on in Settings, which opens on
first install and explains beside the checkbox what it shares.

## Security boundaries

- Cookies and Authorization values are never written to diagnostic logs.
- The native host accepts only our two extension origins (store and unpacked) (validated by Chrome's
  invocation and by `allowed_origins`). Any other ID is refused, including a store package unpacked
  by hand, whose ID comes from its folder. Chrome reports that as `forbidden`, and the extension says
  which ID was refused. To test a store package unpacked: `docs/chrome-store/LISTING.md`.
- Inside the extension, content scripts can send only the messages they need
  (`CONTENT_SCRIPT_MESSAGES`), since they run alongside untrusted pages.
- The host validates every field and enforces size/depth limits.
- The extension may not request arbitrary local file destinations; the host
  applies its own destination policy and shows the resolved path in the UI.
- Credentials are scoped to the destination origin and dropped on cross-origin
  redirect unless the user configured a site login for the target.
