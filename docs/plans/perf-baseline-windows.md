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

The first record in the run (a plain job, `product`) averaged 38.7 ms (p95 47 ms), ten times the others. Rerun alone
(`artifacts/perf-bench/saves-perf12`), the plain job took 2.4 ms and the stalls hit other records instead (a 1-segment
checkpoint averaging 25.6 ms with a p95 of 3.5 ms; a replica averaging 51 ms): the disk stalls now and then, whatever is
written. A normal synced save on this disk takes 2.3 to 2.6 ms.

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

## The window's list bars (2026-11-15)

With the graph hidden, one download cost the window's UI thread 2.3 % of a core; a test build with the row's progress bar
collapsed took that to 0.08 %. WinUI's `ProgressBar` template plays a `RepositionThemeAnimation` on every value change
(its Updating to Determinate transition), frame by frame on the UI thread. The list rows now use
`InboundListProgressBarStyle`: the same parts and look, no visual states, so the control sets the indicator's width
itself and nothing animates; screen readers still read a progress bar with its value. Same runs, graph hidden, quiet
machine:

| Downloads | UI thread % of a core, before / after | Window process % |
| ---: | --- | --- |
| 1 | 2.31 / 0.42 | 3.74 / 1.98 |
| 8 | 7.72 / 0.60 | 9.51 / 1.58 |

With the graph shown (the default) the UI thread also dropped (1 download, 500 jobs: 4.1 to 1.1-2.1 %), but those runs
were taken while a Rust build loaded the machine (60 % total CPU) and swing too much to quote; to measure again on a quiet
machine.

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

## PERF.12: fewer synced saves (2026-11-15)

While an HTTP download runs, its job file was rewritten and synced every 5 s next to the engine's checkpoint, also every
5 s. After a restart the checkpoint's byte count wins over the job's (`ReconcileAsync`), so the job's progress save is now
every 30 s for HTTP downloads (torrents, which have no checkpoint, keep 5 s; the queue quota keeps its own 5 s; every
state change is saved at once as before). That is 14 synced writes a minute per active download instead of 24, counted
from the code, not measured. Job and checkpoint files are serialised straight to UTF-8 bytes; they stay indented, since
compact JSON would save only bytes (serialising takes about 2 µs of a 2.5 ms save), and tests and people read these files.

## Step 7: where the window's and the Host's CPU goes (2026-11-15)

`dotnet-trace` (sampled thread time, 30 s) on a test instance with 500 held jobs:

- **Window, 1 download:** our managed code on the UI thread (event batches, row updates, the speed graph's drawing,
  buttons) is about 0.03 % of the samples; the rest of the UI thread's 4 % is WinUI's own layout and drawing, inside the
  native message loop. With the speed graph hidden (`UI_STATE='{"showSpeedGraph":false}' scripts/perf-window-active.sh`)
  the UI thread drops from 4.1 % to 2.3 % of a core with 1 download, and from 8.2 % to 7.7 % with 8: the graph costs
  about 1.8 points whenever anything downloads, the rows the rest.
- **Host, 8 downloads:** the managed work that shows is flushing the part files and saving checkpoints; building and
  sending progress events does not appear among the samples.

So:

- **PERF.13** (a smaller progress message, serialise once per broadcast): **not worth doing** on these numbers; the
  serialisation it would save does not show.
- **PERF.17** (compiled bindings in the list row): **not worth doing**; bindings are part of managed time that is
  already under 0.1 %.
- **The speed graph** is the one cheap target in the window: about 1.8 % of a core with one download. Its line slides
  between samples with a storyboard on a `TranslateTransform` and is rebuilt every second; which of the two costs is
  not established (a trace with native stacks, WPR, would show it).

  **Done the same day:** the graph drew its whole grid again with every sample (8 dashed lines, 8 labels, a clip). It
  now keeps them until the size, scale, period or theme changes and rebuilds only the lines. Same run as above, graph
  shown, one 60 s run each: 1 download, UI thread 4.1 % to 2.8 % of a core (window 6.1 % to 4.9 %); 8 downloads,
  8.2 % to 7.4 %. With the graph hidden it was 2.3 %, so about a quarter of the graph's cost is left (the lines and the
  slide). The labels were read through UI Automation while downloads ran: each once, the summary updating.

## PERF.19: the web UI's list (2026-11-15)

`scripts/perf-webui.mjs` now runs on Windows too (`perf-seed.sh` builds win-x64 and uses the Windows Host; headless Chrome
157). Headless Chrome against the headless server, 30 s after 10 s, per second of the page's main thread:

| Jobs | Downloads | Before: task / script / layout ms, layouts | Clip-path bar and `content-visibility` | And rows kept between updates |
| ---: | ---: | --- | --- | --- |
| 500 | 1 | 193 / 20 / 33, 118 | (run failed) | 30 / 16 / 0.4, 2.6 |
| 500 | 8 | 354 / 118 / 33, 141 | 165 / 120 / 2.1, 12 | 145 / 92 / 2.4, 14 |
| 5,000 | 1 | 831 / 188 / 102, 94 | (run failed) | 137 / 103 / 2.5, 2.7 |
| 5,000 | 8 | 999 / 811 / 30, 25 | 892 / 820 / 9.0, 8.5 | 699 / 583 / 13, 12 |

Sign-in to a usable list at 5,000 jobs: 1,223 ms before, 712 to 761 ms after. Three changes:

- **The progress bar** animated its `width`, and progress arrives about every 300 ms, so the page was laid out again on
  nearly every frame (about 120 layouts a second with one download, at 500 jobs). The fill is now full width and clipped
  from the right (`clip-path: inset(... round 9999px)`), which repaints the bar only.
- **`content-visibility: auto`** on list rows: rows off screen are not laid out or painted.
- **Rows kept between updates** (`views.ts`): a download's cells and its row object are kept per job object (a job event
  replaces the object), so an unchanged download is not formatted again and Svelte leaves its row alone. Torrents follow
  the clock and are formatted every time.

A CPU profile of that run (`perf-webui.mjs --profile`, an unminified build with `PERF_NO_MINIFY=1`) showed where the
583 ms went: `getBoundingClientRect` 70 ms a second (Svelte measuring every row for `animate:flip` on each update),
Svelte's proxy traps about 130 (going through 5,000 jobs in the deep `$state` model), `buildRows` 37, `counts` 19. Two
more changes:

- Rows slide to their new places only while the list has 200 rows or fewer; a longer list has no row animation.
- The jobs are kept in an object with no prototype (`jobTable`), which Svelte doesn't wrap in proxies; a job event
  replaces the table instead of editing it.

| Jobs | Downloads | Main thread busy ms/s (task / script) | Usable after sign-in | Long tasks while loading |
| ---: | ---: | --- | ---: | --- |
| 500 | 1 | 19 / 6 (was 30 / 16) | 215 ms | 1 |
| 500 | 8 | 81 / 27 (was 145 / 92) | 206 ms | 1 |
| 5,000 | 1 | 66 / 27 (was 137 / 103) | 651 ms | 2 (was 11) |
| 5,000 | 8 | 338 / 169 (was 699 / 583) | 629 ms | 2 (was 40) |

From the start of this work, 5,000 jobs and 8 downloads: 999 to 338 ms a second; with one download 831 to 66. Checked
with Playwright on 20 and 300 rows (both branches): a click selects, the arrows move the cursor, Shift extends,
End reaches the last row on screen, Shift+F10 opens the menu once the scroll settles. A windowed list is not needed on
these numbers.

## PERF.20: the web UI's own files (2026-11-15)

The publish step already leaves a Brotli and a gzip copy of each built file; the server never sent them. Now, under
`/assets/` only (hashed names, no secrets in them), the server sends the copy the browser accepts and marks the file
`private, max-age=31536000, immutable` (private, since the files are behind the sign-in). The main script went from
645 KB to 162 KB with Brotli (205 KB with gzip), checked against a published server with curl: the decoded bytes equal
the plain file, a copy asked for by its own name is a 404, and without a session the request is still sent to the
sign-in. The snapshot and the WebSocket stay uncompressed (the compression side channel in
`ui-performance-areas.md`), and serialising once per broadcast was dropped with PERF.13.

## PERF.10: the native host as Native AOT (2026-11-15)

`scripts/publish.ps1 -SelfContained` (what Setup, the update zip and the Windows workflow use) now compiles the native
host with Native AOT. `Bench start`, the Host running, median of 10: first reply to `host.ping` 100 ms before, 30 ms
after; the program is one 9.5 MB file and the install folder grows from 244 to 253 MB (its 41 MB native symbol file is
left out). One place still went through reflection with default options (`NativeBridgePolicy` writing a `JsonObject`),
which Native AOT would have refused at run time; it now uses the generated context. The integration tests that start
the native host (9 of the 70 in the bridge, hello and pipe groups) pass against the AOT build
(`INBOUND_TEST_NATIVE_HOST` points the sandbox at it). The CLI stays as it is: its JSON still goes through reflection in
many places, and it is started by hand, not on every browser download. The Linux native host is unchanged (Linux tests
are deferred).

## Not measured

Dropped frames while scrolling (no script exists), the web UI on Windows (the Linux run covers the page; the browser
is the same), light mode, and the quiet Linux re-run (Linux tests are deferred).
