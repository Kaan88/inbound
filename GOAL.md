# Goal — Build a private reference app-compatible download manager

> **Tracking fallback note (2026-11-04).** This OpenCode 2.2.5 environment has no
> Goal or Todo tool (verified through the tool catalog, the OpenCode V2 docs, and
> the local API OpenAPI document). Per the task instructions, `GOAL.md`,
> `ROADMAP.md`, `reference notes`, and `PROGRESS.md` are the authoritative tracking
> records. They are maintained as if they were the Goal/Todo features. Do not
> duplicate them into another system.

## Objective

Build a private-use Windows download manager with verified functional parity
against the installed another download manager. The application must:

- run as a per-user background host with a notification-area presence;
- keep downloading when the main window is closed or the frontend exits;
- accept downloads from Google Chrome through our own Manifest V3 extension and
  a per-user Native Messaging host;
- provide a replaceable WinUI 3 frontend over a versioned local IPC contract;
- survive restarts, interruptions, changed remote content, and browser
  disconnects without corrupting partial data;
- be reproducible, installable, upgradable, and cleanly uninstallable;
- document every parity claim with evidence, and label every gap.

## Pinned reference

| Item | Value |
| --- | --- |
| Product | another download manager (reference app) |
| Version | **6.43.12.3** (`v6.43b12 Full`) |
| Vendor | the vendor Inc. / the vendor FZE |
| Architecture | 32-bit x86 PE |
| `reference app.exe` SHA-256 | `B601350AF954D23DFD989295F9FB398AFCD972FEEC4F300A7CF1284CBB2B2C93` |
| `reference app.exe` MD5 | `c96a570117b034cfe83f90f3214ebc90` |
| `IDMMsgHost.exe` SHA-256 | `DBA25A49ADB88F675DB26D2DC7A0FA9D4A5DB2326858CB9D2515F6F34B8E0B65` |
| `IDMGCExt.crx` SHA-256 | `1D2297CCBDBBF06E9E2546BB9F9B9985433EB7C02D75F09BBCBC9784ECE583F7` |
| Chrome extension | reference app Integration Module 6.43.1, ID `ngpampappnmepgilojfohadhhmbhlaek` |
| Native host | `com.the vendor.reference app` → `IDMMsgHost.exe` |
| Registry version marker | `HKCU\Software\DownloadManager` → `idmvers = v6.43b12 Full` |

Details and evidence: `notes/reference-version.md`.

## Architecture constraints

- **DownloadManager.Core** — domain models, state machine, policies, interfaces.
  No WinUI/Windows App SDK references.
- **DownloadManager.Engine** — HTTP transfers, segmentation, resume, retries,
  throttling, integrity. No presentation references.
- **DownloadManager.Persistence** — durable jobs, checkpoints, settings, history,
  migrations. Atomic file replacement; no WinUI references.
- **DownloadManager.Contracts** — versioned IPC commands/events/DTOs.
- **DownloadManager.Host** — single per-user background process; authoritative
  state; scheduler; named-pipe server; owns the tray. One process for engine +
  tray + IPC. Documented in `docs/architecture.md`.
- **DownloadManager.Tray** — tray lifecycle/menu library used by Host.
- **DownloadManager.Desktop.WinUI** — replaceable frontend; talks only over IPC.
- **DownloadManager.NativeHost** — Chrome Native Messaging bridge; stdout is
  reserved for length-prefixed JSON.
- **DownloadManager.Cli** — headless control/diagnostics over IPC.
- **extensions/chrome** — our Manifest V3 TypeScript extension, own ID/name.
- Tests and a controlled local HTTP server.

Core, Engine, Persistence, and Contracts must not reference WinUI, Windows App
SDK, or presentation types.

## Milestones

| # | Milestone | Status | Exit gate |
| --- | --- | --- | --- |
| 0 | Baseline, reference inventory, repo, browser strategy | done | Buildable solution, initial parity inventory, concrete handoff strategy |
| 1 | Correct single-stream engine + CLI | done | Controlled downloads hash correctly; failures never look successful |
| 2 | Persistence, resume, crash recovery | in progress | Kill/restart preserves safe resume and final hashes (process-kill matrix still pending) |
| 3 | Tray + minimal WinUI frontend | done | Downloads continue with window closed; reopen restores state |
| 4 | Chrome integration, first daily version | in progress | One Chrome request → one job while host is in tray; authenticated cookie/referrer/UA handoff verified (failure-path recovery still open) |
| 5 | Parallel segmented transfers + limits | in progress | Correct through pause/crash/retry; measured benchmarks and independent verification pending |
| 6 | Everyday download management | pending | Practical daily use without CLI |
| 7 | Authentication + refresh address | in progress | Cookie/referrer/UA, authenticated HTTP/HTTPS proxy, SOCKS5 auth, DPAPI site logins, redirect credential boundary, and expired-URL refresh with partial resume verified; SOCKS4/4a and proxy-password hardening remain |
| 8 | Queues + scheduler | done | Named queues, caps, start/stop, reassignment, schedules, quotas, completion actions, Download Later, sleep prevention, and DST behavior verified (E9/E10/E13/E16/E22/E23) |
| 9 | Media detection/downloading | in progress | Direct media detection and popup list verified (E24); HLS/DASH assembly and A/V muxing verified (E25–E27); fragment checkpoint recovery verified (E28); live/DRM and additional DASH/HLS modes remain |
| 10 | Download All + site grabber | pending | Controlled offline mirror within crawl boundaries |
| 11 | Remaining compatibility/integration | pending | Every inventory row implemented or explicitly scoped |
| 12 | Final visual/behavioral parity audit | pending | Reproducible parity evidence; gaps labelled |

## Major decisions

1. **Process boundaries:** one background Host process owns the engine,
   persistence, schedule, tray, and IPC. Tray is a library, not a second
   process. UI, CLI, and NativeHost are separate clients. Rationale: simpler
   single-writer persistence and one lifecycle authority; still satisfies
   frontend replaceability.
2. **IPC:** per-user named pipes with a current-user-only ACL; JSON envelopes
   carrying `protocolVersion`; snapshot + event stream; reconnect by snapshot.
3. **Browser protocol:** our own JSON protocol over Native Messaging. No binary
   compatibility with `com.the vendor.reference app` is required or attempted.
4. **Persistence:** per-job JSON documents with atomic temp-file replace plus a
   compact index, avoiding a native SQLite dependency for the first milestone.
   Migration layer included.
5. **Own extension identity:** our extension is named **Download Manager
   Bridge**, uses its own manifest key/ID, and registers only our native host.
6. **No licensing bypass:** reference app activation/licensing code is out of scope. Only
   functional behavior is studied.
7. **Reference binaries stay out of git.** Hashes, addresses, and analysis notes
   are committed; executables and captures are not.
8. **Destination-local payload.** Unlike reference app's fixed temp directory plus
   post-download stitching, every job's single random-access part file lives in
   the chosen destination directory. Segments are written at their final offsets
   into that file and completion is a same-volume atomic rename. The state drive
   is never used for payload bytes, so choosing another drive actually uses that
   drive's free space and never requires a cross-volume copy.

## Completion criteria

The goal is complete only when every in-scope row in `reference notes` has passing
acceptance evidence, interrupted transfers and browser handoff are reliable,
installation/upgrade/uninstall are reproducible, every deviation is documented,
and Goal/Todo-equivalent tracking, parity records, progress notes, and the
repository agree. Until then the goal remains active.
