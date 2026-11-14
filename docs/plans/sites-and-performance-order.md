# Sites and performance: one order of work

Written 2026-11-14 at the owner's request ("make a combined sites, perf plan based on your optimized flow").
This file only sets the order. What each item is stays where it was:

- Sites: `remote-sites.md`, ROADMAP SITES.0 to SITES.9.
- Performance: `performance-areas.md` (Host, engine, saves, IPC) and `ui-performance-areas.md` (window, web UI),
  ROADMAP PERF.8 to PERF.20.

Nothing in either plan is built or measured. The order among the performance items is the same as in
`performance-areas.md`, "Order of work"; here the Sites phases are placed between them.

## What decided the order

1. **Measure before Sites lands.** After Sites is in, its cost cannot be told apart from what was there before.
2. **Lay down what Sites would otherwise copy.** Sites adds a set of message types, new views in the window and
   the web UI, progress events and a new store. Four performance items change the pattern each of those follows.
   Done first, Sites is written the new way once; done after, Sites is written the old way and then reworked.
3. **Change the protocol once.** The progress-message change (PERF.13) is decided after Sites' transfer events
   exist, so one design covers downloads and site transfers, and before the web UI's Sites screen is built on them.
4. **Try Native AOT on the Linux window when its screens exist.** Trimming faults only show when a screen is
   opened, so the trial is worth more after the Sites screens are there.
5. **Durability changes stand alone.** The save change (PERF.12) is kept away from the phase that adds the
   transfer store.
6. **Sites is the feature work and is not held up by anything optional.** Only stage A sits in front of it.

## The order

| Stage | Items | What it is | Why here |
| --- | --- | --- | --- |
| **A1** | SITES.0 | The rclone run: the ten checks in `remote-sites.md` | Needs only the owner's yes to download rclone. Independent of everything else in stage A, so it can run alongside. Its tenth check (rclone's start time, idle memory, size) is taken with the A2 numbers |
| **A2** | PERF.8, PERF.15 | Both baselines in one session: engine, saves, start times, the window on both toolkits, the web UI | The only stage that cannot be made up later |
| **A3** | PERF.11, the ReadyToRun part of PERF.18 | ReadyToRun for the Host, server, window and the AppImage | Publish-only; changes every later start-time number, so before more measuring |
| **A4** | PERF.10 | JSON source generation for the contracts, then Native AOT for the native host and the CLI | SITES.2 adds `SiteDtos.cs`. With the generated context in place, each new type is added to it as it is written |
| **A5** | PERF.16, first half of PERF.19 | Job events applied once per tick in the window (WinUI and Uno) and once per frame in the web UI; constant-time lookups | SITES.3 and SITES.4 add views and transfer events to the same windows. They then use the per-tick path from the start |
| **B1** | SITES.1 | rclone as a supervised child process | - |
| **B2** | SITES.2 | Site model, stores, secrets, the first `sites.*` commands, the switch, host-key trust | - |
| **B3** | SITES.3 | Browsing on the desktop, read-only | - |
| **B4** | SITES.4 | Transfers both ways, their store, the Transfers bar, speed share, scheduler | - |
| **B5** | SITES.5 | Remote changes, "On this PC", Two panes | End of the first usable release: SFTP behind the switch |
| **C1** | PERF.8 and PERF.15 again | The same measurements, with Sites off and with Sites on | Shows what Sites costs, and whether stage A did what was expected |
| **C2** | PERF.13, the serialise-once part of PERF.20 | Decide on the C1 numbers; if it goes ahead, one small progress message covering jobs and site transfers | After the transfer events exist (B4), before the web UI builds on them (D2) |
| **C3** | The AOT part of PERF.18 | Native AOT trial of the Linux window | Every screen, the Sites ones included, is tried once |
| **D1** | SITES.6 | Kinds beyond SFTP | - |
| **D2** | SITES.7, second half of PERF.19, the rest of PERF.20 | Sites in the web UI and on the phone, Docker with rclone; the windowed list; compressed and cached static files, snapshot size | All three are web UI and server work, and a windowed list is needed for large remote folders anyway |
| **D3** | SITES.8, SITES.9 | CLI `dm sites`; popup line, notices, docs | - |
| **E1** | PERF.17 | Compiled bindings in the main list's rows | Decided on the C1 numbers |
| **E2** | PERF.12 | One synced save per job every 5 s instead of two | Durability; on its own, with the kill and recovery tests |
| **E3** | The renderer and row parts of PERF.18 | Linux window rendering | After the D7.W Wayland comparison, if that trial is run |
| **E4** | PERF.14 | Rust comparison of the transfer loop | Only if the engine is still the limit |
| **Any time** | PERF.9 | Transfer loop: read timeout, pooled buffers, larger writes | Sites moves its bytes through rclone, not the HTTP engine, so there is no overlap. After A2 so it can be judged |

Within stage A, A1 to A5 do not depend on each other except that A2 comes before A3. If stage A must be cut
short to start Sites sooner, keep A2 and A4; A5 costs the most rework if left out and A3 the least.

## Rules for the Sites phases that come from the performance plans

These cost nothing extra if followed while Sites is written, and they are the reason stage A is in front.

| Phase | Rule | From |
| --- | --- | --- |
| SITES.2 on | Every new DTO and stored record is added to the generated JSON context. No anonymous objects as payloads | PERF.10 |
| SITES.2, SITES.4 | Site and transfer files are written compact, straight to UTF-8 bytes | PERF.12 |
| SITES.4 | A running batch is saved on a state change and at a bounded rate for progress, as one file. Do not copy the pattern of a job file and a checkpoint both rewritten every 5 s | PERF.12 |
| SITES.4 | `sites.transfer.changed` carries the changed fields of a batch, not the whole batch with all its items; `sites.activity` stays the small batched summary the plan already describes | PERF.13 |
| SITES.3, SITES.4 | Sites events reach the window through the per-tick path from A5. The sidebar activity and status chip are updated once per tick | PERF.16 |
| SITES.3 | New XAML (the Sites panel, the Transfers bar, their rows) uses compiled bindings from the start, and is checked to build under Uno | PERF.17 |
| SITES.3, SITES.7 | A folder listing is a virtualised list on the desktop and a windowed list in the web UI; `sites.browse` is already paged | PERF.19 |
| SITES.1 | With Sites off, or on and idle, there is no rclone process and no timer of ours running. Checked with the idle measurement from PERF.4 | PERF.4 |
| SITES.4 | rclone progress is polled only while a batch runs, at one fixed rate for all batches | PERF.4 |
| SITES.8 | `dm sites` stays within what the AOT-built CLI allows (no reflection JSON) | PERF.10 |

## Added to each Sites phase's "done when"

- SITES.1: idle shows no process (already in the plan) and no extra wake-ups in the Host.
- SITES.4: with 20 files transferring, the window's UI-thread CPU is within the A2 figure for 20 active downloads.
- SITES.5 (end of stage B): the C1 measurements are taken and written into PROGRESS.
- SITES.7: a folder of 10,000 entries opens and scrolls in the web UI and on the desktop.

## What this does not change

- The Sites design, its phases and its open questions for the owner.
- The rule that nothing is marked implemented without evidence.
- SITES.0 still waits for the owner's yes to download rclone.
