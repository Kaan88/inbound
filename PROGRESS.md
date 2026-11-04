# Progress — session handoff

## Current state (2026-11-04)

**Goal:** Build a private reference app-compatible download manager. The full 1:1 parity
goal is **not complete**; Phase 0–5 are substantially implemented, later phases
remain open. Authoritative parity evidence is in `reference notes`.

- Repository: `C:\Users\kaank\Downloads\reference app`; remote
  `https://github.com/Kaan88/download-manager` (verified **private**).
- Commits: `0f3a294` baseline engine/host/IPC/CLI/native bridge,
  `a957f70` Chrome extension, `2143b6e` reference app-shaped WinUI frontend,
  `eb03ef4` segmented transfers, packaging scripts, browser-handoff evidence,
  and updated parity/onboarding.
- reference app reference pinned to **6.43.12.3** (`v6.43b12 Full`); see
  `notes/reference-version.md`.
- Tracking fallback: Goal/Todo tools are unavailable in this OpenCode 2.2.5
  environment. `GOAL.md`, `ROADMAP.md`, `reference notes`, and this file are the
  authoritative records. Do not claim a Goal/Todo update.

## What works now

| Area | State |
| --- | --- |
| Single-stream HTTP/HTTPS | Working; hashes verified; redirects, unknown length, safe names, truncation, cancellation |
| Durable jobs/settings | Atomic JSON stores; restart reconciliation; request-claim dedupe |
| Resume | Byte checkpoints, Range/Content-Range validation, changed-content restart, ignored-range restart |
| Parallel transfers | Fresh downloads split into 2–N ranges (≥1 MiB), RandomAccess assembly, per-segment checkpoints/retries, no-range fallback, segmented resume verified |
| Host lifecycle | One per-user Host, tray, hide-on-close, named-pipe IPC, snapshot/events, CLI control |
| WinUI frontend | reference app-shaped dark shell: Tasks/File/Downloads/View/Help menu, large toolbar, category sidebar, eight-column list; Add URL and Options dialogs; filtering/sorting; single-instance activation |
| Chrome extension | Own MV3 identity `pknddmneejadnnngffojnoecojhdfalc`; automatic interception and context-menu handoff; destination/start dialog; popup/options |
| Native bridge | Registered per user as `com.kaank.downloadmanager`; durable ACK and dedupe; Chrome for Testing handoff verified |
| Queues | Named queues (`Main`, added `Night`), per-queue max concurrency, start/stop, queue assignment at add time, persistence across restart; CLI `dm queue` and WinUI Add URL/Start Queue/Stop Queue |
| Packaging | `scripts/build.ps1`, `publish.ps1`, `register-native-host.ps1`, `install.ps1`, `uninstall.ps1`, `package.ps1`; installed-build download and uninstall verified in this harness; clean external environment still unverified |

## Browser evidence (2026-11-04)

Chrome stable 156 removed `--load-extension`; automated verification used
**Chrome for Testing 156.0.8078.80** with a fresh profile and the unpacked
extension from `extensions\chrome\dist`. The extension ID and service-worker
target were:

```
chrome-extension://pknddmneejadnnngffojnoecojhdfalc/background.js
```

Automatic interception:

- Triggered `http://127.0.0.1:6411/file/browser-auto2.bin?...size=2097152&seed=202`.
- Host job `430cdc3a...` source `chromeAuto`, extension ID
  `pknddmneejadnnngffojnoecojhdfalc`, state `completed`, 2,097,152 bytes.
- Chrome `downloads.search({})` returned an empty list after takeover.
- File SHA-256 matched seed 202:
  `b960b0a9b5085893db0d4a0b8935b48107c90299700d96ff5cafd86d8b4bd8f6`.

Context-menu handoff:

- Context menu contained **Download with Download Manager**; invoking it opened
  the extension handoff dialog.
- Confirming it produced host job `8becefbb...`, source `chromeContextMenu`,
  state `completed`, 1,048,576 bytes, file `browser-context.bin`.
- File SHA-256 matched seed 303:
  `58ad8b2859b3f4b4eb686d0df4c5ac164daf3c36bff7c12f5a653eac8231d53c`.

Native-host registration evidence:

- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager`
  points at `%LOCALAPPDATA%\DownloadManager\native-host\com.kaank.downloadmanager.json`.
- Manifest path:
  `C:\Users\kaank\Downloads\reference app\artifacts\app\DownloadManager.NativeHost.exe`
  (development publish), allowed origin only the pinned extension.

## Queue evidence (2026-11-04)

- `RuntimeServiceTests` now has three queue acceptance tests (see `reference notes`
  E9). The suite is **9/9 passed**.
  - `StoppingQueuePausesOnlyThatQueue`: stopping queue `Night` paused only its
    job; `Main` completed; starting `Night` resumed and the SHA-256 matched
    seed 42.
  - `PerQueueConcurrencyIsRespected`: two queues capped at one each ran exactly
    one job each while the global cap was three.
  - `QueueStateAndMembershipSurviveRestart`: queue name, max, stopped state, and
    the job's queue membership survived service recreation.
- CLI end-to-end against the published host (E10): `queue add Night 2`,
  `queue stop 1`, add with `--queue 1` stayed `queued` with no file,
  `queue start 1` completed 524,288 bytes; SHA-256
  `cd77d60c1cd3feb361d2013317b43a5e40cdac91bcf973cc9421d5a281dd121f`
  matched test seed 707.
- Reassignment (E13): `job.moveToQueue` moves a job between queues, persists the
  new membership, and pauses an active job when the target queue is stopped.
  It is exposed through CLI `queue move`, the WinUI row context menu, and the
  integration test `MovingJobToStoppedQueueUpdatesMembershipAndPauses`.
- WinUI controls (E11): the real window showed the `Queue` column with `Night`.
  UI Automation selected an active row and invoked **Stop Queue**; the host
  reported queue 1 stopped and the job paused at 983,040/8,388,608 bytes.
  Invoking **Start Queue** made queue 1 running and the job resumed at
  1,441,792 bytes. The job was then cancelled and its partial file removed.
- WinUI row menu (E14): the compact `Q` column shows the queue number. Selecting
  a completed row and pressing Shift+F10 opens the row menu with `Open`,
  `Open folder`, `Remove`, `Add to queue >`, `Delete from queue`, and
  `Properties`. The menu is constructed in code and was verified in the running
  window.
- Packaging fix (E15): the published WinUI app was missing
  `DownloadManager.Desktop.WinUI.pri`, causing `XamlParseException` at startup
  while the bin build worked. `publish.ps1` now copies the generated `.pri` and
  fails if it is absent; the republished app started and reached
  `MainWindow activated` with no crash log.

## Payload placement (2026-11-04)

reference app downloads to a fixed temporary directory and then stitches/moves the result,
so choosing another drive does not avoid C: space or a cross-volume copy. Our
engine instead writes all ranges at their final offsets into a single
random-access part file **in the destination directory**:

- `DownloadManager.Runtime` now sets `PartFilePath` to
  `<final path>.<job id>.part` in the chosen destination.
- The engine already used `RandomAccess.WriteAsync` at range offsets, so there
  is no separate segment stitching step; completion is `File.Move` within the
  same directory/volume, which is atomic and needs no extra free space.
- Metadata and checkpoints remain small and stay in the data directory.
- `PartialPayloadLivesOnDestinationVolume` verifies the live part location, the
  empty state partial directory, part cleanup, and the final hash.
- Published CLI check: the live `payload-loc.bin` part file was
  `test-downloads\payload-loc.bin.51948fdc....part` (1,310,720 bytes while
  downloading), `artifacts\app\data\partial` was empty, and after completion the
  part was gone, the 4 MiB file remained, and its SHA-256 matched seed 99
  (`4f1f120d5224489310bb0c39f4ff32ea3798e8ee1a2d99fe0b647696a20afec0`).

## Scheduler evidence (2026-11-04)

- Daily and one-time schedules target one queue, honor weekday selection, and
  start/stop that queue only. Same-day duplicate starts are suppressed; a
  same-day window that already closed is not started late; a scheduled stop
  pauses the queue's active jobs.
- Last start/stop dates persist in settings across service recreation, so a
  restart does not repeat the same day's start.
- The host runs an evaluation immediately at startup and every 30 seconds.
- Integration tests (E16): `DailyScheduleStartsAndStopsOnlyItsQueue`,
  `OneTimeScheduleOnlyRunsOnItsDate`, `ScheduleRunStateSurvivesRestart`.
  Clearing all schedules is also covered (an empty list is not treated as
  "keep existing").
- Published host: added a Wednesday 02:42–04:42 schedule for stopped queue
  `Night` at 02:42:20. At 02:42:42 the host logged
  `Scheduler start queue 1 for 2026-11-04`; `queue list` showed queue 1
  `running` and `schedule list` showed `last-start=2026-11-04`.
- CLI: `dm schedule list|add|remove|enable|disable`; removal accepts a
  hyphenated or compact GUID prefix.

## Authenticated browser handoff evidence (2026-11-04)

- TestServer `/auth/login` sets an HttpOnly `dmsession` cookie and links to
  `/auth/file/secure.bin?size=2097152&seed=123`; that endpoint requires the
  cookie, a Referer starting with `/auth/login`, and a `Chrome/` user-agent.
- Engine test `SendsCookieRefererAndUserAgentToAuthenticatedEndpoint` downloads
  the protected file, asserts the expected hash, checks auth stats, and checks
  that the stats response never exposes the cookie value. Engine suite:
  **15/15 passed**.
- Live Chrome for Testing 156 (extension
  `pknddmneejadnnngffojnoecojhdfalc`) clicked the login page link. Chrome's
  `downloads.search({})` was empty after takeover; host job `b0dc0ea4...` was
  `chromeAuto`, `completed`, 2,097,152 bytes; SHA-256 matched seed 123:
  `e0816c5b74d3960a47028f600af178dda4fe932f80a02417d8ced54f6101d52b`.
- `/auth/stats` before the negative probe: `authorized=10, denied=0,
  lastCookiePresent=true`, last Referer
  `http://127.0.0.1:6411/auth/login?size=2097152&seed=123`, last UA Chrome/156,
  last Range `bytes=1048576-1310719` (segmented requests all authenticated).
- A request without the cookie returned **403** and `denied=1` with
  `missing or wrong dmsession cookie`.
- Job JSON contained header key `Cookie` and the expected test value; host logs
  contained neither `dmsession` nor the cookie value. The value is redacted from
  all committed records.

## Proxy and credential-boundary evidence (2026-11-04)

- Added dynamic manual/system proxy settings (`ProxySettings`) to the engine and
  runtime settings, exposed via CLI (`proxymode`, `proxyaddress`, `proxyuser`,
  `proxypass`) and IPC.
- TestServer now includes `TestProxyServer`, a local Basic-auth proxy supporting
  absolute-form HTTP forwarding and HTTPS CONNECT. It tracks challenge, denied,
  authorized, and tunnel counts.
- Engine tests (**19/19**):
  - `DownloadsThroughAuthenticatedHttpProxy` — 524,288 bytes, expected hash,
    zero wrong-credential denials.
  - `ProxyRejectsWrongCredentials` — job fails with a proxy denial.
  - `CrossOriginRedirectDropsSensitiveHeaders` — target sees
    `cookie=false, authorization=false, referer=false`.
  - `SameOriginRedirectKeepsSensitiveHeaders` — all three remain true.
- Runtime test (**15/15**): `ProxySettingsApplyToRuntimeDownloads` updates the
  service settings, downloads through the proxy, and verifies the hash.
- Real HTTPS/CONNECT test: published host downloaded the Alpine 3.24.3 virt ISO
  (`70,254,592` bytes) through `127.0.0.1:5965` using 5 authenticated CONNECT
  tunnels (`authorized=5, denied=0, tunnels=5`, last target
  `dl-cdn.alpinelinux.org:443`). SHA-256 matched the official checksum
  `cdc1bc0efc64d8d611bf3733f54868172a545888dfd726f518bf279bcb0ff1af`.
- SOCKS5 (`socks5://host:port` + username/password): added `TestSocks5Server`
  with RFC 1929 auth. Engine tests **21/21**: local HTTP via SOCKS5 with 4
  connections and expected hash; wrong credentials recorded and rejected.
  Runtime **16/16** includes `Socks5SettingsApplyToRuntimeDownloads`. Published
  host downloaded the same Alpine ISO through 4 authenticated SOCKS5
  connections (`authorized=4, denied=0, connects=4`, target
  `dl-cdn.alpinelinux.org:443`), matching the official SHA-256.
- Switching proxy schemes at runtime exposed a stale-handler bug: the client is
  now rebuilt when proxy settings change, so HTTP→SOCKS5 changes apply.
- Proxy password is currently stored in settings JSON; DPAPI protection and a
  proxy settings UI remain open. SOCKS4/4a and UDP ASSOCIATE are untested.

## Expired URL and refresh evidence (2026-11-04)

- TestServer `/expiring/{name}` accepts only the current token; stale tokens
  return **403** with `X-Url-Expired: true`. `/expiring/stats` reports expired,
  served, and Range requests.
- The engine now classifies 403 + `X-Url-Expired` and 410 as
  `expiredUrl`, marks it resumable, and preserves the part file/checkpoint.
- `RefreshUrlAsync` validates the new URL, keeps the existing part file,
  filename, and validators, clears the failure, requeues, and resumes. Invalid
  URLs are rejected without touching the job.
- Integration test **17/17** (`RefreshDownloadAddressResumesAfterExpiredToken`):
  paused after >65,536 bytes, rotated token 1→2, resume failed with
  `expiredUrl`/`IsResumable=true` while partial bytes stayed, rejected
  `not-a-url`, then refreshed to token 2, completed 1,048,576 bytes with the
  expected seed-96 SHA-256, and observed `ExpiringExpired>0` and
  `ExpiringRangeRequests>0` (Range resume, not restart).
- Published CLI/IPC check: paused at 589,824 of 4,194,304 bytes, rotated the
  token, resume failed with `expiredUrl` while keeping 589,824 bytes, then
  `dm refresh <id> <token-2-url>` completed 4,194,304 bytes with SHA-256
  `26f44b54b4514a10a47b40a30a6b383985fbb7a2723882a97f2482ded313c24f`;
  `/expiring/stats` showed `expired=1, served=7, rangeRequests=6`.
- CLI: `dm refresh <job-id> <new-url>`. WinUI row menu:
  **Refresh download address...** with an editable URL dialog. Both use the
  `job.refreshUrl` IPC method.

## Site-login evidence (2026-11-04)

- Site passwords are stored only as per-user Windows DPAPI blobs prefixed
  `dpapi:v1:`; settings readback and `site list` never return the plaintext.
- Engine-side Basic auth is applied for the most specific matching host/port/path;
  it does not persist an `Authorization` header in job JSON.
- TestServer `/site/secure/{name}` requires `siteuser`/test password and reports
  authorized/denied counts.
- Integration suite **18/18**: no-login job failed `unauthorized`; adding a login
  produced settings JSON without plaintext and with a DPAPI blob; a 2 MiB
  two-connection download matched seed-98 SHA-256 with no extra denials; a fresh
  service instance decrypted the login and completed a 256 KiB download matching
  seed-99.
- Published CLI: `site add` → `site list` showed `stored`; settings JSON
  `contains plaintext sitepass: False`, `contains dpapi marker: True`; 2 MiB
  download SHA-256 `81953ebf30a903db33b8538341ff9caf964fe635ae7405eb137fd21c4a469b05`;
  after a host restart a 256 KiB download matched
  `eed4f2827224985e51fedd9ece2ca1716093ac418ad4f7db5d22e0f1e570605f`;
  `/site/stats` ended `authorized=5, denied=0`; `site remove` left an empty list.

## Quotas and completion-action evidence (2026-11-04)

- Queue quotas: `QuotaEnabled/QuotaBytes/QuotaWindowHours/QuotaWindowStart/
  QuotaBytesUsed`. Progress deltas accumulate per queue; reaching the limit
  pauses active jobs, stops the queue, and blocks new starts. A new window
  resets usage. CLI: `dm queue quota <id> <MB> <hours>|off`.
- Completion actions on schedules: open file, exit host, or shutdown
  (shutdown/restart/logoff, optional force). Exit/shutdown require
  `AllowSystemCompletionActions`; they are suppressed while any other download
  is unfinished; exceptions are logged and never fail the job.
- Integration suite **22/22**:
  - `QueueQuotaPausesDownloadsAndBlocksNewStarts` — 512 KiB quota paused a 1 MiB
    job, stopped the queue, left the next job queued; a 25-hour-old window and a
    larger limit allowed both jobs to complete with the expected hash.
  - `CompletionActionsFireAfterQueueFinishes` — exactly one open and one exit
    after the last of two queued jobs completed.
  - `SystemCompletionActionsRequireOptInAndNoActiveDownloads` — exit suppressed
    with opt-in off, suppressed while another queue was active, fired once after
    cancellation.
  - `CompletionActionFailureDoesNotFailTheDownload` — throwing open action left
    the job completed; shutdown dispatched `restart` + force.
- Core `ShutdownCommandTests` verify `/s`, `/r`, `/l`, `/t 30`, and `/f`
  argument construction. Tests use a recording executor; no real shutdown was
  performed.

## Phase 8 closure evidence (2026-11-04)

- `DownloadLaterWaitsForExplicitStart`: a Later job stayed queued and absent
  from disk across queue stop/start until `resume`, then completed with the
  expected hash.
- `ScheduleHandlesRepeatedDstHour`: first 01:30 (−04:00) started the queue; the
  repeated 01:30 (−05:00) did not restart it; the next Sunday did.
- `ScheduleHandlesOvernightStopAcrossDst`: 23:00 start before the spring-forward
  shift stopped at 07:00 after it.
- Sleep prevention: `SleepPreventionFollowsActiveDownloads` and
  `SleepPreventionSettingCanDisableAndReenable` verify acquire → pause release →
  resume acquire → complete release, plus live disable/re-enable. Published host
  logs for an 8 MiB transfer:
  `Sleep prevention active.` then `Sleep prevention released.` after pause.
  `powercfg /requests` requires elevation in this shell, so the OS request was
  confirmed through successful API calls/host logs rather than powercfg output.
- Windows implementation uses a process-wide `PowerCreateRequest` system-required
  request (not thread-affine `SetThreadExecutionState`).

## Media detection evidence (2026-11-04)

- Added a content script that scans `video`, `audio`, `source`, and HLS/DASH
  links, deduplicates absolute HTTP(S) URLs, and reports them to the service
  worker. The worker keeps a per-tab list and sets an action badge; the popup
  lists distinct resources and sends a selection through the existing handoff
  dialog.
- Chrome for Testing 156, `/media-page` with video/audio/HLS link:
  - badge count `3`;
  - media list: `media-video.mp4` (video), `media-audio.mp3` (audio),
    `demo.m3u8` (hls);
  - selecting the video produced a 524,288-byte host download with SHA-256
    `d49a100a8afd43dc7f02b4c2d34b8362b0f00d9250dd75d3ddee7afaeb05b1ce`,
    matching seed 201.
- V1 implemented, V2 partial (popup list; no in-page overlay/quality variants).
  HLS detection exists, but HLS/DASH segment downloading is still pending.

## HLS evidence (2026-11-04)

- Engine detects `.m3u8` targets and downloads HLS playlists:
  - master playlist selects the highest-bandwidth variant;
  - media playlist downloads `#EXTINF` segments sequentially into the
    destination-local part file;
  - concatenated TS output is renamed to `.ts`; fMP4 (`#EXT-X-MAP`) output uses
    `.mp4`.
- Rejections are explicit: encrypted HLS (`#EXT-X-KEY` other than NONE),
  byte-range segments, live playlists without `#EXT-X-ENDLIST`.
- Test assets were generated with ffmpeg 9.0.2: H.264 320×180 + AAC, three 2 s TS
  segments, served by TestServer with master/media playlists and
  `/hls/expected`.
- Integration `HlsMasterAndMediaPlaylistsAssembleSegments` (**28/28 suite**):
  both playlists produced 580,732-byte `.ts` files with SHA-256
  `6a1384036fceb6355c2aae29504a8fa5fd899c642fd47d309685607c0031b946`.
- Published CLI download produced the same hash. ffprobe:
  H.264 320×180, AAC, duration `6.023222` s.
- Limitations: no AES-128, live, byte-range, or cross-restart HLS checkpoint;
  no audio/video muxing.

## DASH evidence (2026-11-04)

- Static DASH MPD support for `SegmentTemplate` with `initialization`, `media`,
  `$RepresentationID$`, `$Bandwidth$`, `$Number%05d$`, and `$Time$`; highest
  bandwidth representation selected.
- Rejects dynamic/live manifests, ContentProtection/DRM, and
  SegmentBase/SegmentList with clear failures.
- Test assets: ffmpeg 9.0.2 video-only MPD, init + three fMP4 chunks, served by
  TestServer with `/dash/expected`.
- Integration `DashManifestDownloadsInitAndMediaSegments` (**29/29 suite**):
  produced 498,366 bytes and matched SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`.
- Published CLI produced the same size/hash; ffprobe reported H.264 320×180,
  duration `6.000000` s. Output is video-only when the MPD has no audio.

## Audio/video muxing evidence (2026-11-04)

- Added `IMediaMuxer` and `FfmpegMediaMuxer` behind an explicit FFmpeg path
  setting (`ffmpegpath` or PATH; optional `DOWNLOADMANAGER_FFMPEG` test hook).
  FFmpeg is invoked with `ArgumentList`, `-c copy`, deterministic `-bitexact`
  flags, a 5-minute timeout, and output validation.
- DASH now selects separate video and audio AdaptationSets, downloads each track
  to a destination-local temp file, muxes to `.mp4`, validates the optional hash,
  and cleans up temp tracks.
- `/dash-av` test assets and a reference `muxed.mp4` (364,519 bytes, SHA-256
  `6074bcc38ce18a4507e84d27173d7d77d9f6931801c7173b437c649b1e5088ab`).
- Integration `DashSeparateAudioVideoMuxesWhenFfmpegAvailable` (**30/30 suite**
  with `DOWNLOADMANAGER_FFMPEG`): output matched the reference size/hash.
- Published CLI with `ffmpegpath` configured produced the same 364,519-byte
  output/hash; ffprobe reported H.264 320×180 + AAC, duration `4.000000` s.
- The test returns early when FFmpeg is absent; the recorded run had FFmpeg
  9.0.2 configured. FFmpeg is not bundled.

## Fragment recovery evidence (2026-11-04)

- Added `MediaCheckpoint`/`IMediaCheckpointStore` with per-job/per-track JSON
  under `media-checkpoints/`. Each checkpoint records the track, manifest
  signature, completed segment count, and completed byte count.
- HLS/DASH assembly loads the checkpoint, verifies the signature and part
  length, truncates any partial fragment, resumes at the next completed
  boundary, and saves after every full segment. Manifest changes invalidate the
  checkpoint and restart safely. DASH A/V tracks checkpoint independently.
- `HlsResumesAtCompletedSegmentAfterInterruption`: `/hls/recovery.m3u8` at
  64 KiB/s paused after >210,000 bytes; after resume `seg0.ts` was requested
  exactly once, a later segment was repeated for its partial bytes, and the
  final file matched the expected SHA-256.
- `DashResumesAtCompletedSegmentAfterInterruption`: `/dash/recovery.mpd` paused
  after >184,000 bytes; `chunk-0-00001.m4s` was requested exactly once, a later
  chunk repeated, and the final file matched the expected SHA-256.
- Integration suite **32/32** with FFmpeg configured. Existing ffprobe checks
  confirm playable HLS/DASH output.

## Expiring media-token evidence (2026-11-04)

- Media checkpoint signatures now use scheme/host/path and intentionally ignore
  query strings, so rotating tokens do not invalidate completed fragments.
- A segment 403 carrying `X-Url-Expired` (or 410) makes the engine refetch the
  playlist/manifest once and resume from the media checkpoint.
- `HlsRefreshesExpiredSegmentTokens`: `/hls-rotating/playlist.m3u8` rotated its
  token after `seg0.ts`; the next segment was rejected with 403 +
  `X-Url-Expired`, the playlist was refetched, and the job completed with the
  expected 580,732-byte SHA-256
  `6a1384036fceb6355c2aae29504a8fa5fd899c642fd47d309685607c0031b946`.
  `rotating-seg0.ts` was requested exactly once.
- `DashRefreshesExpiredSegmentTokens`: `/dash-rotating/manifest.mpd` did the
  same after `chunk-0-00001.m4s` and completed with SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`;
  the first chunk was requested exactly once.
- Integration suite **34/34** with FFmpeg configured.
- Limitation: one automatic refresh per job. A playlist URL that itself expires,
  AES key rotation, and DRM license refresh are not handled.

## Live and DRM evidence (2026-11-04)

- Live behavior is an explicit exclusion: HLS without `#EXT-X-ENDLIST` and DASH
  with `type="dynamic"` fail before any segment/payload work with failure kind
  `liveStream`, non-retryable and non-resumable. The message states that a VOD
  playlist or static MPD is required.
- DRM detection:
  - HLS `#EXT-X-KEY`/`#EXT-X-SESSION-KEY` with `METHOD=SAMPLE-AES`, `skd://`,
    or Apple/Widevine/PlayReady key formats → `drmProtected`.
  - DASH any `ContentProtection` element → `drmProtected`, with scheme IDs in
    the message.
  - AES-128 HLS remains separately unsupported (`InvalidContent`), not labelled
    DRM.
- TestServer endpoints: `/live/playlist.m3u8`, `/live/manifest.mpd`,
  `/drm/hls.m3u8`, `/drm/manifest.mpd`.
- `LiveStreamsAreRejectedExplicitly` and
  `DrmProtectedMediaIsIdentifiedAndNotBypassed`: both cases failed with the
  expected kind/message, were not retryable/resumable, and left no final or part
  file.
- Permanent content failures (`liveStream`, `drmProtected`) now report
  `canResume: false` and `isTerminal: true`; other failed jobs remain retryable
  (for example, a 401 after adding a site login).
- Published CLI reproduced all four failures with 0 bytes and no files:
  - `liveStream` for `/live/playlist.m3u8` and `/live/manifest.mpd`;
  - `drmProtected` for `/drm/hls.m3u8` and `/drm/manifest.mpd`.
- No key request, license exchange, decryption, or bypass is implemented.
- Integration suite **36/36** with FFmpeg configured.

## Download All evidence (G1, 2026-11-04)

- The content script collects unique HTTP(S) URLs from anchors, images, and
  media sources; it resolves relative URLs, strips fragments, excludes
  mailto/javascript/hash links, and reports name, kind, extension, and host.
- The service worker exposes `dm.pageLinks` / `dm.confirmBatch` and opens a
  Download All window. The window lists links with type/host, filters by name,
  type, host, or URL, supports per-link and select-all-visible selection, and
  sends the selected URLs as authenticated batch handoffs.
- TestServer `/download-all-page` contained: A, B, duplicate A, `mailto:`,
  `javascript:`, `#hash`, an image, and an HLS link.
- Chrome for Testing 156 with the extension collected exactly 4 unique links
  (A, B, HLS, image); the duplicate and non-downloadable schemes were excluded.
- UI: 4 rows with `BIN`/`M3U8`/`PNG` labels; filtering `all-a` showed 1 row;
  selecting A and B displayed `2 selected`, enabled Download, and reported
  `Added 2 downloads.`
- Host: `all-a.bin` 262,144 bytes SHA-256
  `af96c694df7c1c1d8ec660ce45083576aea5c4a619ae6336a02124f428526d48`
  (seed 210) and `all-b.bin` 131,072 bytes SHA-256
  `96e8343a81a63c25ee1871936870371b385f8ac045d17937a09d0b1a65ec5e39`
  (seed 211), both matching.
- G2–G4 remain: crawl scope/depth, relative path/link rewriting, preview,
  persistent/scheduled projects.

## In-page panel and quality evidence (V2, 2026-11-04)

- The content script injects a shadow-DOM panel (`#dm-media-panel-host`) with a
  `Media (N)` badge and a list of detected resources, each with a Download
  action. The popup additionally lists distinct resources and HLS master
  variants with quality labels.
- Background HLS enrichment fetches master playlists (4 s timeout, 5 min cache)
  and parses `BANDWIDTH`, `RESOLUTION`, and `NAME`; variants are sorted by
  bandwidth.
- TestServer `/hls/multi.m3u8` advertises:
  - `90p`: 160×90, 250 kbps, `media-low.m3u8`;
  - `180p`: 320×180, 800 kbps, `media.m3u8`.
- Chrome for Testing 156, `/media-page`:
  - panel existed with badge `Media (3)` and rows video/audio/HLS (panel was
    visibly open in the first run);
  - popup rows: video, audio, HLS, `180p`, `90p`.
- Selecting `90p` queued `media-low.m3u8`; host completed `v2-low.ts`:
  - 279,368 bytes, equal to the concatenated low segments;
  - SHA-256 `e72c04e62c10eb2e926dda45f1af03b3fcc7693c34e6a7c2cda9641b9dea079e`
    matched;
  - ffprobe: H.264 160×90 + AAC.
- Remaining media work: additional DASH modes, HLS alternate audio, AES-128.

## Clipboard monitoring evidence (M9, 2026-11-04)

- `ClipboardUrlExtractor` finds the first absolute HTTP(S) URL in arbitrary
  clipboard text and trims trailing punctuation; it rejects non-URL text,
  `ftp://`, and `javascript:`.
- The Host polls the clipboard on its STA UI thread every 600 ms only when
  `ClipboardMonitoring` is enabled, suppresses the same URL for one minute, and
  adds a `clipboardMonitor` job through the normal durable pipeline.
- Core `ClipboardUrlExtractorTests` (8 cases) pass; core suite **25/25**.
- Published Host live check:
  - enabled monitoring, copied
    `http://127.0.0.1:6411/file/clip.bin?size=131072&seed=220`;
  - Host log: `Clipboard URL detected`;
  - job completed 131,072 bytes with SHA-256
    `7fd83e6ce6d28fc1854455a80d41dff387c913cf661602d14dc24064fd9868dc`;
  - disabled monitoring, copied `clip2.bin`; no job and no file appeared.
- Completion notifications and sound now honor `NotifyOnCompletion` and
  `PlayCompletionSound`; failures still notify. `ShowCompletionDialog` remains
  unimplemented.

## Batch URL evidence (M12, 2026-11-04)

- `BatchUrlExpander` lives in Contracts so CLI and WinUI share it. It expands:
  - numeric ranges, preserving zero padding (`[001-003]`);
  - ascending/descending ranges (`[1-10]`, `[c-a]`);
  - comma lists (`[1,3,5]`);
  - multiple groups as a cartesian product;
  - and rejects malformed/mixed ranges or more than 1000 URLs.
- Core `BatchUrlExpanderTests` (9 cases) pass; core suite **34/34**.
- CLI `add-batch`:
  - `batch-[1-3].bin?...seed=230` added 3 jobs, all completed at 65,536 bytes
    with SHA-256
    `2377642935741989d3a43dfefd0e3f027a48603012f3ab22b5ef92b14275dc89`;
  - a `[1-30]` pattern without `--yes` exited 2 and added nothing.
- WinUI: Tasks → Add batch download was opened through UI Automation; the
  dialog showed URL pattern, range hint, destination, start, and queue fields.
- Remaining: wildcard `*` substitution and batch import files.

## Editable properties evidence (M3, 2026-11-04)

- Added `job.update` / `JobUpdateRequest`; the runtime trims and persists the
  description, raises an update event, and rejects unknown job ids.
- CLI `dm describe <id> <text>` and the WinUI Properties dialog (editable
  Description + Save/Cancel) use the same IPC method.
- Integration `DescriptionEditPersistsAcrossRestart`: set/reset description
  trimming, unknown-id rejection, and reload from a fresh service instance.
- Integration suite **37/37**.
- Published CLI: `describe` returned `ok`; `status --json` showed
  `"description": "Quarterly report from UI"` before and after a Host restart.
- Remaining: editing URL/file name/destination, categories, recent folders,
  import/export, and the completion dialog.

## Duplicate/filename policy evidence (M6, 2026-11-04)

- Fixed the overwrite path: regular, HLS, and DASH-mux completion now pass
  `overwrite: ConflictPolicy.Overwrite`; previously Overwrite resolved to the
  existing path but the move refused to replace it.
- Integration `ConflictPoliciesHandleExistingFile`:
  - pre-created `conflict.bin` with `old content`;
  - `rename` produced `conflict (1).bin` and preserved the original;
  - `fail` was rejected with `already exists`;
  - `overwrite` replaced `conflict.bin` with the downloaded bytes.
  - downloaded content was 65,536 bytes with SHA-256
    `aca6f316de23308f512c95311ebd78be8efbe0e387748272f33f5c73bba28da0`.
- Integration suite **38/38**.
- Published CLI reproduced rename/fail/overwrite; rename preserved the original
  11-byte file, overwrite replaced it, and fail exited 1 with the destination
  error.
- WinUI Add/Batch dialogs expose `If the file exists` with Rename/Overwrite/Fail;
  the single Add dialog prompts Download again/Select existing on duplicate URLs,
  and batch can skip URLs already in the list.
- Remaining: duplicate-URL prompt automation and recent-URL/import work.

## Category and remembered-folder evidence (M7/M8, 2026-11-04)

- Added shared `FileCategories` (compressed/documents/music/programs/video/other)
  used by the runtime, CLI, and WinUI sidebar.
- Settings gained `CategoryFolders`; an add without an explicit destination uses
  the matching category folder, otherwise the last folder/default. Explicit
  destinations override category rules and update `LastDestinationDirectory`.
- Core `FileCategoriesTests` (7 cases) pass; core suite **41/41**.
- Integration `CategoryDestinationRulesAndLastFolderPersist`:
  - music category routed a destination-less `song.mp3` to `<dest>\music`;
  - explicit destination wrote at the root;
  - `LastDestinationDirectory` and the category folder survived a fresh service.
  - integration suite **39/39**.
- Published CLI: `musicdir` routed `song-cli.mp3` to `test-downloads\music`
  (SHA-256 `0c82b8f07b9f9b406dbfaaa3a7d6dc3887692152afd7571d786c16127d6539a2`);
  explicit destination wrote `song-explicit.mp3` at the root (SHA-256
  `02345a83f430ffe736fb5b1cb8325ee088f0762fddc87f64ba389860d87bb772`);
  `settings get` showed `last folder` and the music folder after a Host restart.
- WinUI Options exposes the five category folders; the sidebar now uses the
  shared category mapping.
- Remaining: recent-URL history, category UI polish.

## Export/import evidence (M13, 2026-11-04)

- Added `JobListFile`/`JobListEntry` in Contracts; `JobDto` now exposes its
  conflict policy so exports preserve it.
- Core `JobListFileTests` (3 cases) cover full-field round-trip, malformed JSON,
  and URL-less entries; core suite **44/44**.
- Published CLI with a fresh state:
  - added two Later jobs in `export-dest` with descriptions `export alpha` and
    `export beta`;
  - `export` wrote 2 JSON entries with URL/name/destination/queue/connections/
    start mode/conflict policy;
  - removed both jobs, then `import` added 2/2;
  - reloaded jobs retained destination, description, queue, `startMode: later`,
    and conflict policy.
  - resuming both imported jobs produced 65,536-byte files with matching
    SHA-256 `6eddea6c3f39796000cea6740cf4490cd497b19e066c2336c72a6a14d1cca863`
    and `d39a1f0c44e90216185bb2d02e21457f77f143ad5317fa37eb562af69c7bc01b`.
- Integration suite **39/39**.
- WinUI File menu exposes Export/Import with a path dialog.
- Remaining: recent-URL history; own JSON format rather than reference app's format.

## Completion dialog evidence (M11, 2026-11-04)

- WinUI now shows a `Download complete` dialog when `ShowCompletionDialog` is
  enabled and the window is open: finished file name/path plus Open file, Open
  folder, and Close.
- Completed jobs are queued so only one dialog is shown at a time.
- Published build: a 65,536-byte `complete-dialog.bin` job completed while the
  WinUI window was open; UI Automation read the title/path and all three
  buttons, and Close dismissed it.
- Tray notifications and sound honor `NotifyOnCompletion`/`PlayCompletionSound`
  (E33); completion actions fire once (E22). Sound was not acoustically verified.
- Remaining: recent-URL history.

## Site grabber evidence (G2/G3, 2026-11-04)

- Added a runtime `SiteGrabber` service behind `grabber.run` IPC:
  - BFS page crawl with depth 0–5 and page cap;
  - same-host scope, extension include-list, include/exclude URL globs, and
    existing-file skip;
  - URL-path preservation under the mirror folder;
  - HTML `href`/`src` rewriting to relative local paths when the target was
    mirrored; external links stay unchanged;
  - a separate HTTP client with redirects/decompression and a 5 MB page cap.
- TestServer `/grab/` graph: index → page1 → page2 → page3, with `a.bin`,
  `b.bin`, `c.bin`, `d.bin`, and `pic.png`.
- Integration `SiteGrabberCrawlsScopeAndRewritesLinks` (suite **40/40**):
  - depth 2 crawled exactly index/page1/page2, not page3;
  - extension filter `bin` skipped the PNG;
  - exclude glob `*/files/b.bin*` skipped b.bin;
  - queued a.bin/c.bin completed with seed 290/293 hashes;
  - saved `grab/index.html` had `href="page1.html"` and `files/a.bin`,
    preserved the external link, and page3/b.bin were absent.
- Published CLI reproduced 3 pages/2 queued/1 skipped:
  - `a.bin` SHA-256 `ac4cc81ea530f1ddca9f335980323eee724a351bd5b8d21d325ffd3b7108663b`;
  - `c.bin` SHA-256 `abef0ea89918113d007c57b8d2260ea10d78d31703ad9313b0eed9859f7ee09b`.
- WinUI Tasks → Run site grabber exposes all scope/filter/mirror controls.
- Remaining: JS-rendered pages, robots.txt, auth/proxy for crawler, CSS url()
  rewriting, preview.

## Grabber project evidence (G4, 2026-11-04)

- Added durable `grabber-projects.json` with project config, status, page/file
  counts, last error, created/updated/last-run times; `running` projects become
  `interrupted` after a restart.
- Runtime project API: create/list/run/remove, one run per project, progress
  saves throttled to 2 s; the same project can be resumed after restart.
- Schedules gained `GrabberProjectId`; a due start time launches the project once
  per day on the Host timer and records `LastStartDate`; stop times are ignored
  for grabber schedules.
- Integration (suite **42/42**):
  - `GrabberProjectPersistsAndResumesWithoutRedownload`: first run 3 pages,
    2 queued, 1 skipped; fresh service loaded the completed project; resume
    reported 0 queued/3 skipped, the job count stayed unchanged, and a.bin
    hash/mtime stayed unchanged; removal left an empty list; a bad start URL
    produced status `failed` with LastError (fixes the previous false
    "completed 0 pages").
  - `ScheduledGrabberProjectRunsOncePerDay`: schedule action `grabber` ran the
    project with files queued; the second same-day evaluation produced no action.
- Published CLI:
  - `grabber add/list/run/remove`; resume printed `0 queued, 3 skipped` and both
    job JSON and a.bin mtime were unchanged;
  - `schedule add 0 00:00 --grabber <id>` displayed `grabber=<id>`;
  - the live Host timer ran a scheduled project (2 pages, 2 files, 1 skipped)
    and recorded `last-start=2026-11-04`.
- WinUI: Tasks → Grabber projects lists name/status/URL/counts with Run, Remove,
  and New project; the grabber dialog can save a reusable project.

## FTP evidence (C1, 2026-11-04)

- Added `FtpDownloadEngine` behind the engine interface:
  - passive binary FTP; URL userinfo or matching DPAPI site login;
  - SIZE for total bytes, REST/ContentOffset for resume from the
    destination-local part file;
  - cancellation, progress, expected-hash verification, overwrite policy;
  - falls back to a zero restart once if a server rejects REST.
- Added a local `FtpServer` to TestServer (USER/PASS, SIZE, PASV/EPSV, REST, RETR)
  with `plain.bin` (65,536), `auth.bin` (131,072), and throttled `slow.bin`
  (524,288 at 128 KiB/s), plus byte/REST counters.
- Integration `FtpRequiresLoginAndResumesFromPartialBytes` (suite **43/43**):
  - anonymous request failed `unauthorized`;
  - after adding the FTP site login, `plain.bin` completed with source hash match;
  - `slow.bin` paused at 229,376/524,288, resumed with REST, completed 524,288
    bytes with SHA-256
    `ba2e57b4683ec0881913a6a0637e7c78e6215e263102a45e23bb345a2cb7a681`,
    and the server served less than 1.5× the file.
- Published CLI:
  - `plain.bin` SHA-256
    `d565bfa5c214b4851b4622b9cfa6bd5d1c2eed814d88cf8d9140ae4689002c0b`;
  - `slow.bin` paused at 229,376, resumed to 524,288 with matching hash;
  - `/ftp/stats`: `slowRetr=2, rest=1, slowBytesServed=294912`.
- Limitations: no FTPS, single-stream FTP, no proxy integration; FTP credentials
  come from site logins or URL userinfo.

## Post-download hook evidence (C4, 2026-11-04)

- Settings gained `PostDownloadCommand`. After a successful download, the Host
  runs the command with the final path as one `ProcessStartInfo.ArgumentList`
  entry and `UseShellExecute=false`; a 5-minute timeout kills the process and
  failures are logged without changing the completed job.
- Core `PostDownloadCommandLineTests` verify the command name, no shell, and the
  exact single argument.
- Integration suite **45/45**:
  - `PostDownloadHookRunsOnceWithExactFilePath` delivered
    `hook file (1).bin` unchanged;
  - `PostDownloadHookFailureDoesNotFailDownload` left the job completed after a
    throwing hook.
- Added `tools/HookSpy` (writes its received arguments). Published Host with a
  65,536-byte download named `hook live (2).bin` produced the exact path in the
  hook log and `Post-download hook ran` in the Host log; the CLI showed the
  configured command.
- Remaining: fixed file-path argument only; no custom argument template or
  scanner verdict handling.

## CLI integration evidence (C2, 2026-11-04)

- The published CLI drove the background host externally:
  - `dm add ... --wait` created/completed `cli-c2.bin` (65,536 bytes,
    SHA-256 `abca4b11e6670203f950742f7a62c0702583539630781014785ad32057243292`);
  - `dm list --json` showed `state: completed`, source `cli`, queue/conflict
    policy, ETag, and Last-Modified;
  - `dm host status` returned host/protocol/state/jobs plus tray and native-host
    health;
  - `dm queue list`, `dm schedule list`, and `dm site list` responded via IPC.
- The same CLI surface covers pause/resume/retry/cancel/remove, settings,
  grabber projects, FTP, and export/import (E9/E16/E31/E38/E41/E42).
- Limitation: our own command surface, not reference app.exe switch-compatible; no
  registered `dm:` URL protocol.

## Connection profile evidence (C5, 2026-11-04)

- Settings gained `ConnectionProfiles` (host pattern + max connections), with
  normalization (trim, 1–32, max 50). `AddJobAsync` takes the minimum of the
  requested connections, the global `MaxConnectionsPerHost`, and the
  most-specific matching profile (`*`/`?` wildcards).
- Integration `ConnectionProfilesCapRequestedConnections` (suite **46/46**):
  - `127.0.0.1` profile 2 reduced a requested 8 to 2;
  - `localhost` did not match and used the global 4;
  - `*.example.com` reduced to 1.
- Published CLI: `profile add 127.0.0.1 2`; `profile list` showed `2 127.0.0.1`;
  a Later job requested with `--connections 8` reported
  `requestedConnections: 2`; `profile remove 127.0.0.1` left an empty list.
- WinUI Options includes a `pattern=max` multi-line profile editor.
- Limitation: caps per job, not aggregate connections across simultaneous jobs.

## Settings transfer evidence (C6, 2026-11-04)

- CLI `settings export <file>` writes the full settings snapshot with proxy and
  site-login passwords cleared; `settings import <file>` applies it via
  SettingsUpdate.
- Proxy DTOs now expose `HasPassword` instead of returning the password over IPC;
  UpdateSettings preserves the existing password when address/username match.
  Site logins already preserve DPAPI passwords by id/site/user.
- Fixed a UI bug where the Options clone omitted proxy, site logins, queues,
  schedules, profiles, sleep/system flags, and ffmpeg path; saving Options could
  wipe them.
- Integration `SettingsUpdatePreservesUnreturnedSecrets` (suite **47/47**):
  a snapshot with no passwords kept both proxy and site-login `HasPassword`
  flags after an update.
- Published CLI:
  - export contained neither `site-secret-123` nor `proxy-secret-456`, but did
    contain `hasPassword`, the music folder, and `127.0.0.1`;
  - after mutating max-concurrent/profiles/music, import restored max 4,
    `127.0.0.1 = 3`, the music folder, site login `PASSWORD stored`, and proxy
    `password=***`.
- Limitations: secrets are not portable to another install; WinUI has no
  export/import buttons yet.

## Installer repair evidence (C7, 2026-11-04)

- Added `scripts/repair.ps1` (`-InstallRoot`, `-AppDir`, `-Source`,
  `-NoShortcut`, `-NoStart`):
  - restores app files from a source if any required executable is missing;
  - re-registers the Chrome native host with `-Force`;
  - restores extension files and the Start Menu shortcut;
  - writes/updates `INSTALL.txt` and optionally starts the Host.
- Test: deleted the HKCU native-host key and manifest, then ran repair against
  `artifacts\app` with `-NoShortcut -NoStart`.
  - key recreated and pointed at the manifest;
  - manifest `path` targeted `DownloadManager.NativeHost.exe` and included the
    extension origin;
  - `extension\manifest.json` and `INSTALL.txt` restored under the test install
    root;
  - starting the Host reported `tray: True native host: registered`.
- Limitation: verified against published artifacts, not a clean Windows install.

## Upgrade/uninstall evidence (C8, 2026-11-04)

- `install.ps1` and `uninstall.ps1` now accept `-InstallRoot` for isolated tests.
- Workspace test in `test-upgrade`:
  - install A created app, extension, `INSTALL.txt`, and native-host
    registration;
  - created `data\marker.txt` to represent durable state;
  - re-running install refreshed the app and preserved the data marker;
  - uninstall without flags removed app files, extension files, and the
    native-host key while keeping `data\marker.txt`;
  - `uninstall -RemoveData` removed the entire install root.
- Dev native-host registration was restored after the test.
- Limitation: verified with a workspace install root, not a clean Windows
  machine. Job-level preservation follows from the untouched data subtree.

## Update-check evidence (C9 foundation, 2026-11-04)

- `install.ps1` now writes `install.json` (version/channel/installRoot).
  `InstalledBuild` walks up to find it or uses
  `DOWNLOADMANAGER_INSTALL_ROOT`; no marker means portable/dev.
- Added a signed manifest format: version, channel, installer URL, size,
  SHA-256, RSA PKCS#1/SHA-256 signature; `UpdateManifestVerifier` checks the
  canonical fields.
- Runtime `update.check`/`update.download`:
  - refuses portable builds before any network call;
  - HTTPS only (loopback allowed for tests), channel filter, newer-version
    check;
  - downloads to `data\updates`, enforces signed size and SHA-256, deletes on
    mismatch.
- Core verifier test passes (**46/46**).
- Integration `UpdateCheckExcludesPortableAndVerifiesSignedDownload` (**48/48**):
  portable `notInstalled`; a signed 1.0.1 manifest against a 1.0.0 install
  returned `updateAvailable`; an 8,192-byte package downloaded with the expected
  SHA-256.
- Published CLI portable check printed `notInstalled`; `update download` exited
  1 with the portable-exclusion message. WinUI Help exposes check/download.
- Remaining: periodic/auto checks, tray notification, critical-download gating.
  C9 remains **partial** (P11.3/P11.7).

## Silent install/rollback evidence (C9 P11.5/P11.6, 2026-11-04)

- `scripts/apply-update.ps1`:
  - waits for the requested Host PID;
  - backs up `app` and `install.json`;
  - runs the package `install.ps1` with `-Source/-InstallRoot/-Version/-NoLaunch`;
  - verifies the installed version and Host executable, removes the backup, writes
    a JSON result, and starts the new Host;
  - on any failure restores both backups, writes `failed`, and restarts the old
    Host.
- `update.install` extracts the verified zip, launches the helper with the Host
  PID, returns `scheduled`, and raises a clean Host shutdown request. CLI
  `update install` and WinUI Install now/Later use it.
- Fixture test:
  - success: 1.0.0→2.0.0, `status=succeeded`, version 2.0.0, `VersionB` created;
  - failure: package wrote `Corrupt.txt` then exited 1; version stayed 1.0.0,
    marker A restored, `Corrupt.txt` absent, `status=failed`.
- Real Host test: installed 1.0.0 in a workspace root, started the Host, ran a
  2.0.0 package with `-HostPid`, shut the Host down; helper returned `succeeded`,
  `install.json` became 2.0.0, `VersionB` existed, and a started Host served
  status. The helper's Host-launch was changed to hidden/explicit exit after a
  lingering-process issue was observed.
- Remaining: clean-Windows end-to-end acceptance.

## Update notification/gating evidence (C9 P11.3/P11.7, 2026-11-04)

- Settings gained `AutoCheckUpdates`, `UpdateCheckIntervalHours` (1–168),
  `SkippedUpdateVersion`, and a Host update monitor timer.
- Host monitor:
  - checks on startup when auto-check is enabled and every configured interval;
  - notifies through the tray once per version unless that version was deferred;
  - exposes tray “Install update …” and “Check for updates…” actions;
  - blocks automatic install with active downloads and reports “Update postponed”.
- UI Options exposes manifest URL, PEM public key, channel, auto-check, and
  interval; Help → Check for updates shows installed/available; Later stores
  `skippedUpdateVersion`; Install now offers “Install anyway” when downloads are
  active.
- Live run against an installed 1.0.0 Host and signed local 1.0.1 manifest:
  - Host log: `Update 1.0.1 available.`;
  - UI: `Installed: 1.0.0 / Available: 1.0.1 / Size: 8192 bytes`;
  - clicking Later set `"skippedUpdateVersion": "1.0.1"`;
  - with a throttled 1-connection job at 327,680/1,048,576 bytes downloading,
    `dm update install` failed with `Downloads are active…`; the Host stayed up.
- Integration suite **48/48** includes busy/defer assertions. Manifest and
  installer redirects to non-HTTPS (non-loopback) are rejected.
- Remaining: clean-Windows end-to-end acceptance.

## Browser scope decision (C3, 2026-11-04)

- Chrome is the only supported browser. Edge (Chromium), Firefox, and others are
  explicit non-goals: no Edge native-messaging registration/package, and no
  Firefox WebExtension/native-messaging implementation was built or verified.
- The decision and reuse path are documented in
  `docs/browser-integration-scope.md`. `C3` is marked **blocked** (explicit
  scope), not implemented.

## WinUI scheduler (2026-11-04)

- Enabled the toolbar Scheduler button and Downloads → Scheduler.
- The dialog lists schedules with target, kind, start/stop, days and status;
  Enable/Disable and Remove act immediately; Add schedule supports a queue or
  grabber-project target, daily/one-time dates, start/stop, and days.
- Verified with UI Automation: added `UI Scheduler` daily 10:30 on the Main
  queue, the reopened dialog showed `UI Scheduler - enabled` / `daily 10:30
  [...] Main`, and `dm schedule list` showed the same schedule.
- Settings clone deep-copy (fixed in C6) keeps queues/schedules when saving
  Options.

## Recent-URL history evidence (M8, 2026-11-04)

- Settings gained `RecentUrls` (normalized to valid HTTP/HTTPS/FTP URLs, deduped,
  newest first, max 20). Every accepted add except grabber/import sources moves
  its URL to the front and persists settings.
- WinUI's Add URL field is an editable ComboBox populated from the history;
  CLI `settings get` prints the three newest URLs.
- Integration `RecentUrlsAreRecordedDeduplicatedAndPersisted`:
  - added history-a then history-b, re-added history-a;
  - observed `[history-a, history-b]`;
  - a fresh service loaded the same order. Integration suite **51/51**.
- Published CLI showed both URLs newest-first; expanding the Add URL combo listed
  `hist-two.bin` and `hist-one.bin`.
- Phase 6 is complete; M8's previous "no recent history" limitation is closed.

## T4/T3/T7/T8/T10 evidence (2026-11-04)

- T4: the published Host downloaded a 200 MB (209,715,200-byte) file with one
  connection at 16 MB/s. Across 117 samples at 100 ms:
  - working set 66,809,856 -> peak 80,457,728 bytes (delta 13.0 MiB);
  - private memory 19,447,808 -> 29,179,904 bytes (delta 9.3 MiB);
  - elapsed 12.92 s; SHA-256
    `99f8eb83d20ba51b3c79aeba226031f1bf79bdc9d281d4e494735b79f0a0bedc`
    matched `/large/expected`.
- T3: engine `ReportsHttpDnsTlsAndTimeoutFailureClasses` maps 404 -> `NotFound`
  (non-retryable), 500 -> `Http` (retryable), DNS -> `Dns`, TLS -> `Tls`, and a
  250 ms read timeout -> `Timeout`; every case fails without a final file.
- T7: engine `ProgressReportsAreMonotonicAndComplete` saw at least three
  reports, non-decreasing bytes, final 1,048,576 bytes, positive speed, and ETA
  values within 0-5 minutes.
- T8: integration `PausePreservesPartialAndCancelDeletesIt` paused at 65,536
  bytes with the part present/final absent, resumed past another 32,768 bytes,
  then deleted both on cancel.
- T10: engine `HtmlPageIsSavedOnlyForHtmlLikeTargets` saved `/html` as
  `legit.html`, while `error-page.bin` received the same `text/html` body and
  failed `invalidContent` with an actionable message and no final file.
- TestServer now has `/large/{name}` (streams 200 MB without allocating it) and
  `/large/expected` for the streaming hash.

## Host-instance and speed-limit evidence (L1/A7, 2026-11-04)

- L1: one published Host was running (PID 45304); a second `--background`
  launch exited while the count stayed at 1 and the original PID remained.
  `dm host status` still answered over IPC.
- A7:
  - engine `PerDownloadSpeedLimitIsApplied`: 512 KiB with a 128 KiB/s
    per-download limit completed in 4.0 s (bound 3.2-8.0 s) with the expected
    hash;
  - integration `GlobalSpeedLimitAppliesToNewJobs`: a 128 KiB/s global setting
    was inherited by a new job, which completed 512 KiB in 4.3 s with the
    expected hash.
- Limitations: limits are per job; aggregate fairness across simultaneous
  limited jobs is not enforced.
- Engine suite **26/26**, integration **57/57**.

## Disk-space, permission, and part-reconciliation evidence (R6/R7, 2026-11-04)

- R6:
  - `DiskFullPreflightFailsBeforeWriting` injected a 1,024-byte free-space
    provider with a 1 MiB reserve; a 65,536-byte job failed `DiskFull` with the
    destination in the message and no part/final file.
  - `PermissionFailureIsReportedWithoutSuccess` placed a directory at the part
    path; the engine failed `Permission` and wrote no final file.
- R7:
  - `CrashReconciliationTruncatesLongPartFile`: a 5,000-byte part with a
    3,000-byte checkpoint was truncated to 3,000 and reloaded as
    `ReceivedBytes=3000`.
  - `OrphanPartFilesAreReported`: an unattached `.part` file appeared in
    `HostStatusDto.OrphanPartFiles`; `dm host status` prints orphan paths.
  - Short and missing part cases from E66 still pass.
- Limitations: disk-full is provider-injected, not a physically full volume;
  HLS/DASH total size is unknown before download; orphans are reported, not
  deleted or quarantined.
- Engine suite **28/28**, integration **59/59**.

## Retry/reconciliation evidence (R5/R7/R8, 2026-11-04)

- R5: engine `RetriesTransientServerErrors` hit `/flaky/retry.bin` with two
  injected 500 responses; the third request completed 65,536 bytes with the
  expected SHA-256, and the server saw exactly 3 requests.
- R7: integration `CrashReconciliationClampsMetadataToPartFile` shortened a
  1,024-byte part while its checkpoint claimed 3,000 bytes; a fresh service
  reported `ReceivedBytes=1024`. Removing the part made a fresh service report
  `ReceivedBytes=0`.
- R8: existing `ServerIgnoringRangeRestartsFromZero`,
  `ChangedRemoteContentRestartsSafely`, and
  `AlwaysBadRangeFailsWithoutCorruption` cover safe zero restarts and the
  terminal `RangeUnsupported` case.
- Engine suite **25/25**, integration **56/56**.

## DASH SegmentBase/SegmentList range evidence (V4, 2026-11-04)

- Media segments now carry inclusive byte ranges. Range requests are issued with
  `Range`, don't send `If-Range` for sub-resources, accept `206`, and handle a
  server that ignores `Range` with a `200` skip/limit fallback.
- `SegmentBase` requires a `BaseURL`; it reads `Initialization@range` and parses
  the SIDX in `indexRange` (versions 0/1, `first_offset`, 31-bit media
  references). Hierarchical SIDX returns a clear error.
- `SegmentList` now accepts `Initialization@range` and
  `SegmentURL@mediaRange`; the previous `mediaRange` rejection is removed.
- TestServer `/dash-range/single.mp4` is 498,434 bytes: an 806-byte init,
  68-byte SIDX, and three media chunks. `/dash-range/segmentbase.mpd` points at
  the SIDX and `/dash-range/segmentlist-range.mpd` lists the media ranges.
- Integration `DashSegmentBaseAndSegmentListRangesMatchExpectedOutput`:
  - both completed at 498,366 bytes with the reference SHA-256;
  - at least 4 ranged requests were observed per manifest;
  - integration suite **54/54**.
- Published CLI reproduced both at 498,366 bytes with SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`.
- Phase 9 is complete; dynamic/live, multi-period, hierarchical SIDX, and DRM
  decryption remain excluded.

## DASH SegmentTimeline/SegmentList evidence (V4, 2026-11-04)

- `SegmentTimeline` now advances `startNumber` for each expanded segment, so
  `$Number$` templates work alongside `$Time$`; `S@t/@d/@r` repeats expand
  correctly.
- `SegmentList` support reads `Initialization@sourceURL` and full-URL
  `SegmentURL@media` entries; `mediaRange` is rejected explicitly for now.
- TestServer:
  - `/dash/timeline.mpd` uses one `S` with `r=2`;
  - `/dash/segmentlist.mpd` lists all three media URLs.
- Integration `DashTimelineAndSegmentListMatchExpectedOutput`:
  both completed at 498,366 bytes with the reference SHA-256;
  integration suite **53/53**.
- Published CLI reproduced both at 498,366 bytes with SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`.
- Remaining: SegmentBase and SegmentList mediaRange/byte ranges; dynamic/live and
  multi-period remain unsupported.

## HLS alternate-audio evidence (V11, 2026-11-04)

- HLS master parsing now understands `#EXT-X-MEDIA:TYPE=AUDIO` entries
  (GROUP-ID, NAME, DEFAULT, AUTOSELECT, URI) and the variant `AUDIO` attribute.
  Selection prefers `DEFAULT=YES`, then `AUTOSELECT=YES`, then the first
  rendition with a URI; in-band-only audio leaves the single-variant path.
- HLS media parsing was extracted into `PrepareHlsMediaAsync`; a reusable
  `DownloadMediaTrackToFileAsync` now feeds both DASH and HLS muxing with
  per-track `hls-video`/`hls-audio` checkpoints and signatures.
- TestServer `/hls/av-master.m3u8` lists `Commentary` first and `English`
  `DEFAULT=YES` second, pointing at different audio playlists. The video/audio
  tracks are fMP4 init+segments derived from the DASH A/V fixture.
- Integration `HlsAlternateAudioMuxesWhenFfmpegAvailable`:
  - downloaded the default English audio, not the first-listed commentary;
  - muxed to an MP4 matching `/hls/av-expected`;
  - integration suite **52/52**.
- Published CLI:
  - `hls-av.mp4` completed at 364,524 bytes;
  - SHA-256
    `03cfeddb0e614f8189f7a7502ed08731f66cdf204ab2ad1cf81cda07f7fc1ce5`
    matched the reference;
  - ffprobe reported H.264 320x180 + AAC, 4.0 s.
- Remaining media work: additional DASH modes. Alternate-language selection UI
  is not exposed.

## HLS AES-128 evidence (V10, 2026-11-04)

- HLS parsing now supports `#EXT-X-KEY:METHOD=AES-128` with an identity key
  format, relative/absolute key URIs, explicit `IV` attributes, sequence-derived
  IVs, `METHOD=NONE`, and per-segment key rotation. SAMPLE-AES and session keys
  remain `drmProtected`.
- Keys are fetched with the job's headers/auth and cached per key path; any key
  that is not 16 bytes is rejected. Segments are CBC-decrypted with PKCS7
  padding before being appended to the output. Checkpoint signatures include the
  key bytes and IVs.
- TestServer fixtures:
  - `/hls/aes.m3u8` — one key, sequence-derived IVs;
  - `/hls/aes-rotate.m3u8` — key1 for seg0, key2 for seg1/seg2.
- Integration `HlsAes128AndRotatingKeysDecryptToExpectedOutput`:
  both playlists completed at 580,732 bytes with the clear seg0-2 SHA-256;
  integration suite **50/50**.
- Published CLI: `aes-live.ts` and `aes-rotate-live.ts` both completed at
  580,732 bytes with SHA-256
  `6a1384036fceb6355c2aae29504a8fa5fd899c642fd47d309685607c0031b946`,
  matching the concatenated clear segments.
- Remaining media work: additional DASH modes and HLS alternate audio; live
  recording stays out of scope and SAMPLE-AES stays DRM-detected.

## UI parity audit (M1/M2/M4/M5/M14, U3/U6, 2026-11-04)

- M14: added `job.segments` IPC and a Progress details dialog (state, bytes,
  percent, connections, speed, ETA, part path, overall + per-segment bars).
  Integration `SegmentDetailsMatchTheEngineCheckpoint` validated 2+ segments
  against an active throttled transfer. Live WinUI showed 3,145,728/33,554,432
  bytes (9.4%), 1/1 connections, 65,525 B/s, and `1 segment from the engine
  checkpoint` with 3 progress bars.
- M1: live counts for seven jobs — All 7, Active 2, Queued 0, Failed 1,
  Finished 4, Unfinished 3; the Queues manager opened. The sidebar now exposes
  Active/Queued/Failed and a working Queues entry.
- M2: Extended multiselect showed `(2 selected)`; bulk Stop paused both
  bulk1.bin/bulk2.bin. Sort now uses numeric size, bytes/sec and updated time
  instead of formatted strings.
- M5: delete dialog has `Also delete the downloaded file from disk` checked by
  default. Unchecking removed keep.txt from the list while keeping the file;
  checking removed remove.txt from both the list and disk.
- M4: shared `ShellActions` (unit-tested) and `dm open`/`dm reveal`; live
  `reveal` opened `test-downloads - File Explorer`, and `open` opened
  open2.txt in Notepad.
- U3: `ui-state.json` persists window size, category, sort key/direction,
  toolbar scale and hidden categories. A seeded 1024x640/`sortKey=size`/
  `toolbarLarge=false` state restored a 1024x640 window, 34x32 Add URL button,
  and size-a (1 MB) before size-b (64 KB).
- U6: `AppSettingsDefaultsTests` pin the observed baseline (8 connections,
  startup/clipboard off, notify/sound/dialog on, auto-resume, stable/24h).
- U7: toolbar buttons now expose UIA names; the dark-theme layout was captured.
- Audit limits: synthetic keyboard injection was blocked by Windows keeping
  another process foreground, so U4 stays partial; the tray icon was hidden
  from the overflow UIA tree, so U5 stays partial; column resize/reorder and
  localization remain out of scope.
- Core suite is now **50/50** (added ShellActions, defaults, and manifest tests).

## Tests

- Engine: **28/28 passed** (`tests\DownloadManager.Engine.Tests`), including
  segmented transfer/resume, authentication, HTTP/SOCKS5 proxy auth, wrong
  credentials, redirect credential boundaries, 404/500/DNS/TLS/timeout classes,
  HTML-page policy, monotonic progress/ETA, transient-500 retry recovery,
  per-download speed limiting, disk-full preflight, and permission failure.
- Core: **50/50 passed**, including shutdown-command construction, clipboard URL
  extraction, batch URL expansion, file categories, download-list round-trip,
  safe post-download argument construction, update-manifest signatures,
  shell-open/reveal command construction, and settings defaults.
- Runtime/persistence: **59/59 passed** (`tests\DownloadManager.Integration.Tests`),
  including queues, scheduler, quotas, completion actions, Download Later, DST,
  sleep prevention, destination-local payload, authenticated handoff, runtime
  HTTP/SOCKS5 proxy settings, expired-URL refresh/resume, DPAPI site logins,
  HLS master/media, HLS AES-128 key rotation, HLS alternate-audio muxing, DASH
  SegmentTemplate/Timeline/SegmentList/SegmentBase byte ranges, DASH A/V muxing,
  HLS/DASH fragment recovery, rotating-token refresh, live-stream exclusion, DRM
  detection, editable properties, duplicate policies, category destinations,
  recent-URL history, pause-vs-cancel partial files, short/long/missing part
  reconciliation, orphan-part reporting, global speed limiting, the scoped site
  grabber/offline mirror, persistent/scheduled grabber projects, FTP auth/resume,
  post-download hook isolation, connection profiles, secret-preserving settings
  updates, installer-only update check/download, and segment/progress details.
- Extension: `npx tsc --noEmit` passes; `npm run build` succeeds (including the
  `batch.js` Download All window and the media panel/quality UI).
- Solution: `dotnet build DownloadManager.slnx -c Release` succeeds, including
  the WinUI x64 build.

## Environment limitations observed

- This harness denies child-process writes to `C:\Users\kaank\Downloads` and
  default `%LOCALAPPDATA%\DownloadManager`, so `StoragePaths.CreateDefault()`
  falls back to `artifacts\app\data`. Browser tests used the workspace
  `test-downloads` folder. On a normal user account, LocalAppData is the intended
  data root.
- The same restriction denied the Host's HKCU Run-key update; `L4` is marked
  **blocked** in `reference notes` until tested from a normal shell/installed build.
- Native host registration works from PowerShell; the development registration
  currently points at `artifacts\app`, not an installed copy.
- Chrome stable requires manual **Load unpacked**. The extension is at
  `C:\Users\kaank\Downloads\reference app\extensions\chrome\dist`.

## Next actions (dependency order)

1. Commit the G1 Download All increment (collection, filter/selection UI, batch
   handoff, evidence).
2. Phase 9 remainder: additional DASH modes, HLS alternate audio, and AES-128.
   Direct media detection, in-page/quality panel, unencrypted HLS, static DASH
   SegmentTemplate, DASH A/V muxing, fragment recovery, rotating-token refresh,
   live exclusion, and DRM detection are done (E24–E32).
3. Phase 10 is complete (G1–G4; E31/E40/E41). Remaining grabber polish: preview,
   WinUI schedule editor, JS-rendered pages, robots.txt, crawler auth/proxy.
4. Phase 6 remainder: recent-URL history and remaining UI polish. Clipboard
   monitoring (E33), batch URLs (E34), editable descriptions (E35), duplicate
   policies (E36), category/last-folder rules (E37), export/import (E38), and
   the completion dialog (E39) are done.
5. Phase 7 remainder: SOCKS4/4a, a proxy settings UI, and DPAPI protection for
   proxy passwords. Cookie/referrer/UA, HTTP/HTTPS proxy, SOCKS5, DPAPI site
   logins, redirect credential boundaries, and expired-URL refresh are done
   (E17/E18/E19/E20/E21).
6. Phase 11/12: remaining compatibility rows, clean-environment
   install/upgrade/uninstall verification, visual/behavioral audit, final parity
   evidence. Continue the reference app GUI inventory for U1/U2/U4 (see
   `notes/ui-inventory.md`). FTP auth/resume is done (E42).
7. Installer-only update check and auto-update (ROADMAP P11.1–P11.7, PARITY C9);
   portable and development builds must be excluded.
8. Side quest (after parity): TorBox/account-provider integration with the
   account manager, unrar extraction, a Linux/Docker headless host, and a web UI.
   See ROADMAP.md A1–A5/D1–D4. Provider tokens must be supplied via environment
   variables and never committed.

## Commands

```powershell
# Build everything (solution includes WinUI x64)
dotnet build DownloadManager.slnx -c Release

# Tests
powershell -ExecutionPolicy Bypass -File scripts\test.ps1

# Extension
cd extensions\chrome
npm ci
npm run build
# chrome://extensions -> Developer mode -> Load unpacked -> dist

# Publish/package/install (development)
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
powershell -ExecutionPolicy Bypass -File scripts\package.ps1

# Local fault-injection server
tools\TestServer\bin\Debug\net10.0\TestServer.exe --port 6411
```

## Session 1 archive (2026-11-04)

Baseline: pinned reference app 6.43.12.3; verified .NET 10.0.403, Node 26.11.1, Chrome
156, Windows App Runtime 1.8/2.x, GitHub authentication, and disassembler 12.1.4
headless MCP (`http://127.0.0.1:8081/mcp`, project `reference_project`; loaded
`/reference app.exe`, SHA-256 matches the pinned executable, 18,988 functions).
Created the private repo, initial parity inventory, initial architecture, and
the first engine/host/IPC implementation.
