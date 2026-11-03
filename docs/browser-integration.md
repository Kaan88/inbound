# Browser integration strategy

Reference: reference app Integration Module 6.43.1 (MV3) + `com.the vendor.reference app` native host.
Analysis notes: `notes/browser-integration.md`.

## What Chrome actually permits (MV3)

These constraints drive the design; they must be verified against the installed
Chrome version during Phase 4.

1. **No arbitrary request replay.** A service worker cannot read a request body
   and construct an equivalent authenticated request for a different client in
   the general case. POST bodies, multipart uploads and short-lived signed URLs
   may be unavailable. Such downloads stay in Chrome.
2. **Interception point is the Downloads API.** `chrome.downloads.onCreated`
   fires when Chrome starts a download. `chrome.downloads.cancel(id)` and
   `resume(id)` are available. `onDeterminingFilename` can suggest a filename
   but cannot itself cancel. We therefore pause/cancel only *after* a durable
   ACK from our host.
3. **`webRequest` is observational in MV3.** Blocking requires a
   policy-installed extension; we do not rely on it. `onBeforeSendHeaders` /
   `onHeadersReceived` are used to enrich metadata, not to redirect.
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
   manifest must list exactly our extension origin.

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
| Cookie-authenticated GET | **Supported** | Cookie set transferred; redacted in logs |
| Basic/Bearer-auth GET | **Conditional** | Only if header is visible and user enables header transfer |
| POST form/file upload | **Unsupported → Chrome** | Body replay is not reliable |
| `blob:` / `data:` generated downloads | **Unsupported → Chrome** | Not addressable by the native client |
| DRM-protected media segments | **Unsupported** | Detection only; no decryption |
| Chrome Web Store / browser-protected URLs | **Unsupported → Chrome** | Browser policy |
| Incognito (unless extension allowed) | **Unsupported → Chrome** | Session boundary |
| Very short-lived URL | **Ask / later** | May need refresh-address workflow |

The fallback is explicit: if we cannot accept, the extension resumes or never
cancels Chrome's download, and tells the user why. A failed handoff must not
silently lose a download.

## Our protocol

Our extension and host use a versioned JSON protocol (not reference app's `MSG#` text
protocol). Native Messaging frames are 4-byte little-endian length + UTF-8 JSON.
The native host wraps messages in:

```json
{ "v": 1, "type": "handoff.request", "id": "<uuid>", "payload": { ... } }
```

The host replies with `handoff.ack` / `handoff.error`, then streams
`job.event` messages on the same port. The extension may re-query with
`jobs.snapshot` after reconnecting. Full schema lives in
`src/DownloadManager.Contracts` and `docs/ipc-protocol.md`.

## Permissions requested by our extension

Minimal but sufficient: `downloads`, `contextMenus`, `storage`, `cookies`,
`webRequest`, `nativeMessaging`, `notifications` (optional), `tabs` (page URL
for context/referrer). Host permissions only for origins the user enables
where practical; the first version uses `<all_urls>` for parity with the
reference, with an explicit settings toggle.

## Security boundaries

- Cookies and Authorization values are never written to diagnostic logs.
- The native host accepts only our extension origin (validated by Chrome's
  invocation and by `allowed_origins`).
- The host validates every field and enforces size/depth limits.
- The extension may not request arbitrary local file destinations; the host
  applies its own destination policy and shows the resolved path in the UI.
- Credentials are scoped to the destination origin and dropped on cross-origin
  redirect unless the user configured a site login for the target.
