# Baseline A2, Linux (2026-11-14)

The numbers of stage A2 in `sites-and-performance-order.md` (ROADMAP PERF.8 and PERF.15), taken on Linux at the commit
tagged `perf-baseline-a2` (`ff949a4`): before the generated JSON (PERF.10) and before events were applied once per tick
(PERF.16, PERF.19). How they are taken: `tools/Bench/README.md` and `perf-baseline-how.md`. The raw CSV files are
under `artifacts/` in the worktree they were run in and are not in the repository.

Not here: anything on Windows (the WinUI window, the bench on NTFS), dropped frames while scrolling.

## Machine and noise

- Intel i7-14700KF (28 logical CPUs), 32 GB, CachyOS, .NET 10.0.14, btrfs, governor `powersave`, NVIDIA RTX 4070 Ti SUPER.
- **The machine was not idle.** Other work ran throughout (a cargo build, an Android emulator, a Gradle daemon). Load
  average, sampled every 30 s over the first run: median 3.8, 90th percentile 7.4, highest 12.6 (during the engine
  bench, which itself adds to it). Over the rerun: 2.3 to 6.0.
- How far that moves a figure: the window with 500 jobs, 8 downloads running and OpenGL was measured twice by accident
  (see "20 active" below) and gave 15.5 % and 30.3 % of a core. Treat window CPU figures as good to about a factor of
  two, and compare a later run only against a difference larger than that, or repeat both on a quiet machine.

## "20 active" is 16 running

The product runs at most 16 downloads at once (`MaxConcurrentDownloads` is clamped to 1..16), so of 20 added, 16 run and
4 wait. In the first run only 8 ran: all test downloads come from 127.0.0.1 and the per-host limit is 8. The seeding
script now raises that limit when more than 8 are asked for; the "20" rows below are from the rerun with 16 running.

## Engine throughput (PERF.8)

Against the bench's own static server in a second process; median of 3 runs; MB = 10^6 bytes. The disk runs took 14 to
17 s each (16 GiB file), under the tool's 30 s target, and the RAM runs under 1 s (4 GiB), so the RAM rows are rough.

| Destination | Segments | Host limit | MB/s | CPU s/GB | Allocated MB/GB | Peak working set MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| disk (btrfs) | 1 | 8 | 1157 | 0.59 | 1.4 | 112 |
| disk | 8 | 8 | 1266 | 1.45 | 1.5 | 117 |
| disk | 16 | 8 | 1176 | 1.33 | 1.6 | 119 |
| disk | 16 | 16 | 1118 | 1.90 | 1.9 | 117 |
| RAM (tmpfs) | 1 | 8 | 5063 | 0.39 | 1.4 | 112 |
| RAM | 8 | 8 | 6295 | 1.07 | 2.0 | 115 |
| RAM | 16 | 8 | 6920 | 0.95 | 2.3 | 116 |
| RAM | 16 | 16 | 5870 | 1.58 | 3.3 | 118 |

Raw read-and-discard from the same server: 6,295 MB/s on 1 connection, 24,130 on 8, 26,606 on 16 (curl, 1: 6,820).

What it says: to disk the engine is limited by the disk at about 1.2 GB/s whatever the segment count. To RAM one
connection reaches 80 % of the raw figure; with 8 or 16 the engine reaches about a quarter of what the server can send.
Allocation is small (under 3.5 MB per GB). More segments cost more CPU per GB (0.4 to 0.6 s at 1, 1 to 1.9 s at 8 to 16).

## Saves (PERF.8)

The real stores on btrfs, with write-through and fsync:

| Record | Bytes | Mean ms | p95 ms | Writes/s |
| --- | ---: | ---: | ---: | ---: |
| Job, plain | 1,710 | 5.55 | 7.28 | 180 |
| Job, from the browser | 2,901 | 0.95 | 1.52 | 1,054 |
| Checkpoint, 1 segment | 623 | 0.90 | 1.32 | 1,112 |
| Checkpoint, 8 segments | 1,467 | 0.84 | 1.29 | 1,197 |
| Checkpoint, 16 segments | 2,415 | 0.95 | 1.27 | 1,052 |

- The plain-job row was the first thing measured on disk and is five times slower than the larger browser job; it is
  most likely a first-run effect and not a property of the record. Not checked.
- A copy of the write with no durability steps: 0.05 to 0.10 ms (10,000 to 18,500 per second). So nearly all of a
  save's time is the sync, not the JSON.
- Serialising alone: 0.001 to 0.003 ms. Compact output is 10 to 30 % smaller than indented (a 16-segment checkpoint:
  2,415 to 1,704 bytes).
- On tmpfs the real stores take 0.011 to 0.019 ms and the copy 0.006 to 0.007 ms.

## Start times (PERF.8)

Self-contained Release, no ReadyToRun; 10 runs after one warm-up; ms from process start.

| Program | Case | Median | Min | Max |
| --- | --- | ---: | ---: | ---: |
| CLI | `--version`, to exit | 22.0 | 14.5 | 35.6 |
| CLI | `doctor --no-start`, no Host, to exit | 40.2 | 33.0 | 41.7 |
| CLI | `status --no-start`, Host running, to exit | 55.1 | 53.7 | 64.3 |
| Native host | first reply to `host.ping` | 76.2 | 75.3 | 107.6 |
| Host | endpoint accepts a connection, fresh data folder | 151.5 | 135.1 | 445.9 |
| Host | exit after the shutdown command | 79.1 | 72.5 | 171.0 |

## Web UI (PERF.15)

Headless Chrome 157, 1600x1000, the headless server on the same machine; steady state is 60 s after a 10 s warm-up.

Load, from the Enter key on the sign-in page:

| Jobs | Added active | Running | Usable ms | Long tasks: count / total ms / longest ms | Snapshot MB | Reload usable ms |
| ---: | ---: | ---: | ---: | --- | ---: | ---: |
| 500 | 1 | 1 | 281 | 1 / 89 / 89 | 0.62 | 219 |
| 500 | 8 | 8 | 323 | 1 / 94 / 94 | 0.63 | 171 |
| 500 | 20 | 16 | 352 | 1 / 105 / 105 | 0.65 | 210 |
| 5,000 | 1 | 1 | 1,296 | 13 / 2,287 / 773 | 6.17 | 1,158 |
| 5,000 | 8 | 8 | 1,210 | 73 / 8,825 / 772 | 6.18 | 1,107 |
| 5,000 | 20 | 16 | 1,120 | 91 / 10,533 / 702 | 6.21 | 1,377 |

First load is 0.65 MB on the wire at every size (script 592 KB, stylesheet 82 KB, uncompressed); a reload with a warm
cache is 1 to 5 KB. The snapshot is about 1.3 KB per job.

Steady state, per second of wall time:

| Jobs | Running | Script ms | Main-thread busy ms | Layout ms | Layouts | WS messages | WS KB | DOM elements | Heap after GC MB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 1 | 19.8 | 91.9 | 11.2 | 54.8 | 1.8 | 3.0 | 12,858 | 14.0 |
| 500 | 8 | 192.2 | 421.5 | 27.4 | 74.1 | 13.9 | 23.0 | 13,061 | 14.4 |
| 500 | 16 | 358.8 | 606.5 | 33.9 | 86.7 | 27.5 | 45.5 | 13,393 | 14.8 |
| 5,000 | 1 | 200.0 | 574.5 | 71.2 | 44.9 | 1.7 | 2.9 | 125,358 | 113.2 |
| 5,000 | 8 | 892.5 | 999.6 | 21.1 | 14.2 | 10.6 | 17.5 | 125,561 | 307.4 |
| 5,000 | 16 | 897.8 | 999.9 | 20.7 | 14.7 | 10.6 | 17.6 | 125,893 | 342.9 |

What it says: with 5,000 jobs and 8 or more downloads running the page's main thread is busy all the time (1,000 ms per
second), and it takes in only about 10.6 messages a second where the 500-job page takes 13.9 and 27.5, so it is falling
behind. One download alone keeps it busy more than half the time. The list is not virtualised: 25 elements per job.

## Linux window (Uno) (PERF.15)

In a private `kwin_wayland --virtual` display through XWayland; "OpenGL" ran on the NVIDIA GPU. 60 s after a 10 s
warm-up; CPU in percent of one core. The Host was already running.

Time to open, ms from process start to the first frame with the list (median of 3 opens):

| Jobs | 0 running | 1 | 8 | 16 |
| ---: | ---: | ---: | ---: | ---: |
| 500, OpenGL | 1,060 | 1,102 | 1,479 | 1,112 |
| 5,000, OpenGL | 1,112 | 1,090 | 1,145 | 1,199 |
| 500, software | 1,111 | 1,001 | 1,056 | 1,115 |
| 5,000, software | 1,044 | 989 | 1,145 | 1,182 |

The stages are alike everywhere: runtime up at 20 to 80 ms, window activated at about 570, snapshot applied at about
900, first frame at about 1,100. The first open of a combination was often slower (up to 2,792 ms). The 1,479 is
one disturbed combination, not an effect of 8 downloads.

While open:

| Jobs | Running | Renderer | Process CPU % | UI thread % | UI ms/min | Render thread % | RSS max MB | Host CPU % | Host RSS MB |
| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 0 | OpenGL | 0.02 | 0.0 | 0 | 0.0 | 414 | 0.0 | 122 |
| 500 | 1 | OpenGL | 10.7 | 3.7 | 2,190 | 4.7 | 392 | 0.3 | 94 |
| 500 | 8 | OpenGL | 15.5 | 5.4 | 3,230 | 7.5 | 299 | 1.4 | 86 |
| 500 | 16 | OpenGL | 22.8 | 7.9 | 4,710 | 11.7 | 403 | 3.6 | 125 |
| 5,000 | 0 | OpenGL | 0.07 | 0.0 | 0 | 0.0 | 409 | 0.0 | 222 |
| 5,000 | 1 | OpenGL | 15.8 | 5.6 | 3,330 | 7.0 | 443 | 0.3 | 254 |
| 5,000 | 8 | OpenGL | 19.3 | 7.9 | 4,750 | 8.8 | 448 | 1.8 | 241 |
| 5,000 | 16 | OpenGL | 19.6 | 8.4 | 5,060 | 8.4 | 441 | 2.6 | 187 |
| 500 | 0 | software | 0.02 | 0.02 | 10 | 0.0 | 250 | 0.0 | 128 |
| 500 | 1 | software | 21.2 | 6.6 | 3,960 | 11.6 | 288 | 0.4 | 130 |
| 500 | 8 | software | 25.3 | 8.9 | 5,340 | 13.2 | 287 | 2.2 | 126 |
| 500 | 16 | software | 39.6 | 14.6 | 8,750 | 20.0 | 279 | 6.5 | 128 |
| 5,000 | 0 | software | 0.03 | 0.0 | 0 | 0.0 | 305 | 0.02 | 246 |
| 5,000 | 1 | software | 32.6 | 10.6 | 6,330 | 18.0 | 343 | 0.6 | 238 |
| 5,000 | 8 | software | 39.0 | 15.8 | 9,490 | 19.0 | 344 | 3.1 | 249 |
| 5,000 | 16 | software | 32.5 | 14.7 | 8,810 | 13.5 | 336 | 4.7 | 246 |

What it says, within the noise above:

- Idle is idle: 10 to 40 CPU ms a minute with no download running, at either size.
- One running download already costs 10 to 30 % of a core, about half of it drawing. Going from 1 to 16 downloads
  roughly doubles it; the first download is the expensive one.
- Software rendering uses about twice the CPU of OpenGL and about 100 to 150 MB less memory.
- The list's size (500 or 5,000) changes little in the window, unlike in the web UI.
- The Host itself stays under 7 % of a core with 16 downloads; 5,000 jobs cost it about 100 MB.
