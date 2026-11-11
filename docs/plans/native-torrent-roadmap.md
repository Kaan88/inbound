# Native torrent client: implementation roadmap

Status: proposal, not started (owner, 2026-11-11: "plan a roadmap ... not sure of the backend ... the idea of it being
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
| (not a candidate) bundling qBittorrent or Transmission | 69 MiB for qBittorrent's static Linux build, GPL | would roughly double the AppImage |

Gates (proposed; the owner may change the numbers before T0 starts):

1. **Cold start to first piece** on a public Linux ISO magnet: reasonable on a normal connection (record the number).
2. **Throughput** within an agreed share of qBittorrent on the same torrent and machine (record both).
3. **Idle**: worker alive with 0 torrents, with 1 and with 20 seeding: CPU, wake-ups, disk IO, memory, sampled 30 minutes
   at 10 s (the PERF.4 method). Off must be exactly zero, which the lifecycle guarantees.
4. **Size**: added MB to the Setup, the update zip, the AppImage and the Docker image.
5. **Packaging**: self-contained publish works; trimming/AOT result recorded; Linux x64 and arm64 both run.
6. **Protocols**: DHT, PEX, magnet metadata, MSE, selective files, IPv6, uTP (yes/no), v2/hybrid (yes/no).
7. **Fit**: per-file selection, resume blob round trip, speed limits applied from the Host, clean shutdown.

**Decision rule.** Take A if it passes every gate. If it fails only on protocols or maintenance, take B. If neither
passes the idle and size gates, **do not build a native client**; do the "connect to my qBittorrent/Transmission"
account (phase T6) instead, which costs the app nothing at idle.

## 4. Phases

| Phase | Deliverable | Effort (opinion) | Exit gate |
| --- | --- | --- | --- |
| **T0. Bake-off** | the contract as a small library with a fake engine, one worker per candidate, the measurements above, a written decision in PROGRESS | 1.5 to 2 weeks | decision recorded; owner says go or no-go |
| **T1. Smallest useful slice** | Host supervises the worker with the lifecycle above; `torrents` capability flag; Options > Torrents (off); Add Torrent dialog (magnet or file, file tree, destination, start paused); Torrents category; download to completion; seeding off; outgoing connections only; no firewall rule | 4 to 5 weeks | Integration test against a loopback seeder and tracker; kill-switch test (no process, no socket); idle comparison with and without the feature compiled in |
| **T2. Seeding and control** | ratio and time limits, upload limit sharing the global budget, queues and scheduler semantics, details window (Files, Peers, Trackers, Pieces & Speed), firewall rule through the existing UAC flow, UPnP/NAT-PMP toggle, extraction waits until a torrent's download has finished | 3 to 4 weeks | scheduler and quota tests; firewall rule tests; 24-hour seeding soak with flat memory |
| **T3. Web UI and Docker** | Torrents in the web UI including phone width, upload through the existing `/api/upload/torrent`, compose ports documented, UPnP off by default in the image, arm64 check | 2 to 3 weeks | `npm run screens` walk at 1360 and 390 px, vitest, container idle measurement against the memory target |
| **T4. Linux desktop** | the worker inside the AppImage, port hint, Linux tests | 1 week | run on the owner's KDE/Wayland machine |
| **T5. Polish and limits** | clearer errors, disk-full handling, torrent-level notifications, import/export of the list | 1 to 2 weeks | usability pass like TEST.5 |
| **T6. Optional: external client account** | "Connect to my qBittorrent or Transmission" as a provider-like account (WebUI API / RPC) | 2 to 3 weeks | tests against both in Docker |

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

## 6. Decisions only the owner can make

1. Is seeding wanted, or download-only for good? (It changes the firewall, privacy and queue semantics a lot.)
2. Which gates in section 3 are hard limits, and what numbers?
3. Ship the worker in the main Setup/AppImage/image, or as an optional download the user triggers from Options?
4. Docker: publish torrent ports in the example compose file, or require the user to add them?
5. The repository names no licence ("Private use"): is linking an MIT or BSD engine acceptable as it stands, and is a
   separate GPL process acceptable if aria2 or a client ends up in the picture?
6. Should T6 (external client account) come first because it is cheap, and the native client wait for its result?
