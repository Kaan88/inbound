# Torrent worker protocol (version 1; T0, extended in T1, T2 and T3)

The Host starts `inbound-torrent` only while a torrent is active and talks to it over stdin and stdout. stderr is a
plain-text log. Plan and gates: `docs/plans/native-torrent-roadmap.md`.

## Frames

Each message is a 4-byte little-endian length followed by that many bytes of UTF-8 JSON (one object). Both sides refuse
a frame above 32 MiB and close the stream. Binary values (`.torrent` files, resume blobs) are standard base64.

## Lifecycle

- The worker sends `hello` first: `{"event":"hello","protocol":1,"worker":"0.1.0-t0","libtorrent":"2.1.2.0"}`.
- It starts quiet: no listen socket, no DHT, LSD, UPnP or NAT-PMP until `setListen`.
- With no torrent it sleeps on a condition variable: no timer runs. With torrents it wakes for commands, libtorrent's
  alert notification, the progress interval and a 5-minute resume checkpoint.
- Closing stdin (or the `shutdown` command) pauses everything, sends a last `resumeBlob` per torrent (up to 5 s),
  writes the DHT state when `--dht-state <file>` was given, sends `bye` and exits with code 0. If stdout breaks it does
  the same. So the worker never outlives the Host.
- `--version` prints the versions; `INBOUND_TORRENT_TEST_HOOKS=1` enables `debugAbort` (tests only).

## Commands

Every command is an object with `cmd` and an optional `seq` (a number). Each gets exactly one reply:
`{"event":"reply","seq":7,"ok":true}` or `{"event":"reply","seq":7,"ok":false,"message":"unknown torrent"}`.
Torrents are named by the Host's `id` (a string). A whole-number field given a value outside the 64-bit signed range
(or a fraction that is) is refused with `<field> is out of range`; a fraction inside it is cut to its whole part.

| cmd | Fields | Notes |
| --- | --- | --- |
| `add` | `id`, `savePath`, one of `resume` / `torrent` (base64) / `magnet`; optional `filePriorities` (0 to 7 per file), `paused`, `seed`, `sequential` | Not auto-managed (the Host queues). A duplicate is an error. Metadata over 16 MiB, more than 100,000 files, or a path that would leave `savePath` is refused. |
| `add` (T3 fields) | optional `downloadLimit`, `uploadLimit` (bytes/s, 0 or absent = none), `recheck` | The torrent's own limits, as `setTorrentLimits`, within the session's. `recheck` checks the files on disk once the torrent is added (when it has its metadata): libtorrent first checks the resume data, and the full check follows when that is done. The reply then carries `recheckQueued: true` (a `0.3.0-t2` worker ignored the field and said nothing). A torrent added `finalized` (with its metadata) starts in upload mode until its first check is done, so a file damaged or gone since is never downloaded into: when the check finds a wanted piece missing it stays paused, is not reported `completed`, and an `error` event follows as for `recheck`. A seed added `finalized` and not `paused` is auto-managed while `setPeerLimits` `activeSeeds` is above 0, so libtorrent's seeding queue decides which seeds run (state `queued` for the ones held back). With PEX off (`setListen` `pex: false`) the torrent is added with PEX disabled. |
| `add` (T1 fields) | optional `partSuffix` (a dot, then at most 63 letters, digits, `.`, `-`, `_`), `rootName` (one safe name), `hold`, `finalized` | `partSuffix` is added to every file name while it downloads; `rootName` replaces the torrent's top-level name (a single file's name, or a multi-file torrent's folder). The Host sends the same two with every add of a torrent, a resume included. `hold` keeps the torrent in upload mode after its metadata (no piece is requested, nothing is written) until `setFilePriorities`; an add without `hold` clears upload mode, even when the resume data was saved while held. Every file gets its part name unless the resume data already names it; what is on disk never decides, so a user's file of the same name is never taken for a finished one. `finalized` (the Host finalized this torrent earlier) gives every file its own name instead, whatever the resume data holds, and `finalize` then does nothing. |
| `finalize` | `id`, optional `rootName` | Renames every wanted file from its part name to its own name. `rootName` replaces the one given on add (the Host picks the final top-level name at the end, when it knows which names are taken). If any target is already on disk, nothing is renamed and the reply is `ok:false`, `already there: <path>`; the old `rootName` stays. Otherwise the reply comes when libtorrent has renamed them all (`ok:false` with the first failure). A second `finalize` replies at once. |
| `pause`, `resume` | `id` | `pause` is graceful and asks for a resume blob, and takes the torrent out of the seeding queue. |
| `recheck` (T3) | `id` | Checks the files on disk again (libtorrent's `force_recheck`), while downloading, paused or seeding; the progress rows show `checking` (also for a queued seed waiting for its check). A finished torrent (files renamed by `finalize`) is checked in upload mode, so nothing is downloaded into its files: when every wanted piece is good it goes on as before; otherwise it is paused, no longer counts as completed, and an `error` event (`kind` `torrent`, `the file check found damaged or missing pieces`) follows. Refused with `the torrent has no file list yet` before the metadata. |
| `announce` (T3) | `id` | Asks every tracker for peers now, ignoring their minimum interval, and the DHT when it is on. |
| `sequential` (T3) | `id`, `on` | Downloads pieces in order (on) or rarest first (off). |
| `details` (T3) | `id` | Reply carries `infoHashV1`, `infoHashV2` (empty when the torrent has none), `private`, `pieces`, `pieceLength`, `wasted` (redundant plus failed bytes), `failedBytes`, `hashFailures` (pieces that failed their check in this process), `connections`, `connectionsLimit`, `downloadLimit`, `uploadLimit` (0 = none), `sequential`, `pex`, `distributedCopies` (-1 when libtorrent has no piece picker, as for a seed), `nextAnnounce` (seconds; 0 = due now, -1 while paused), `addedAt` (Unix seconds), `activeSeconds`, `dhtNodes` (-1 with DHT off). |
| `setPeerLimits` (T3) | optional `connections` (session, 0 = libtorrent's default), `connectionsPerTorrent` (0 = none), `activeSeeds` (0 = all seeds run) | Up to 100,000 connections and 10,000 active seeds, else `limit out of range`. Applies to running torrents too. |
| `setProxy` (T3) | `type` (`none`, `http`, `socks4`, `socks5`), `host`, `port` (1 to 65535), optional `username`, `password` | Peers, trackers and host-name lookups go through the proxy. With SOCKS4, libtorrent looks tracker host names up on this PC and does not send the user name as the SOCKS4 user ID (it passes host names only to SOCKS5 and HTTP proxies); the Host therefore never sends `socks4`. An HTTP or SOCKS4 proxy carries no UDP, so DHT, LSD and uTP stay off while one is set (`setListen` asks for them in vain). Send it before `setListen` so nothing connects around it. Only the type is logged; credentials are never logged. Other types are refused with `type must be none, http, socks4 or socks5`. |
| `remove` | `id`, optional `deleteFiles` | |
| `setFilePriorities` | `id`, `priorities` | |
| `setLimits` | `download`, `upload` (bytes/s, 0 = none), optional `connections` | Session-wide. libtorrent exempts peers on the local network from these by default. `connections` above 100,000 is refused with `limit out of range`. |
| `setTorrentLimits` | `id`, `download`, `upload` | |
| `setSeedPolicy` | `ratio`, `seconds` (0 = no limit) | Checked on progress; a torrent at its limit is paused with state `seedLimitReached`. |
| `setListen` | `port` (0 = any), optional `interfaces` (addresses; IPv6 in brackets; default all IPv4 and IPv6), `dht` (default true), `lsd`, `upnp`, `natpmp` (default false), `encryption` (`enabled`, `forced`, `disabled`), `pex` (T3, default true) | `pex` turns peer exchange on or off for every torrent, running ones included. |
| `setProgressInterval` | `ms` (100 to 60000) | Default 1000. The Host uses 5000 while no window shows torrents. |
| `addPeer` | `id`, `address`, `port` | Kept and connected again after a file check. |
| `saveResume` | | Asks every torrent for a blob now. |
| `stats` | | Reply carries `listenPort`, `torrents` and from T3 `dhtNodes` (the last count libtorrent reported, added up over its DHT nodes, one per listen address; 0 before the first count and again after `setListen`; -1 with DHT off) and `connectionsLimit` (connections in all, as in force). |
| `files` | `id` | Reply carries `done`: bytes of each file on disk so far (whole checked pieces), by file index, pad files included; empty before the metadata. A worker older than this answers `unknown command`. |
| `seed` (T2) | `id`, `on` (default true) | Keeps the torrent seeding once its download is done (as `seed` on add), or stops it: a finished torrent that was paused for lack of seeding goes on with `on`, and one that seeds is paused gracefully (with a resume blob) without it. |
| `peers` (T2) | `id` | Reply carries `peers`: at most 500 connected peers, each `address`, `port`, `client`, `flags` (any of `seed`, `incoming` or `outgoing`, `encrypted`, `utp`, `interested`, `peerInterested`, `choked`, `chokedByPeer`, `snubbed`), `downRate`, `upRate` (bytes/s), `downloaded`, `uploaded` (bytes this connection), `progress` (0 to 1). |
| `trackers` (T2) | `id` | Reply carries `trackers`: `url`, `tier`, `status` (`notContacted`, `updating`, `working`, `error`), `message` (the tracker's or the error's, else empty), `seeds`, `peers` (from the last scrape; -1 when not known), and from T3 `nextAnnounce` (seconds to its next announce; -1 when none is due). |
| `pieces` (T2) | `id` | Reply carries `count`, `pieceLength`, `have`, and either `ranges` (`[first, last]` pairs, inclusive, of pieces the torrent has; at most 4096 of them) or, past that, `bitmap` (base64, one bit per piece, piece 0 in the highest bit of the first byte). `count` is 0 before the metadata. |
| `shutdown` | | As closing stdin. |

A worker older than a command answers `unknown command`; the Host then does without it (T2: the details queries; T3:
`announce`, `details` and `sequential` answer the client that the torrent program is too old (`recheck` is in `0.3.0-t2`; its `add` field is not, so the job keeps `recheckPending`), `setPeerLimits`
is only logged, and a set proxy keeps the worker from starting, so nothing connects around it).
Worker versions: `0.2.x-t1` (T1), `0.3.0-t2` (adds `seed`, `peers`, `trackers`, `pieces`, and `ratio`/`seedingSeconds` in
progress rows), `0.4.0-t3` (adds `announce`, `sequential`, `details`, `setPeerLimits`, `setProxy`,
`add` `downloadLimit`/`uploadLimit`/`recheck` (with `recheckQueued` in the reply), a finished torrent's `recheck` in upload mode, `setListen` `pex`, `trackers` `nextAnnounce`, `stats` `dhtNodes` and `connectionsLimit`, and the
`queued` state).

## Events

| event | Fields |
| --- | --- |
| `metadata` | `id`, `name`, `totalSize`, `pieceLength`, `files` (`index`, `path`, `size`, `pad`), `held` when held. `path` is the name the file has once it is done (with `rootName` and the Windows device rename, without `partSuffix`). |
| `progress` | `torrents`: rows of `id`, `state`, `done`, `wanted`, `downRate`, `upRate`, `peers`, `seeds`, `uploaded`, `downloaded`, `progress`, and from T2 `ratio` (all-time upload over the wanted size) and `seedingSeconds`; only torrents that changed |
| `state` | `id`, `state` (`paused`, `seedLimitReached`) |
| `completed` | `id`: the download finished (never "seeding finished"). Without `seed` the torrent is then paused. |
| `resumeBlob` | `id`, `data` (base64 libtorrent resume data, with the info dictionary so a magnet needs no swarm after a restart) |
| `stats` | `listenPort`, `mappedPort`, or `incoming: true` (once per process, the first peer that connected in; from T2) |
| `error` | optional `id`, `kind` (`torrent`, `file`, `listen`, `metadata`, `protocol`), `message` (the system's words, which may be in the system's language), optional `reason` on `torrent` and `file` errors: `diskFull`, `permission` (also a read-only drive) or `pathTooLong`. Tracker failures are not reported. A worker older than `reason` leaves it out. |
| `bye` | last message before a clean exit |

States in progress rows: `checking`, `metadata`, `held` (has its metadata, waits for `setFilePriorities`), `downloading`, `seeding`, `paused`, `queued` (T3: a seed the seeding queue holds back), `error`.
