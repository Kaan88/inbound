# BitTorrent engine: libtorrent or MonoTorrent, and in what shape

Research only, 2026-11-11. No product code was changed. This answers three questions from the owner and checks
`native-torrent-research.md` and `native-torrent-roadmap.md`. Every external claim has a source number `[n]` from the
list at the end, all accessed on 2026-11-11. **Opinion** marks my judgement and **unverified** marks a claim I could not
confirm. Where I say "measured", I opened the published package myself and read the file sizes or ELF headers
(scratch work, nothing kept in the repo).

## Executive summary

1. **"Isn't libtorrent the mature one?"** Yes, by a wide margin. libtorrent has 10+ years of point releases, runs
   inside qBittorrent and Deluge, is fuzzed continuously in OSS-Fuzz, and covers every protocol feature on the list.
   MonoTorrent's last stable release is 3.0.2 (2024-08-04). It has **no uTP in any release**: uTP was merged into
   `master` on 2026-07-26, is off by default, and its maintainer says it is not yet tuned. It has no IPv6 DHT and no
   fuzzing. Its million NuGet downloads come mostly from the *arr apps, which only use it to read `.torrent` files.
2. **"Is libtorrent not a good idea for C#?"** Using it *through a .NET binding* is a poor idea today. The only living
   bindings are one-person projects (`csdl`, and the anonymous `TorrentSharp.Wrap`). Both pin libtorrent 2.0.11 from
   January 2025, and they add 11 MB (Windows) to 17 MB (Linux, unstripped) per runtime. A self-written C shim works,
   but you then own a native build anyway, plus the interop layer.
3. **"Can it be a side module?"** Yes, and that is libtorrent's best fit: a **small native C++ worker executable**
   (shape a) that speaks the roadmap's contract over stdio. It needs no .NET binding, has the lowest memory floor, and
   isolates crashes. It ships as one second binary per platform. Size estimate: 5 to 11 MB uncompressed, 3 to 5 MB
   compressed (**opinion**, extrapolated from measured libraries).

**Recommendation.** Make the libtorrent 2.1 native worker (shape a) the **preferred** engine. Keep the worker
architecture and contract exactly as the roadmap has them. Change the T0 bake-off in three ways:
- Drop "libtorrent through a .NET binding" and aria2.
- Keep MonoTorrent only as a cheap baseline, built from a pinned `master` commit so uTP can be measured. The 3.9 alpha
  the roadmap names does not contain uTP.
- Flip the decision rule. Take libtorrent unless it fails the size gate or the owner rejects owning a C++ build on
  three targets. Take MonoTorrent only in that case.

The reason for the flip (**opinion**): MonoTorrent's weaknesses are security, interoperability over time, an
untuned uTP and a single maintainer with a stalled stable release. A two-week spike cannot show any of them, so
passing the spike does not prove much.
libtorrent's weaknesses (C++ toolchain, CI, binary size) are exactly what a spike *can* show.

---

## 1. Maturity: libtorrent-rasterbar vs MonoTorrent

| Factor | libtorrent-rasterbar | MonoTorrent |
| --- | --- | --- |
| Age | GitHub repo created 2015-06-03; the project is older (pre-GitHub start date **unverified**); 13,093 commits by Arvid Norberg [1][2] | Repo created 2010-10-02; first NuGet 0.9.0 on 2011-06-10 [3][4] |
| Latest releases | **2.1.2 and 2.0.15 on 2026-09-25**; 2.1.1/2.0.14 on 2026-08-10; 2.1.0 on 2026-07-09; 2.0.13 on 2026-06-08; 2.0.12 on 2026-03-13 [1] | **3.0.2 (stable) 2024-08-04**; 3.0.3 betas to 2024-09-14; 3.9.0-alpha-0000 on 2026-06-14; nothing since [3][4] |
| Cadence | Two branches (2.0 and 2.1), point releases every 1 to 3 months in 2026. **But no release at all between 2025-01-28 (2.0.11) and 2026-03-13 (2.0.12)**, a 13.5-month gap [1] | Stable releases: 2.0.0 (2021-05), 2.0.7 (2022-12), 3.0.0 to 3.0.2 (2024-07/08). Commits: 81 in Aug/Sep 2024, then about one per month until April 2026, then 108 in Apr to Jul 2026, then **none since 2026-07-31** [3]. Asked for a release in June 2026, the maintainer said a stable would come "ideally before august" [5]. None has shipped. |
| Contributors, bus factor | Commits since 2024-11-11: arvidn 834, then M-Hassan-Raza 72, xavier2k6 34, isl-Ramzi 24, glassez 12, Chocobo1 10 (both qBittorrent developers) [6]. Effectively one maintainer (about 80%), but with a large downstream that contributes fixes (**opinion**) | Commits since 2024-11-11: alanmcgovern 111 of 119; nobody else has more than 3 [6]. All-time: 3,657 of about 3,800 [2]. Bus factor 1 |
| Who ships it as a download engine | qBittorrent 5.3.0 (2026-10-19; its default build is now on 2.1.x, and a 1.2.x build is still offered) [7]; Deluge, Free Download Manager, Folx (libtorrent's own list, which is old) [8]; Alpine's package is required by qbittorrent, btfs and the Python binding [9] | GitHub "used by" lists Sonarr, Radarr, Prowlarr, Lidarr, Readarr and rdt-client [10]. Sonarr and rdt-client reference it only in torrent-file reading code; a search finds no `ClientEngine` there [11]. **KKManager** (game-mod updater) does use `ClientEngine` [11]. Other dependents unverified. 1.0M total downloads, 263,660 of them for 3.0.2 [4] |
| Security track record | 5 CVEs on NVD, all old: CVE-2008-0646, CVE-2009-1760 (path traversal), CVE-2016-5301, CVE-2016-7164, CVE-2017-9847 (crashes and over-reads) [12]. No GitHub advisories [13]. **In OSS-Fuzz** (libFuzzer, AFL, honggfuzz) [14]; 62 fuzz targets in `fuzzers/src` on RC_2_1 [15]. 2.1.0 added fuzz targets for its WebRTC parsers [16]. Memory-unsafe language | No CVEs found on NVD and no GitHub advisories [12][13]. **No fuzzing**: the source tree has no fuzz targets [17]. Managed code, so no buffer-overflow class of bugs, but its network parsers have had no adversarial testing (**opinion**) |
| DHT | Yes, IPv4 and IPv6 | Yes, IPv4. **IPv6 DHT not implemented**: the IPv6 tracking issue #588 is still open, and its final step was done "except for IPv6 DHT support" [18] |
| PEX, MSE/PE, magnet (BEP 9), web seeds | Yes, yes, yes, BEP 19 (BEP 17 removed in 2.1) [16] | Yes, yes (MSE), yes, BEP 19 [19] |
| uTP (BEP 29) | Yes, on by default (long-standing; exact first version **unverified**) | **Not in 3.0.2 or in 3.9.0-alpha** (no uTP files in either tag) [20]. Merged to `master` on 2026-07-26 (PR #755: +8,674 lines in 51 files) [21]. Off by default: `AllowedTransports` defaults to `Tcp` only [22]. Maintainer: "definitely *not* saturating my connection" with uTP, and tuning for real-world packet loss will be "one of the biggest challenges" [23]. In the same thread he estimated 3 to 6 months to make it stable and fast [23]. |
| IPv6 | Yes; listens on `0.0.0.0:6881,[::]:6881` by default [24] | TCP and trackers yes [19]; IPv6-only use reported broken (issue #705, open) [25]; no DHT6 [18] |
| BitTorrent v2 and hybrid | Yes, since 2.0 | Yes (BEP 52), since 3.0 [19] |
| Sequential and selective download | Yes | Yes [19] |
| UPnP, NAT-PMP | Yes, both on by default [24] | Yes, through Mono.Nat [19] |
| Proxy | SOCKS4/5, HTTP, I2P (**unverified** in detail) | SOCKS5 was removed; a request to bring it back is open (#704) [26] |
| WebTorrent | Yes in 2.1, **on by default**. Its notes say it "significantly widens the attack surface" [16]. It contacts `stun.l.google.com:19302` by default [24]. Turn it off. | No |
| Published performance evidence | Huge field use; I found **no current published benchmark** from the project. The tuning page is from the 1.x era [27] | No published benchmark. The maintainer reports TCP downloads above 900 Mbps on his 1 Gbit/s link (screenshot in an issue, 2026-06-20) [28] |
| Memory per torrent and connection | Per peer-list entry 32 bytes for IPv4 (the 1.x-era page, which assumes 4,000 entries per torrent) [27]. 2.1 defaults: `max_peerlist_size` 3,000, `connections_limit` 200, `max_queued_disk_bytes` 100 MiB, `send_buffer_watermark` 500 KiB [24]. No current per-connection figure is published. | `DiskCacheBytes` defaults to 5 MiB [22]. Nothing else published. **No number for either beyond these**: T0 must measure them. |
| Disk I/O model | 2.0: memory-mapped files by default; a `posix_disk_io` backend also exists [29]. 2.1: new `pread_disk_io` (pwritev/preadv and the Windows equivalents) **is the default**. The 2.1 release notes call the move to mmap a "misstep" and say pread suits spinning disks, network mounts and FUSE better, while mmap is "probably more efficient" on local NVMe [16]. The Windows memory complaints about 2.0 are long-running, anecdotal forum reports [30], and qBittorrent kept offering 1.2-based builds through 5.3.0 [7]. **Use 2.1 with pread; never ship 2.0's mmap on Windows (opinion).** | Ordinary file streams through `IPieceWriter`/`DiskWriter`, with a 5 MiB memory cache [22][17]. Sparse files on NTFS [19]. Hashing is incremental per block, which saves rereads [19] |
| Idle behaviour | Session `tick_interval` 500 ms; DHT announce every 15 min; LSD every 5 min; UPnP and NAT-PMP on [24]. No published wake-up figures. | The engine schedules a 500 ms `LogicTick` for as long as it exists [31]; DHT bucket refresh. No published figures. |

**What the table means (opinion).** On both engines, the idle cost while a torrent is active is one 500 ms tick plus
UDP traffic. The roadmap's rule that the worker *exits* when nothing is active is what makes the feature cost nothing
when off, whichever engine is used. Neither engine is "idle-friendly" by itself. The real maturity gap is in uTP, IPv6
DHT, adversarial testing and release discipline, and every one of these favours libtorrent.

## 2. Using libtorrent from this app

| Option | Alive? | Facts | Verdict |
| --- | --- | --- | --- |
| **`csdl` + `csdl.Native`** (Apache-2.0) | Barely | One maintainer (341 of 346 commits), 11 stars [32]. Last stable 1.2.1 (2024-09-21); 2.0.0-beta / 2.0.0-pre on 2026-05-10 [4]. **Measured: `csdl.Native` 2.0.0-pre bundles libtorrent 2.0.11** (Jan 2025) with OpenSSL 3.6.2 linked in [33]. It is four point releases behind 2.0.x and not on 2.1. Runtimes: win-x64/arm64, linux-x64/arm64, osx, android [33]. Built with CMake + vcpkg on GitHub Actions; Linux built on Ubuntu 22.04 [34]. Uses `LibraryImport`, which suits AOT in principle; no `IsAotCompatible` flag [35]. **Measured sizes**: win-x64 11.06 MB (torrent-rasterbar.dll 4.78 + libcrypto 5.33 + libssl 0.87 + shim); linux-x64 17.26 MB unstripped (about 13.5 MB stripped); linux-arm64 16.18 MB. Needs glibc 2.34 and GLIBCXX 3.4.30 [33]. | **No.** Stale libtorrent, a single maintainer, and over the 10 MB Docker gate as shipped. Useful as a reference for how to build a shim. |
| **`TorrentSharp.Wrap` + `.Native`** (Apache-2.0) | New, provenance unclear | 14 versions between 2026-09-07 and 2026-10-14; the nuspec has no repository URL and an anonymous author [4][36]. **Measured**: also libtorrent 2.0.11 + OpenSSL 3.6.2, one fat library per runtime (`libtsw.so` 17.0 MB on linux-x64, `tsw.dll` 9.25 MB on win-x64). Its shim symbols look like a variant of csdl's (**unverified**) [33]. | **No.** Cannot audit or rely on it. |
| `LibtorrentSharp` (Apache-2.0) | No | One prerelease 0.1.0-alpha on 2026-05-01; repository created and last pushed the same day, 1 star [4][37] | No |
| Ragnar, LibtorrentDotNet, libtorrent4tsunami, LibtorrentRTWrapper | Dead | Ragnar last 0.6.0 in 2014, repo gone; LibtorrentDotNet (C++/CLI) last 1.1.0 in 2024-12, repo gone; libtorrent4tsunami targets libtorrent 1.x [4][37] | No |
| libtorrent's in-tree C binding (`bindings/c`) | Maintained only incidentally | Tiny: session create/close, add/remove torrent, pop alerts, settings, status. It uses **C varargs tag lists**, which P/Invoke cannot call portably (**opinion**). It has no resume-data or file-priority calls in its header [38]. | No, but proof that a C shim is small. |
| SWIG | Only for Java | frostwire-jlibtorrent (SWIG for Java) is alive: 2.0.15.3 on 2026-10-31 [39]. No maintained SWIG C# project found. | Possible, but SWIG output for the full API is huge; no. |
| **Own thin C shim + `LibraryImport`** | Our own | About 25 to 40 C functions shaped around the contract. Everything else as in shape (b) below. | Feasible, but it carries both burdens: a native build *and* an interop layer. Worse than shape (a) for a separate-process design (**opinion**). |
| C++/CLI | Windows only | Mixed-mode assemblies run on Windows only, so this rules out Linux, the AppImage and Docker | **Out.** |
| **No .NET in the engine: native C++ worker** | Our own | Written directly against libtorrent's C++ API; speaks the contract over stdio | **Yes.** See shape (a). |

**Build and CI burden of any native route.** The repository has no C++ toolchain today. CI is one GitHub Actions job
that builds the Docker image for `linux/amd64` on version tags; the AppImage is built on the owner's Forgejo runner,
for x86_64 only; Setup is built locally, and the `.iss` file contains no signing step [40].

A native worker would need:
- **Windows x64:** MSVC with CMake and vcpkg. vcpkg's libtorrent port is at 2.1.2, maintained by Arvid himself, and
  was updated one day after the release [41]. Its dependencies are about 14 mostly header-only Boost ports plus OpenSSL
  [41].
- **Linux x64 and arm64:** GCC built on an old enough base, so the binary runs on the AppImage's target distros.
  GitHub's arm64 Linux runners are free for public repositories, and this repository is public [42][40]. Other options
  are cross-compiling, or a musl static build in the style of qbittorrent-nox-static.
- **Docker:** a builder stage for the worker. The runtime image is Ubuntu 24.04 and already contains `libssl3t64`,
  because .NET needs it [43], so the Docker worker can link OpenSSL dynamically. Only amd64 is published today [44].
  arm64 Docker (D1) and arm64 AppImage builds don't exist yet for anything, so torrents on arm64 means building those
  pipelines first.
- **Pinning:** the libtorrent version, its SHA-256 and the vcpkg baseline, the same way the Dockerfile pins 7-Zip [44].

The cost is CI time and one more thing that can break on a toolchain update. A first libtorrent build takes minutes per
target without a cache (**unverified**; vcpkg binary caching makes repeat builds fast).

**Antivirus and signing.** Setup is unsigned today [40], so the new exe is no worse off than the existing ones.
Microsoft Defender's PUA list includes "Torrent software (Enterprise only)" [45], so on managed corporate PCs Defender
may flag whatever exe does BitTorrent, whichever engine is inside it. A separate worker exe keeps any flag on that file
rather than on the Host (**opinion**). Whether a new native exe draws more heuristic false positives than managed DLLs
is **unverified**.

## 3. The side module: three shapes

All three use the roadmap's architecture unchanged. The Host is the only state holder, starts the worker on demand,
and kills it when idle. The contract runs over stdio, and the Host keeps the resume blobs. The Host-side work is the
same for every shape: supervisor, contract library, fake engine, safety checks and tests, about 1.5 to 2 weeks
(**opinion**). It is not counted below.

### (a) Native C++ worker against libtorrent 2.1 (preferred)

- **Size of the work (opinion):** about 1,500 to 2,500 lines of C++. That is the framed-JSON stdio loop (Boost.JSON or
  a single-header library), settings mapping, commands, alert-to-event translation, resume handling and argument
  checks, plus about 200 lines of CMake/vcpkg and about 200 lines of CI YAML. **3 to 4 weeks** for the worker, plus
  **1 to 2 weeks** the first time for three-target builds and packaging. Ongoing: a version bump per libtorrent
  release (hours), and toolchain breakage now and then.
- **Resume data:**
  - The worker calls `save_resume_data(save_info_dict)` on a timer while something changed, and on pause, completion
    and shutdown.
  - It turns each `save_resume_data_alert` into a `resumeBlob` event with `write_resume_data_buf`.
  - On `add`, the Host passes the blob back and the worker calls `read_resume_data`.
  - With `save_info_dict`, the blob also carries the metadata fetched for a magnet, so a restarted worker doesn't need
    the swarm to get it again (API names from libtorrent's reference; behaviour per the docs, not tested here).
  - Fast resume skips the full recheck when the files on disk match the blob.
- **Settings and alerts to the contract:**
  - `setListen`, `setLimits` and `setSeedPolicy` become `settings_pack` changes (`listen_interfaces`, `enable_dht`,
    `enable_upnp`/`enable_natpmp`, rate limits, `share_ratio_limit`/`seed_time_limit`).
  - Per-torrent limits and file priorities become `torrent_handle` calls.
  - Events: `metadata_received_alert` becomes `metadata`; `state_update_alert` (from `post_torrent_updates()`, sent
    only at the throttled rate) becomes `progress`; `state_changed_alert` and `torrent_paused_alert` become `state`;
    `torrent_finished_alert` becomes `completed`; `file_error_alert`, `torrent_error_alert` and `listen_failed_alert`
    become `error`; `listen_succeeded_alert`, `portmap_alert` and `dht_stats_alert` become `stats`.
  - `set_alert_notify` wakes the loop, so the worker never polls.
  - Turn `webtorrent` off at build time (no STUN contact, smaller attack surface) [16][24]. Use `pread_disk_io`, which
    is the 2.1 default [16].
- **Linking and size:**
  - Measured anchors: Alpine's stripped shared libtorrent 2.1.0 (`-O2 -flto`, WebTorrent off) is 5.2 MiB on x86_64
    and 4.7 MiB on aarch64, with OpenSSL linked dynamically [9][46]. Alpine's OpenSSL 3.5 is 5.0 MiB (libcrypto) +
    0.8 MiB (libssl) [47]. The libtorrent 2.1.2 Python module for win_amd64 is 13.19 MB uncompressed and 5.03 MB
    zipped; it includes Boost.Python and OpenSSL 3.6.1 [48]. The linux x86_64 module is 25.9 MB unstripped.
  - Estimates for the worker (**opinion**): Linux static with OpenSSL, libstdc++ and `--gc-sections`, about 8 to
    11 MB, and 3.5 to 5 MB compressed in the AppImage. Docker, dynamic against the image's libssl3, about 5.5 to 7 MB.
    Windows x64 static with `/MT` and only the OpenSSL parts libtorrent uses, about 7 to 10 MB, and 3 to 4.5 MB after
    Setup's LZMA2.
  - These fit the 10 MB gate as the roadmap words it (added to Setup, the update zip, the AppImage and the image), but
    only just in the worst case. T0 must measure them.
  - For comparison, the static qBittorrent-nox is 72.5 MB (69.1 MiB) on x86_64 and 69.7 MB on aarch64, including Qt
    [49]. libtorrent is a small part of that.
- **Runtime dependencies:**
  - Windows: none if built with the static CRT and static OpenSSL. The wheel's `.pyd` needs `MSVCP140.dll` and
    `VCRUNTIME140.dll` [48]; avoid that with `/MT`, which I believe vcpkg's `x64-windows-static` triplet supports
    (**unverified**).
  - Linux: glibc only, as long as libstdc++ is linked statically.
  - Boost is header-only for what libtorrent needs (**unverified** in detail).
- **What ships:** one `inbound-torrent` binary per platform, next to the Host. In Setup and the update zip:
  `torrent-worker.exe`. In the AppImage: `usr/lib/inbound/inbound-torrent`. In Docker: `/app/inbound-torrent` plus
  nothing else.
- **Isolation:** a crash (segfault) or leak takes down only the worker. The Host sees the pipe close, restarts it once,
  re-adds the torrents with their blobs, and loses at most one checkpoint interval (gate 9). A leak can be capped by the
  Host restarting the worker above a memory limit. The isolation buys *availability*, not security: the worker runs as
  the same user. A memory-safety bug exploited by a peer has the user's rights. Sandboxing the worker (a
  low-integrity token on Windows, a separate UID in Docker) is a later hardening step (**opinion**).
- **Idle:** absent when off. While active, one native process: no GC, no JIT, and a lower memory floor than a .NET
  process (**unverified**: no published figure; T0 measures it).

### (b) C# worker that P/Invokes libtorrent (binding or own shim)

- **Size of the work (opinion):**
  - With csdl: 1 to 2 weeks, if its API covers resume blobs, per-torrent limits and file priorities. Its README shows
    file priorities, start, stop and status; resume coverage is **unverified** [32]. You would be tied to libtorrent
    2.0.11 and the mmap disk backend, or would have to fork.
  - With an own shim: about 1,000 to 1,500 lines of C++ shim + 1,000 to 1,500 lines of C# interop + about 800 lines of
    worker, so **5 to 7 weeks**, and the same native CI as (a).
- **Resume and alerts:** same libtorrent mechanics as (a), marshalled across the boundary. Alerts are popped on a
  native thread and must be copied before the next pop. That is a known source of use-after-free bugs in bindings
  (**opinion**).
- **Size:** the native library as measured above (csdl: 11.06 MB on win-x64, 17.26 MB on linux-x64 unstripped) plus
  the worker's managed DLLs. The worker should be published into the existing app folder so it reuses the bundled .NET
  runtime, as the AppImage already does for the Host, CLI, NativeHost and Server [50]. A separate self-contained copy
  would add tens of MB.
- **AOT/trimming:** possible with `LibraryImport`, but not needed if the worker reuses the bundled runtime.
- **Isolation:** the same process boundary as (a), but a native crash takes the .NET runtime with it. The managed layer
  adds a GC heap and JIT, and gains nothing (**opinion**).
- **Verdict:** dominated by (a). It has every native cost, plus interop and a .NET process floor.

### MonoTorrent as a C# worker (baseline)

- **Size of the work (opinion):** about 800 to 1,200 lines of C#, **1 to 1.5 weeks**. There is no native build at all.
- **Resume data:** `TorrentManager.SaveFastResumeAsync()` returns a `FastResume`, and `FastResume.Encode()` gives
  the bytes. `FastResume.TryLoad(stream)` followed by `LoadFastResumeAsync(...)` restores it [51]. Turn
  `AutoSaveLoadFastResume`, `AutoSaveLoadMagnetLinkMetadata` and `AutoSaveLoadDhtCache` off, so the worker stays
  stateless, and hand the Host the bytes [22]. Metadata fetched for a magnet must be kept separately by the Host;
  fast resume does not include it (**unverified**).
- **Settings and events:**
  - `EngineSettings`/`TorrentSettings` are immutable records since June 2026 [3]. Changing them means
    `UpdateSettingsAsync`, so `setLimits` and `setListen` become a settings swap.
  - `SetFilePriorityAsync` maps to file priorities [51].
  - Events come from `TorrentStateChanged`, `PieceHashed` and polled `Monitor` stats. MonoTorrent has no alert queue,
    so progress events need the worker's own throttled timer.
- **uTP:** only on `master` after 2026-07-26, opt-in through `AllowedTransports` [21][22]. So the worker would ship a
  build from `master` that is not on NuGet, or go without uTP. The maintainer's own report from issue #754: without
  uTP, a client could not finish low-peer torrents that qBittorrent finished over uTP, apparently because peers that
  prefer uTP dropped it [23].
- **Size:** measured 1.23 MB of DLLs (3.9.0-alpha, net8.0, 11 assemblies) plus Mono.Nat and ReusableTasks (small,
  not measured), about 1.4 MB in all [52]. It reuses the bundled runtime. Comfortably inside the gate on every
  artefact.
- **Isolation:** the same process boundary. Managed code turns most bugs into exceptions rather than memory
  corruption, which is a real advantage (**opinion**). But the network parsers have never been fuzzed [17].
- **Idle:** the 500 ms tick lives as long as the `ClientEngine` [31], so the worker must exit, as planned. On top of
  that comes the floor of a .NET process. The repository measured 28.6 to 37.8 MiB for the server in Docker, a
  different program, so this is an **unsourced estimate** for the worker.

### Shapes side by side

| | (a) native libtorrent worker | (b) C# + libtorrent | MonoTorrent C# worker |
| --- | --- | --- | --- |
| Engine maturity | Highest | Highest (if current) / 2.0.11 via csdl | Lowest of the three |
| Worker code (opinion) | 1.5k to 2.5k C++ | 2k to 3.5k C++ and C# | 0.8k to 1.2k C# |
| Effort (opinion) | 4 to 6 weeks | 5 to 7 weeks (own shim) | 1 to 1.5 weeks |
| New toolchains | MSVC, GCC, vcpkg, CMake, arm64 build | same | none |
| Added size per artefact | 5 to 11 MB raw, 3 to 5 MB compressed (estimate) | 11 to 17 MB raw per runtime (measured, csdl) | about 1.4 MB (measured) |
| Process floor | lowest (native) | .NET + native | .NET |
| uTP, IPv6 DHT | yes, yes | yes, yes | master only / no |
| Crash isolation | process | process | process |
| Licence | BSD-3 + OpenSSL Apache-2.0 + Boost BSL (notices) [41] | same + Apache-2.0 binding | MIT |

## 4. What changes in the roadmap

**Corrections to the earlier documents.**
- "uTP in stable not verified": it is now verified that **no release has uTP**, including 3.9.0-alpha. The roadmap's
  suggestion to use "the 3.9 alpha for uTP" is wrong. uTP needs a build from `master` at or after the merge of PR #755
  (2026-07-26) [20][21].
- "`csdl` ... with a 2.0 prerelease": the "2.0" is csdl's own version number. It **bundles libtorrent 2.0.11**, not
  2.x current and not 2.1 [33]. The package is 30.2 MB, not 27.5 MB [33].
- "libtorrent through a binding, 5 to 9 MB per runtime (estimate)": the measured figure is 11.06 MB on Windows x64 and
  17.26 MB on Linux x64 unstripped [33]. That breaks the Docker gate unless the build is stripped and OpenSSL is linked
  dynamically.
- "MonoTorrent IPv6: yes": partly. DHT has no IPv6 [18], and IPv6-only use is reported broken [25]. SOCKS5 was removed
  [26].
- "libtorrent: very active": true for 2026, but the 13.5-month release gap in 2025-26 was missed [1].
- "MonoTorrent 1 to 2 MB": confirmed by measurement (about 1.4 MB) [52].

**Where the earlier work was too thin or biased (candidly).**
- It treated libtorrent only as "libtorrent *through a .NET binding*" and never considered a native worker, although
  its own architecture (a separate process with an engine-neutral contract) is exactly what makes a native worker
  cheap. That framing made libtorrent look harder than it is.
- It weighed MonoTorrent's popularity without checking what the dependents do with it. The largest only read
  `.torrent` files.
- It took "uTP factory exists on master" as a hopeful sign without reading the issue thread. The thread shows uTP is
  new, AI-assisted (the maintainer used Codex [23]), opt-in and untuned.
- It had no security comparison (fuzzing, CVEs) for a process that faces the open internet.

**Gate and phase changes I propose.**

| Item | Change |
| --- | --- |
| T0 candidates | **B′ = native libtorrent 2.1.x worker (shape a)** and **A = MonoTorrent worker built from a pinned `master` commit** (run gate 4 with uTP on and off, and record both). Remove "libtorrent through a .NET binding" and aria2 (last release 2023-11-15 [53]). |
| Decision rule | Take B′ if it passes rows 1 to 3, 6, 9 and 10 and the new row 12. Take A only if B′ fails size (6) or build (12), or the owner decides not to own C++. If neither passes rows 1 to 3 and 6, stop (unchanged). |
| New gate 12: build | Worker builds reproducibly from a pinned libtorrent tag and SHA-256 and a vcpkg baseline, in CI, for win-x64, linux-x64 and linux-arm64. The binary runs on the oldest distro the AppImage claims. |
| New gate 13: patch path | Time from a libtorrent point release to an Inbound build containing it: at most one working day of effort. |
| New gate 14: crash containment | Inject a crash (kill -9 and an abort inside the engine) during download and seeding. The Host restarts once, and gate 9 holds. |
| Gate 6 wording | Say whether "10 MB" means compressed (Setup, zip, AppImage) or as `docker images` shows it (the image). The native worker passes the first comfortably and the second only with dynamic OpenSSL in Docker (estimate). |
| Gate 10 | Add: WebTorrent off (no STUN traffic), `pread_disk_io` in use, LSD and UPnP defaults per platform. |
| T0 effort | 1.5 to 2 weeks becomes **2.5 to 3 weeks** (first native build for three targets). |
| T1 | +about 1 week to package a native binary into Setup, the update zip, the AppImage and the Dockerfile (a builder stage with pinned sources). |
| T3 / arm64 | Torrents on arm64 depend on arm64 Docker and AppImage builds that do not exist yet (D1 is still amd64-only [44]). Plan that work explicitly. It costs nothing extra for MonoTorrent and a native build for libtorrent. |
| Unchanged | The contract, the lifecycle (absent when idle), the kill switch, Host-side path and size checks, and keeping the extension untouched. |

## 5. What I could not verify

1. **The real size of a minimal static libtorrent worker per platform.** The figures are extrapolated from Alpine's
   shared library, the PyPI wheels and csdl's binaries. Only a T0 build settles them, and gate 6 depends on them.
2. **Idle wake-ups, CPU and memory of either engine while seeding.** Neither project publishes them, and the 1.x
   tuning page is outdated.
3. **MonoTorrent's uTP throughput and interoperability** beyond the maintainer's own comments, and whether a stable
   release is coming. The promised "before August" passed.
4. Whether libtorrent 2.1's `pread_disk_io` ends the Windows memory complaints of 2.0. There are only release notes and
   anecdotes.
5. csdl's API coverage of resume blobs and limits; who uses csdl; who is behind TorrentSharp.Wrap.
6. Antivirus false-positive rates for a new native exe versus managed DLLs.
7. Trimming and AOT compatibility of MonoTorrent and csdl (neither declares it). It is not needed for the plan.
8. libtorrent compile times on GitHub's runners, and whether the static-CRT vcpkg build works without patches.
9. Which other GitHub dependents (Lampac, Totoro, Gelato) use MonoTorrent as an engine. The search API rate-limited me.

## Sources (all accessed 2026-11-11)

1. libtorrent releases (GitHub API): https://api.github.com/repos/arvidn/libtorrent/releases
2. Contributors (GitHub API): https://api.github.com/repos/arvidn/libtorrent/contributors and https://api.github.com/repos/alanmcgovern/monotorrent/contributors
3. MonoTorrent releases and commits (GitHub API): https://api.github.com/repos/alanmcgovern/monotorrent/releases and https://api.github.com/repos/alanmcgovern/monotorrent/commits
4. NuGet registration and search API (MonoTorrent, csdl, csdl.Native, TorrentSharp.Wrap, LibtorrentSharp, Ragnar, LibtorrentDotNet, PeerSharp): https://api.nuget.org/v3/registration5-gz-semver2/{id}/index.json and https://azuresearch-usnc.nuget.org/query?q=libtorrent
5. MonoTorrent issue #748 "Release current version": https://github.com/alanmcgovern/monotorrent/issues/748
6. Commit authors since 2024-11-11 (GitHub API, commits?since=2024-11-11): https://api.github.com/repos/arvidn/libtorrent/commits and https://api.github.com/repos/alanmcgovern/monotorrent/commits
7. qBittorrent news (5.3.0, libtorrent 1.2.20 / 2.1.2, default now 2.1.x): https://www.qbittorrent.org/news
8. libtorrent "projects using libtorrent" (old list): https://www.libtorrent.org/projects.html
9. Alpine package libtorrent-rasterbar 2.1.0 (size, dependents): https://pkgs.alpinelinux.org/package/edge/community/x86_64/libtorrent-rasterbar and .../aarch64/libtorrent-rasterbar
10. MonoTorrent on NuGet, "Used by": https://www.nuget.org/packages/MonoTorrent
11. GitHub code search for `ClientEngine` and `MonoTorrent` in Sonarr/Sonarr, rogerfar/rdt-client, IllusionMods/KKManager: https://api.github.com/search/code
12. NVD keyword search "libtorrent": https://services.nvd.nist.gov/rest/json/cves/2.0?keywordSearch=libtorrent
13. GitHub security advisories (none): https://api.github.com/repos/arvidn/libtorrent/security-advisories and https://api.github.com/repos/alanmcgovern/monotorrent/security-advisories
14. OSS-Fuzz project: https://github.com/google/oss-fuzz/tree/master/projects/libtorrent
15. libtorrent fuzzers (RC_2_1): https://github.com/arvidn/libtorrent/tree/RC_2_1/fuzzers/src
16. libtorrent 2.1.0 release notes: https://github.com/arvidn/libtorrent/releases/tag/v2.1.0
17. MonoTorrent source tree (master, recursive listing; no fuzz targets): https://api.github.com/repos/alanmcgovern/monotorrent/git/trees/master?recursive=1
18. MonoTorrent issue #588 "Complete IPV6 support": https://github.com/alanmcgovern/monotorrent/issues/588
19. MonoTorrent README (BEPs, features): https://github.com/alanmcgovern/monotorrent
20. Trees for tags `release-v3.0.2` and `release-v3.9.0-alpha-0000` (no uTP files): https://api.github.com/repos/alanmcgovern/monotorrent/git/trees/release-v3.0.2?recursive=1
21. MonoTorrent PR #755 "Implement UtpPacket as per library norms": https://github.com/alanmcgovern/monotorrent/pull/755
22. MonoTorrent `EngineSettings.cs` (master): https://raw.githubusercontent.com/alanmcgovern/monotorrent/master/src/MonoTorrent.Client/MonoTorrent.Client/Settings/EngineSettings.cs
23. MonoTorrent issue #754 "μTP, BEP 29 capabilities": https://github.com/alanmcgovern/monotorrent/issues/754
24. libtorrent `settings_pack.cpp` defaults (RC_2_1): https://github.com/arvidn/libtorrent/blob/RC_2_1/src/settings_pack.cpp
25. MonoTorrent issue #705 "only v6 seem not working": https://github.com/alanmcgovern/monotorrent/issues/705
26. MonoTorrent issue #704 "Can we get SOCKS5 proxy support back?": https://github.com/alanmcgovern/monotorrent/issues/704
27. libtorrent tuning page: https://www.libtorrent.org/tuning.html
28. MonoTorrent issue #706 "Compared to Qbittorrent the download speed is very slow": https://github.com/alanmcgovern/monotorrent/issues/706
29. libtorrent headers `posix_disk_io.hpp`, `mmap_disk_io.hpp` (RC_2_0), `pread_disk_io.hpp` (RC_2_1): https://github.com/arvidn/libtorrent/tree/RC_2_1/include/libtorrent
30. Anecdotal reports on 2.0 memory: https://forum.deluge-torrent.org/viewtopic.php?p=236535 , https://www.truenas.com/community/threads/libtorrent-2-x-used-by-deluge-qbittorrent-introduced-memory-mapped-files-implications-with-zfs-arc.100505/ , https://ryansouthgate.com/fixing-qbitorrent-in-docker-oom/
31. MonoTorrent `ClientEngine.cs` (TickLength 500): https://raw.githubusercontent.com/alanmcgovern/monotorrent/master/src/MonoTorrent.Client/MonoTorrent.Client/ClientEngine.cs
32. csdl repository and README: https://github.com/aspriddell/csdl
33. Packages opened and measured: https://api.nuget.org/v3-flatcontainer/csdl.native/2.0.0-pre/csdl.native.2.0.0-pre.nupkg and https://api.nuget.org/v3-flatcontainer/torrentsharp.wrap.native/4.0.0/torrentsharp.wrap.native.4.0.0.nupkg (file sizes, ELF DT_NEEDED, embedded version strings "libtorrent/2.0.11.0", "OpenSSL 3.6.2")
34. csdl native build workflow and vcpkg manifest: https://github.com/aspriddell/csdl/blob/master/.github/workflows/native-build.yml and https://github.com/aspriddell/csdl/blob/master/vcpkg.json
35. csdl project file: https://github.com/aspriddell/csdl/blob/master/csdl/csdl.csproj
36. TorrentSharp.Wrap 4.0.0 nuspec (inside the package): https://api.nuget.org/v3-flatcontainer/torrentsharp.wrap/4.0.0/torrentsharp.wrap.4.0.0.nupkg
37. Repositories: https://github.com/adammpkins/LibtorrentSharp ; https://github.com/libragnar/ragnar (404) ; https://github.com/ligenq/LibtorrentDotNet (404)
38. libtorrent C binding (RC_2_1): https://github.com/arvidn/libtorrent/tree/RC_2_1/bindings/c
39. frostwire-jlibtorrent releases: https://github.com/frostwire/frostwire-jlibtorrent/releases
40. Repository facts: `.github/workflows/docker.yml`, `scripts/package-appimage.sh`, `installer/DownloadManager.iss` (no signing step); GitHub API repos/Kaan88/inbound (public)
41. vcpkg libtorrent port 2.1.2: https://github.com/microsoft/vcpkg/blob/master/ports/libtorrent/vcpkg.json
42. GitHub changelog, arm64 runners for public repositories GA: https://github.blog/changelog/2025-08-07-arm64-hosted-runners-for-public-repositories-are-now-generally-available/
43. .NET 10 runtime-deps Dockerfile (Ubuntu noble, libssl3t64): https://github.com/dotnet/dotnet-docker/blob/main/src/runtime-deps/10.0/noble/amd64/Dockerfile
44. Repository `Dockerfile` and `docs/docker.md` (Ubuntu 24.04 base, amd64 only, 350 MB image, pinned 7-Zip)
45. Microsoft, "How Microsoft identifies malware and potentially unwanted applications": https://learn.microsoft.com/en-us/defender-xdr/criteria
46. Alpine APKBUILD for libtorrent-rasterbar (`-flto`, `-Dwebtorrent=OFF`) and package contents: https://gitlab.alpinelinux.org/alpine/aports/-/raw/master/community/libtorrent-rasterbar/APKBUILD and https://pkgs.alpinelinux.org/contents?name=libtorrent-rasterbar&repo=community&branch=edge&arch=x86_64
47. Alpine libcrypto3 and libssl3 packages: https://pkgs.alpinelinux.org/package/edge/main/x86_64/libcrypto3 and .../libssl3
48. libtorrent 2.1.2 wheels, opened and measured: https://github.com/arvidn/libtorrent/releases/tag/v2.1.2 (cp312 win_amd64, manylinux x86_64 and aarch64); PyPI https://pypi.org/pypi/libtorrent/json
49. qbittorrent-nox-static release-5.3.0_v2.1.2 assets: https://api.github.com/repos/userdocs/qbittorrent-nox-static/releases/latest
50. Repository `scripts/package-appimage.sh` (all apps published into `usr/lib/inbound`)
51. MonoTorrent `FastResume.cs` and `TorrentManager.cs` (master): https://github.com/alanmcgovern/monotorrent/tree/master/src/MonoTorrent.Client/MonoTorrent.Client
52. MonoTorrent nupkgs opened and measured: https://api.nuget.org/v3-flatcontainer/monotorrent/3.9.0-alpha.unstable.rev0000/monotorrent.3.9.0-alpha.unstable.rev0000.nupkg and .../3.0.2/monotorrent.3.0.2.nupkg
53. aria2 releases: https://github.com/aria2/aria2/releases
