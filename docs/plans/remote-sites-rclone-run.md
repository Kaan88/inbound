# Sites: the rclone run (ROADMAP SITES.0)

Date: 2026-11-14. rclone v1.76.0 (go1.27.3, statically linked). Companion to `remote-sites.md`; that file is not edited here.

**Everything was run on Linux x86_64 only**, as a trial with no product code. Not checked on Windows: how `rclone.exe`
behaves (start time, memory, the `--config` equivalents, unix-socket rc, child-process lifetime with a job object,
the Windows environment-variable exposure, the Windows SFTP/FTP/WebDAV code paths, antivirus effects on a bundled exe).
Where a Windows statement appears below it is from the docs or general knowledge and says so. The Windows zip and
exe were downloaded only to measure them and were never run.

Machine note (owner, 2026-11-14): the machine was under heavy load from other work (load average above 100 on 28 cores
at one point, about 7 to 9 during the final measurements). **Point 10's timing figures are provisional.** Functional
results are not affected.

Servers used, all on 127.0.0.1 on high ports: an unprivileged OpenSSH `sshd` 10.6 with its own config, host keys and
authorized keys (key auth only; an unprivileged sshd cannot check passwords); `rclone serve sftp|ftp|webdav` from the
same binary; a pure-ftpd container (`stilliard/pure-ftpd`, explicit FTPS, our own self-signed certificate). Keys and
certificates were generated for the run. Caveat: where the server is `rclone serve`, what a server leaves behind after
a cut upload (point 7) is that server's behaviour, not a real FTP or WebDAV server's.

## Source and sizes

| Item | Value |
| --- | --- |
| Version | v1.76.0 (`https://downloads.rclone.org/version.txt`) |
| Linux zip | `https://downloads.rclone.org/v1.76.0/rclone-v1.76.0-linux-amd64.zip`, 32,194,302 bytes, SHA-256 `b00f00b3266c3764413afe599d3ec316f6f43b3e0ea8a0bc1d186d2133e46df7` |
| Windows zip | `https://downloads.rclone.org/v1.76.0/rclone-v1.76.0-windows-amd64.zip`, 32,193,987 bytes, SHA-256 `8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc` |
| Check | Both hashes equal the lines in the `SHA256SUMS` file of the same directory (that file is PGP-clearsigned; the signature was not verified). |
| Unpacked | `rclone` (Linux) 85,754,016 bytes; `rclone.exe` (Windows) 85,647,872 bytes |

For the plan's "Setup, updates" section: the exe is about 85.6 MB unpacked and the official zip is 32.2 MB, so the
update zip grows by about 32 MB and the unpacked install by about 86 MB. That is a large share of the 200 MB install
(about 43%) and of an update download. "About a third of the unpacked exe" in the plan is right (37.6%). A trimmed
rclone (fewer backends) would need our own build and is out of scope.

## 1. `fs` as an object, no config, password: files left behind

Ran: `rclone rcd --config "" --rc-addr 127.0.0.1:0` (HOME, XDG_CONFIG_HOME and XDG_CACHE_HOME pointed into the trial
folder, listings of `~/.config/rclone` compared before and after), then `operations/list` with

    {"fs":{"type":"sftp","host":"127.0.0.1","port":"<p>","user":"alice","pass":"<obscured>"},"remote":""}

and the same as a connection string (`":sftp,host=...,pass=<obscured>:"`).

Seen:
- Works with both forms. `config/listremotes` and `config/dump` stayed empty.
- **The password must be obscured.** A plain password gives `input too short when revealing password - is it obscured?`
  (a plain value that happens to look like long base64 would not give that error and would be revealed to garbage; so
  always obscure). `core/obscure {"clear":"..."}` over rc does the obscuring, so the Host need not implement it
  (clear text only crosses the loopback connection).
- No file appeared in the trial folder or in the real `~/.config/rclone` (listing hash unchanged) from rcd's own
  use. The only cache-dir files came from `rclone serve sftp` (its own server host keys under
  `<cache>/serve-sftp/`), not from rcd.
- `--config ""`, `--config /dev/null`, `--config notfound` and `RCLONE_CONFIG=` all print `Configuration is in memory
  only` from `rclone config file`, and `rclone config create` with each wrote no file. So all four work on Linux. The
  Windows equivalent is `--config ""` (same code path) or `NUL`; from the docs and Go's null device, not run.
- Cache location: `--cache-dir` / `RCLONE_CACHE_DIR`, default `~/.cache/rclone` (`%LOCALAPPDATA%\rclone` is expected on
  Windows, not checked). Temp files: `--temp-dir`. Point both at a folder under the Host's state folder when starting
  rcd so nothing lands elsewhere; this costs nothing.
- Error responses from rc **echo the whole request** under `input`, including `pass` and `key_file_pass`
  (seen: the plain passphrase came back in the error body). The Host must never log rc error bodies unredacted.
- `--rc-addr 127.0.0.1:0` works; the chosen port is printed at the default log level as
  `NOTICE: Serving remote control on http://127.0.0.1:<port>/`. Reading it from the child's output is possible. A
  Host-chosen free port is simpler and has a small race.
- The docs say that unless rc auth is set, only remotes already in the config may be served through `--rc-serve`;
  we do not use `--rc-serve`.
- On-the-fly remotes take an absolute server path through `_root` in the `fs` object (documented), or after the colon
  in a connection string. Putting an absolute path in `remote` did not work against SFTP (`directory not found`).

Answer: works, nothing is written, passwords must be obscured (use `core/obscure`). Plan keep ("Credentials and
trust", `fs` as an object). Add: pass absolute server paths with `_root`; point `--cache-dir` and `--temp-dir` into
our state folder; never log rc error bodies.

## 2. Passing the rc user and password

Ran four starts and looked at `ps -o args=` and `/proc/<pid>`:

| Way | `ps args` shows the password? | Result |
| --- | --- | --- |
| `--rc-user U --rc-pass P` | **Yes**, in clear | do not use |
| `RCLONE_RC_USER` / `RCLONE_RC_PASS` | No. `/proc/<pid>/environ` is mode 0400, owner only | works; 401 without or with a wrong password, 200 with |
| `--rc-htpasswd <file>` (apr1 hash made with `openssl passwd -apr1`) | No (path only) | works; 401 / 200 as above |
| `--rc-addr unix:///path/rc.sock` | No | works; socket created `srwxr-xr-x` (connect needs write, so only the owner connects under the usual umask) |

Further:
- With a unix socket and no user/password, any call is accepted from whoever can connect; with env credentials added,
  401 without and 200 with. No TCP port is opened with a socket.
- **`-vv` logs the env password**: `Setting --rc-pass "<the password>" from environment variable RCLONE_RC_PASS`.
  At `-v` the line is masked (`--pass XXXX`). Never run rcd above the default or `-v` level in production, and do not
  put the rcd log at debug into support bundles.
- Windows: environment variables are readable by other processes of the same user (and by admins); a process of the
  same user could also read a user-only file. So env and htpasswd protect against other users and against `ps`/Task
  Manager command-line views, not against malware running as the same user. From general knowledge, not run. Unix
  sockets exist on Windows 10 1803 and later and Go supports them, so `unix://` may work there; **not run**.
  Loopback TCP with env credentials is the safe common choice; the Host-held credentials are random per start.

Answer: environment variables (simplest, no file) or htpasswd; never flags. A unix socket is optional hardening on
Linux and unverified on Windows. Plan keep ("Start": "by environment variable or an htpasswd file"); settle on
environment variables, and add the `-vv` warning.

## 3. SFTP host keys

Ran against the unprivileged sshd (ed25519 host key A), with `host_keys`, `known_hosts_file`, `pin_host_key` and then
with the server swapped for another host key on the same port.

Options present in v1.76.0 (`rclone help backend sftp`): `known_hosts_file`, `pin_host_key`, `host_keys`,
`host_key_algorithms`. `host_keys` is a comma-separated list of `algo base64key` entries (full public keys, not
fingerprints), at most 16, and **rclone then offers only those algorithms**.

Seen over rc (`operations/list`, `fs` object):
- **Default (nothing set): no check**, connects, logs `NOTICE: No host key validation is being performed...`.
- `host_keys` = the right key: connects. Both keys in the list: connects.
- `host_keys` = a different key (or the server really replaced by a new key): refused, HTTP 500, exact text:
  `NewFs: couldn't connect SSH: ssh: handshake failed: sftp: host key mismatch for 127.0.0.1:46022: server offered
  ssh-ed25519 key with fingerprint SHA256:<offered>; pinned fingerprints for this algorithm: SHA256:<pinned> -- if the
  server key has changed legitimately, clear host_keys and re-run with --sftp-pin-host-key`
  Both fingerprints are in the message, so the "stops with both fingerprints" design is possible from this text.
- Server that now offers only an RSA key while an ed25519 key is pinned: `ssh: no common algorithm for host key; we
  offered: ["ssh-ed25519"], peer offered: ["rsa-sha2-512" "rsa-sha2-256"]`. This is a different message for the same
  situation from the user's view; the Host must treat both as "host key problem".
- Server with two host keys (ed25519 and RSA): pinning either one alone works.
- `known_hosts_file`: matching line (`[127.0.0.1]:port algo key`) connects; a wrong key gives `knownhosts: key
  mismatch` (no fingerprints); an empty file gives `knownhosts: key is unknown`; a missing file gives `couldn't parse
  known_hosts_file: open ...`. A file path is needed, so `host_keys` is the better fit for us.
- `pin_host_key` is documented as "once, as a flag". See point 4 for what it does over rc.

Version: from the changelog, `pin_host_key` and `host_keys` came in **v1.75.0 (2026-07-31)**; the "no host key
validation" warning in v1.74.0; `known_hosts_file` in v1.56.0 (2021-07-20). So the minimum rclone for the plan's
`host_keys` design is 1.75.0. "Use my own rclone" must check for at least that, and an older rclone must be refused
for SFTP rather than silently connecting unchecked.

Answer: unchecked by default (plan correct); `host_keys` works and a changed key is refused with a clear text. Plan
change in "Credentials and trust" and "Which binary": minimum version 1.75.0.

## 4. Getting the host key for the first prompt

Ran: `config/create` of a named in-memory remote with `pin_host_key=true`, then a list, then `config/get`.

- With working credentials: log `Accepted ssh-ed25519 host key SHA256:... on first use`, then `Pinned ... in config`, and
  `config/get` returns `host_keys` with the full key. So the full key can be read back over rc and the temporary remote
  removed with `config/delete`.
- On the **on-the-fly `fs` object**, rclone says `pin_host_key on an on-the-fly remote cannot persist the pinned
  key`; only the fingerprint is logged (NOTICE). So a named in-memory remote is needed for this route.
- **Pinning needs a successful login.** With wrong or no credentials (server rejects auth) the key is not recorded:
  `config/get` shows no `host_keys`. The credentials are also sent to a server whose key the user has not yet
  approved. That breaks the plan's rule "nothing is sent to the server before the user says yes".
- A deliberately wrong `host_keys` makes the handshake fail before authentication and the error contains the offered
  key's **fingerprint** (point 3), but not the key itself. A fingerprint cannot be fed back as `host_keys`.
- Check of an own handshake: a 50-line Python probe (trial only, not product code) opened a TCP connection, sent the
  version line, a KEXINIT offering `curve25519-sha256` with the host key algorithm list, and a KEX_ECDH_INIT with 32
  random bytes; the server answered KEX_ECDH_REPLY containing the host key blob. The key and its SHA-256 fingerprint
  equalled `ssh-keygen -lf` of the server's key. Nothing was authenticated; the server logged `Connection closed by
  authenticating user ... [preauth]`. Asking for `ssh-ed25519` first or `rsa-sha2-512` first returned a different key
  type on a two-key server.

Answer: rclone cannot give us the key before authentication. A small SSH handshake of our own is needed; in .NET
about 100 lines on `TcpClient` and `RandomNumberGenerator` (version line, KEXINIT, ECDH_INIT with random bytes, parse
the reply's host key blob string, SHA-256 and base64 for the fingerprint, close). Signature verification is not
needed there because the later real rclone connection verifies the key against `host_keys` including the signature.
The Host asks for the algorithms the user would accept (ed25519 first, then RSA, ECDSA) and passes the pinned
`algo base64` as `host_keys`. Alternatives: SSH.NET's `HostKeyReceived` (adds a dependency, not tried). Plan change in
"Credentials and trust" step 1 and the "To settle" item 4: choose the own handshake; drop the
"rclone's one-shot pin flag" idea.

## 5. Key files

Ran `operations/list` with keys of every kind against the sshd (server logs confirmed the accepted key each time).

| Key | Passphrase | Result |
| --- | --- | --- |
| ed25519, OpenSSH format | none | works (`key_file`) |
| RSA, PEM (`BEGIN RSA PRIVATE KEY`) | none | works |
| RSA, PEM, `Proc-Type: ENCRYPTED` | `key_file_pass` obscured | works |
| same | passphrase not obscured | `input too short when revealing password - is it obscured?` |
| same | wrong (obscured) | `failed to parse private key file: x509: decryption password incorrect` |
| same | none given | `failed to parse private key file: ssh: this private key is passphrase protected` |
| **ed25519, new OpenSSH format, passphrase** | `key_file_pass` obscured | **works** |
| **RSA, new OpenSSH format, passphrase** | obscured | **works** |
| new format, no passphrase given | | `ssh: this private key is passphrase protected` |

- `key_pem` (inline) works for all three kinds, **but only with literal backslash-n sequences** in the string (the form
  the help shows: `awk '{printf "%s\\n", $0}'`). Real line breaks in the JSON string give `pem key not formatted
  properly: invalid syntax`. So the Host must convert a pasted key to a single line with `\n` text.
- The passphrase goes in `key_file_pass` even with `key_pem`, and it must be **obscured** (`core/obscure`).
- `key_pem` set together with a bad `key_file`: the `key_pem` is parsed first and decides (the docs say it overrides);
  a good `key_pem` with a bad `key_file` was not separately confirmed because the first form failed. Not settled.
- `rclone help backend sftp` still says new-format encrypted keys "can't be used". **That text is wrong for v1.76.0**:
  both encrypted new-format keys worked. The changelog has no entry explaining it; which version changed it is not
  known. The plan's statement and its open item ("detect the current OpenSSH format with a passphrase and say what
  to do") can be dropped for 1.76.0, but a test against the minimum supported version is needed if "use my own
  rclone" stays.
- Using `key_pem` means no key file on disk is needed for rclone (nothing to clean up); `key_file` needs the Host to
  write a file somewhere rcd can read.

Answer: all three formats work in 1.76.0; the passphrase is `key_file_pass`, obscured. Plan change in "Credentials
and trust" (Key files bullet): remove the conversion/detection requirement; add the single-line `\n` rule.

## 6. FTPS with a self-signed certificate

Ran against the pure-ftpd container (explicit TLS required, plain FTP refused) with an ECDSA self-signed certificate
(SAN 127.0.0.1) and a second unrelated one.

Seen:
- No trust: `tls: failed to verify certificate: x509: certificate signed by unknown authority`.
- `explicit_tls=false` to that server: `Sorry, cleartext sessions and weak ciphers are not accepted on this server.`
- **`no_check_certificate=true` (remote option; `--ftp-no-check-certificate`) connects.** It switches off all
  verification of that remote (no pinning, no hostname check); it is all or nothing.
- The ftp backend has **no CA option of its own** (`rclone help backend ftp`: only `no_check_certificate`).
- Global `--ca-cert <file>` on the command line, `RCLONE_CA_CERT=<file>` in the environment and (Linux only, Go's
  own behaviour) `SSL_CERT_FILE=<file>` all make the right certificate trusted and a different certificate refused.
- **They only work when set at process start.** On a running rcd: `options/set {"main":{"CaCert":[...]}}` returns `{}`
  but changes nothing; per-call `_config {"CaCert":[...]}` changes nothing; rewriting the bundle file under a running
  rcd changes nothing. Only a restart with the new file works. (Per-call `_config` does work for some options, see
  points 7 and 8.)
- A certificate trusted this way is trusted by every remote of that rcd (it is a trust anchor in one process-wide
  pool), but name checking still applies, so a leaf pinned for host A is not accepted from host B unless B presents
  that same certificate.

Not run: implicit FTPS against pure-ftpd (rclone's own `serve ftp --cert` turned out to speak implicit TLS, and
`tls=true` worked against it only with `no_check_certificate`; that was only a side observation), the HTTPS WebDAV
CA test (a quoting error in my connection string made it inconclusive).

Answer: per-site CA is not possible. Options for the plan: (a) the Host keeps one CA bundle of all certificates the
user trusted, passed as `RCLONE_CA_CERT` at rcd start, and restarts rcd (when idle, or between transfers) when the
list changes; (b) `no_check_certificate` plus the Host's own pinning check: the Host does the AUTH TLS handshake
itself, compares the certificate fingerprint with the stored pin and refuses to start the rclone call on a
mismatch (not built; leaves a gap between check and use). "Do not verify is never offered" stays true for the user,
but (b) uses it internally. Plan change in "Credentials and trust" (FTPS bullet): the "passed as that site's CA"
wording is wrong; choose (a) or (b). (a) is closer to the plan's intent; it also needs the same fetch-the-certificate
step as the host-key probe (a TLS handshake of our own, which .NET's `SslStream` does directly).

## 7. Partial files and interrupted transfers

Ran 200 MiB transfers at a 20 MiB/s limit, `kill -9` of rcd after 4 s, started a new rcd, and ran the same
`operations/copyfile` again; looked at the destination folder every second.

Names seen while transferring (default settings):

| Direction | Name while running |
| --- | --- |
| download to local folder (from SFTP, FTP, WebDAV, local) | `big.bin.<8 hex>.partial` in the destination folder, renamed to `big.bin` at the end |
| upload to SFTP | `big.bin.<8 hex>.partial` on the server, renamed at the end |
| upload to FTP | same (`.partial`) |
| upload to WebDAV (server was `rclone serve webdav`) | **no temporary name**: the file grows under its final name |

- The suffix is `--partial-suffix` (default `.partial`); `--inplace` writes the final name directly. Both are
  settable per call: `_config {"Inplace":true}` gave `big2.bin` growing under its final name;
  `_config {"PartialSuffix":".inbound-part"}` gave `big2.bin.<hex>.inbound-part`. Both finished with the right SHA-256.
- The 8-hex part was the same before and after the restart for one source and destination pair (it is not random per
  run).
- **A cut transfer does not resume.** After `kill -9` the partial stayed at about 84 MB. The second run started again
  from zero (about 31 MB after 1.5 s at 20 MiB/s), reused the same partial name, and sent the whole file. This held
  for SFTP, FTP, WebDAV (download) and local to local, and for uploads to SFTP, FTP and WebDAV. After a cut upload to
  WebDAV the server was left with a **truncated file under the final name** (84 MB of 200 MB) until the second run
  overwrote it; on SFTP and FTP the same wreck is at the `.partial` name only.
- A file already complete at the destination was skipped (0 bytes sent) by the default comparison; that check was
  light (name, size, time).
- An rclone stop (`job/stop`, SIGTERM, SIGINT, `core/quit`) removes the partial file; only `kill -9` (or a crash)
  leaves it. So the Host must remove stray `*.partial` files for batches it knows about after a crash, or on the next
  start of that batch.

My wait loop in the first run polled the wrong job number (jobs are numbered across all rc calls, not only
transfers), so the "run 2 finished" lines in the raw output of that run are not meaningful; the sha and size checks
come from a second run (`default`, `Inplace`, `PartialSuffix` cases above) that polled the returned job id.

Answer: partials are `<name>.<hex>.partial` next to the final name for downloads and for SFTP/FTP uploads; WebDAV
uploads have no temporary name; no resume anywhere, a restart sends the whole file again. Plan changes:
"Transfers", Payload placement: confirmed ("temporary name ... renamed in place"), name not ours; confirmed "uploads
use a temporary name on servers that can rename" (SFTP, FTP yes, WebDAV no). "Resume": state in the UI that a stopped
large file starts again from the beginning; the "Rejected" adapter alternative (own HTTP engine through
`rclone serve`) is therefore worth keeping in mind for big downloads, since the HTTP engine does resume.

## 8. Progress, groups, stop, bandwidth

Ran two async jobs from SFTP to two folders with `_group` `gA` and `gB`, then more.

- `operations/copyfile` with `_async:true` returns `{"jobid":N}`; `_group` sets the stats group (default
  `job/<N>`).
- **Job numbers count every rc call**, async or not (the first copy came back as 2 or 6). Always use the returned id.
- `job/status {"jobid":N}`: `finished`, `success`, `error`, `group`, `startTime`, `duration`, `output` (`{}` on
  success for copyfile). `job/list` gives `runningIds`, `finishedIds`, `jobids`. Finished jobs are kept for
  `--rc-job-expire-duration` (default 1 minute, checked every 10 s), so the Host must read the result in time or raise
  it (the lifetime test used 5 min).
- `core/stats {"group":"gA"}`: `bytes`, `totalBytes`, `speed`, `transfers`, `totalTransfers`, `checks`, `errors`,
  `lastError`, `eta`, and `transferring` with one entry per file: `name`, `size`, `bytes`, `percentage`, `speed`,
  `speedAvg`, `eta`, `group`, `srcFs`, `dstFs`. `core/stats` without a group aggregates everything, and
  `transferring` entries carry their group. `core/group-list` lists groups; `core/stats-delete {"group"}` removes
  one. The `srcFs`/`dstFs` fields contain the connection string with a short random id, not the password.
- `sync/copy` over a folder: `transferring` lists the files in flight (3 at once, `Transfers` default 4);
  `_config {"Transfers":1}` per call gave one file at a time; works.
- **Stop:** `job/stop {"jobid"}` and `job/stopgroup {"group"}` return `{}` at once; the job ends shortly after
  (under 2.3 s in the runs) with `error:"context canceled"`, `success:false`. The partial file is removed. The other
  job continued unchanged (about 6 MB/s on its own at a 6 MB/s limit). Stopping a `sync/copy`: files already done
  stay, the files in flight vanish (partials deleted); a new `sync/copy` skips the finished file (`checks` 1) and
  restarts the others from zero.
- **There is no pause.** The rc command list has `job/stop`, `job/stopgroup`, `serve/stop` and no pause or resume.
  Pause = stop; resume = a new call that restarts the in-flight files at byte zero (point 7). For big files the
  UI's "Pause transfers" costs everything sent so far for those files. Honest label: "Stops and restarts files
  that were in progress".
- **`core/bwlimit`** is global to the process: `{"rate":"20M"}` (also `"up:down"` like `"10M:2M"`, `"off"`); it returns the
  parsed limit (`bytesPerSecondTx`, `bytesPerSecondRx`). Changed while two transfers ran: 20 MiB/s total measured 20 MB/s;
  to 4 MiB/s measured 4 MB/s within about 1 s; to 40 MiB/s measured 41 MB/s; 8 to 2 MiB/s measured 8 then 2 MB/s
  total (shared evenly between the two jobs, about 4.2 and 4.2 MB/s). A change to 16 MiB/s measured 12 MB/s over a
  3 s window starting 1.5 s later (the machine was loaded; not a clean reading). It applies to running transfers
  immediately.
- **Per-job limits: not settled.** A per-call `_config {"BwLimit":"2M"}` was tried three times and gave no clear
  difference from no limit (the unlimited rate on this loaded machine was itself only about 3 to 5 MB/s for SFTP
  after a slow start, so the comparison could not separate them). Treat the limit as global only, which is what
  the plan says ("applied with rclone's `core/bwlimit`").

Answer: groups, per-file progress, and stopping one job while another runs all work; bandwidth changes apply live;
no pause exists. Plan change in "Transfers" and the toolbar: "Pause transfers" is stop plus restart, and it has a
cost for big files; the Host must store the batch state itself (already planned). Add: read job results within the
expiry window.

## 9. OAuth token refresh with an in-memory config

Not testable with a real account. Ran: `config/create` of a `drive` remote with a junk token whose expiry is in 2099
(`parameters.token`, `opt.nonInteractive`, `noObscure`), `config/get`, `config/update` with another token, and a
list with an on-the-fly `fs` object of type `drive` with the same junk token.

Seen:
- `config/create` and `config/update` work with `--config ""`; `config/get` returns the token exactly as stored.
  Nothing is written to disk. (They return a `config_refresh_token` question object in `Option` when a token already
  exists; with `nonInteractive` the value stayed what was set.)
- The on-the-fly `fs` object with a token builds the backend and makes a real call (the junk token was rejected by
  Google with 401, so that one call reached Google with a fake token; no account involved). It works with a token, so
  an on-the-fly OAuth remote is possible, **but** the SFTP pin message ("on-the-fly remote cannot persist") shows the
  rule: an on-the-fly remote has no name to write a changed value back to. The pinning case for a **named** in-memory
  remote did write the new value back into the in-memory config and `config/get` showed it. That is the same
  `SetValueAndSave` path the OAuth refresh uses (from the source-level knowledge that the token is saved after
  a refresh; not read in this run).

Known: named in-memory remote works, tokens can be set and read over rc, nothing touches disk.
**Not tested:** an actual refresh. Whether rclone writes the refreshed token into the in-memory config of a named
remote (very likely, by the pinning evidence) and whether `config/get` shows it; how the refresh behaves when a
token's `expiry` is past; Dropbox and OneDrive refresh-token rotation.
Plan: use a named in-memory remote per OAuth site (not an on-the-fly object), and after each batch (and before rcd
stops) call `config/get`, compare `token`, and re-seal it if it changed. Plan change in "Credentials and trust",
OAuth bullet: the "how it learns of a refresh is not documented" question becomes "poll `config/get`" subject to the
untested refresh. The `config/oauthstatus`, `config/oauthstop` and `config/providers` rc commands exist (listed); the
authorize flow was not run.

## 10. Start time, memory, size

Provisional: the machine was loaded (see the top). Linux only.

- First successful rc call after spawn, 10 runs (ms): 75, 24, 29, 21, 24, 23, 23, 25, 23, 40. **Median 24 ms**, min
  21, max 75. Measured from process spawn until `POST core/version` returns 200 (curl polling in a loop, so it
  includes the polling cost). Provisional. A Windows exe adds antivirus scanning of an 86 MB file on first runs;
  not measured.
- Idle RSS 68.8 MB after 1 s and still 68.8 MB after 30 s (peak equal). 18 threads.
- Idle CPU over 60 s (after 30 s of idle): 0 ticks (1/100 s) for the main thread, 1 tick for all threads since start,
  and 0 context switches in the main thread. So an idle rcd does not wake up in a way this measurement could see.
  Provisional, since only the main thread's context switches were counted.
- Binary: 85,754,016 bytes (Linux), 85,647,872 bytes (Windows exe), zips 32.2 MB (see top).

Answer: starts in about 25 ms on Linux, about 69 MB resident when idle, no idle CPU. This supports "started when
needed, stopped when idle" as cheap; the idle delay can be short. The 69 MB is the memory cost to weigh against
"Sites off: never starts".

## Also checked: lifetime and shutdown

- **Parent dies:** rcd started plainly under a bash parent that was then killed with `SIGKILL`: rcd **kept running**
  (parent became pid 1) and the transfer kept going (12.6 MB to 25.2 MB in 3 s). rclone has no option for this.
  Started through `setpriv --pdeathsig SIGKILL`: rcd was killed with its parent and the transfer stopped. So the Host
  must do it: a Windows job object with kill-on-close (the plan's wording, not run on Windows), and on Linux
  `prctl(PR_SET_PDEATHSIG)` (note: it fires when the parent *thread* that spawned the child exits; spawn from a
  long-lived thread). A stray rcd after a Host crash is the risk; a startup sweep for an old rcd could use the
  per-start random credentials being lost as a reason to kill any rclone that the Host did not start (pid file).
- **Clean shutdown:** `core/quit` exits with code 0 in about 1.5 s with a transfer running (partial file removed);
  `core/quit {"exitCode":7}` exits with 7. SIGTERM exits with 143 in 8 ms, SIGINT with 130 in 4 ms, partials removed in
  both. `kill -9` leaves partials. Plan keep ("stopping it at shutdown"): `core/quit`, wait up to a few seconds, then
  kill.

## Changes this suggests to remote-sites.md

1. "Which binary" / "rclone as a child process": minimum rclone 1.75.0 (for `host_keys`); refuse older for SFTP.
2. "Start": credentials by environment variables; never flags; do not use `-vv` (it logs the password); add
   `--cache-dir` and `--temp-dir` into our state folder; `--config ""` is confirmed (`/dev/null` and `notfound` too).
3. "Credentials and trust": obscure every password and key passphrase with `core/obscure`; absolute paths through
   `_root`; never log rc error bodies (they echo secrets).
4. SFTP host key step 1: use our own small SSH handshake to read the key (rclone's pin flag needs a working login
   and so sends credentials first, and an on-the-fly remote cannot hold the pin). Message texts for a changed key are
   available (point 3), including a second form when the algorithm no longer matches.
5. FTPS bullet: no per-site CA exists. Choose a Host-managed CA bundle in `RCLONE_CA_CERT` read at rcd start (restart
   rcd when the list changes), or `no_check_certificate` with the Host's own certificate-pin check.
6. Key files bullet: current OpenSSH-format passphrase keys work in 1.76.0; remove the conversion work; `key_pem`
   needs literal `\n`.
7. Transfers: no resume for any backend (a stop or crash restarts the file); WebDAV uploads have no temporary name;
   "Pause transfers" is stop and restart; say so in the UI; remove stray `.partial` after a crash; job results expire
   after a minute by default.
8. OAuth bullet: named in-memory remote per site and a `config/get` write-back check after each batch (refresh itself
   still untested).
9. "Never outlives the Host": rclone does nothing itself; the Host must use the job object or PR_SET_PDEATHSIG.
10. Setup size: about 86 MB unpacked and 32 MB in the zip, about 43% of the current install.
11. Tests: `rclone serve sftp|ftp|webdav` was enough for most checks; SFTP host-key change and key formats need an
    OpenSSH `sshd` (unprivileged one worked, key auth only); explicit FTPS needs a real FTP server (pure-ftpd in a
    container worked; `rclone serve ftp --cert` is implicit TLS).

## Still not known

- Everything on Windows (point 2's socket option, point 10 timings, antivirus, job-object lifetime, `NUL`).
- Point 10 timings on an unloaded machine (provisional here).
- An actual OAuth refresh and where its new token appears (point 9).
- Per-job bandwidth limits (a per-call `_config` limit was inconclusive).
- Which rclone version made encrypted new-format SFTP keys work, and whether `key_pem` beats a bad `key_file` (point 5).
- Implicit FTPS against a real server, FTPS with a CA-signed certificate, HTTPS WebDAV with a self-signed certificate.
- Truncated-file behaviour after a cut upload to a real WebDAV, FTP or cloud server (here the servers were rclone's).
- The unix-socket rc on Windows and its permissions there.
- Cut transfers over cloud object stores (multipart uploads leave server-side parts; not touched).
- What `operations/fsinfo` reports per kind (not run; not one of the ten points).
