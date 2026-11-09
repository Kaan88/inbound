# Web UI in the Windows app (D8) and remote management (D9): design

Status: agreed with the owner 2026-11-09 (decisions below), after three reviews (security, the code,
simplicity). Being built. Built so far: section 2 (web-role rights) and the Host and bridge parts of section 1
(bridge mode, `webui.*`, the supervisor; see "Section 1 as built" below). Not yet: the Options page, the firewall
button, publish and Setup shipping the bridge, the web UI's `hostKind` texts and host switcher.

Owner, 2026-11-08: "put webui to installer too. have it be possible to enable webui from desktop app. like how
qbittorrent does." and "since host is separate, like transmission, have desktop or webui be able to connect to each
other easily. maybe I want to manage my homeserver from my desktop ui or vice versa."

## What the user sees

1. **Options → Web UI** (new page in the desktop window):
   - one box, "Let other devices on my network control this PC's downloads";
   - port (default 8080, the same as the Docker server);
   - password, set and changed only here (the box cannot be ticked without one; there is no default password and no
     setup code);
   - the address to type on the other device, and "Open in browser" (signs in with a one-time ticket from the
     window, so the password is never typed into a page on this PC);
   - "Allow through Windows Firewall" (one UAC prompt; see Firewall), with the network's profile shown and a note
     when it is Public;
   - with the box off nothing runs: no process, no port, no timers.
2. **The web UI** is the Docker server's. Served from Windows it shows Windows paths, its folder browser starts at
   the app's folders, and texts that today name Docker follow a new `hostKind` (`windows` or `server`) in the hello
   and the sign-in status. Its header gains a host switcher: a dropdown of saved hosts (kept in that host's
   web UI settings) that opens the chosen host's own web UI in the same tab.
3. **Servers in the desktop window**: a Servers list (File → Servers, and a switcher in the status bar); the
   window itself shows the chosen server (section 5).

## Architecture

The Host stays the single writer; nothing runs a second engine on the same data folder.

### 1. A web bridge process, started by the Host

When the box is ticked, the Host starts `DownloadManager.Server.exe` in a **bridge mode** and stops it when the box
is cleared. The bridge has no engine, takes no data-folder lock of its own and keeps DPAPI (it never calls
`SecretProtector.Use`). Each browser WebSocket or `/api/ipc` call becomes one connection to the Host's hello pipe,
whose `client.hello` asks for the **web role** (a new field). Only this Windows user can open the pipe, and that
user already has full rights, so a client asking for fewer rights is safe.

Why a separate process rather than Kestrel inside the Host: with the box off there is no process and no timer by
construction; network-facing code stays out of the process that owns the engine; the firewall rule names the bridge
exe, not the Host; the server's tested sign-in, origin checks, uploads and static files are reused unchanged, so
there is no library split. Setup grows by the same amount either way (the ASP.NET Core files, about 29 MB on disk,
about 8 to 10 MB in Setup, to be measured, plus the 0.3 MB web UI).

- The bridge reads and writes `auth.json` and `sessions.json` in the Host's data folder (only this user can read
  it). The window sets the password over the pipe; the Host writes it and restarts the bridge.
- On Windows the bridge maps no `/api/auth/setup` (no setup code, no "first visitor sets the password") and refuses
  to start without a stored password. Password changes, pairing codes and token management stay in the web UI's
  Settings as on Docker, behind sign-in.
- Uploads (`.torrent`) go to the Host's upload folder, read by the Host through the existing `upload:<id>` reference.
- A port in use, or any other start failure, comes back to Options and the tray from the bridge's exit code.
- Idle with the bridge running is measured early (Kestrel keeps a 1 s housekeeping timer; ASP.NET Core's HTTP.sys
  server is the fallback if that is too costly).

#### Section 1 as built (2026-11-09)

The protocol is in `docs/ipc-protocol.md` ("The web UI's own settings" and "The web bridge on Windows").

- `DownloadManager.Server.exe --bridge` (`src/DownloadManager.Server/Bridge`): options as one JSON line on standard
  input (nothing on the command line, no password at all: the bridge reads the hash from `auth.json`), `ready` on
  standard output, the reason on standard error, exit codes 0 stopped, 1 crashed, 2 refused, 3 port in use, 4 port
  not allowed. Standard input closing stops it. The endpoints are the server's, moved into `WebFront.cs` with the
  dispatcher or the pipe behind them; sign-in is the server's with `/api/auth/setup` left out and
  `GET /api/auth/ticket` added.
- The Host (`src/DownloadManager.Runtime/WebUi`, `src/DownloadManager.Host/WebBridgeLauncher.cs` and the other
  `WebUi`-linked files): `webui.json`, the five pipe commands and `webui.changed`, and `WebUiSupervisor`, which starts
  the bridge when the box is ticked (or at Host start when it was), restarts it on a port, names or password change,
  restarts it after a crash (1, 2, 4... s, at most 60; a tray notice after 5 short runs), stops it and ends the web
  sessions and every browser's sign-in when the box is cleared (paired apps stay paired), and stops it before the
  Host exits. A port in use or not allowed sets `failed` with the bridge's words and a tray notice, without retrying.
  Setting the password in force again changes nothing; a new one unpairs the paired desktop apps, and the browser
  extensions too unless the window asks to keep them (`keepPairedBrowsers`, as on the web UI's own change).
- Firewall: the Host reads the rules through `HNetCfg.FwPolicy2` (no program started, the same in every Windows
  language) instead of parsing `netsh` output. Ready means the window's rule as it makes it (allow, the bridge, TCP,
  the port, `LocalSubnet` only) covering a network profile in force now (`CurrentProfileTypes`), with no block rule
  for the program covering the port and that profile. While the rule is missing or unreadable, the bridge listens on
  127.0.0.1 and ::1 only; `webui.recheck` (the window calls it after adding the rule) restarts a running bridge on
  every address, and starts nothing that is off, failed or waiting to restart. A process below medium integrity is
  refused the read (`unknown`): builds in this repository are, the installed app is not. Read-only evidence at
  medium integrity (2026-11-09, a throwaway console build outside the repository, run from a normal shell): the
  reader enumerated this PC's 708 inbound program rules, with `CurrentProfileTypes` 6 (Private and Public at once),
  every property read and cast without an error, and a program with no rule came out `missing`. The `ready` path
  against a real rule has not been seen: that needs a rule added, which these tests may not do; it is covered by the
  rule-matching unit tests.
- "Open in browser": `webui.openTicket` first checks in the TCP table that the listener on the port is the bridge
  the Host started (exclusive address use stops another account sharing the port), then makes a ticket with the
  run's random key.
- Where the bridge program is: next to the Host or in its `web` folder (`bridgePath` in `webui.status`). Shipping it
  there is part of the publish and Setup work.

Idle, measured 2026-11-09 on this PC with a test instance (`DOWNLOADMANAGER_INSTANCE`, data under `artifacts\`,
Release build, no window and no browser connected), over 2 minutes after 30 s or more of rest. CPU is the process's
total processor time; switches are the sum of its threads' context switches (`Win32_PerfRawData_PerfProc_Thread`;
threads that ended in between drop out, so these are lower bounds).

| | Host CPU | Host switches | Bridge CPU | Bridge switches |
| --- | --- | --- | --- | --- |
| Web UI off | 0 ms | 63 | no process | no process |
| Web UI on, no browser | 31 ms | 91 | 47 ms | 743 (about 6 a second) |

With the box off the Host has no bridge process, no listening port and no ASP.NET Core module loaded (it does not
reference the ASP.NET Core framework at all; the sign-in file code, `auth.json` and its password hash, is in
`DownloadManager.Runtime/WebAuth`, which the server and the Host both reference: one copy, one owner). The bridge's switches
are Kestrel's 1 s heartbeat and the runtime's own threads: small, but not nothing, which is one more reason the box
is off by default.

### 2. Web-role rights in the Host (the security core)

A web-role session on the Windows Host gets the Docker server's rights, plus rules that only matter on a desktop.
The Host builds a second set of the path-confined parts the way the Docker server does (archive commands, folder
browser, provider and container commands with roots) and routes web-role sessions to them. The two shared services
(`DownloadManagerService`, `RemoteJobCoordinator`) take the caller's roots per call instead of one global value.

- **Roots (where a browser may read or write files)**: the app's folders, worked out live: the download folder,
  every category folder, the favorites and the extraction folder. Because the browser cannot
  change those settings (below), it cannot widen its own roots.
- **Jobs outside the roots** are shown but read-only: no delete-file, move, rename, refresh-URL, extract or
  completion edits.
- **Paths a browser sends** are checked by text first: local fixed-drive paths only; UNC (`\\server\...`), `\\?\`,
  `\\.\`, device names and `:` streams are refused before any file call, so a crafted path cannot make the PC
  contact another machine. Paths are normalised (`GetFullPath`, trailing dots and spaces refused) before the root
  check. A Windows-only test matrix covers junctions, short names, case and the cases above.
- **No way to run anything**: refused for a web session: program settings (post-download command, FFmpeg path,
  update source), completion programs, "open the file when done", system completion actions (shut down, sleep,
  exit), every schedule completion field, the native folder picker, opening windows, shutting the Host down, the
  updater.
- **Settings by allow-list**: a web session may change only listed fields (connections, speed limits, retries,
  duplicate rule, file-type categories' names, queue and scheduler times, archive and provider preferences other than
  keys). Refused: proxy, local-network access, clipboard watching, browser integration and its lists, start at
  sign-in, sounds, folders (download, category, favorites, extraction), the provider key. Jobs a web session adds
  never reach local-network addresses.
- **Web UI configuration** lives in its own file (`webui.json`), behind pipe-only commands (`webui.get`, `webui.set`,
  `webui.setPassword`, `webui.openTicket`). It is never part of `settings.update`, export or import.
- **The browser hand-off** over the network no longer trusts the extension id in its payload; a paired token's role
  decides.
- **Hello names**, appended so older clients are unaffected: `hostKind`, `updater`, `programSettings`,
  `folderPicker`, `openUi`, `shutdown`, `completionActions`. The web UI and the window decide from them.
- **Turning the box off** ends all web sessions (paired tokens are kept unless the user also removes them).
- Tests: every refused command and field for a web session and allowed over the pipe; each path entry point (add,
  move, settings, archive extract, container and torrent paths, folder browse, folder space); UNC and device paths
  refused without network access; jobs outside the roots read-only; the Docker server's rights unchanged.

"Other devices on my network" means any device that can reach the port, and also every other Windows account on
this PC. The bridge binds with exclusive address use and the Host checks that it is the bridge it started before
showing "Open in browser".

Known limit, for the owner to decide (2026-11-09): ports are no cookie boundary. "Open in browser" signs the owner's
browser in at `http://127.0.0.1:<port>`, so its session cookie (HttpOnly, SameSite=Strict, named
`inbound_session_<port>`) is also sent to any other port on 127.0.0.1 once the owner has a page from that port open,
for example one another Windows account on this PC listens on and gets the owner to visit. That account could then
replay the cookie to the bridge. The per-port name stops two sign-ins on one address replacing each other, not this.
Options: a short absolute lifetime for sessions signed in by a ticket, or a session bound to the address it was made
for (which a replay with the right Host header still passes). Until decided it stays as it is: the attack needs a
second account on this PC and the owner opening its page.

### 3. Firewall

Setup is per user and cannot add firewall rules, and a block rule Windows creates at its own prompt overrides any
allow rule. So the bridge never listens beyond this PC until the rule check passes:

- "Allow through Windows Firewall" runs System32 `netsh.exe` (never a helper from the user-writable install folder)
  through one UAC prompt, removes any inbound block rules Windows made for the bridge exe, and adds one inbound allow
  rule: the bridge exe, TCP, that port, local subnet. Profile: Private by default, with Public added if the user
  ticks it (many home PCs are set to Public).
- As built, the one elevated program is System32 Windows PowerShell (fixed arguments, an encoded script), not netsh:
  one prompt has to remove only the block rules and replace this app's own rule, which netsh can't do in one
  command, and a netsh script file would be a user-writable file. The script uses the firewall module of System32
  only (it sets the module path and imports it by full path), compares the program and rule name exactly (no
  patterns), accepts only the fixed rule name and a program inside this app's own folder, and never trusts the
  Host for either. Open question for the owner: keep PowerShell, or accept a cmd-free netsh sequence with several prompts.
- The page shows whether the rule exists and offers it again when the port changes. Uninstall leaves the rule (it
  names a program that no longer exists); a note in INSTALL.txt says how to remove it.

### 4. Pairing and HTTPS

- The extension keeps its pairing code. It can now pair with a Windows PC exactly as with the Docker server.
- A desktop window adding a server  types the address and the
  server's password once; the server returns a token with a new `RemoteClient` role (same sign-in waits and
  lock-out as the page). The password is not kept; the token is kept for this Windows user only (DPAPI). Tokens get
  a role field (none means extension, so older files keep working), work on `/api/ws` for that role only, close
  their open sockets when revoked, and requests that carry a token and no `Origin` are exempt from the host-name
  check (a rebinding page cannot hold a token), so `http://homeserver:8080` works by name.
- Plain HTTP, as on the Docker server today. A desktop window refuses to send a token to a non-loopback `http://`
  address until the user confirms it for that server. A self-made certificate that the window pins at pairing
  (as Syncthing does) is a later step.

### 5. The desktop window managing other servers

- `IpcClient` becomes an interface with the pipe and a WebSocket implementation; every connection is built from a
  `ServerTarget`. The WebSocket accepts an optional `client.hello` (snapshot and events opt-out, as the pipe does);
  short calls go as one-shot `/api/ipc` requests, not new sockets; remote reconnects back off up to about a minute
  and stop events while the window is hidden.
- Progress and package windows get `--server <id>` and read the token from the window's own store; their
  single-instance names include the server id.
- Data from a remote server is marked remote: no local file checks, no icons read from real files (extension icons
  only), no shell open or reveal, no sounds from paths. A remote `host.exiting` means "disconnected", not "quit".
- Folder choosers use a dialog backed by `folders.browse`; `.torrent` and container files are uploaded; "extract
  from disk" offers only the server's folders.
- Options split: this PC's settings (theme, start at sign-in, clipboard, updates, sounds) always go to the local
  Host; the server's own page shows the fields it allows. Settings saves carry a revision so two clients saving at
  once do not overwrite each other silently.
- Completion window and notifications come from the window's own job events while it is open; nothing alerts for
  a remote server while it is closed.
- The CLI stays local-only in the first version.

### 6. Discovery

Manual address. No network announcement (mDNS): it adds periodic network traffic and wake-ups on every PC, and a
home setup has one or two servers.

## Found on the way (not part of D8/D9)

Downloads are saved without the "came from the internet" mark (Zone.Identifier) that browsers add, so a file
handed off from the browser loses SmartScreen's and Office's warnings. Only extracted files get it today. Recorded
as its own item.

## Owner decisions (2026-11-09)

1. The desktop window manages other servers **natively** (section 5): it switches between "This PC" and saved servers. The app-window (WebView2) variant is not built.
2. A browser may save and browse only in **the app's folders** (download, category, favorites, extraction), worked out live; it cannot change them.
3. The web UI switches hosts with a **dropdown in its header** that lists saved hosts and opens the chosen host's own
   web UI in the same tab (sign in once per host). One page controlling several hosts is not built: it would need
   cross-site sign-in and looser origin checks on every host, and would have to cope with every host's version.

## Order of work

1. Web-role rights in the Host (section 2) with the Windows path tests. Reviewed as security work.
2. Bridge mode in `DownloadManager.Server`, started and stopped by the Host; `webui.*` pipe commands; idle measured.
3. Options → Web UI page; web UI texts by `hostKind` and the host switcher; publish and Setup ship the bridge and
   the web UI; the firewall button.
4. Token roles and password pairing (both server kinds), then the native Servers list and switcher (section 5).
5. End to end: a browser on another device → this PC; the window → the Docker server on the owner's server; the
   window → a test instance on this PC on another port. Reviews; ROADMAP evidence.
