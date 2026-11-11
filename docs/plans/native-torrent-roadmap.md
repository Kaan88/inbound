# Native torrent client: implementation roadmap

Status: decided in principle, not started. Owner decisions of 2026-11-11 are in section 6; the plan runs T0 to T5 (the external-client
account, T6, is dropped). Origin (owner, 2026-11-11: "plan a roadmap ... not sure of the backend ... the idea of it being
disabled when no torrents are active is good"). Based on `docs/plans/native-torrent-research.md` (read its executive
summary first) and `docs/plans/torrent-mockups/`. Effort figures are opinions, not measurements.

## 1. Rules that every phase must keep

1. **Off costs nothing.** With no torrent downloading or seeding there is no torrent process, no timer, no socket and no
   open port. "Idle" for torrents means *absent*, not *quiet*.
2. **Nothing changes for anyone who never uses torrents.** Same Setup behaviour, same Host idle numbers (the PERF.4
   method is the yardstick), same Docker idle memory.
3. **The backend is replaceable.** The Host talks to a small contract (section 2), never to a torrent library. Choosing
   or changing the engine touches one adapter.
4. **Kill switch.** Options > Torrents (off by default) and `DOWNLOADMANAGER_TORRENTS=off`, which wins over the setting.
5. **The extension stays untouched.** No magnet or torrent handling in it (store-policy risk, see the research).
6. **No unsourced claims** about libraries or providers in UI text or docs; measured numbers go in PROGRESS.

## 2. Architecture that works with any backend

```
  UI (WinUI / Uno / web UI)  <--- existing IPC envelopes, new torrent commands + capability flag --->  Host
  Host: job model, queue, scheduler, limits, checkpoints, persistence   (knows nothing about peers)
        |  spawns only when needed, supervises, restarts once, kills on shutdown
        v
  TorrentWorker (separate process, stateless)  <-- stdio or local pipe/Unix socket, length-prefixed JSON -->
        |  engine adapter  (MonoTorrent | libtorrent binding | aria2 RPC)  -- chosen in phase T0
        v  peers, DHT, trackers, disk
```

**Worker lifecycle (the "disabled when idle" idea)**

| State | Meaning | Process | Sockets / timers |
| --- | --- | --- | --- |
| Off | no active torrent, or setting off | none | none |
| Starting | first torrent becomes active, or Add dialog needs metadata | spawning | none yet |
| Active | at least one torrent downloading (or seeding, when seeding is on and the ratio/time limit is not reached) | running | listening port and DHT only while Active |
| Draining | last active torrent finished or paused | running, short grace (default 30 s) so a second add reuses it | closing |
| Off | grace expired | exits | none |

The worker is **stateless**: the Host keeps the torrent list, per-file selection, destination, limits and the engine's
resume data (opaque blob, checkpointed like HTTP segments today). A crash or exit loses nothing; the next start re-adds
from the Host's records. The DHT routing table is cached in the data folder so a cold start does not begin from zero.

**Contract (engine-neutral, the seam between Host and worker)**

- Commands: `add(magnet | .torrent bytes, destination, selected files, start paused, resume blob?)`, `pause`, `resume`,
  `remove(keep files?)`, `recheck`, `setFilePriorities`, `setLimits(down, up, per-torrent)`, `setSeedPolicy(ratio, time)`,
  `setListen(port, upnp, dht, pex, encryption)`, `shutdown`.
- Events: `metadata(name, files[], size)`, `progress(bytes, rate, peers, seeds, eta)` at a throttled rate (1 per second while
  a window is open, 1 per 5 s otherwise), `state(downloading|seeding|checking|paused|error)`, `completed`,
  `resumeBlob(id, bytes)` (periodic and on exit), `error(code, detail)`, `stats(listen port, dht nodes, upnp result)`.
- Safety limits enforced in the Host before and after the worker: maximum file count, maximum metadata size, every file
  path resolved under the destination (reject `..`, absolute paths, reserved names, links), declared size checked against
  free space.
- "Completed" for queue and "when the queue finishes" purposes means *download finished*, never *seeding finished*.

## 3. Choosing the backend (phase T0)

You said you are unsure, so the first phase is a time-boxed bake-off behind the contract above, with pass/fail gates
set before anything is measured. Candidates:

| Candidate | Why it is in | Main doubt |
| --- | --- | --- |
| A. MonoTorrent in the worker | MIT, pure C#, same language, trivial arm64, ~1-2 MB (estimate) | thin release cadence, uTP in stable not verified, timer runs even with no torrents (fine: the worker exits) |
| B. libtorrent-rasterbar through a .NET binding | reference engine, full protocols, BSD | third-party binding, native library per platform, packaging into the AppImage and image |
| C. aria2 (its own RPC) as the engine | tiny, simple | dormant releases, no mention of uTP or BitTorrent v2 in its manual, GPL (separate process) |
| (not a candidate) bundling qBittorrent or Transmission | 69 MiB for qBittorrent's static Linux build, GPL | cannot meet the size gate |

Gates. These numbers are my proposals, anchored on figures the repository already measured (PERF.4: the Host idles at about
0.17% of one core and 12 context switches/s; the Docker container idles at 34 to 54 MiB against a 100 MB target; Setup is
about 62 MB, the AppImage about 70 MB). They are opinions, to be changed before T0 starts if you disagree. Every row is
measured with the PERF.4 method (30 minutes, 10 s sampling) on the same machine, and recorded in PROGRESS.

| # | Gate | Proposed pass line |
| --- | --- | --- |
| 1 | **Feature off** | no torrent process, socket or timer; Host idle CPU, context switches, I/O and memory within measurement noise of the build without the feature (about +0.02% of a core, +1 switch/s, 0 added I/O operations) |
| 2 | **Seeding, 1 torrent with no peers transferring** | worker at most 1% of one core on average and 100 context switches/s; no disk writes beyond one resume checkpoint per 5 minutes; memory at most 80 MB |
| 3 | **Seeding, 20 torrents** | at most 150 MB and 3% of one core on average |
| 4 | **Throughput** | at least 80% of qBittorrent's median of three runs, same torrent, machine and connection |
| 5 | **Cold start to first piece** | magnet to first piece at most 15 s with a warm DHT cache, at most 45 s cold, on a healthy public swarm |
| 6 | **Size** | added to each of Setup, the update zip, the AppImage and the Docker image: at most 10 MB |
| 7 | **Docker** | container with the feature off unchanged from today; with 5 seeding torrents under 100 MiB in total |
| 8 | **Soak** | 24 hours seeding: memory growth at most 5% after the first hour, handles flat, no unbounded log |
| 9 | **Resume** | kill the worker mid-download and restart: continues within seconds; loses no more than one checkpoint interval |
| 10 | **Packaging and protocols** | self-contained publish runs on Windows x64, Linux x64 and Linux arm64; DHT, PEX, magnet metadata, MSE, selective files and IPv6 work; uTP and v2/hybrid recorded as yes or no |
| 11 | **Fit** | per-file selection, speed limits from the Host, clean shutdown, resume blob round trip |

**Decision rule.** Take A (MonoTorrent) if it passes every row. If it fails only rows 4 or 10 or on maintenance, take B
(libtorrent). If neither passes rows 1 to 3 and 6, **do not build a native client**: record the numbers and stop (there is
no fallback phase).

## 4. Phases

| Phase | Deliverable | Effort (opinion) | Exit gate |
| --- | --- | --- | --- |
| **T0. Bake-off** | the contract as a small library with a fake engine, one worker per candidate, the measurements above, a written decision in PROGRESS | 1.5 to 2 weeks | decision recorded; owner says go or no-go |
| **T1. Smallest useful slice** | the worker ships in the main Setup, update zip, AppImage and image (owner: yes), inside the size gate; Host supervises the worker with the lifecycle above; `torrents` capability flag; Options > Torrents (off); Add Torrent dialog (magnet or file, file tree, destination, start paused); Torrents category; download to completion; seeding off; outgoing connections only; no firewall rule | 4 to 5 weeks | Integration test against a loopback seeder and tracker; kill-switch test (no process, no socket); idle comparison with and without the feature compiled in |
| **T2. Seeding and control** | seeding on (owner: yes; proposed defaults: stop at ratio 1.0 or 48 hours, whichever comes first, editable), ratio and time limits, upload limit sharing the global budget, queues and scheduler semantics, details window (Files, Peers, Trackers, Pieces & Speed), firewall rule through the existing UAC flow, UPnP/NAT-PMP toggle, extraction waits until a torrent's download has finished | 3 to 4 weeks | scheduler and quota tests; firewall rule tests; 24-hour seeding soak with flat memory |
| **T3. Web UI and Docker** | Torrents in the web UI including phone width, upload through the existing `/api/upload/torrent`, the torrent ports published in `docker/compose.example.yml` (owner: yes; nothing listens while the feature is off), UPnP off by default in the image, arm64 check | 2 to 3 weeks | `npm run screens` walk at 1360 and 390 px, vitest, container idle measurement against the memory target |
| **T4. Linux desktop** | the worker inside the AppImage, port hint, Linux tests | 1 week | run on the owner's KDE/Wayland machine |
| **T5. Polish and limits** | clearer errors, disk-full handling, torrent-level notifications, import/export of the list | 1 to 2 weeks | usability pass like TEST.5 |

Go/no-go checkpoints: after T0 (is any backend good enough?), after T1 (does the idle comparison prove "unchanged for
non-users" and is the slice worth finishing?), after T2 (is seeding wanted at all?).

## 5. Test plan (where things go)

- `tests/DownloadManager.Torrents.Tests` (new, xunit): path safety, metadata limits, priority mapping, resume blobs,
  limit splitting, lifecycle state machine with the fake engine.
- `tests/DownloadManager.Integration.Tests`: Host plus worker against a local seeder and tracker on loopback;
  kill switch; golden frames for the new envelopes.
- `tests/DownloadManager.Core.Tests`: the input classifier's third answer (provider, native, refuse).
- `tests/DownloadManager.Server.Tests` and `webui` vitest/screens for T3.
- Extension suite: one guard test that the manifest and source contain no magnet or torrent handling.

## 6. Owner decisions (2026-11-11)

1. **Seeding: yes.** It stays off per torrent until the owner or the default policy says otherwise; the firewall rule, UPnP
   toggle and port hint come in T2.
2. **Gate numbers:** section 3's table, through T5, as proposed above (the owner may still edit the numbers before T0).
3. **Packaging: in the main Setup** (and the update zip, AppImage and image), not an optional download, so the 10 MB size
   gate is a hard limit.
4. **Docker: yes**, publish the torrent ports in the example compose file.
5. **Licence: acceptable** as it stands: an MIT or BSD engine linked into the worker; a separate GPL process would also be
   acceptable if it ever came to that.
6. **T6 (connect to an external client): dropped.** The roadmap runs T0 to T5 only.
