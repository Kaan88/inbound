# Possible performance gains

Written 2026-11-14 from a read of the code. **Nothing here is measured.** Each item is a candidate: measure
before and after (the soak driver `scripts/soak.sh` and `scripts/perf-window.sh` already exist) and drop the
ones that show nothing. Already done and not repeated here: PERF.3 (GC conserve-memory), PERF.4 (idle
wake-ups), PERF.5 (window process ends on close), PERF.7 (no snapshot for short-lived pipe clients).

Order is by expected value for the effort.

## 1. Startup and memory of the small processes

| Area | What the code does now | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| JSON source generation | No `JsonSerializerContext` anywhere in `src`; every DTO, job and checkpoint goes through reflection-based `System.Text.Json` | Faster first serialise in every process (CLI, native host, Host), less memory; it is also the precondition for Native AOT | Medium: `Contracts` has about 6,000 lines of DTOs to list; payloads built from anonymous objects (`new { }`) need named types |
| Native AOT for `NativeHost` and `Cli` | Both are ordinary .NET apps, started once per browser handoff or command | Start in a few ms instead of a runtime start, a few MB of memory, no runtime files needed for them | Medium, after source generation. `NativeHost` is 437 lines, so start there |
| ReadyToRun for Host, Server, WinUI | `scripts/publish.ps1` passes no `PublishReadyToRun` | Shorter cold start (the window's 650 to 1,100 ms open, the Host at sign-in) for a larger install folder | Low: one publish property; measure the size against REV.3's trim |
| Native AOT for the Host | Not attempted | The largest idle-memory gain available without a rewrite (Host idles at 19 to 52 MB today) | High: WinForms tray, torrent and archive libraries may not be AOT-safe; try only after the two above |

## 2. Transfer loop (`HttpDownloadEngine.cs`)

These matter only on fast links (roughly 500 Mbit/s and up) or with many segments; on a normal connection the
network is the limit.

| Area | What the code does now | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| Read timeout per read | Each read is `stream.ReadAsync(...).AsTask().WaitAsync(ReadTimeout, token)` (line 1850, also 723): a `Task` and a timer per read, thousands a second at gigabit speed | Less allocation and CPU per byte; replace with one linked `CancellationTokenSource` per segment that is re-armed with `CancelAfter` | Low to medium: the timeout's behaviour (retryable failure, not a cancel) must stay the same; tests exist |
| Buffers | `new byte[BufferSize]` (128 KB) per segment attempt and per single-stream run (lines 713, 1845); above 85 KB, so each is a large-object-heap allocation | `ArrayPool<byte>.Shared` removes LOH churn on retries and segment steals | Low |
| Small reads, one write each | Every read is written at once with `RandomAccess.WriteAsync` (line 1881); a TLS read often returns 16 KB, so a 128 KB buffer is mostly unused | Fill the buffer (or read until N KB) before writing: fewer write calls, fewer progress and lock rounds | Medium: bytes read but not yet written are refetched after a stop, so the batch size bounds the waste |
| Per-read bookkeeping | Each read takes `run.Sync` twice, calls two rate limiters, the reporter and the checkpoint check | Skip the limiters when no limit is set; fold into the batching above | Low |
| Checkpoint flush | Every 5 s (`CheckpointInterval`) the part file is flushed to disk (`FlushToDisk`, line 2069) and a checkpoint JSON is written with write-through | On slow disks or HDDs a 5 s full flush can stall writers; a longer interval for large files, or flushing only when N MB changed | Medium: this is the durability rule, so any change needs the kill tests rerun |
| MD5 check | `Hashing.Md5FileAsync` rereads the whole part file after the download (line 2565) | Hash while downloading for single-stream; for segmented keep the reread | Low gain: only jobs with an expected MD5 |
| HTTP version | No `Version`/HTTP/2 setting found in the engine, so requests go out as HTTP/1.1 with one connection per segment | Probably none: separate connections are what segmenting wants. Listed so nobody spends time on it | None |

## 3. Persistence (`JsonStores.cs`) and progress saves

| Area | What the code does now | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| Indented JSON | `WriteIndented = true` for jobs, checkpoints, settings | About a third fewer bytes per write and per load | Low; files stay readable with any formatter |
| Job save every 5 s per active job | `DownloadManagerService.cs:4655`: full job file rewritten (temp file, write-through, fsync, rename) every 5 s while downloading, next to the engine's own checkpoint every 5 s | Two fsynced writes per job every 5 s become one: let the checkpoint carry progress and save the job only on state changes | Medium: recovery reads both; needs the crash-recovery tests |
| One file per job, all read at start | `LoadAll` enumerates `jobs\*.json` and deserialises each (line 318); 5,000 jobs means 5,000 opens | Faster Host start with a large history: load finished jobs lazily, or keep finished jobs in one archive file | High: a storage format change with a migration. Worth it only if large lists are a real use |
| Serialise to string, then to bytes | `WriteAsync` serialises to a `string`, then encodes to UTF-8 | `SerializeToUtf8Bytes` halves the allocation per save | Low |

## 4. IPC and events

| Area | What the code does now | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| Progress events | Each active job announces at most every 300 ms (`DownloadManagerService.cs:4633`), a whole `JobDto` each time | A small progress DTO (bytes, speed, state) instead of the full job; or one batched message for all active jobs per tick | Medium: an IPC change, so a protocol note and old-client handling |
| Broadcast | `Broadcast` enqueues the same `IpcMessage` to each client (`HostCommandDispatcher.cs:1170`); the WebSocket transport serialises per client (`WebSocketTransport.cs:193`); the pipe transport was not checked | Serialise once and share the bytes | Low to medium |
| Snapshot size | 8.5 MB of JSON at 5,040 jobs (PERF.7); the window still gets it on connect | Paging or a slimmer list DTO would cut the window's open time at 5,000 jobs (890 to 935 ms of 1,065 to 1,129 ms is the snapshot) | High: every frontend reads the snapshot |

## 5. Window (WinUI)

| Area | What is known | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| Memory | 215 to 235 MB private with an empty list (PERF.1); that is WinUI and the Windows App SDK, not our code | Little from our side; PERF.5 already removes it while closed | - |
| Row updates | Job events are marshalled to the UI thread one by one (`MainWindow.Servers.cs:84`) | Coalesce progress per frame and update only rows on screen; matters with tens of active jobs | Medium |
| Cold open | 650 ms empty, about 1,100 ms with 5,000 jobs | ReadyToRun (section 1) and the snapshot work (section 4) | - |

## Benchmarking, and whether to try Rust

Benchmark the current code first (ROADMAP PERF.8). Without a baseline, neither the items above nor a Rust
comparison can be judged.

**Baseline to take**

- *Throughput:* loopback downloads from a fast local server with 1, 8 and 16 segments, a file large enough to run
  for 30 s or more, writing to an SSD and to a RAM-backed folder (to separate disk from engine). Record MB/s, CPU
  seconds per GB and allocated bytes per GB. First check the TestServer can serve faster than the engine reads;
  otherwise use a plain static server.
- *Saves:* job and checkpoint writes per second, with and without write-through, to see what the 5 s saves cost.
- *Start:* time to first output for the CLI and the native host, time to pipe-ready for the Host.
- *Idle:* already covered by the PERF.4 and PERF.5 method.

**When a Rust comparison is worth running (PERF.14)**

Only for the transfer loop, and only if the baseline shows the engine uses a meaningful share of a core at the
speeds that matter, and PERF.9 has not removed it. Then a throwaway Rust program (segmented ranged GETs written at
offsets, no resume, no checkpoints) against the same server gives the ceiling. If .NET after PERF.9 is within
about 10 to 20% of it on CPU and equal on throughput, the question is closed.

**Not worth a Rust benchmark**

- Start time and memory of the CLI and native host: Native AOT (PERF.10) is the like-for-like comparison and
  costs far less to try.
- Persistence, IPC and the scheduler: their cost is fsync and JSON size, which a language change does not remove.
- The window: it is the toolkit.

## What not to expect

- Faster downloads on an ordinary connection: the engine is not the limit there.
- A smaller window process: the toolkit sets that.

## Order of work (both plans, PERF.8 to PERF.20)

With the Sites work placed between these steps, the order is `sites-and-performance-order.md`; the order among
the PERF items is the same there.

Revised 2026-11-14 after going through the dependencies. This replaces the two separate lists; the window and web
UI items are described in `ui-performance-areas.md`.

What decided the order:

- The two baselines use the same test instance and the same 500 and 5,000 job sets, so they are one session.
- Publish-only changes come first: no code risk, and they change every later start-time number.
- Client-side event gathering (window, web UI) comes before the IPC change (PERF.13). It needs no protocol
  change, and it may remove most of what PERF.13 would save, so PERF.13 is decided on numbers taken after it.
- JSON source generation comes before every Native AOT step, including the Linux window's trial, which today
  only works by keeping reflection JSON on.
- Anything that touches durability (PERF.12) or depends on an outside event (D7.W, the Uno pull request) is late.

| Step | Items | Why here |
| --- | --- | --- |
| 1 | PERF.8 and PERF.15 together: engine, saves, start times, window on both toolkits, web UI | Everything else is judged against it. Linux numbers on this PC, Windows numbers on the VM |
| 2 | ReadyToRun: PERF.11, and the ReadyToRun part of PERF.18 for the AppImage | One publish property each; rerun the start times and install sizes |
| 3 | PERF.16 (window: events once per tick, constant-time lookups) and the first half of PERF.19 (web UI: updates once per frame, one collator) | The same idea in both clients, no protocol change, helps WinUI and Uno at once |
| 4 | PERF.9 (transfer loop) | Independent of the UI work and covered by engine tests; can run alongside step 3 |
| 5 | PERF.10: JSON source generation, then Native AOT for the native host and the CLI | Opens the way to step 6 |
| 6 | The Native AOT part of PERF.18 (Linux window) | Needs step 5 to be a fair trial |
| 7 | Measure again, then decide: PERF.13 with the serialise-once part of PERF.20; the windowed list (second half of PERF.19); PERF.17 (compiled bindings) | Each is larger, and steps 3 to 5 may have made it unnecessary. If step 1 shows the web UI already slow at 500 jobs, the windowed list moves up to step 3 |
| 8 | The rest of PERF.20: compressed and cached static files, the snapshot's size | Matters only for a server reached over a slow link; the compression question is a security decision |
| 9 | PERF.12 (saves) | Touches the durability rule; needs the kill and recovery tests rerun |
| 10 | The renderer and list-row parts of PERF.18 | After the D7.W comparison, if that trial is run |
| 11 | PERF.14 (Rust comparison of the transfer loop) | Only if steps 1 and 4 leave the engine as the limit |

Steps 2, 3, 4 and 5 do not depend on each other and can be done in any order or side by side.
