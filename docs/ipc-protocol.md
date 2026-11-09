# IPC protocol

How clients talk to the background Host: the desktop UI, the CLI and the browser's native host. This
page gives the framing rules, the remote provider messages and the archive messages. The full list of message names is
`MessageTypes` in `src/DownloadManager.Contracts/Protocol.cs`, and the payloads are the classes in
`Dtos.cs` and `RemoteDtos.cs` next to it.

## Connection and framing

- A per-user named pipe, `\\.\pipe\DownloadManager-<hash>`. Only the current user and SYSTEM can open it.
- Each frame is a 4-byte little-endian length, then that many bytes of UTF-8 JSON. The limit is 8 MB per frame.
- Each message has `v` (protocol version), `type`, `id`, and on replies `replyTo` with the id of the request, plus `payload`.
- JSON uses camelCase names and leaves out null values. Unknown fields are ignored.
- On connect the Host sends `server.hello` and then `jobs.snapshot`. After that a client gets replies to its own requests and events meant for everyone.
- A second pipe, `\\.\pipe\DownloadManager-<hash>-hello`, with the same ACL, is for clients that want less (PERF.7).
  There the client speaks first: its first frame is `client.hello` with `{ "snapshot": bool, "events": bool, "role":
  string }`, both bools true when left out. `role` is left out (or `desktop`) by the user at this PC; `web` asks for
  the rights of a browser on another device (below). A role the Host doesn't offer closes the connection. The Host then sends `server.hello`, then `jobs.snapshot` only if `snapshot` is true, and the
  broadcasts only if `events` is true; replies always come. Any other first frame closes the connection, and one on
  another protocol version gets `server.hello` and `server.incompatible`. The CLI asks for neither (a command that
  needs the list sends `jobs.query`), and the native host asks for events only. The main pipe is unchanged. A Host
  older than this has no hello pipe, so a client that finds only the main pipe connects there and reads the snapshot
  as before. The desktop UI's windows stay on the main pipe.

The protocol version is **1**. It changes only for a change that existing clients can't ignore. New
abilities arrive as new message types and new optional fields, and clients ignore message types they
don't know. A request with a different version gets `server.incompatible` and the connection closes.

`server.hello` lists capabilities. `remote` means this Host has the provider messages below.

The meaning of every message lives in `HostCommandDispatcher` (`src/DownloadManager.Runtime/Ipc/`), which has no
transport of its own; the desktop Host's named pipe is one transport over it. The capability list is a set: look a
name up, don't read a position. The pipe Host sends `jobs`, `settings`, `handoff`, `tray`, `status` and, with the
provider, `remote`, in that order, and a new name is only ever appended; a Host without a tray leaves `tray` out, so
the names after it sit one place earlier there. After those come, when the session has them, `folders`, `updater`,
`programSettings` (it may set programs the Host runs), `folderPicker` (`host.pickFolder` opens a dialog),
`openUi`, `shutdown` and `completionActions` (a finished download or schedule may open the file, exit or shut the
machine down), `archives` (the `archive.*` messages are answered, and an extractor was there when the session
started: 7-Zip found; one installed later shows on the next connection, and at once in `archive.settings.get`'s
`tool`), and `uploads` (the server takes `POST /api/upload/torrent` and `/api/upload/container`, and the `remote.*`
commands read their references; the Docker server has it, the Windows Host until its web bridge stores uploads does
not). The list is per session: a `web` session on the desktop Host gets fewer. `server.hello` also carries
`hostKind`: `windows` for the desktop Host, `server` for the headless one. A Host on a machine that can't do something answers instead of
acting: `host.openUi` and `host.shutdown` reply `ok: false`, `host.pickFolder` replies with an `error`, and
`update.check`, `update.download`, `update.install` and `update.defer` reply `ok: false` with "Updates are not
available here." A Host whose clients may not set what it runs (a network server) refuses a `settings.update` that
changes `postDownloadCommand`, `ffmpegPath`, `updateManifestUrl`, `updatePublicKey` or
`allowSystemCompletionActions`, and a `job.update` that sets a completion program; sending those values back
unchanged is fine. The desktop Host can do all of these.

### Web sessions on the desktop Host (D8)

A session whose `client.hello` has `role: "web"` is a browser on another device, through the web bridge. It gets
what a client of the server gets, held to the app's folders, and nothing that acts on this PC beyond its downloads.
Every other session keeps the desktop rights above. `HostCommandDispatcher.CloseSessions("web")` ends them all.

- Refused: `host.openUi`, `host.pickFolder`, `host.shutdown` and `update.*` (the replies above), program settings
  and completion programs, a `job.update` that turns on `openFile`, a `systemAction` other than `none` or
  `forceSystemAction` ("... can only be set in the app on this PC."), `remote.account.setKey`/`clearKey`, and
  `remote.container.sendToJd` (handing a container to another download manager on this PC; `remote.container.preview`
  says `canSendToJd: false` to a web session).
- `folders.browse`/`create` work, limited to **the app's folders**: the download folder, every built-in and custom
  category folder, the favorites and the extraction folder, worked out again whenever the settings change. The
  same folders hold for `job.add` and a hand-off, `job.update` Move/Rename (`newFilePath`), `folders.space` (a folder
  outside them comes back unmeasured), `host.cleanOrphans`, the archive commands' `filePath` and `folder`, and the
  provider's `.torrent` and container paths and destinations. A refusal says "That folder is outside the app's folders."
- One of those folders is left out when it can't be used from another device: one on a network, mapped or removable
  drive (the text check below), and one that is, holds or is inside the user's AppData, Windows, Program Files or
  ProgramData (a favorite such as `C:\` or the user's own folder). A path in it is refused with "That folder can't
  be used from another device." and the reason.
- On Windows a path is checked as text first, and nothing on disk is touched for one that fails: only a drive letter,
  a colon and a slash, on a fixed local drive; no UNC, `\\?\`, `\\.\` or `\??\` path, no device name (`CON`, `NUL`,
  `COM1`...), no `:` after the drive (a stream), no part ending in a dot or a space, none of `<>"|?*`. A link
  whose target fails the same check (another computer, or a drive letter that isn't a fixed drive) is not followed.
- A download whose files are outside the app's folders is read-only for a web session: it may pause, resume, retry
  and remove it without its file, but `job.update`, `job.refreshUrl`, removing or cancelling with its file,
  `archive.extract` of it (by `jobIds` or `packageId`) and `archive.setPassword` for a package with such a
  download are refused with "This download's file is outside the app's folders, so it can't be changed from another
  device." The same goes for a provider job saved to a folder outside them (`remote.select`, `remote.retry`,
  `remote.startQueued`, `remote.archivePassword`).
- An archive set whose folder or output folder is outside the app's folders is not listed in `archive.list` for a
  web session (nor, on a server, one outside its folders), and `archive.retry`, `archive.cancel`, `archive.remove` and `archive.setPassword` by `setId` leave it
  alone ("These archives are outside the app's folders, so they can't be changed from another device.").
- `job.refreshUrl` is refused for a download set to open its file or run a program when it is done: those would then
  act on whatever the new address sends.
- What a web session adds or hands off (an add, an import, a hand-off) is marked `addedFromAnotherDevice: true` on
  the job, so the Host opens no progress or completion window for it on this PC's screen. Its `source` stays the one
  it named (an unknown name such as `web` is read as `api`), and a provider's file keeps `remote`. The mark is a
  property of its own rather than a source value, so a job file written now still loads in an older build. A local
  client can't set it. Resuming or refreshing a download added at this PC from a web session still opens its windows
  here (known, not changed).
- `settings.update` follows an allow-list (`WebSettingsRules`): queues, schedules, connections, speed, categories'
  names and file types. Every other field must be sent back as it is, or the save is refused with the names of
  the changed fields and nothing is saved; what the Host keeps itself (recent folders and addresses, packages, the
  last update check) is taken from the Host instead. A schedule's when-done fields (`openFileWhenDone`,
  `exitWhenDone`, `shutdownWhenDone`, `shutdownMode`, `forceShutdown`) and a category's folder stay as they are.
  `archive.settings.set` may not change `folder`, nor turn on `deleteArchives` or `conflictPolicy: "overwrite"`:
  those apply to every archive the user downloads, wherever it is. Turning them off is allowed.
- What a web session is shown of the settings (`settings.get`, the snapshot, every `settingsResult`) leaves out the
  post-download command, the FFmpeg path, the update source, the proxy's address and user (its mode stays), the site
  logins, the sound files and the recent addresses and folders. Sent back as shown, they are kept as they are; sent
  with another value, they are refused like any field it may not change.
- No local network: a download added, handed off or pointed at a new address by a web session never reaches
  loopback, private, link-local, carrier-grade NAT or `.local` addresses (`errorCode: "local_network"` on `job.add`
  and `handoff.request`), and an address's name is resolved and checked again when the connection is made. Any
  connection that isn't to the proxy the user set is checked so. Through the proxy, the site's name is resolved on
  this PC first and refused the same way; a name only the proxy can resolve goes ahead, as does one that resolves
  differently by the time the proxy asks. FTP is refused. A hand-off's `extensionId` is not checked and grants
  nothing, and request ids are kept apart from a local client's.
- A finished download a web session added gets the mark Windows gives files from the internet (`Zone.Identifier`,
  zone 3, with only the site's scheme and host), so SmartScreen and Office warn before it is opened. A drive without
  streams keeps it unmarked.
- Removing several downloads with their files skips one that is already gone, as it does for any other session.
- `jobs.snapshot`, `jobs.query` and `host.status` leave `stateDirectory` empty and list only leftovers inside the
  app's folders.

### The web UI's own settings: `webui.*` (desktop Host, D8)

Options, Web UI in the window sets the web UI up over the pipe. These commands are answered for a desktop session
(`role` left out or `desktop`) only. A `web` session, and the Host scope of a remote session, get
`host.actionResult` `{ok: false, message: "The web UI can only be set up in the app on this PC."}` and nothing
changes; a Host without the web UI (the headless server) says "The web UI is not available here." The settings live
in `<data>/webui.json` (written to a temporary file, flushed, then moved over it), never in `settings.json`, export
or import. A damaged file counts as off.

| Request | Payload | Reply |
| --- | --- | --- |
| `webui.get` | `{}` | `webui.status` |
| `webui.set` | `{enabled?, port?, alsoReachableAs?}`; a field left out stays | `webui.status` |
| `webui.setPassword` | `{password, keepPairedBrowsers?}` | `webui.status` |
| `webui.recheck` | `{}` | `webui.status` |
| `webui.openTicket` | `{}` | `webui.ticket` `{url?, error?}` |

`webui.status` (and the broadcast `webui.changed`) carry `WebUiStatusDto`:

- `enabled`, `port` (default 8080), `alsoReachableAs` (computer names a browser may use besides an IP address,
  `localhost` and this PC's own names, its DNS host name and its NetBIOS name, which the bridge adds itself: at most
  16, lower case, without `http://` or a port), `hasPassword`;
- `state`: `off`, `starting`, `running` or `failed`, and `error` (plain words, for Options and the tray) when a
  request was refused or the bridge could not run;
- `firewall`: `ready` when, for at least one network profile in force now, there is an enabled inbound allow rule
  named `ruleName` for `bridgePath`, TCP, covering the port, with remote addresses `LocalSubnet` only, covering that
  profile, and no enabled inbound block rule for that program covering the port and that profile; `missing`
  otherwise (a rule widened to other addresses, or one for Private while the network in force is Public, is not
  ready); or `unknown` (the rules could not be read; a process below medium integrity is refused the read);
- `localOnly`: the bridge listens on 127.0.0.1 (and ::1) only, which it does while `firewall` isn't `ready`;
  `addresses`: `http://<ip>:<port>/` for each of this PC's network addresses, empty while `localOnly` or not running;
- `bridgePath` (the program the firewall rule names) and `ruleName` ("Inbound Download Manager web UI").

Refusals come back as `webui.status` with `error` set and nothing changed: turning the box on without a password
("Set a password first. Other devices sign in with it."), a port outside 1024 to 65535, a name that isn't one, a
password the sign-in rules refuse. `webui.set` saves first and then starts, restarts (a changed port or names) or
stops the bridge. Turning it off also ends every web session and every browser's sign-in (`sessions.json` is removed
once the bridge has stopped), so a browser signs in again when it is turned on; paired apps stay paired, since their
tokens were made with the password that is still in force. `webui.setPassword` with the password already in force
changes nothing (no restart, nobody signed out or unpaired). Otherwise it stops the bridge, ends the web sessions,
writes the hash to `<data>/auth.json` (unpairing every paired desktop app, and every browser extension unless
`keepPairedBrowsers` is true, as on the web UI's own password change; removing `sessions.json`: every browser signs
in again), and starts the bridge again if the box is ticked. `webui.recheck` reads the firewall rules again and, when
the bridge is running on the wrong addresses (this PC only with the rule ready, or every address without it),
restarts it on the right ones; open pages reconnect, still signed in. It starts nothing that isn't running: a bridge
that is off, failed (a port in use, say) or waiting to restart after a crash is left as it is (the last reads the
rule when it starts). `webui.openTicket` checks that the bridge it started is the program listening on the port, and
answers `http://127.0.0.1:<port>/api/auth/ticket?t=<ticket>`: a one-time sign-in for a browser on this PC, valid
60 s. When another program listens there, or nothing does (the bridge ended between two checks), `error` says which.

`webui.changed` goes to desktop sessions that take events, never to a `web` session, whenever any of the above
changes, the bridge included (started, ready, stopped, failed).

### The server's transports (D1b)

`DownloadManager.Server`, the headless Host for Linux and Docker, serves the same envelopes over HTTP:

- `GET /api/ws` is a WebSocket. Each text message carries exactly one envelope, as JSON, with no length prefix (the
  WebSocket frames it). Binary messages and messages over 8 MB close the connection. On connect the server sends
  `server.hello` and `jobs.snapshot`, then replies and events, exactly as on the pipe, and the one-command-at-a-time
  rule below holds per WebSocket. `GET /api/ws?hello=1` works like the hello pipe instead (PERF.7): the client speaks
  first, and its first message must be `client.hello` with `{ "snapshot": bool, "events": bool }` (both true when
  left out), within 5 seconds. The server then sends `server.hello`, `jobs.snapshot` only if `snapshot` is true, and
  broadcasts only if `events` is true. Another first message closes the socket (policy violation); a first message
  on another protocol version gets `server.hello` and `server.incompatible`, then the socket closes. Without the
  flag nothing changes, so the web UI needs no change.
- `POST /api/ipc` takes one envelope as the request body and answers with the one reply envelope. There is no hello,
  no snapshot and no events. A request with another version gets `server.incompatible` back. It is for short-lived
  clients that needn't hold a connection.
- `GET /health` answers `{"status":"ok","version":...}` and is the only path a health check needs.
- A request to `/api/` that carries an `Origin` header (every browser request does) must come from the server's own
  origin. Every request to `/api/`, with an `Origin` or without (a page's same-origin `GET` carries none), must name
  the server in its `Host` by an IP address, `localhost` or a name in `DOWNLOADMANAGER_HOSTS`. Anything else gets
  403. There are two exceptions. A paired extension's `POST /api/ipc`, `POST /api/pair` and `DELETE /api/pair/self`
  (below). And an app that isn't a browser, such as the desktop app managing a server by name
  (`http://homeserver:8080`), may use any name when its request carries no `Origin` and either a valid token on a
  token path (`GET /api/ws`, `POST /api/ipc`, `DELETE /api/pair/self`) or is `POST /api/pair`. A page that points
  its own name at the server (DNS rebinding) gains nothing from this: a browser always sends `Origin` on a POST, a
  DELETE and a WebSocket upgrade, its own page's too, and a page has no valid token. A request with an `Origin`, a
  token or not, meets both rules.
- Sign-in (D2) is on by default; see the next section.

The server's hello leaves out `tray` and adds `folders`. Its Host can't open a window, show a folder dialog, exit on
request, update itself or run programs a client names, so those commands answer as described above.

### The web bridge on Windows (D8)

With the web UI ticked, the desktop Host starts `DownloadManager.Server.exe --bridge` (next to the Host, or in its
`web` folder) with no window, and gives it its options as one JSON line on standard input (`WebBridgeStartDto`: the
Host's data folder, its hello pipe, the port, `localOnly`, the extra names, and a random 32-byte ticket key for this
run). Nothing is on the command line. The bridge writes `ready` to standard output once it listens; closing its
standard input stops it, so it never outlives the Host. All three streams are UTF-8 at both ends, whatever the
console's code page (the data folder's path holds the Windows user's name). Its exit code says why it ended:
0 stopped (its standard input closed), 1 crashed (also when its web front ended without being asked), 2 refused (bad
options, no password), 3 the port is in use, 4 Windows doesn't allow the port; the last line of standard error is
the reason, in plain words. The Host restarts a crashed bridge after 1, 2, 4... seconds (at most
60), and gives up with a tray notice after 5 crashes that each ran less than 2 minutes; a refused or busy port is
not retried.

It serves the same web UI, `/api/ws`, `/api/ipc`, `/api/upload/torrent` and sign-in as the server, and differs so:

- No engine and no state of its own: each WebSocket and each `/api/ipc` call is one connection to the Host's hello
  pipe (this Windows user's only, both ends checked), with `client.hello` `role: "web"` whatever the browser sent
  (`?hello=1` keeps its `snapshot` and `events`). Envelopes pass through unchanged, one per WebSocket message and
  one per pipe frame. The Host ending the connection (the web UI turned off, the password changed, an exit) closes
  the socket with 1001. At most 32 browser connections hold a pipe at once. A Host that can't be reached, or that
  sends something that isn't a frame, is 503 `"The app on this PC isn't answering. Try again in a moment."`; an
  `/api/ipc` call it doesn't answer within 5 minutes is 504 with the same `{ok: false, error}` form.
- Sign-in is always on, from `auth.json` and `sessions.json` in the Host's data folder. There is no
  `/api/auth/setup` and no setup code, and the bridge refuses to start without a stored password. Changing the
  password, pairing codes and paired apps work as on the server, behind sign-in. A paired extension keeps the
  extension's commands only; a paired desktop app (`remoteClient`) has a signed-in browser's rights.
- The session cookie is named by the port, `inbound_session_<port>`, since ports are no cookie boundary: a browser
  signed in to this PC's bridge and to another server on the same address keeps both. After a port change a
  browser signs in again.
- Names: besides IP addresses and `localhost`, the names set in Options (`alsoReachableAs`) and this PC's own DNS
  host name and NetBIOS name. A refused name says to add it under Web UI in the app's Options.
- `GET /api/auth/ticket?t=...` signs in a browser on this PC with the window's ticket ("Open in browser"): the
  ticket is a nonce, an expiry and an HMAC-SHA256 with the run's key; it works once, for 60 s, from a loopback
  address with no forwarding header (`X-Forwarded-For`, `Forwarded`, `X-Original-For`), else 403. A good one sets the
  session cookie and redirects to `/`; a wrong, used or expired one redirects to `/signin/`. Wrong tickets are not
  counted as wrong sign-ins: a ticket can't be guessed, and counting them would let any web page make this PC's
  browsers wait (an image pointing at the address needs no Origin, and it comes from loopback like the owner's own
  browser). The ticket travels in the query, so it can sit in the browser's history for its 60 s; it is single use,
  and the bridge logs no request URLs.
- Uploads go to `<data>/uploads`, which the Host reads through the same `upload:<id>` reference.
- It listens with exclusive address use, on 127.0.0.1 and ::1 while the firewall rule is missing, and on every
  address once it is there. Its log is `<data>/logs/web-bridge-<date>.log`.
- `GET /api/auth/status` says `hostKind: "windows"`.

### Sign-in and paired apps (D2, D9)

With sign-in on (the default), every path except `/health`, `/favicon.svg`, `/theme.js`, `/signin/...` and the calls
below needs a session cookie or, on the token paths only, a paired app's token (see "A paired token" below). Without one, `/api/...` answers
401 with `{"ok":false,"error":...,"signIn":"/signin/"}`, a page `GET` is sent to `/signin/` (302), and the WebSocket
upgrade is refused with 401. A failure always answers `{"ok":false,"error":"...","retryAfterSeconds":n?}`; a wait after
wrong tries is 429 with `Retry-After`.

| Call | Session? | Body / answer |
| --- | --- | --- |
| `GET /api/auth/status` | no | `signInRequired`, `signedIn`, `setupNeeded`, `setupCodeNeeded`, `passwordFromEnvironment`, `sessionEndsAt`, `hostKind` (`server` for this server; the Windows app's web bridge says `windows`), and `version` only for a signed-in browser. `hostKind` is shown to anyone because the sign-in page and a desktop app that hasn't paired yet need it; it tells a stranger whether this is a server or a Windows PC, not which version runs there. |
| `POST /api/auth/setup` | no | `{password, setupCode?}`: the first password (8 to 1,024 characters). A browser that isn't on the server's own machine (or comes through a proxy) also gives the setup code the server wrote to its log. Signs this browser in. 409 once a password exists. |
| `POST /api/auth/signin` | no | `{password}`; sets the cookie. |
| `POST /api/auth/signout` | no | ends this browser's session and closes its WebSockets. |
| `POST /api/auth/password` | yes | `{current, password, keepPairedBrowsers?}`; every other session ends, every paired desktop app (`remoteClient`, made with the old password) is unpaired, and so is every browser extension unless `keepPairedBrowsers` is true; their WebSockets and running `/api/ipc` requests end. Answers `{ok, unpaired}`. 409 when the password comes from the environment. |
| `POST` / `DELETE /api/auth/pairing/code` | yes | makes (or cancels) the one pairing code: `{code, expiresAt}`, 8 characters from `A-Z` and `2-9` without look-alikes, ten minutes, once. |
| `GET /api/auth/tokens`, `DELETE /api/auth/tokens/{id}` | yes | the paired apps (`id`, `name`, `createdAt`, `lastUsedAt`, `role`: `extension` or `remoteClient`), and revoking one, which also closes its WebSockets and ends its running `/api/ipc` requests (401). An open WebSocket counts as use on the sessions' 10-minute beat, so a desktop app that stays connected keeps a current `lastUsedAt` and isn't the first pushed out when a 51st app pairs. |
| `POST /api/pair` | no | `{code, name}` from the extension; answers `{ok, token, id, name, role}` once, `role` always `extension`: a code never makes another role, whatever `role` the body names. 1,000 wrong codes (from all addresses together) end the code. A desktop app sends `{password, name, role: "remoteClient"}` instead, with no `Origin` header, and gets a `remoteClient` token; with an `Origin` (a page or an extension) it is 403. The password is checked as on `POST /api/auth/signin`, with the same waits: a wrong one counts as a wrong sign-in from that address (401), and while the address waits it is 429. 400 with sign-in off (there is no password then) and for a role the server doesn't know; 409 before a password is set. |
| `DELETE /api/pair/self` | token | the app's own Unpair, with `Authorization: Bearer ibt_...` and no body: removes the record of that token, and only that one (an id in the query or body is ignored), closes its WebSockets, and answers 204 with no body. 401 without a token, with an unknown or removed one, or with only a session cookie; 500 when `auth.json` can't be written (nothing removed then). |
| `POST /api/upload/torrent` | yes | the raw `.torrent` (16 MB at most); answers `{ok, upload}`, a reference (`upload:` and 32 hex digits, not the file's path) that the server's own `remote.classify` and `remote.add` accept as their text or input. |
| `POST /api/upload/container` | yes | the raw `.rsdf`, `.ccf` or `.dlc` file (16 MB at most), its file name in the `X-File-Name` header, percent-encoded (`encodeURIComponent`), so it stays out of the address and the access logs; its kind is the name's extension (400 for any other). Answers `{ok, upload}` as above, which `remote.container.preview` accepts as its `path`, with the file's `fileName` to name its packages. Too large is 413, with or without `Content-Length`; an empty body is 400. Torrents and containers are kept 1 day, at most 200 of each kind; a reference whose file has gone gets "The uploaded file is no longer there. Upload it again." |

- **Password:** PBKDF2-SHA256, 600,000 iterations, 16-byte salt, kept in `<data>/auth.json` (mode 600 off Windows) with
  the token hashes. `DOWNLOADMANAGER_PASSWORD` or `DOWNLOADMANAGER_PASSWORD_FILE` sets it instead (hashed at start;
  the web UI can't change it then).
- **Session:** a 256-bit random id in the `inbound_session` cookie (`HttpOnly`, `SameSite=Strict`, `Path=/`,
  `Secure` over HTTPS); the server keeps only its SHA-256. Sessions survive a restart: `<data>/sessions.json` (mode
  600 off Windows, written to a temporary file, flushed and moved over the old one) holds each session's hash, start
  and last use, never the id. A sign-in, sign-out or password change is written at once; an expired session is
  refused at once and leaves the file at its next write or the next start. Last-use times are written at most every
  10 minutes per session and on a clean stop, so a busy browser doesn't keep writing the disk; an open WebSocket
  counts as use on the same 10-minute beat even when the page sends nothing, so after a kill a session may end up to
  10 minutes early. A password change saves the new password and signs the other browsers out as one step for the
  file, and a sign-in whose check began under the old password is refused. The file notes a stamp of the password (a
  SHA-256 of its salt and hash): if the password changed while the server was down, every saved session is dropped.
  A damaged file is deleted. A write that fails deletes the file, or empties it when it can't be deleted, so the
  next start restores nobody; only a data folder that refuses all three keeps the old file, and the log then says to
  delete it by hand. Off Windows the folder is flushed after each replace, so a power loss can't bring back the file
  from before a sign-out (best effort, not run on Linux in this project's tests). With sign-in off
  (`DOWNLOADMANAGER_AUTH=off`) sessions aren't kept and the file is removed at start, so turning sign-in on again
  restores nobody. A session ends after 24
  hours without use (an open WebSocket counts as use) and 7 days after sign-in at most, across restarts, and its
  WebSockets close then. `X-Forwarded-Proto` and `X-Forwarded-For` are believed only from the proxies `DOWNLOADMANAGER_TRUST_PROXY`
  lists (addresses or networks; `1` is refused at start).
- **Wrong tries** make that client address wait 1, 2, 4 ... seconds, up to 5 minutes, before the next try counts. A
  try counts as wrong from the moment it starts until it turns out right, so parallel tries can't pass the wait
  together. An IPv6 client counts by its /64 network. The setup, sign-in and pairing bodies are read up to 16 KB,
  chunked or not.
- **Setup code:** asked of every browser except one on the server's own machine connecting directly: a connection
  from a listed proxy, or with `X-Forwarded-For` / `Forwarded`, always needs it.
- **Origin:** every state change and the WebSocket upgrade must carry the server's own origin (above).
- **A paired token** (`Authorization: Bearer ibt_...`) has a role, kept with its hash in `auth.json`. A record
  without one is the extension's, so files from before roles read as they did, and a role this version doesn't know
  opens nothing.
  - `extension` (from a code) is for `POST /api/ipc` (and for unpairing itself), and only for what the extension
    role allows (`NativeBridgePolicy`, `BridgeRole.ServerExtension`): the extension's own commands plus
    `folders.browse`. Anything else answers 403 with `host.actionResult`. Replies pass the same filter, so a snapshot
    comes without settings. `GET /api/ws` with it is refused (403).
  - `remoteClient` (from the password, for a desktop app managing the server) has a signed-in browser's rights on
    `GET /api/ws` (with or without `?hello=1`) and `POST /api/ipc`, and can unpair itself. It is not a session: the
    other calls in the table still want the cookie. Its WebSockets close, and its running `POST /api/ipc` requests
    end with 401, when it is revoked, unpaired, or removed by a password change (always, whatever
    `keepPairedBrowsers` says).
  - On `GET /api/ws` only `Authorization: Bearer ibt_...` is read as a token, so a browser behind a proxy that sends
    its own header (`Basic` for the proxy's password, or a `Bearer` token of the proxy's own) keeps using its cookie
    there.
  - Turning sign-in off and on again keeps the paired tokens, though the sessions are dropped: a desktop app's token
    was made with the password that is still in force, so its holder could pair again anyway. A password change, on
    the page or in the environment, removes them.

  Only the token's SHA-256 is stored. Requests from our Chrome extension ids
  (`chrome-extension://`, the store and the unpacked id) and from any `moz-extension://` origin (Firefox gives each
  install a random id) pass the origin check for `POST /api/ipc` and `DELETE /api/pair/self` with a bearer token,
  which the sign-in gate then checks, and for `POST /api/pair`, where the one-time code is the credential. They get
  CORS headers for those three calls, without credentials; preflights answer 204 (`Access-Control-Allow-Methods`
  is `DELETE` for `/api/pair/self`, `POST` for the other two).
- `DOWNLOADMANAGER_AUTH=off` turns sign-in off. Then the D1b rule holds again: the server refuses to listen beyond
  loopback unless `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1` is set too.

### Folder browsing: `folders.*`

Only a session whose hello lists `folders` gets these answered; the desktop Host's own sessions don't (they have a
folder dialog) and get `error` "Folder browsing is not available here." A web session there does. Both requests reply with `folders.browseResult`
(`FolderBrowseResult` in `FolderBrowseDtos.cs`).

- `folders.browse` `{ path, files? }`: the folders inside `path`, sorted by name, hidden folders (the
  Hidden or System attribute on Windows, a leading dot on Linux) and unreadable ones left out, at most 2,000 (then
  `truncated: true`, and the 2,000 are the first by name, not the first the disk returned). A hidden folder is left
  out of the list, not out of reach: its path typed in opens it. An empty or missing `path` lists the allowed roots. The
  reply carries `path` (with links resolved), `parent` (null at a root), `roots`, `folders` (`name`, `path`),
  `files`, `freeBytes` and `totalBytes` of the mount that holds the folder. Files are listed only with
  `files: "archives"`, for an extraction's chooser: the files whose names extraction can start from, by name only
  (`ArchiveNames.IsChooserName`): any `.rar`, a `.7z`, `.zip` or `.tar`, a compressed tar (`.tar.gz`, `.tgz`,
  `.tar.bz2`, `.tbz2`, `.tbz`, `.tar.xz`, `.txz`, `.tar.zst`, `.tzst`) and the first piece of a split 7z, zip, RAR or
  tar set (`name.7z.001`); not a lone `.gz` or `.xz`, any other `.001`, or a set's later pieces. Each has `name`,
  `path` and `size` (a link's target's size), under the same rules as the folders (hidden ones left out, a link only
  when it leads inside the roots, at most 2,000). Any other `files` value is refused.
- `folders.create` `{ parent, name }`: makes the folder `name` inside `parent` and answers with the listing of the
  new folder. A name with a slash, a leading or trailing space or a trailing dot, `.`, `..`, a colon, a control
  character, more than 255 bytes, a character or device name Windows can't keep (`<>"|?*`, `CON`) or the app's own
  working-folder names is refused, as is a name already taken. These are the rules an extracted file's name meets,
  so a folder made here can also be shared to Windows.
- The roots are `DOWNLOADMANAGER_ROOTS` (`/downloads` by default; several separated by `:` or `;`). Every path is
  resolved component by component, following links, before it is checked, so a link inside a root that points out
  of it is refused, and so is `..` used to climb out. A link inside a root that stays inside is listed and works.
  A refusal is `ok: false` with an `error`, never a listing.
- The same roots hold on such a server for every path a client gives: `job.add` (`destinationDirectory`, or the
  folder settings it falls back on) answers `invalid_destination`; `job.update` Move/Rename, `settings.update` with a
  changed folder outside them, `remote.container.preview`/`sendToJd`, a `.torrent` path in `remote.add`/`classify`,
  `archive.extract` and `archive.setPassword` with a `filePath`, and `archive.settings.set` with a `folder` answer
  `ok: false` with an error. The desktop Host has no roots for its own sessions; a web session there has the app's
  folders (above).

### Downloads as every client sees them

- A download whose address is hidden (`addressHidden`, a direct provider link) or that is a provider's file carries
  `pageUrl` as its site alone (`https://host[:port]/`, no user name, path or query) and no `finalUrl`. The Host does
  this, so every client (the window, the web UI, the CLI) gets the same; the desktop window, which showed such a
  page in full when it wasn't on a provider's host, now shows its site too. `url` is sent as stored: a link with a key
  in it is stored masked, and every client shows a hidden one as its site by `addressHidden`. `statusNote` has any
  address taken out as well, as a safeguard: today it holds at most a host name.
- `speed.history` `{}` answers `speed.historyResult`: the total download speed of the last hour, one figure a second
  (`start`, `intervalSeconds`, `bytesPerSecond`, oldest first, the last the second under way), the desktop's speed
  graph's own. Every session gets it, web sessions and the server's clients too, so a graph opened later starts full.
  A Host from before it answers with an error.

## One command at a time per connection

The Host handles a connection's commands strictly in order, and each waits for the one before it.
Events keep flowing, but the next command doesn't start until the current one has been answered.

**So a call that waits on the network holds up everything else sent on that connection.** A frontend
should send these on a separate connection of their own and keep its main one for quick commands:

- `remote.account.get` with `refresh`, and `remote.account.validate`
- `remote.classify` and `remote.add` (the provider's hoster list may have to be fetched)
- `remote.item.get`
- `remote.files`
- `remote.deleteRemote` and `remote.startQueued`
- `remote.container.preview` and `remote.container.add` (the hoster list again)
- `remote.container.sendToJd`, which waits for a person to answer another program's question, for up to five minutes
- `job.refreshUrl` with an address that differs from the job's (the hoster list again). With the job's own address it answers at once

If a client closes its connection during one of these, the call is cancelled. The rest of the
`remote.*` messages only touch local state, and answer without waiting on the network.

## Remote provider messages

A remote provider is an account service that fetches content on the user's behalf and offers it for
download. The Host owns the account key and all provider state.

Three rules hold for every reply and event below:

- **No secrets go out.** No field holds an API key, a direct link, a magnet, a hoster link or a link
  password. Clients get names, a display host, stages, the provider's own state word, progress and
  failures. Text that came from a provider or an input is scrubbed of addresses and keys first.
- **The key is write-only.** `hasKey` and `keyUnreadable` are all a client can ever learn about it.
- **Remote stages aren't local progress.** `progress` on a remote job is the provider's own. Bytes
  reaching this computer are ordinary jobs, with `remote://` references in `job.event` and
  `jobs.snapshot`. Pausing or removing those never touches the provider.

Every reply has `ok` and, when it is false, `error` in words for the user. `failureKind` where present is
one of `authentication`, `accountLimit`, `accountBusy`, `linkPasswordRequired`, `rateLimited`,
`notFound`, `rejected`, `serviceError`, `network`, `badResponse`, `notCached`, `alreadyExists`. A
request whose reply would pass the frame limit gets `ok: false` instead, and the connection stays up.

`provider` is a provider id such as `torbox`. It may be left out while only one provider is
registered. Kinds are `torrents`, `webdl` and `usenet`.

### Requests

| Type | Payload | Reply |
|---|---|---|
| `remote.account.get` | `provider?`, `refresh?` | `remote.account.result` |
| `remote.account.setKey` | `provider?`, `key` | `remote.account.result` |
| `remote.account.clearKey` | `provider?` | `remote.account.result` |
| `remote.account.validate` | `provider?` | `remote.account.result` |
| `remote.classify` | `text`, `withFiles?` | `remote.classifyResult` |
| `remote.add` | see below | `remote.addResult` |
| `remote.list` | none | `remote.listResult` |
| `remote.item.get` | `provider?`, `kind`, `itemId?`, `offset?`, `limit?`, `includeFiles?`, `fresh?` | `remote.item.result` |
| `remote.files` | `jobId` | `remote.filesResult` |
| `remote.select` | `jobId`, `all?`, `fileIds?` | `remote.actionResult` |
| `remote.retry` | `jobId`, `linkPassword?`, `approveUncached?` | `remote.actionResult` |
| `remote.checkNow` | `jobId?`, `provider?` | `remote.actionResult` |
| `remote.remove` | `jobId` | `remote.actionResult` |
| `remote.deleteRemote` | `jobId`, `confirmed` | `remote.actionResult` |
| `remote.startQueued` | `jobId` | `remote.actionResult` |
| `remote.archivePassword` | `jobId`, `password` | `remote.actionResult` |
| `remote.container.preview` | `path` | `remote.container.previewResult` |
| `remote.container.add` | `previewId`, `entryIds`, `destination?`, `queueId?`, `startMode?`, `approveUncached?` | `remote.container.addResult` |
| `remote.container.sendToJd` | `path` | `remote.container.sendResult` |

**`remote.account.*`** replies carry `accounts`: `provider`, `providerName`, `capabilities`, `itemKinds`,
`hasKey`, `keyUnreadable`, `plan`, `premium`, `premiumUntil`, `cooldownUntil` and `checkedAt`. Without
`refresh`, `get` gives the last answer known since the key changed, with no network call. A key
the provider refuses stays saved and `validate` reports it. `keyUnreadable` means a key is saved and
can't be decrypted on this Windows account, so it has to be entered again.

**`remote.classify`** says what a pasted string or a local `.torrent` path is: `kind` is `folderLink`,
`magnet`, `torrentFile`, `hosterLink`, `providerDirectLink`, `ordinaryLink`, `undetermined` or
`unsupported`, with a `reason` for the last two. `undetermined` means an account could take the link
and there is no list of supported sites to tell whether it does, so it must not be treated as
ordinary: offer a retry, or an explicit choice to request it directly. Show `note` when there is one:
it can say a stale list couldn't be refreshed, or that a site couldn't be checked. An `ordinaryLink` with
`checkIncomplete` true is the second of those: a listed site couldn't be checked against it, so it may be
one the provider handles. A stale list leaves the flag false. Don't request such a link directly without
saying so. A `hosterLink` also
carries what the provider last said about that site, each null when it sets no limit:
`hosterDailyLinksLeft`, `hosterDailyBytesLeft` and `hosterLargestFile`. Clients that don't know a
value of `kind` should treat it as they would `unsupported`.

With `withFiles` true, a `magnet` or `torrentFile` also says which files it holds, so they can be chosen
before it is added. A `.torrent` file is read for its own list: `files` has each file's `relativePath`
(inside the torrent's own folder) and `size`, and `fileId` only numbers them, so choose these with
`filePaths`. A magnet is checked against the provider's cache, which is the only call this makes:
`cached` says whether it is, and a cached one's `files` are the provider's, chosen by `fileIds`. `cached`
is null when the check couldn't be made, and `files` is null when the files aren't known, or there are more
than 5,000 of them. The entries are those of `remote.files`.

`job.add` never classifies. It stays an explicit direct request, because a browser handoff is made of
those. A frontend with a hoster link has to route it through `remote.add`. `dm add`, `dm add-batch` and
`dm import` classify each address first: a hoster or folder link is refused, and in a batch or an import it
counts as failed. An `undetermined` one is refused too unless `--direct` is given. `checkIncomplete` only
prints the note, because it can go on for hours and a script shouldn't stop for it.

**`job.refreshUrl`** points a download at a new address. One that isn't finished goes back to the queue and starts
(`job.event` `refreshed`). A finished one keeps its file and its state, and only the address it would be downloaded
from again changes (`updated`); nothing is downloaded, though a new address is still checked as below (which may
ask the provider), so a provider's link can't become the address a redownload uses. It does classify, because it isn't a request to add anything: it takes `jobId`, `url` and
the optional `explicitDirect`. While a usable key exists, a changed http or https address that is a
`hosterLink` or `folderLink` gets `ok: false` with words pointing to Add Links, with or without the flag.
So does an `unsupported` one. An `undetermined` one, or one that couldn't be classified at all, gets
`ok: false` unless `explicitDirect` is true, which a client sends only when the user chose a direct
request for it. The same goes for an `ordinaryLink` with `checkIncomplete`. The job's current address and any `ftp` address are always accepted, and with no usable
key nothing is classified. `dm refresh <id> <url> --direct` sets the flag.

The clipboard monitor classifies too, while a usable key exists. An ordinary or provider direct link
is added directly. An `ordinaryLink` with `checkIncomplete` is added with `startMode: later` and no request
to its server, not even for its name, and the tray says so. A hoster or folder link becomes a job with
`state: intended`, `approvalNeeded` and `heldForApproval` set, and the tray says so without the address.
Anything else isn't added, and the tray says to use Add Links. `heldForApproval` means nothing has been sent
or asked about it, not even whether it is cached, and a job holding it stays where it is across a restart, a
changed key and `remote.checkNow`, which refuses it. `remote.retry` with `approveUncached` releases it, as does
a `remote.add` of the same input from someone asking for it, which then needs its own `approveUncached` as
usual. That `remote.add` is answered with the same job and `existing` true, and its options replace the
clipboard's defaults: `destination`, `queueId`, `startMode`, `selection`, `fileIds` and `linkPassword`. With no usable key the
monitor adds every link directly, as it always has.

**`remote.add`** takes either `input` (a folder link, magnet, hoster link or a full path to a local
`.torrent` file) or `provider`, `itemKind` and `itemId` (an item from `remote.item.get`). Other fields:
`destination` (full path), `queueId`, `startMode` (`startNow`, `queue`, `later`), `selection` (`ask`,
`all`, `chosen`), `fileIds`, `filePaths`, `approveUncached`, `linkPassword`, `archivePassword`. The input is stored encrypted and is
never returned. With no `selection`, `fileIds` or `filePaths` mean `chosen`. Otherwise a hoster link takes
`all` and starts by itself when ready, and a folder link asks. A magnet or `.torrent` file is
`selectionMode: askIfCached` until its cache is checked: it then asks if it was cached and takes `all` if
it wasn't, and one found on the account already counts as cached when it is done there. `ask` makes it wait
for a choice either way. `filePaths` is a list of `path` and `size`, as `remote.classify` with `withFiles`
gave a `.torrent` file's files, and can't come with `fileIds`. The Host matches them to the provider's files
once it lists them: the first folder of each path is left out, because the provider names that folder its
own way, and the rest of the path and the size must agree exactly. When any chosen file, by id or by path,
isn't in the provider's list, nothing is started and the job waits in `awaitingSelection` with
`failureMessage` saying how many are missing, until a new `remote.select`. The files that were found are in
`selectedFileIds`. A job that already has local downloads goes on with what was found instead.
The reply has `job`, `inputKind`, `note` when options given were not used, and
`existing` when an active job already covers
this input. An ordinary link gets `ok: false`: those go through `job.add`. Adding only saves the
intent, and the Host does the work afterwards, so watch `remote.changed`.

**Jobs** (`remote.addResult`, `remote.listResult`, `remote.actionResult`, `remote.changed`) have `id`,
`provider`, `providerName`, `kind`, `state`, `status` (words to show), `stage`, `rawState`, `progress`,
`speedBytesPerSecond`, `etaSeconds`, `itemId`, `name`, `displayHost`, `cached`, `approvalNeeded`,
`heldForApproval`, `approvedUncached`, `hasLinkPassword`, `hasArchivePassword`, `failureKind`, `failureMessage`, `nextCheckAt`, `destination`,
`queueId`, `startMode`, `selectionMode`, `selectedFileIds`, `packageId`, `notes`, `createdAt` and `updatedAt`.

| `state` | Meaning |
|---|---|
| `intended` | Saved, nothing sent. `approvalNeeded` means it waits for the user's go-ahead |
| `submitting` | Being sent |
| `submissionUnknown` | The answer was lost. The provider's list decides, and nothing is sent twice |
| `waiting` | The provider is working on it |
| `awaitingSelection` | Ready, with more than one file to choose from, or with chosen files missing (`failureMessage` says so). `remote.files` lists the files, `remote.select` chooses |
| `localStarted` | Local downloads exist |
| `blocked` | The account is busy for now. Retries by itself |
| `failed` | Waits for the user: `remote.retry` |

`stage` is `unknown`, `queued`, `metadataPending`, `processing`, `stalled`, `ready` or `failed`. An
unknown provider state stays visible in `rawState` and never counts as ready.

**`remote.item.get`** reads the account's own list for a browser: a page by `kind`, `offset` and `limit`
(1 to 250, 50 otherwise), or one item with its files by `itemId`. It changes nothing. `hasMore` means
the page was full.

**`remote.files`** gives each file's `fileId`, `relativePath`, `size`, `infected`, `selectable` and
`note`. `pending` means the provider doesn't know the files yet, and none are made up. A path that
isn't safe has a null `relativePath` and can't be selected.

**`remote.retry`** also approves a job waiting with `approvalNeeded` when `approveUncached` is true.
`linkPassword` is the source site's password for the link, which is not an archive password. Retrying
a known item looks at it again. Only an item confirmed gone from the account is ever submitted again.

**`remote.checkNow`** looks at a job, or at everything, without waiting for its timer. A rate-limit wait
the provider asked for still holds.

**`remote.remove`** takes the job off this app's list and leaves the provider's item and any local
downloads as they are. **`remote.deleteRemote`** deletes the item from the provider for good, and is
refused unless `confirmed` is true. For a job still waiting in the provider's queue, with no item yet, it takes
the job's entry out of that queue instead and then takes the job off the list.

**`remote.startQueued`** (added 2026-11-08, TB.S1; an added message, so no protocol bump) starts a job's entry
waiting in the provider's queue, found by the job's hash in a fresh read of the queue. It is refused for any job
not waiting there, and the account is looked at again at once so the new item is picked up.

**`remote.archivePassword`** sets the password for a job's archives, before or after its downloads exist.
It is not `linkPassword`: that is the source site's password for a link and goes to the provider, while this
goes to the archive side and never leaves the computer. It is kept encrypted on the job until there is a
package, and then handed to `archive.*`'s record for that package. A password can't hold a line break. An
empty one clears it on both sides. Only `hasArchivePassword` ever comes back, and it stays true after the
hand-over, because the password is still there. If a run has already started without it, the set runs again
by itself.

### Containers

A container is a local `.rsdf`, `.ccf` or `.dlc` file. Reading one takes two steps, because a decrypted link
is a secret and a client never gets to see it.

**`remote.container.preview`** reads the file by its full path, or by the reference of one a browser uploaded
(`upload:...`, from the server's or the web bridge's `POST /api/upload/container`; `fileName` then gives the name the
packages are called by). A reference can only name a file in the upload folder, so it is read even though that folder
is outside the caller's folders, while that folder's path typed in is refused like any outside one. A path has to start with a drive. Network and
device paths are refused before anything looks at them, because even asking whether such a file exists
sends this Windows account's credentials to the host it names. Files are limited to 16 MB and 5000 links. The
reply has:

- `ok`, `error`. A damaged file gets `ok: false` and a reason in words.
- `format` (`rsdf`, `ccf`, `dlc` or `unknown`), `formatVersion`.
- `notSupported` with `canSendToJd`, see below.
- `previewId` and `expiresAt`. The Host holds the links in memory only, against this id, for 15 minutes, with at
  most 8 previews at a time. Both are absent when nothing in the file can be added.
- `accountProblem`, such as no API key being set. Each entry also says so in its `reason`.
- `packages`: `index`, `name`, `comment`, `hasArchivePassword` and `entryCount`.
- `entries`: `entryId`, `packageIndex`, `displayHost`, `displayName`, `size`, `kind`, `reason` and `canAdd`.
  No entry holds its link, and names and comments have addresses, keys and the package's passwords taken out.
- `problems`: what was wrong with parts of the file, or is worth saying about it as a whole.

`kind` is `folderLink`, `magnet`, `hosterLink`, `undetermined` or `unsupported`, and a client should treat a
value it doesn't know as `unsupported`. Each `unsupported` entry has a `reason`. An ordinary link is
`unsupported` here, with a reason that says the provider won't take it and that it can be added as a normal
download; nothing from a container is ever requested directly. A link naming a file on this computer is
refused, and so is a direct link holding an account key.

**`remote.container.add`** adds the chosen entries one at a time, each through the same path as `remote.add`.
So the cache check, the approval of uncached items, the spacing between creations and the dedupe all apply,
and adding only saves the intent. `outcomes` has one line per entry: `entryId`, `ok`, `error`, `note`,
`existing`, `inputKind`, `jobId` and `job`. The reply's `ok` means at least one was added. An expired or unknown
`previewId` gets `ok: false` and no outcomes. When so many entries were added that their full jobs wouldn't fit
one frame, the reply has `jobsLeftOut: true` and every `job` is null. `ok`, each outcome and `jobId` still hold,
the entries are saved, and `remote.list` has the jobs.

The links of one container package share a local package, in a folder named after it under the destination.
Links in no package share one too, and go in the destination itself. An item holding one file puts it straight
in that folder, so the parts of a split archive that came through separate links meet in one place and are
claimed as one set. An item with several files keeps its own folders. `status` on these jobs describes the
whole local package.

**Archive passwords from a container.** A CCF can state a password for a package's archives, and that value goes
to the archive side for that package as an offer: it never replaces one the package already has, so what the
user has said stands. A package can name several, in which case the first is used and `problems` says so. It is
never sent to the provider, and it is never used as a link password.

**DLC.** A DLC can't be opened here, because unlocking one depends on an online service this app has no
permission to use. `remote.container.preview` gives `ok: false` with `notSupported` and the reason in `error`.

**`remote.container.sendToJd`** is the separate, optional route for a `.dlc` or `.ccf`. It only happens when a
client asks. It hands the file to a JDownloader running on this computer, on `127.0.0.1:9666`. JDownloader asks
its user to confirm, and the links stay in JDownloader: they do not come back. The reply has `ok`, `status`
(`sent`, `notRunning`, `rejected`, `timedOut`, `invalidInput` or `failed`), `jdAnswered` and `message`.
`jdAnswered` is true only when JDownloader itself said yes or no. Nothing else in this app depends on JDownloader.
This route has only been tried against a stand-in so far, not a real JDownloader.

### Event

`remote.changed` goes to every connected client after the change is saved. Its `change` says which:

- `job`, with `job` holding the whole job
- `removed`, with `jobId`
- `account`, with `account`

A client that connects later reads `remote.list` and `remote.account.get`. These aren't part of
`jobs.snapshot`.

## Archive extraction: `archive.*`

The browser bridge does not carry any of these, in either direction (`NativeBridgePolicy`). A reply has
`replyTo` set to the request's `id`. No reply or event carries a password; `hasPassword` is the only
thing a client can learn about one. None of these requests waits for an extraction, so they are safe on
a client's main connection.

| Request | Payload | Reply |
|---|---|---|
| `archive.settings.get` | none | `archive.settings.result` |
| `archive.settings.set` | `ArchiveSettingsUpdate`: only what is set changes | `archive.settings.result` |
| `archive.list` | none | `archive.listResult` |
| `archive.extract` | `ArchiveExtractRequest` | `archive.extractResult` |
| `archive.setPassword` | `ArchiveSetPasswordRequest` | `archive.actionResult` |
| `archive.retry` | `{ setId }` | `archive.actionResult` |
| `archive.cancel` | `{ setId }` | `archive.actionResult` |
| `archive.remove` | `{ setId }`: the record only, never a file | `archive.actionResult` |

**`archive.settings.result`**: `{ ok, error?, settings, tool }`.

- `settings`: `autoExtract`, `destination` (`nextToArchive`, `subfolder` or `folder`),
  `folder`, `preserveFolders`, `conflictPolicy` (`rename`, `overwrite` or `skip`), `deleteArchives`.
- `tool`: `found`, `path` (the bundled 7-Zip an extraction would run), `message`. An older client's
  `unrarPath` or `clearUnrarPath` is ignored.

**`archive.extract`** takes `filePath` (absolute, a volume of the set), `jobIds`, or `packageId`, and an
optional `password`. With `packageId` the password is kept for the package, otherwise for each set. Its
result is `{ ok, error?, sets, ignored }`, where `ignored` lists what was asked for and left alone, each
with a `reason`, such as a file that is not a RAR archive or a set that has already been extracted.

**`archive.setPassword`** takes one of `setId`, `packageId` or `filePath`, plus `password`. An empty
password clears what is on record. `filePath` need not exist yet, which is how a set gets a password
before it downloads. A set that was stopped waiting for a password runs again as soon as it gets one.

**`archive.retry`** runs a stopped or finished set again. The conflict policy decides what a second run
does to the output of the first.

### Set state

`ArchiveSetDto` carries a set's whole state each time. `state` is one of:

| State | Meaning |
|---|---|
| `downloading` | A volume is still coming, and nobody has asked for extraction |
| `downloaded` | The files are here, and nobody has asked for extraction |
| `waiting` | Asked for; see `downloadingVolumes`, `failedVolumes` and `missingVolumes` for what it waits for |
| `extracting` | Running. `phase` is `probing`, `extracting`, `verifying` or `finalizing`; `percent` comes from the tool |
| `completed` | Every check passed and the files are in `destination`. `volumes` is what the tool really read; `note` names any file with a matching name that it didn't, which was neither extracted nor deleted |
| `failed` | `failureKind` says why, and the archives are kept |

Deleting archives is opt-in, and only what the tool read can go, only once the output is flushed to the disk. Once
a download's file has gone this way its job stays `completed`, with `fileRemovedAfterExtraction: true` and a
`statusNote` that says so in words. There is no file to open for such a job.

`failureKind` is one of `passwordRequired`, `wrongPassword`, `missingVolume`, `corrupted`,
`unsupportedVersion`, `toolUnavailable`, `diskSpace`, `permission`, `unsafePath`, `cancelled` or
`unknown`. `needsPassword` is set for the first two. `stateText` says all of this in plain words, ready
to show.

### Event

**`archive.changed`**: `{ set }` with the set's whole state, or `{ removedId }` when it left the list. Sent
to every connected client, and throttled during a run.

## The browser extension

The native host passes only `handoff.request`, `host.ping`, `host.openUi`, `host.pickFolder` and
`jobs.query` from the extension, and refuses anything else with `host.actionResult`. Nothing named
`remote.*` or `archive.*` crosses in either direction, and neither does `groups.changed`. A snapshot
reaches the extension without its settings. See `NativeBridgePolicy` in Contracts.
