# TorBox Integration: implementation plan

**Goal name:** TorBox Integration
**Branch:** `feature/torbox-integration`. Commit here only.
**Full requirements:** the owner's brief, `C:\Users\kaank\Downloads\torbox.md`, with a copy at `notes-local/torbox-brief.md`. The brief decides any conflict with this plan.
**Done means:** scenarios A to J (end of this file) pass with recorded evidence, the regression suites pass, and tracking files are current.

## Rules for this work

- Do not push, merge, release or publish. The owner reviews the branch.
- Do not stop to ask. Record anything that needs the owner under "Open for the owner" in `PROGRESS.md`, take the safer option, and continue.
- Stop only if a task is impossible without the owner. Say which task and why.
- Do not change unrelated finished features to make this easier.
- No other providers, no direct hoster downloading, no BitTorrent engine, no plugin loader.

## Secrets

The owner's test file is `C:\Users\kaank\Desktop\credentials.keep.txt`. It holds an API key, CDN links, a magnet, a torrent link and MEGA links. Treat all of it as secret.

- Never print its values in output, logs, commits, fixtures, screenshots or docs. This includes subagent prompts: give agents the path to a fixture, or an environment variable name, never the value.
- **The sample CDN links carry the account API key in their `token` parameter**, and `requestdl` takes the key as a `token` query parameter. So a direct link is itself a credential. It must never be written to job JSON, logs, IPC snapshots, list export, or a "copy address" action.
- MEGA links hold their decryption key after `#`. Treat original inputs as secret too.
- JDownloader's logs and `cfg` folder may hold the key. Do not read them unless needed, and redact if you do.
- Live tests take the key from `TORBOX_API_KEY` only. They carry their own xUnit trait and are off by default. Normal tests must pass without the file.

## Live test budget

Allowed without asking:
- Read-only calls: `user/me`, `mylist`, `hosters`, `checkcached`, `torrentinfo`.
- `requestdl` for any file already on the account, whenever a link is needed or has expired. The owner said to choose whichever file is needed. Prefer small files, to save time and traffic.
- Submitting the inputs in the owner's file, at most once each, after checking `mylist` for an existing item. The owner supplied these for this purpose and hopes they are uncached.
- Other magnets only if a scenario needs one and only for freely licensed content such as a Linux distribution image.

Not allowed:
- Any remote delete or cancel on items you did not create.
- Retrying a submission that may have succeeded, without reconciling first.
- Continuing after a quota error. Record it and use mocks.

Do not assume any supplied link works. If the CDN links have expired, get new ones through the API. The file has no Copy JDownloader Folder Link: for scenario A, build one from a real `mylist` id, which is the same URL shape.

## What is already known

**Evidence labels:** [API] read from `https://api.torbox.app/openapi.json` on 2026-11-06. The docs site renders by script, so use this schema. [JD] strings read with `javap` from the installed plugins, not yet run. [Support] TorBox support articles. [Inferred] needs testing.

- [API] Base `https://api.torbox.app/v1/api`, bearer auth, `requestdl` also requires `token` in the query.
- [API] Three families share a shape: `torrents`, `webdl`, `usenet`, each with create, async create, control, `requestdl`, `requestdl/batch`, `mylist`, `checkcached`. `mylist` takes `id`, `bypass_cache`, `offset`, `limit`. `checkcached` takes `hash`, `format`, `list_files`. Also `webdl/hosters`, `torrents/torrentinfo`, `user/me`, `user/stats`.
- [API] Response bodies and status values are not yet confirmed. Read the schema and a live `mylist`. Do not invent fields.
- [JD] Plugins at `%LOCALAPPDATA%\JDownloader 2\jd\plugins\decrypter\TorboxAppCrawler.class` and `...\hoster\TorboxApp.class`. `javap` is at `C:\Users\kaank\tools\jdk\jdk-21.0.13+9\bin\javap.exe`. See Phase 0 for the full research.
- [JD] Folder link pattern: `/download?id=<digits>&type=(torrents|web_downloads|usenet_downloads)...`. It is resolved through the user's own `mylist` for that type, so it is **an account-scoped reference, not a public share**. Its `name` is not used for identity.
- [JD] File data read from `files[]`: `id`, `name`, `short_name`, `size`, `md5`, plus an infected flag. JD's stable identity is type, download id and file id or path. It requests a fresh link per download.
- [JD, Inferred] It spaces web download creation by about 65 s, uses `bypass_cache=true` while waiting, and reads `cached`, `download_finished`, `download_state`, `download_present`, `progress`. Confirm in bytecode. It also stores a download password for retries; find out what that password is for.
- [Support] Links last 3 hours, use at most 4 connections per file, and do not share one link across apps or devices.
- [Inferred] What `hash` means for `webdl/checkcached`. Test it with a known cached item.

**Existing code to reuse, not rebuild:**
- Packages: `Runtime/DownloadGroups.cs`, `Core/MultipartArchiveName.cs`, `Desktop.WinUI/PackageWindow.cs`, expandable rows, group IPC. The ROADMAP's "Planned" note is out of date, so Phase 1 is a gap check.
- Secrets: `Persistence/SecretProtector.cs` (DPAPI).
- Link refresh: `RefreshUrlAsync` (`DownloadManagerService.cs`), which drops logins when the site changes.
- Connection caps: per-job limit calculation near `DownloadManagerService.cs:3401`.
- After-download seam: `IPostDownloadHook`, `MaybeRunPostDownloadHookAsync`.
- Add Links dialog with review list, and `dotnet tools/TestServer` for fault injection.
- `C:\Program Files\WinRAR\UnRAR.exe` is installed. 7-Zip is not. JDK 21 is available.

## Architecture

The Host stays the only owner of provider state, secrets and scheduling. The engine stays provider-free.

| Part | Where | Job |
|---|---|---|
| Provider contracts | `Core/Providers/` | Small capability interfaces (`IRemoteAccount`, `IRemoteCatalog`, `ICacheChecker`, `IRemoteSubmitter`, `ILinkResolver`) plus a capability declaration. A provider implements only what it supports. Replaces the old `IFileStorageProvider` idea. |
| TorBox | new `DownloadManager.Providers.TorBox` | Typed client, response models, state mapping, rate limits, error classes. |
| Input resolvers | `Runtime/Remote/` | Classify a pasted string or file: folder link, magnet, `.torrent`, hoster URL, container, unsupported. |
| Remote job coordinator | `Runtime/Remote/` | Durable remote jobs, submission reconcile, polling, starts local jobs when ready. |
| Link resolution | small seam the engine calls before each attempt | Returns a fresh URL held in memory only. |
| Extraction | `Core/Archives/IArchiveExtractor`, `Runtime/Archives/` | Works on any local files, knows nothing about providers. |
| UI | WinUI | Reads provider capabilities. "TorBox" appears only in the account and provider display name. |

**Identity.** These are separate records, linked by id: remote item, local package, archive set, extraction job.

**Local jobs.** A provider-backed job stores a stable reference in `Url` (for example `remote://torbox/torrents/<id>/<fileId>`), plus expected size, md5 and relative path. The real link is never persisted.

**Durable remote job.** Stored atomically in its own file through the existing store conventions. The original input is protected with `SecretProtector`.
- States: `Intended → Submitting → Submitted | SubmissionUnknown → Waiting → Processing → Ready → LocalStarted`, plus `Failed`, `Blocked`.
- Write the intent before the POST. After a timeout or crash, reconcile against `mylist` by hash, and resubmit only if it is confirmed absent.
- Map real API statuses to these states in one table with a test per status. An unknown status stays visible as itself and never counts as ready.
- A local child is created under a deterministic key (remote job + file id), so retries cannot duplicate it.

**Polling.** One background loop per account, on the Host. Bounded backoff with jitter, honours `Retry-After`, slows when idle, stops at a terminal state, resumes after restart, and offers **Check Now**. A failed status check never leads to a resubmit.

**Failure classes.** Classify CDN and API failures separately: expired link, throttled, auth, quota or plan, remote failed, item gone, network. Do not treat every 403 or 429 as expiry: on a 401, 403, 404 or 410 from the CDN, ask the API for a new link, then decide from the item state. On a new link, compare size, md5 and validators. Resume only if identity and range behaviour are safe, otherwise restart that child from zero. Never append mismatched bytes.

**Connections.** Cap each TorBox file at 4, from a provider policy value. Ordinary downloads keep their settings.

**Pausing or cancelling locally never touches the remote item.** Remote delete is a separate action with its own confirmation.

## Design decisions (after research, 2026-11-06)

These are binding. Research notes are in `notes-local/` (`torbox-code-audit.md`, `torbox-research-jd-plugins.md`, `torbox-research-api.md`, `torbox-research-containers-extraction.md`).

**TorBox facts that shape the code.** Ready means `download_present` and (`cached` or `download_finished`), with a file list, and is checked first. Otherwise, failed means a non-empty `error` or a state starting `failed`. Web hash = md5 of the exact original URL; torrent hash = btih. The CDN sends no ETag, and `md5` is usually null, so identity is size, then Last-Modified, then md5 when present. Space web download creation about 65 s per account, reserved before the POST. Poll at `eta` clamped to 15 to 60 s, otherwise 30 s. Before calling an item gone, check `mylist`, then `queued/getqueued` by hash, then allow 1 minute of grace. `Retry-After` counts only on a 429.

**Local jobs.**
- `DownloadJob.Url` = `remote://torbox/<torrents|webdl|usenet>/<itemId>/<fileId>`. It is the durable identity and holds nothing secret.
- New persisted fields: `RemoteSource` (provider, kind, item id, file id, relative path, remote job id), `ExpectedTotalBytes`, `ExpectedMd5`.
- New memory-only field: `[JsonIgnore] ResolvedUrl`. `Clone` copies it, serialization and `JobDto` never see it. `FinalUrl` is never set for these jobs.
- The engine stays provider-free: it validates and requests the effective URL (`ResolvedUrl ?? FinalUrl ?? Url`), and uses the effective host for its limiter. `originalOrigin` stays on `Url`, so cookies, logins and referrers never reach the CDN.
- Public `job.add` keeps rejecting `remote://`. Provider jobs enter through an in-process method, with a provider connection cap added to the `Math.Min` at creation and re-clamped at each run.
- Dedupe: owner `provider:torbox`, request id `<remote job id>/<file id>`.
- Link resolution happens in `RunJobAsync`, once per run, through an optional `IRemoteLinkBroker` on the service. Re-run with a fresh link when the engine reports `ExpiredUrl`, `Unauthorized`, `Forbidden` or `NotFound`, at most twice per run. Temporary API failures retry with backoff inside the run (cancellable), then fail as resumable. The broker checks the item is still there and the file's size and md5 still match. If not, it deletes the part and checkpoint and restarts from zero, or fails with the real reason.

**Pasted CDN links** (`*.tb-cdn.*` or any URL whose query holds the account key): `Url` becomes a display form with the token masked, the real address goes into a new DPAPI-protected persisted field `ProtectedUrl`, loaded into `ResolvedUrl` at run start. Capped at 4 connections. Never added to `RecentUrls`. Applied in `AddJobCoreAsync`, so it covers every entry path.

**Log safety.** Add `UrlRedactor` in Core. Use it in clipboard logs, tray text, and exception logs. API errors are sanitized before reaching `ex.Message` paths.

**Remote jobs.** `RemoteJobCoordinator` in `Runtime/Remote`, with its own gate and store (`remote/jobs/<id>.json`, not in `AppSettings`). It calls the service in-process and never calls the network under a gate.
- States: `Intended`, `Submitting`, `SubmissionUnknown`, `Waiting`, `AwaitingSelection`, `LocalStarted`, `Failed`, `Blocked`, `Removed`. A restart turns `Submitting` into `SubmissionUnknown`.
- Record: id, provider, kind, protected input (magnet or link), protected `.torrent` bytes, protected link password, predicted hash, item id, name, display host (no path or key), stage, raw state, progress, speed, eta, failure kind and message, next check, consecutive failures, destination, queue, start mode, file selection (`All`, a list of ids, or ask when ready), approved-uncached flag, package id.
- Order: write intent, then reconcile by hash against `mylist` (fresh, paged, bounded) and queued, then POST. On a lost answer, reconcile. Resubmit only when confirmed absent after grace. A failed status check never leads to a resubmit.
- `Blocked` (`AccountBusy`) retries by itself with backoff. `Failed` (`Rejected`, `AccountLimit`, not found) is terminal until the user retries. `LinkPasswordRequired` is a failed state that asks for the password.
- Polling: one loop, jittered backoff up to 15 minutes on failures, **Check Now** wakes it.
- Ready: create local children idempotently in a package with key `remote:<id>`, under validated relative paths (`RemotePaths`, then a final check that the full path stays under the destination). Infected files are skipped with a note. Empty files are skipped with a note. No automatic progress windows for `Source = "remote"`.
- Pausing, cancelling or removing locally never touches the remote item.

**Input classification** (Host): folder link, magnet, `.torrent`, hoster link (provider regex with a 100 ms match timeout, hoster list cached for 1 hour, real availability shown), provider direct link, ordinary link, unsupported with a reason. With no key set, hoster links stay ordinary links; there is no fallback once TorBox has refused one. With a key and no hoster list to go by, a link is `Undetermined`, never ordinary. A hoster that lists domains is only matched on those domains, and they alone decide if its pattern can't be used. `job.add` stays an explicit direct path, and `dm add` refuses a hoster link.

**Packages.** Parent Resume does not revive a file cancelled by itself. It undoes a parent Cancel only when that Cancel stopped every file (`PackageRules.ResumeTargets`). A package holds at most 1000 files; an add over that fails before any job exists, because a second part would give one provider item two sets of controls and two completions. `PackageCompleted` means every job in the package finished; it is saved before it is raised, so a subscriber that must not miss one sweeps `GetCompletedPackages` at startup and dedupes by package id and `CompletedAt`. `AddRemoteJobsAsync` completes once per batch. A package built by single `job.add` calls has no end marker, so it waits for 10 s of quiet and can still complete more than once; a subscriber that needs a whole set has to check for it.

**IPC.** Protocol version stays 1. Add new message types only: `remote.*` and `archive.*`. No DTO carries the key, a direct link, a magnet or a hoster link. The key is write-only, with `HasKey` as the only read. The native host gets an allowlist: `handoff.request`, `host.ping`, `host.openUi`, `host.pickFolder`, `jobs.query`, and does not forward `remote.*` or `archive.*` events to the browser. Slow calls use a separate connection from the frontend.

**API proxy.** The API client uses the same proxy settings as downloads.

## Phases

Work in this order. Each phase ends with a build, its tests, a commit and a `PROGRESS.md` entry.

**0. Research on the owner's installed JDownloader.** This is authorized by the owner and comes before any design is final. Inspect the real install at `%LOCALAPPDATA%\JDownloader 2` directly:
- `TorboxApp` and `TorboxAppCrawler` in full: every API call, its order, polling and waits, error handling, link refresh, connection limits, password handling.
- LinkGrabber routing, magnet and hoster routing, package and archive metadata, the container importers (DLC, CCF, RSDF), and archive extraction with passwords. These live in `Core.jar`, `JDownloader.jar` and `jd\plugins`.
- Tools: `javap` first, and a Java decompiler if it helps. Ghidra and its MCP are also authorized and may be used wherever they help, for native parts especially. Inspect the Ghidra MCP's configuration first and do not disturb its existing project; use a new project.
- Run JDownloader itself to compare behaviour against ours, with the owner's links. Leave its settings and account setup as they are unless a comparison needs a change, and restore them after.
- Label each finding as verified in JDownloader, verified in TorBox docs, inferred, or excluded. Do not copy JDownloader code or ship its binaries.

Then confirm the facts above. Record JD and reverse-engineering notes in `notes-local/torbox-research.md`, which stays out of the public repo. Public docs state interoperability facts only and do not describe other products' internals. Update the state table and the file fields. Save anonymised sample responses as test fixtures, with ids, names and links replaced.

**1. Package gap check.** Test the existing packages against brief section 10: aggregates, parent controls, mixed states, child retry, restart, queue and priority. Fix gaps only. Mark the ROADMAP note done or updated.

**2. Account and client.** Contracts, typed client, mock TorBox server for tests, key storage with DPAPI, Options page to set, validate and clear the key, account info. Update ROADMAP A1 and A2.

**3. Folder links, magnets, `.torrent`.** Folder link parser with all the brief's cases, file tree with safe relative paths (reject traversal, handle identical names in different folders), cache check, submit, pending metadata state, file selection dialog, local children in a package.

**4. Hoster links and monitoring.** Add Links routes supported hosters through TorBox only, with cache check and confirmation for uncached links. Durable remote jobs, polling, automatic local start, real errors, no direct fallback. Scenarios B, C, D, J.

**5. Reliable transfer.** Fresh links per attempt, expiry handling, 4-connection cap, safe resume or clean restart, sibling files kept. Scenario F. Also treat pasted `tb-cdn` links as sensitive: redact in logs, cap connections, keep them out of plain stores where possible.

**6. RAR extraction.** `IArchiveExtractor` with a process-based UnRAR backend, configurable path, argument list only (never a shell string).
- Volume grouping for `.partNN.rar` and `.rar/.rNN`. Wait until all volumes are present, and show which are missing.
- States: Downloaded, Waiting for extraction, Extracting, Extraction completed, or a specific error: password required, wrong password, missing volume, corrupted, unsupported version, tool unavailable, disk space, permission, unsafe path, cancelled.
- List entries first and refuse traversal, absolute paths and unsafe links. Extract to a staging folder, then move.
- Persist state so a crash cannot read as success. Keep archives on failure. Deleting archives is an opt-in setting, and only after verified success.
- Passwords: per package or archive, protected at rest, never logged. Try passing them without the command line first. If UnRAR needs `-p`, document it as a tool limitation. No guessing or password lists.
- Works on ordinary downloads too, with Extract in the context menu. Generate fixtures at test time with `Rar.exe` if present, and skip with a clear reason if not.

**7. Containers.** Import preview, classification, reasons for rejected links, routing through the same flow. Do RSDF and CCF first. Investigate DLC separately. Do not take any private key or application secret out of JDownloader, and do not call its service while impersonating it. If standalone DLC is not possible on those terms, ship a clearly labelled "not supported" message or an optional import through the owner's running JDownloader, and record it for the owner.

**8. WinUI.** My TorBox (Torrents, Web Downloads, Usenet) with refresh, files, selection, destination and Check Now. Add entry points: account, add link, add magnet, import torrent, import container, browse, extract. Remote stages shown as words, never as local progress. Light and dark themes, DPI, keyboard, accessible names. No controls that do nothing. Follow `docs/design-guidelines.md`. Check real screens with the sandbox profile (`DOWNLOADMANAGER_INSTANCE=uiqa`), never the installed app's state.

**9. Final check.** Full build, all suites, the interruption matrix, live scenarios within budget, an independent review of security and durability, tracking files updated. Search the branch for the key and for `token=` values before calling it done.

## When a scenario cannot be finished live

Do not loop on it, and do not call it passed. If quota, the account's plan, a dead link or a TorBox outage blocks a live run, prove it with mocks, mark it "not verified live" with the reason, and move on. If standalone DLC cannot be done within the rules in Phase 7, the labelled outcome described there counts as finishing that part.

## Tests

Deterministic tests use a mock TorBox API and `tools/TestServer`. Cover the case lists in brief section 15 and section 8. Live tests are opt-in. Passing builds are not evidence on their own. Say plainly what was not verified.

## Tracking

Update the existing entries, not a new system: ROADMAP A1 to A5 and the grouped-downloads note, `GOAL.md`, `PROGRESS.md`. `PARITY.md` is now local only. Keep `credentials*.txt` and `*.keep.txt` ignored.

## Open for the owner (recorded, no answer needed to proceed)

1. Merging, pushing the branch, and releasing the feature.
2. DLC: which route is acceptable if standalone import is not possible.
3. Whether the feature ships in the public build, given the store listing does not mention it.
4. Bundling an extractor is not planned. The owner has WinRAR, others would have to install one.
5. The owner's key has been in plain text in a file and in CDN links. Consider rotating it after testing.

## Acceptance scenarios

| | Scenario | Must show |
|---|---|---|
| A | Copy JDownloader Folder Link | Paste, select files, download with folder structure kept |
| B | Cached hoster link | Cache confirmed, downloads without any hoster protocol |
| C | Uncached hoster link | Submitted, remote state shown, polled, local start when ready |
| D | TorBox failure | Real cause shown, safe stop, no direct attempt |
| E | Magnet | Cache check, approved submit, wait, file choice, download |
| F | Expired link | Fresh link, validated, safe resume or restart, no corruption |
| G | Password RAR | Detected, asks, extracts safely, result shown |
| H | Multi-part RAR | Waits for all volumes, one set, no duplicated output |
| I | JD container | Links and packages shown, compatible ones sent through TorBox |
| J | Restart | Remote reference restored, monitoring resumes, no second submit |

Second-provider check: review that a new provider would need only an implementation, a capability declaration and a resolver.
