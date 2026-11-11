# A native BitTorrent client in Inbound: research and plan

Research only, 2026-11-11, against the shipped 0.6.7 tree (`main`). No code was changed. Mockups are in
`docs/plans/torrent-mockups/` (open `index.html`). External claims carry a source number from the list at the
end. Where no source exists, the text says **unsourced estimate** or **opinion**.

## Executive summary

Today the app has no peer-to-peer code. A magnet or `.torrent` goes only to a provider account (TorBox). The provider
fetches the files and the app downloads them over HTTPS like any other job. Without a provider account the classifier
refuses torrents with "This app has no provider for torrents, and can't download them by itself."
(`src/DownloadManager.Runtime/Remote/RemoteInputClassifier.cs`).

A built-in client would serve four groups the provider route can't: people with no provider account, people who want
to seed or keep a ratio (private trackers), content the provider can't or won't fetch, and Docker users who want one
box for everything. It also costs the most of anything the app has done so far. It adds an open inbound port, the
user's IP address in every swarm, a second long-running engine with its own timers, a new job shape (a download that
doesn't end when its bytes arrive), changes to the protocol and web UI, a firewall rule that differs from the web UI's,
and a legal and store-policy exposure the extension must stay clear of.

Of the engines, **MonoTorrent** (MIT, pure C#, works on any .NET platform including arm64) fits the codebase best.
It has the weakest proof of maturity: one main maintainer, the last stable release was 3.0.2 in August 2024, and the
2026 work exists only as prereleases [1][2][3]. **libtorrent-rasterbar** (BSD-3, 2.1.2 released 2026-09-25) is the
reference engine with full protocol coverage [4][5], but it has no maintained official .NET binding. The best
third-party one (`csdl`, Apache-2.0) is a 27.5 MB multi-platform package with a 2.0 prerelease [6]. **qBittorrent-nox**
as a sidecar is complete and current (5.3.0 on 2026-10-19), but its static Linux binary alone is 69 MiB [7][8]. That
would roughly double the Docker payload the app adds and the AppImage size, and it is GPL [9]. **aria2** is tiny
(2.4 MiB) but its last release was 2023-11-15, and its manual mentions neither uTP nor BitTorrent v2 [10][11].

**Recommendation in one line:** don't put BitTorrent into the Host or the Engine. If the owner wants it, build it as
an optional, off-by-default **torrent worker process** on MonoTorrent. The Host starts the worker only while torrents
are on and at least one torrent exists, so it costs nothing at idle. Before committing, run a two-week spike that
must pass measured idle-cost and throughput gates. If MonoTorrent fails them, keep the same worker shape and switch
the engine to libtorrent. Connecting to a client the user already runs (qBittorrent or Transmission) is a cheaper,
separate option for later, not the main path.

---

## 1. Does a built-in BitTorrent client make sense here?

### What exists today (from the repository)

- **Input.** `RemoteInputClassifier` recognises `magnet:` (40-character hex or 32-character base32 info hash, via
  `InfoHashOfMagnet`), local `.torrent` paths (network paths are refused before the file is opened, because opening
  one would send Windows credentials), provider folder links, hoster links, and ordinary links. A magnet or torrent is
  classified only if a provider with `RemoteCapabilities.Torrents` can take it (`ForTorrents`). Otherwise it is
  `Unsupported`.
- **`.torrent` parsing.** `Runtime/Remote/TorrentMetadata.cs` is a strict, bounded bencode reader. Files are capped at
  16 MiB (`MaxFileBytes`). The info hash is taken over the exact bytes, padding files are dropped, v2-only file trees
  are noticed, and refusals never quote the file because it may hold a private tracker's passkey. A native client can
  reuse this as its first gate.
- **Flow.** The desktop window (`MainWindow.AddFlows.cs`, `AddLinksDialog.cs`, `Remote/AddLines.cs`) and the web UI
  (`webui/src/screens/AddLinks.svelte`, `AddUrl.svelte`, `lib/addlines.ts`) send the input to `remote.classify`, then
  `remote.add`. The web UI and remote windows upload `.torrent` files through `POST /api/upload/torrent`
  (`Server/TorrentUploads.cs`), which hands the Host a reference. `RemoteJobCoordinator` submits the item, waits for
  the provider to cache it, lets the user choose files (owner requirement TB.F9: file choice comes first), then
  creates ordinary local HTTP jobs grouped as a package. Archive extraction (A4) and package windows apply to those
  jobs as to any other.
- **Extension.** The browser extension's source (`extensions/chrome/src`) contains no torrent or magnet handling.
  That should stay true (see section 3).

### Who a native client is for, and what the provider route already covers

| Need | Provider route today | Native client |
| --- | --- | --- |
| Get a public torrent's files fast, with no local peers | Yes. The app's own traffic is HTTPS to the provider only; the app opens no peer connections (repository fact). | Yes, at swarm speed |
| No provider account, or not wanting one | No (refused) | Yes |
| Keep your IP address out of swarms | Yes, for the app's own traffic (same repository fact). How the provider itself behaves is not covered here. | No. Every peer sees the address you connect from. That is how the protocol works: peers connect to each other directly [12]. |
| Seeding, ratio, private trackers | No local seeding | Yes. Private torrents need DHT and PEX off; libtorrent and MonoTorrent both handle the private flag (BEP 27) [1][13]. |
| LAN or offline sharing (Local Service Discovery, local peers) | No | Yes (LSD is on by default in libtorrent [13]) |
| Very large libraries (hundreds of torrents) | Bound by the account | Possible, but every torrent costs memory and timers (section 4) |
| Content the provider won't or can't fetch | No | Yes |

**Opinion.** Two groups gain the most: users without a provider, and seeders. Both are real but secondary for a
product whose pitch is HTTP acceleration, browser capture and a provider bridge. For the owner's own priorities
(idle cost, small Docker image, one codebase), "no torrent client" is a defensible answer too. The plan below is built
so that building it costs nothing when it is off.

### Compared with launching or driving an external client

- **Hand off only** (open the magnet with the system's default handler): almost free to build, no new surface, but no
  progress in the app and nothing in Docker. A reasonable stopgap that could ship on its own (opinion).
- **Connect to a client the user already runs** (not bundled): qBittorrent's WebUI API has `torrents/add` and an
  incremental `sync/maindata` with a response id [14]. Transmission 4.1 speaks JSON-RPC 2.0 over HTTP POST with a
  session-id header against CSRF and a host whitelist on by default [15]. aria2 has JSON-RPC, including over a
  WebSocket, off by default on port 6800 [11]. The app would be a remote control, much like D9 already does for
  another Inbound server. It adds little size and no swarm code, but the user must install and run the other client.
- **Bundle one as a managed sidecar:** the app ships and supervises the other client. That means the full GPL
  obligations for the binary (see section 2), a second settings model to keep in sync, a second web server to secure
  (Transmission's RPC once allowed remote code execution through DNS rebinding, CVE-2018-5702 [16]), and the size
  cost in the table below.

**Recommendation:** build (embed) in an optional worker process, behind a spike. Offer "connect to my qBittorrent or
Transmission" later as a separate, small feature for Docker users who already run one. Don't bundle a GPL client as
the main path.

## 2. Ways to build it for .NET 10 on Windows, Linux and Docker

The repository names no license. `Directory.Build.props` says `Copyright: Private use`, and the Dockerfile leaves out
the license label for that reason, while the images are published publicly on GHCR. That matters for what can be
linked in:

- MIT or BSD code can be linked in-process with a notice.
- LGPL is already handled for 7-Zip (`docs/extraction.md`).
- Linking GPL code into the app's own process would make the combined program fall under the GPL. Shipping a GPL
  program as a separate executable next to the app, talking over sockets, is normally "mere aggregation" [17]. The
  FAQ says the line between the two is a legal question, and shipping the binary still brings source-offer duties.

| | **MonoTorrent** | **libtorrent-rasterbar** through a binding | **aria2** sidecar | **qBittorrent-nox** sidecar | **Transmission daemon** sidecar |
| --- | --- | --- | --- | --- | --- |
| License | MIT [3] | BSD-3 (plus small zlib, public-domain and BSD parts) [18]. `csdl` binding Apache-2.0 [6] | GPLv2+ (separate process) [10] | GPLv2/GPLv3 files [9] | GPLv2 or v3, OpenSSL linking allowed [19] |
| Latest release | 3.0.2 stable (2024-08-04); 3.9.0-alpha prerelease (2026-06-14); commits through 2026-07-31 [2][20] | 2.1.2 and 2.0.15 (2026-09-25); 2.1.0 in July 2026 [4][5] | 1.37.0 (2023-11-15) [10] | 5.3.0 (2026-10-19), with libtorrent 2.1.2 [7] | 4.1.4 (2026-10-24) [21] |
| Maintenance (opinion from the dates) | Active but slow; one main maintainer | Very active; the reference engine | Dormant | Very active | Active |
| DHT, PEX, encryption | Yes, yes, yes (MSE) [1] | Yes, yes, yes (on by default) [13] | Yes, yes, yes (arc4 obfuscation) [11] | Yes (libtorrent) | Yes |
| uTP (BEP 29) | Not in the README's feature list [1]. A uTP connection factory exists on `master` [22], but whether stable 3.0.2 has it is **not verified** | Yes | Not mentioned in the manual [11] | Yes | Yes |
| Magnet metadata (BEP 9) | Yes [1] | Yes | Yes [11] | Yes | Yes |
| Web seeds | BEP 19 [1] | BEP 19 (2.1 dropped BEP 17) [5] | Uses HTTP/FTP mirrors alongside the swarm [11] | Yes | Yes |
| IPv6 | Yes [1] | Yes (listens on `[::]:6881` by default) [13] | DHT6 option [11] | Yes | Yes |
| BitTorrent v2 and hybrid | Yes, since 3.0 [23] | Yes, since 2.0 [24] | Not mentioned [11] | Yes | **not verified** |
| Sequential, selective files | Yes, yes [1] | Yes, yes | `--select-file` (neighbouring files may still be fetched) [11] | Yes, yes | Yes, yes |
| UPnP / NAT-PMP | Both (Mono.Nat) [1][2] | Both, on by default [13] | No (**not verified**) | Yes | Yes |
| Size added | nupkg 3.9 MiB for all targets together [20]. **Unsourced estimate:** 1 to 2 MB of DLLs in one publish | Python wheel for win_amd64 4.8 MiB compressed [4]. `csdl.Native` nupkg 27.5 MB across 6 platforms [6]. **Unsourced estimate:** 5 to 9 MB per runtime | 2.4 MiB zip (Windows x64) [25] | 69.1 MiB (x86_64), 66.5 MiB (aarch64) static builds [8] | 22.4 MiB MSI with the Qt GUI [21]; daemon alone **not measured** |
| Native AOT / trimming / self-contained | Managed code, so self-contained works. Trimming and AOT compatibility is **not verified** (the docs say nothing) | P/Invoke; works with AOT. One native library per runtime, which must also be packed into the AppImage and the image | Separate exe; no effect | Separate exe | Separate exe |
| arm64 | Free (managed) | `csdl.Native`: x64 and arm64 [6] | Official builds are Windows and Android only; on Linux use the distro package [25] | aarch64 static build [8] | Distro packages |
| Fit with Engine, queues, limits | Same language and same `async` model. The limiter, scheduler hooks and file pickers stay in C# | Same, through the binding. Alerts are polled or pushed from native threads | Second engine with its own queue and limits. Its HTTP side overlaps the app's own engine | Its own queues, limits, categories and web server, all to be mirrored | Same as qBittorrent |

**Notes.**

- **PeerSharp** (MIT, net10.0, claims uTP, v2/hybrid, MSE and DHT) appeared in 2026. It is at 5.4.0 after about
  twelve releases in eight months, has about 3.2K downloads in total, and one owner [26]. Too young to bet on (opinion).
  Worth re-checking at the spike.
- libtorrent 2.1 turns on **WebTorrent** by default. Its own notes say it "significantly widens the attack surface";
  it can be turned off [5].
- With qBittorrent or Transmission, their full desktop experience (RSS, search plugins and so on) would sit beside the
  app's, not inside it.

### How each fits today's engine

The Engine's contract, `IDownloadEngine.ExecuteAsync(job, checkpoint, progress, ct)` (`Core/Interfaces.cs`), runs one
job until it ends. A torrent doesn't fit that shape: it is one swarm session over many files, it keeps going after the
bytes arrive (seeding), it can need upload limits, and it has its own resume data. So whichever engine is chosen, a
torrent should **not** become another `IDownloadEngine`. It belongs beside the engines as a session service, much as
`RemoteJobCoordinator` sits beside them for providers. Concretely:

- **Packages.** A torrent maps naturally onto the existing package model (TB.F9 file choice, `PackageWindow`, archive
  extraction after completion). Its files are one package. Unlike a provider package, the files belong to one session
  and are not separate HTTP jobs.
- **Checkpoints and resume.** Both engines keep their own fast-resume data. The Host stores it as an opaque blob in
  the data folder, written with the existing atomic-file helper. The engine's own piece hashes replace the app's
  segment checkpoints.
- **Speed limits.** `SharedSpeedBudget` and `TransferRateLimiter` work within one process. A worker needs a share of
  the global budget, which the Host would hand it, for example re-split each second only while both sides are
  active. Upload limits are new.
- **Part files.** The app writes into a destination-local part file and renames it in place. Torrent engines write
  into the final layout. Either keep them separate, or give MonoTorrent a custom piece writer
  (`MonoTorrent.PieceWriter` exists as a project [27]).

## 3. What changes, what could break, and what new risks appear

### Architecture (what would have to change)

| Area | Change |
| --- | --- |
| Core / job model | A new job kind "torrent" with an info hash, file priorities, seeding state and ratio. `DownloadState` has no state for "complete and still uploading". Adding `Seeding` touches every client. Whether older desktop windows and web UIs tolerate an unknown state value needs checking (`Contracts`, `webui/src/lib`). The safer route is an extra field and leaving `Completed` as it is. |
| Runtime | A `TorrentCoordinator` beside `RemoteJobCoordinator`. The classifier gets a third answer, "native torrent", used when the provider is absent or the user picks it. Choosing between "via TorBox" and "on this PC" becomes a user decision in the Add dialog. |
| Host (must stay small) | The Host supervises a worker process (start, stop, restart with backoff, kill on exit). It never loads the torrent engine. When torrents are off, nothing new is loaded or scheduled. |
| IPC protocol and web UI | New commands (`torrent.add`, `torrent.files`, `torrent.peers`, `torrent.trackers`, `torrent.setPriority`, `torrent.limits`) and events. A `PlatformCapabilities` flag in the hello, so clients show the Torrents category only when the Host has it. `docs/ipc-protocol.md` and the golden frames baseline (`HostGoldenFramesTests`) change. Peer lists are noisy, so send them only while a details window asks for them. |
| Extension | No change. The extension must not catch magnets or offer "send torrent". A `.torrent` file caught as an ordinary file download stays an ordinary download. |
| Queues and scheduler | A torrent occupies a queue slot while downloading. Seeding should not hold a slot. "When the queue finishes" actions (shutdown and others) must count a torrent as done when its download finishes, not when seeding ends. Quotas should count received bytes; whether uploaded bytes count is a decision for the owner. |
| Extraction (A4) | Running after the download is fine, since extraction only reads. "Delete archives after extraction" would delete files that are being seeded, so it must be blocked or must stop seeding first. |
| Docker | Publish one TCP and one UDP port (for example `6881:6881/tcp` and `6881:6881/udp`) in `compose.example.yml`, plus a setting for the port the outside world sees. UPnP from inside a bridge network usually can't reach the router (opinion, **unsourced**), so default it to off in the image. |
| Windows Firewall | The existing flow (`Host/WindowsFirewallRules.cs`, `Desktop.WinUI/Remote/WebUiFirewall.cs`) adds a **TCP** rule for the bridge program, **local subnet only**, with one UAC prompt. A torrent rule is different: TCP **and** UDP, the worker exe, **any** remote address. It must be a separate rule with its own explanation, never a widened web UI rule. Without the rule, torrents still work through outgoing connections, with fewer peers (opinion). |
| Linux desktop (AppImage) | No firewall management. Show the port and a plain hint. UPnP and NAT-PMP work as on Windows. |
| Installer and update | One more exe and its libraries, plus Restart Manager coverage for the worker. Uninstall removes the firewall rule. |

### What could get worse for current users

With the plan below (off by default, separate process, capability-gated UI): almost nothing, provided the spike holds.
The real risks:

- Protocol and snapshot changes break an older web UI or remote window. Mitigate with the capability flag and
  additive fields only.
- A shared speed budget that misbehaves when HTTP and torrent jobs run together.
- A larger Setup, AppImage and image for everyone, even users who never turn torrents on. About 1 to 2 MB for
  MonoTorrent (estimate); 69 MiB for a bundled qBittorrent-nox [8].
- The TorBox route quietly changes behaviour if the classifier's default flips. Keep "via provider" as the default
  whenever an account exists.

### New security and privacy surface

| Risk | Facts and mitigations |
| --- | --- |
| Open inbound port | libtorrent listens on 6881 TCP and UDP by default and turns UPnP and NAT-PMP on, which opens the router port automatically [13]. Off by default; when on, show the port and whether UPnP is used. |
| IP address shown to peers | Peers connect to each other directly [12]. Say so plainly on the Options page (mockup d). |
| DHT and LSD | DHT nodes keep a routing table and refresh buckets that haven't changed in 15 minutes [28], so the process takes part in a public network for as long as it runs. LSD announces on the LAN every 300 s by default in libtorrent [13]. Turn both off for private torrents. |
| Malicious metadata | `TorrentMetadata` already bounds `.torrent` files (16 MiB, strict). Metadata fetched from peers for a magnet (BEP 9) must go through the same reader and limits before any file is made. |
| Path traversal in file lists | Real precedents: CVE-2010-0012 (Transmission, `..` in a torrent path) and CVE-2007-1799 (KTorrent) [29][30]. Reuse the archive extractor's traversal-safe path code, and also refuse Windows reserved names (`CON`, `NUL`), `:` (alternate data streams), trailing dots and spaces, and over-long paths. |
| Bomb-like torrents | A tiny `.torrent` can claim terabytes or a million files. Check free space with `IFreeSpaceProvider` against the selected size before starting. Cap the file count (for example 10,000, with a confirmation above it). Use sparse files rather than full preallocation. |
| Disk filling | Selected size plus a margin must fit. Pause on `DiskFull`; the `FailureKind` already exists. |
| A sidecar's web server | If qBittorrent or Transmission is ever bundled: loopback only, random credentials, and keep the host whitelist on. CVE-2018-5702 shows what DNS rebinding did to Transmission's RPC [16]. |
| WebTorrent (libtorrent 2.1) | Off. Its own release notes call it a much wider attack surface [5]. |

### Legal and store policy (facts first, then opinion)

- **Chrome Web Store:** "Do not encourage, facilitate, or enable the unauthorized access, download, or streaming of
  copyrighted content or media" (Malicious and Prohibited Products). An extension must also have a single, narrow
  purpose [31]. In 2013 Google removed torrent *search* extensions under that wording [32].
- **Edge Add-ons:** section 2.8 has the same clause. Section 1.1.1 requires a single purpose. Section 1.2.3 allows
  depending on other software if the description says so [33].
- **GitHub:** the Acceptable Use Policies forbid content that infringes others' rights [34]. Client software itself
  is hosted there: qBittorrent and Transmission develop and release on GitHub [7][21].
- **Opinion:** keep the extension and both store listings exactly as they are. No torrent wording, no magnet capture,
  no "send to torrent client" menu item. The extension's single purpose stays "send browser downloads to the app".
  The torrent feature lives only in the desktop app and the server and is described neutrally (legal content, Linux
  ISOs), with no search or indexer integration.

## 4. Size and performance

Baselines from the repository's own measurements:

- Setup is 65,298,545 bytes (`artifacts/installer/DownloadManagerSetup-0.6.7.exe`).
- The idle Host makes 12 context switches per second and uses 0.17% of one core (PERF.4 in `ROADMAP.md`).
- The Docker server idles at 31 to 54 MiB, and the server plus web UI add 3.1 MB to the image (`docs/docker.md`).

| Item | MonoTorrent worker | libtorrent worker | qBittorrent-nox sidecar | aria2 sidecar |
| --- | --- | --- | --- | --- |
| Binary size | 1 to 2 MB of DLLs (estimate; 3.9 MiB nupkg [20]) plus a small worker exe. In Setup, about +1 MB compressed (estimate) | 5 to 9 MB per runtime (estimate from [4][6]) | 69 MiB (x86_64) / 66.5 MiB (aarch64) [8] | 2.4 MiB [25] |
| Idle memory, worker not running | 0 | 0 | 0 if not started | 0 if not started |
| Idle memory, worker running, no transfers | About 20 to 35 MiB. **Unsourced estimate**, based on this repository's own measurement of a fresh .NET server at 28.6 to 37.8 MiB in Docker | Similar process floor plus the native heap. libtorrent's own worked example is about 4 MB of cache for one download, and 32 bytes per peer-list entry (about 128 kB per torrent at the default 4000) [35] | Not measured. Forum reports for Transmission range from 6 to 70 MB, anecdotal [36] | Not measured |
| Timers with 0 torrents | `ClientEngine` schedules a 500 ms timer when it is built and keeps it until disposal. With nothing running it fires and returns [22]. So the worker must exit, not just idle | `tick_interval` 500 ms [13] | Its own | Its own |
| Timers with N seeding torrents | 500 ms tick, plus DHT (bucket refresh every 15 min [28]), plus incoming DHT queries and peer traffic. The rate of those depends on the swarm and was not measured | Same tick; DHT announce every 900 s; LSD every 300 s [13] | Same (libtorrent inside) | Its own |
| Disk I/O | Hash checks read whole pieces. A full recheck reads every byte. Seeding reads on demand | 2.1 defaults to `pread`/`pwritev` and hashes on separate threads; mmap is still available [5]. Up to 100 MiB of queued disk writes by default [13] | Same as libtorrent | Its own |

**Wake-ups (opinion with arithmetic).** A 500 ms tick adds at least 2 wake-ups per second, on top of the Host's
measured 12. The UDP sockets for DHT and uTP wake on every incoming packet. On a public IPv4 address that is
plausibly dozens to hundreds of packets a minute once the node is known (**unsourced**). That is why the engine must
not live in the Host, and why the worker exits when no torrent is active or seeding.

**SSD wear (opinion).** Torrent writes are random in piece order, but each byte is written once, except after a
hash failure. Full preallocation writes the whole file once more as zeros, so use sparse allocation. Seeding is
read-only.

**Docker 100 MB budget (estimate).** Server 34 to 54 MiB, plus a worker of 20 to 35 MiB, plus a capped cache of 16 to
32 MiB, gives 70 to 120 MiB. It fits only if the cache is capped. An in-process engine would save the second runtime
(about 20 MiB, estimate), but then the server can't stay small when torrents are off unless the assembly is loaded
only on demand. The worker keeps it simple and isolates crashes. The spike decides.

**Startup.** The worker starts on demand. A .NET 10 ReadyToRun console app starts in a fraction of a second
(**unsourced estimate**: 100 to 300 ms). Resume data loads per torrent.

## 5. Phased plan (nothing changes for anyone who never turns torrents on)

Principles: off by default; a separate optional process; a kill switch in Options and an environment variable
(`DOWNLOADMANAGER_TORRENTS=off` forces it off even if the setting is on); zero processes, timers or sockets when off;
UI gated by a capability flag.

| Phase | Content | Effort (one developer, opinion) | Exit gate |
| --- | --- | --- | --- |
| **0. Spike** | Console worker on MonoTorrent 3.0.2, and the 3.9 alpha for uTP. One public Linux ISO torrent: download, seed ratio 1.0, stop. Measure with the PERF.4 method (30 minutes, 10 s sampling): wake-ups, CPU, I/O, memory at 0 torrents (process alive) and with 1 and 20 seeding. Throughput against qBittorrent on the same torrent. Trimming and AOT build check. Repeat on Linux x64 and arm64 in Docker. | 1.5 to 2 weeks | No worse than about 5 wake-ups/s and 0.1% CPU with 20 seeding torrents. At least 80% of qBittorrent's throughput on a well-seeded torrent. Path-safety test passes. If any gate fails, re-run the spike with libtorrent through `csdl` (+1 week). |
| **1. Smallest useful slice** | Desktop on Windows: Options > Torrents (off by default), Add Torrent dialog (magnet or file, file tree, destination, start paused), a Torrents category, download to completion, **seeding off** (ratio 0). No firewall rule; outgoing connections only. Worker supervised by the Host, then exits when idle. Capability flag in the hello. | 4 to 5 weeks | Integration test against a local seeder; golden frames updated; idle Host unchanged when off (PERF.4 re-run). |
| **2. Seeding and control** | Ratio and time limits, upload limit, shared global budget, queues and scheduler semantics, details window (Files, Peers, Trackers, Pieces & Speed), firewall rule with UAC, UPnP/NAT-PMP toggle, extraction guard while seeding. | 3 to 4 weeks | Scheduler and quota tests, firewall rule tests (as for the web UI's), a 24-hour seeding soak with memory flat. |
| **3. Web UI and Docker** | Torrents in the web UI (including phone width), uploads through the existing `/api/upload/torrent`, compose ports, Docker defaults (UPnP off), arm64 check. | 2 to 3 weeks | `npm run screens` walk at 1360 and 390 px, vitest, container idle measurement against the 100 MB target. |
| **4. Linux desktop** | AppImage packaging of the worker, port hint, Linux tests. | 1 week | AppImage run on the owner's KDE/Wayland machine. |
| **Optional later** | "Connect to my qBittorrent or Transmission" as a provider-like account. | 2 to 3 weeks | Against both APIs in Docker. |

Total for phases 0 to 4: about **12 to 15 weeks** (opinion).

**Risks.**

1. MonoTorrent's thin release cadence and unclear uTP status in stable.
2. Protocol changes reaching older clients.
3. The firewall and port story confusing users.
4. Store review linking the extension to torrents if wording slips.
5. Seeding semantics breaking "when queue finishes" actions.

**Where tests would live.**

- New `tests/DownloadManager.Torrents.Tests` (xunit): path safety, metadata limits, priority mapping, resume blobs,
  limit splitting.
- `tests/DownloadManager.Integration.Tests`: Host plus worker against a local seeder and tracker on loopback, built
  with the same library and hosted from `tools/TestServer`. Golden-frame update. Kill-switch test (no process, no
  socket when off).
- `tests/DownloadManager.Core.Tests`: the classifier's third answer.
- `tests/DownloadManager.Server.Tests`: commands over the WebSocket, upload, capability flag.
- `webui` vitest and the `npm run screens` walk.
- The extension suite gets one guard test: the manifest and source contain no `magnet`/torrent handling.

## 6. Mockups

`docs/plans/torrent-mockups/index.html` links:

- a. the main window with a Torrents category
- b. Add Torrent
- c. Torrent Details (Overview, Files, Peers, Trackers, Pieces & Speed)
- d. Options > Torrents
- e. the web UI at phone width

They are static, dark, and use the colours and layout of the main-window mockup in the development repository's
`docs/plans/qt-parity/mockup-main.html`.

## What I could not verify

- Whether MonoTorrent 3.0.2 (stable) includes uTP. The README doesn't list it; `master` has a uTP connection factory
  (2026).
- MonoTorrent's and `csdl`'s compatibility with trimming and Native AOT.
- The real publish size of MonoTorrent's DLLs and of one runtime's libtorrent native library. The numbers given are
  estimates from package sizes.
- Idle CPU, wake-ups and memory of any engine on this app's machines. No measured numbers are published by the
  libraries. Phase 0 exists to measure them.
- Transmission's BitTorrent v2 status; aria2's UPnP status.
- Whether older desktop and web UI builds tolerate a new `DownloadState` value.
- Whether UPnP works from Docker's default bridge network.
- Anything about how a provider or a tracker limits users. Deliberately not covered.

## Sources (all accessed 2026-11-11)

1. MonoTorrent README: https://github.com/alanmcgovern/monotorrent
2. MonoTorrent on NuGet (versions, dependencies): https://www.nuget.org/packages/MonoTorrent
3. MonoTorrent license (MIT): https://raw.githubusercontent.com/alanmcgovern/monotorrent/master/LICENSE
4. libtorrent releases (GitHub API, asset sizes): https://api.github.com/repos/arvidn/libtorrent/releases and https://github.com/arvidn/libtorrent/releases
5. libtorrent 2.1.0 release notes: https://github.com/arvidn/libtorrent/releases/tag/v2.1.0
6. csdl.Native on NuGet: https://www.nuget.org/packages/csdl.Native and https://github.com/aspriddell/csdl
7. qBittorrent news (5.3.0): https://www.qbittorrent.org/news
8. qbittorrent-nox-static release-5.3.0_v2.1.2 (GitHub API): https://api.github.com/repos/userdocs/qbittorrent-nox-static/releases/latest
9. qBittorrent repository (GPL files): https://github.com/qbittorrent/qBittorrent
10. aria2 releases: https://github.com/aria2/aria2/releases
11. aria2c manual: https://aria2.github.io/manual/en/html/aria2c.html
12. BitTorrent protocol specification (BEP 3): https://www.bittorrent.org/beps/bep_0003.html
13. libtorrent settings reference: https://www.libtorrent.org/reference-Settings.html
14. qBittorrent WebUI API (5.0): https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-5.0)
15. Transmission RPC spec: https://github.com/transmission/transmission/blob/main/docs/rpc-spec.md
16. CVE-2018-5702, oss-security: https://openwall.com/lists/oss-security/2018/01/11/1
17. GNU GPL FAQ, mere aggregation: https://www.gnu.org/licenses/gpl-faq.html#MereAggregation
18. libtorrent LICENSE: https://raw.githubusercontent.com/arvidn/libtorrent/RC_2_0/LICENSE
19. Transmission COPYING: https://raw.githubusercontent.com/transmission/transmission/main/COPYING
20. MonoTorrent releases and commits: https://api.github.com/repos/alanmcgovern/monotorrent/releases and https://github.com/alanmcgovern/monotorrent/commits/master
21. Transmission releases (GitHub API): https://api.github.com/repos/transmission/transmission/releases/latest
22. MonoTorrent `ClientEngine.cs` (500 ms tick, uTP factory): https://raw.githubusercontent.com/alanmcgovern/monotorrent/master/src/MonoTorrent.Client/MonoTorrent.Client/ClientEngine.cs
23. MonoTorrent 3.0.0 release notes (v2 and hybrid): https://www.nuget.org/packages/MonoTorrent/3.0.0
24. libtorrent blog, BitTorrent v2: https://blog.libtorrent.org/2020/09/bittorrent-v2/
25. aria2 1.37.0 assets (GitHub API): https://api.github.com/repos/aria2/aria2/releases/tags/release-1.37.0
26. PeerSharp on NuGet: https://www.nuget.org/packages/PeerSharp/3.0.0
27. MonoTorrent source tree: https://github.com/alanmcgovern/monotorrent/tree/master/src
28. BEP 5 (DHT): https://www.bittorrent.org/beps/bep_0005.html
29. CVE-2010-0012 (Transmission path traversal): https://cyber.vumetric.com/vulns/CVE-2010-0012/path-traversal-vulnerability-in-multiple-products
30. CVE-2007-1799 (KTorrent): https://nvd.nist.gov/vuln/detail/CVE-2007-1799
31. Chrome Web Store program policies: https://developer.chrome.com/docs/webstore/program-policies/policies
32. TorrentFreak, 2013-12-11: https://torrentfreak.com/google-bans-torrent-search-extensions-for-chrome-over-piracy-concerns-131211/
33. Microsoft Edge Add-ons developer policies (updated 2026-07-24): https://learn.microsoft.com/en-us/legal/microsoft-edge/extensions/developer-policies
34. GitHub Acceptable Use Policies: https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies
35. libtorrent tuning: https://www.libtorrent.org/tuning.html
36. Transmission forum, memory reports (anecdotal): https://forum.transmissionbt.com/viewtopic.php?p=72460

Repository sources: `src/DownloadManager.Runtime/Remote/RemoteInputClassifier.cs`, `TorrentMetadata.cs`,
`RemoteJobCoordinator.cs`, `src/DownloadManager.Server/TorrentUploads.cs`, `src/DownloadManager.Core/Interfaces.cs`,
`Models.cs`, `src/DownloadManager.Host/WindowsFirewallRules.cs`, `Dockerfile`, `docker/compose.example.yml`,
`docs/docker.md` (measured idle), `ROADMAP.md` (PERF.4, D1 to D9, TB.F9), `Directory.Build.props`.
