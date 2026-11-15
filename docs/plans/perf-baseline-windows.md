# Baseline, Windows (2026-11-15)

The Windows half of ROADMAP PERF.8 and PERF.15, the counterpart of `perf-baseline-a2-linux.md`. How they are taken:
`tools/Bench/README.md` and `perf-baseline-how.md`. Raw CSV files are under `artifacts/perf-bench/` and
`artifacts/perf-window/` in this checkout and are not in the repository.

**Commit.** Taken at `b45b7ca`, not at the `perf-baseline-a2` tag the Linux numbers come from: the generated JSON
(first half of PERF.10) and once-per-tick events in the window (PERF.16) are already in. Compare later Windows runs
with these numbers, not with the Linux ones.

## Machine and noise

- Windows 11 Pro (10.0.26300), .NET 10.0.15, 28 logical CPUs, 32 GB, power plan Balanced; the destination folder on the
  system SSD (NTFS), 57 GB free.
- The owner said the PC was idle; total CPU was 3 to 6 % before the runs. The only other work during the runs was this
  session's own few commands between runs (no build ran during a measurement).

## Engine throughput (PERF.8)

Against the bench's own static server in a second process; median of 3 runs; MB = 10^6 bytes; 16 GiB file. Some runs took
22 to 28 s, under the tool's 30 s target. No RAM rows: Windows has no RAM-backed folder by default.

| Destination | Segments | Host limit | MB/s | CPU s/GB | Allocated MB/GB | Peak working set MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| disk (NTFS) | 1 | 8 | 357 | 0.96 | 1.4 | 76 |
| disk | 8 | 8 | 481 | 1.10 | 2.6 | 79 |
| disk | 16 | 8 | 555 | 1.10 | 2.5 | 80 |
| disk | 16 | 16 | 606 | 1.30 | 2.9 | 83 |

The server is not the limit: its raw ceiling was 4.6 GB/s on one connection and 21.5 GB/s on 16. The engine reaches
about a third to a half of the Linux figures (1118 to 1266 MB/s on btrfs) at a similar CPU cost per GB. Whether the
difference is the disk, NTFS, the antivirus scanning the growing file or the engine's writes on Windows is not
established; a run with the work folder excluded from Defender, or on a RAM disk, would separate them.

## Saves (PERF.8)

The product's stores write through and sync. One save takes about 3.7 to 3.9 ms on this disk (257 to 267 a second) for
a browser job and for checkpoints of 1, 8 and 16 segments, the same as a plain write plus a sync; without the sync it
would take 0.43 ms, without any durability step 0.25 ms. Serialising takes about 2 µs. So on Windows a save's cost is
the sync, not the JSON (PERF.12).

The first record in the run (a plain job, `product`) averaged 38.7 ms (p95 47 ms), ten times the others. It is the first
save to the fresh folder, but why each of its 300 saves is slow is not established; rerun `Bench saves` alone before
reading anything into it.

## Start times (PERF.8)

Median of 10 runs after one warm-up:

| Program | What | ms |
| --- | --- | ---: |
| CLI | `--version`, to exit | 28.8 |
| CLI | `status --no-start` with the Host running, to exit | 82.5 |
| CLI | `doctor --no-start` without a Host, to exit | 757.9 |
| Host | start to its pipe accepting a connection (fresh data folder) | 234.5 |
| Host | shutdown command to exit | 114.6 |
| Native host | start to its reply to `host.ping` (Host running) | 102.5 |

## WinUI window (PERF.15)

### Time to open

`scripts/perf-window.sh coldopen`, 5 repeats each; milliseconds from `dm host open` (median):

| Jobs | Window shown | Snapshot applied | First frame with the list | Showing the hidden window again |
| ---: | ---: | ---: | ---: | ---: |
| 0 | 629 | 530 | 619 | 377 |
| 500 | 607 | 568 | 763 | 373 |
| 5,000 | 588 | 836 | 1,043 | 353 |

The PERF.5 figures were 650 ms empty and 1,065 to 1,129 ms at 5,000 jobs; these are at or below them.

### CPU and memory with downloads running

`scripts/perf-window-active.sh` (new): N held jobs and A downloads of 256 KiB/s each; the window open and on screen;
10 s warm-up, 60 s sampled. "UI thread" is the window's first thread (XAML runs on it in WinUI 3). "20" runs 16
(the product's cap). Percent of one core.

| Jobs | Active | Window CPU % | UI thread % | Window private MB | Host CPU % | Host private MB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 0 | 0.08 | 0.00 | 256 | 0.08 | 48 |
| 500 | 1 | 6.05 | 4.13 | 276 | 0.81 | 46 |
| 500 | 8 | 10.31 | 8.16 | 279 | 3.92 | 52 |
| 500 | 16 | 13.64 | 10.37 | 288 | 5.17 | 53 |
| 5,000 | 0 | 0.08 | 0.00 | 357 | 0.00 | 110 |
| 5,000 | 1 | 6.42 | 3.97 | 357 | 1.01 | 110 |
| 5,000 | 8 | 11.15 | 9.07 | 369 | 4.00 | 96 |
| 5,000 | 16 | 13.60 | 10.66 | 376 | 7.39 | 120 |

The number of jobs in the list hardly changes the window's CPU once downloads run; the number of running downloads
does. A single slow download already costs about 4 % of a core on the UI thread: the list, the speed graph and the
status bar redraw every tick, whatever changed. That is where PERF.16 and PERF.17 should be judged.

### Idle, window closed (hidden)

`scripts/perf-window.sh idle ... off 10`: the window opened, closed (it stays as a hidden process, the option
"Closing the window ends it" off), then 10 minutes sampled; no downloads.

| Jobs | Window CPU s per hour | Window context switches/s | Window private MB | Host CPU s per hour |
| ---: | ---: | ---: | ---: | ---: |
| 500 | 4.2 | 126 (max 148) | 258 | 0.1 |
| 5,000 | 4.5 | 126 (max 144) | 357 | 0.0 |

The Host is quiet. The hidden window wakes about 126 times a second while doing nothing: PERF.4's remaining
wake-ups, now measured. With the option on (the default since PERF.5) there is no window process after closing.

## PERF.9: the transfer loop (2026-11-15)

`TransferReader` gives each response one pooled buffer and one cancellation source re-armed per read, instead of a
task, a timer and a 128 KB array; uncapped, it fills half the buffer before each write. The same `Bench throughput` run
after the change (`artifacts/perf-bench/perf9-windows`):

| Segments | Host limit | Allocated MB/GB before | after | GCs (0/1/2) before | after | CPU s/GB before | after |
| ---: | ---: | ---: | ---: | --- | --- | ---: | ---: |
| 1 | 8 | 1.4 | 0.8 | 1/0/0 | 0/0/0 | 0.96 | 0.97 |
| 8 | 8 | 2.6 | 1.7 | 3/1/1 | 1/0/0 | 1.10 | 1.19 |
| 16 | 8 | 2.5 | 1.6 | 3/1/1 | 1/0/0 | 1.10 | 1.07 |
| 16 | 16 | 2.9 | 1.9 | 4/2/2 | 1/0/0 | 1.30 | 1.29 |

Allocation fell by a third to a half and the gen-2 collections went. CPU per GB is within noise. MB/s can't be compared on
this disk: the same configuration gave 237 to 1,000 MB/s from one run to the next, before and after alike, so the disk (or
what scans it) sets the speed, not the engine.

## PERF.11: ReadyToRun (2026-11-15)

Self-contained publishes of the whole app, plain and ReadyToRun (`scripts/publish.ps1 -SelfContained [-ReadyToRun]`);
start times by `Bench start`, the window by `scripts/perf-window.sh coldopen` with `PERF_APP_DIR` (5 opens, median).

| Build | Install folder | Host ready | Native host reply | `dm status` | Window first frame, 0 / 500 / 5,000 jobs |
| --- | ---: | ---: | ---: | ---: | --- |
| plain | 244 MB | 238 ms | 100 ms | 80 ms | 635 / 774 / 1,085 ms |
| ReadyToRun | 296 MB | 182 ms | 88 ms | 67 ms | 536 / 671 / 1,270 ms |
| ReadyToRun except Microsoft's window libraries | 255 MB | | | | 589 / 716 / 1,223 ms |
| ReadyToRun except every window library | 251 MB | | | | 637 / 774 / 1,215 ms |

The .NET runtime's own libraries are already ReadyToRun in both; 44 MB of the 52 MB added is the WinUI and Windows SDK
projections. With 5,000 jobs the window applies the snapshot sooner but takes twice as long (about 400 ms instead of
200 ms) to draw the list, steadily over every open; that comes with the ReadyToRun copies of the libraries the window
shares with the Host (the last row), which sit in the same folder. ReadyToRun code is not replaced by optimised code
inside a long-running loop, and it cannot inline across libraries, which is the likely reason; not confirmed.

**Decision: not turned on.** 52 MB more (against the REV.3 trim), about 55 ms off the Host's start at sign-in and 12 ms
off a browser handoff, and a window that is faster with a short list but slower with a long one. `-ReadyToRun` stays as
an opt-in switch for measuring. Worth trying again with the window in its own folder, or with Native AOT for the native
host and the CLI (PERF.10).

## Not measured

Dropped frames while scrolling (no script exists), the web UI on Windows (the Linux run covers the page; the browser
is the same), light mode, and the quiet Linux re-run (Linux tests are deferred).
