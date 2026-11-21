# Android link catching — implementation checkpoint

2026-11-18. Continue after the audited updater 0.0.2 was delivered. Scope ANDROID.5;
images B1-B6 in android-mockups are the visual spec, subject to recorded owner decisions.
This is an implementation brief, not acceptance evidence.

## Slices

1. Bounded HTTP(S) shared-text intake and page-link selection/filtering as pure
   Mobile models. ACTION_SEND text only, no automatic job admission; confirm on phone.
2. Native in-app WebView page, share/caught sheet and page-link picker. Keep it in
   MainActivity's navigator to preserve existing Host visibility/lifetime authority.
   No JavaScript-native bridge; bounded one-shot DOM evaluation for anchor collection.
3. Captured file metadata and Cookie/User-Agent/Referrer transfer for the precise
   request origin, then durable Host job.add ACK before confirming success. Cookie
   capture stays transient in views; Host owns protected request persistence.
4. Expired-link paste/open-page workflow using Host refresh commands; verify range
   continuation/hash against controlled files. Never promise resume if the server
   cannot prove it is the same content. Provider-managed files keep provider refresh.
5. Local scripted share/browser/auth-cookie/page-batch/expired-link checks, screenshot
   comparisons, regression suites and Fable fix/reverification loop. Record Fable scope.

## Decisions and constraints

- Android 12+ routes arbitrary unverified web intents to browsers. Do not advertise
  generic Open with for all sites; Share and the built-in browser are the supported
  paths. Official source: https://developer.android.com/training/app-links/create-deeplinks.
- Owner chose (2026-11-18): ask before sending cookies to the selected server.
  Name the precise target and obtain per-download consent before forwarding cookies;
  B3 must distinguish local cookies from an explicitly approved remote transfer.
- No file/content/javascript/intent navigation or unsafe TLS bypass. Disable file and
  content access, mixed content, unrequested popup windows and native JS interfaces.
  Do not import another browser's profile or cookies. Do not log URLs, headers or tokens.
- Preserve the actual observed WebView referrer when validated; an absent observed referrer remains absent. Never infer one merely from the page. No HTTPS-to-HTTP downgrade.
  Never attach a page's cookies to another origin merely because its link was clicked.
- DownloadListener does not supply a POST body. Blob/data streams, DRM/live playlists
  and sign-ins that reject embedded browsers must be reported accurately, not bypassed.
- Default destination is Download/ directly. Hide queue/folder options without an
  implemented Host operation; do not build nonfunctional copies of mockup chips.
- Preserve or safely cancel pending capture on rotation; do not put cookies in Bundles,
  intents or logs. A changed target Host must not silently inherit a consent/selection.
- Browser-auth refresh adds optional request context and Host capability negotiation; shared-runtime validation, backward compatibility and durability passed the Source audit; Fable review remains open.

## Current evidence

Source passed the ANDROID.5 review after confirmed fixes and controlled regressions.
Permanent-key 0.0.3 is on the Desktop and S24; the signed local release bundle is
not published and the public GitHub update feed remains 0.0.2. See PROGRESS's
0.0.3 delivery entry for exact APK hash and device evidence.

NativeAOT fixture checks cover signed-in catching, page-link collection and
authenticated refresh of the existing failed job, with matching payload hashes.
Debug checks cover category filtering, batch selection/admission, Later/Resume,
remote cookie consent and cancellation, delayed-metadata page closure and POST
refusal. Light picker/caught-sheet spot checks passed and the emulator theme was
restored. GitHub's public sign-in form renders; no real account was used.

Core 2373, Engine 210 and Mobile 408 passed. The last full Integration run was
2059 passed, 32 skipped and one queue-retry failure before its fix. The exact
failure and deterministic queue regressions subsequently passed, queue policy
passed 10, and the final refresh/reservation/wakeup checks passed 16. No clean
full Integration rerun after those fixes is claimed.

Real-account compatibility, broader visual/accessibility and rotation acceptance
remain open; Fable review stays OPEN. Unknown or ambiguous request provenance,
including some redirect downloads, requires a manually supplied URL. POST/blob
streams are not replayed as GET; no TLS bypass or browser-profile import exists.
The reusable loopback fixture is tools/AndroidApp/link-fixture/.
