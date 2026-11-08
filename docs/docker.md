# Running Inbound in Docker

The image runs the headless server (`src/DownloadManager.Server`) with the web UI (`webui/`) on port 8080. It is for a
home server or NAS on a network you trust; for access from outside, put a reverse proxy with TLS in front (below).

What is inside:

- the server on the ASP.NET Core 10 runtime image (Ubuntu 24.04), started as a non-root user;
- the web UI as static files (no Node at run time);
- the official 7-Zip 26.04 for Linux (`7zz`) in `/app/tools/7-Zip`, for extraction (its license is next to it);
- no FFmpeg (see "Adding FFmpeg").

Image: `ghcr.io/kaan88/inbound` for `linux/amd64` (arm64 is not published yet), built when a version tag is pushed
(`.github/workflows/docker.yml`). Tags: the version (`0.6.0`), the minor line (`0.6`) and `latest` (not for a
pre-release such as `v0.6.0-rc1`).

## Quick start

```sh
mkdir -p ~/inbound/data ~/downloads
docker run -d --name inbound --init --restart unless-stopped \
  -p 8080:8080 \
  -e PUID=$(id -u) -e PGID=$(id -g) -e TZ=Europe/Istanbul \
  -v ~/inbound/data:/data -v ~/downloads:/downloads \
  ghcr.io/kaan88/inbound:latest
```

Open `http://<server>:8080`. Sign-in is on: the first visit sets the password. A browser that is not on the server
itself (which is every browser, for a container) must also give the setup code the server writes to its log once:

```sh
docker logs inbound 2>&1 | grep "setup code"
```

To set the password without the page, give it in a file (a Docker secret) or a variable, below.

## Compose

`docker/compose.example.yml` has the two volumes, `PUID`/`PGID`, `TZ`, the password as a Docker secret, sign-in on,
and a commented Caddy reverse proxy. Copy it to `compose.yml`, then:

```sh
printf '%s' 'a long password' > inbound-password.txt && chmod 600 inbound-password.txt
docker compose up -d
```

The password file is read by the server's user (`PUID`), so it must be readable by that user. A trailing newline is
ignored.

## Settings

| Variable | Default | What it does |
| --- | --- | --- |
| `PUID`, `PGID` | `1000` | The user and group the server runs as. Use the owner of your download folder on the host (`id -u`, `id -g`). `0` is refused. Only the numbers count: no account is created in the container, and an existing one with the same id (the base image's `app` is 1654) changes nothing. |
| `TZ` | `Etc/UTC` | Time zone for the scheduler, quotas and the log. |
| `DOWNLOADMANAGER_PASSWORD_FILE` | - | A file holding the sign-in password. The web UI can't change it then. Changing it and restarting signs every browser out and removes the paired extensions. |
| `DOWNLOADMANAGER_PASSWORD` | - | The password itself (visible in `docker inspect`; the file is better). |
| `DOWNLOADMANAGER_AUTH` | on | `off` turns sign-in off. The server then listens beyond loopback only with `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1` as well, and anyone who reaches the port controls it, saved logins included. |
| `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED` | - | `1`: with sign-in off, listen on every address anyway. |
| `DOWNLOADMANAGER_HOSTS` | - | Host names you type in the browser for the server (comma separated), besides IP addresses and `localhost`. Needed for a name such as `nas.home.arpa`: other names are refused, against DNS rebinding. |
| `DOWNLOADMANAGER_TRUST_PROXY` | - | The reverse proxy's address or network, such as `172.30.0.10` or `172.30.0.0/24` (several separated by `,`). `X-Forwarded-For` and `X-Forwarded-Proto` are believed from those addresses only, and every browser through them is asked for the setup code. `1` is refused: a client that reached the port directly could then pick a new address for every wrong password and pass as this machine. |
| `DOWNLOADMANAGER_ROOTS` | `/downloads` | The folders the web UI may use, separated by `:`. Mount a volume at each. The first is the default download folder. |
| `DOWNLOADMANAGER_DATA` | `/data` | The state folder. |
| `DOWNLOADMANAGER_URLS` | `http://0.0.0.0:8080` | Where the server listens inside the container. |
| `DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS` | - | `1`: start although the saved secrets can't be opened with `secrets.key` (they are lost). See "Backup". |

## Volumes and file owners

- `/data`: the download list, settings, logs, the password hash (`auth.json`), the paired browsers and `secrets.key`.
  It must be a local disk (the server holds a lock file there, which some network shares don't support).
- `/downloads`: where downloads and extractions go. Mount more folders and list them in `DOWNLOADMANAGER_ROOTS` to
  choose between them in the web UI's folder browser; nothing outside them is reachable.

The container starts as root only to prepare these, then runs the server as `PUID:PGID` with no root rights left
(`setpriv`):

- `/data` is given to `PUID:PGID` (`chown -R`) only when something in it has another owner, so a restart changes
  nothing. A new, empty data folder is made `700`.
- The download folders are never changed recursively. An empty one the user can't write (the image's own folder,
  when nothing is mounted) is given to the user; otherwise the log warns that `PUID:PGID` can't write there.
- Started with `--user` (or `user:` in compose), the container changes no owners and runs as that user.

**Upgrading from an image whose default user was 1654.** Earlier images ran as `1654:1654` when `PUID`/`PGID` were
not set; the default is `1000:1000` now. `/data` follows on its own (above), but a download folder that already holds
files is never changed, so with a named volume at `/downloads` the log warns that `1000:1000` can't write there (it
names the folder's owner) and downloads fail. Either keep the old user with `-e PUID=1654 -e PGID=1654`, or give the
volume to the new one once: `docker run --rm -v <volume>:/downloads alpine chown -R 1000:1000 /downloads`.

## Backup

Stop the container, then copy the whole data folder:

```sh
docker stop inbound && tar -czf inbound-data-$(date +%F).tgz -C ~/inbound data && docker start inbound
```

**`secrets.key` in the data folder is the key to every saved secret** (site logins, the proxy password, the TorBox
key): they are sealed with it (AES-256-GCM). Keep it with the backup, and keep the backup as private as the
secrets. Without the key the server stops at start with exit code 2 and a message naming the file: put the key back,
or start once with `DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS=1` and enter the logins, the proxy password and the
TorBox key again. Secrets from the Windows app (sealed by Windows) can't be opened in the container at all.
That choice is remembered in `secrets.key.discarded` beside the key (key ids only, nothing secret), so later
starts don't need the variable; a secret sealed with any other key still stops the start. Back it up with the key.

Downloads in progress keep their part files in the download folder (`<name>.<id>.part`), so a backup of `/data`
alone resumes them only if the download folder is still there.

## Reverse proxy and HTTPS

The server speaks plain HTTP. To reach it over HTTPS, put a proxy in front, publish only the proxy's ports, and set
`DOWNLOADMANAGER_TRUST_PROXY=<the proxy's address>` and `DOWNLOADMANAGER_HOSTS=<your name>` on the server. Give the
proxy a fixed address on the compose network (`docker/compose.example.yml` does: `172.30.0.10`) and name that
address, not the whole network: Docker's own port forwarding can make a connection come from the network's gateway.
The proxy must pass WebSockets (`/api/ws`); Caddy and Traefik do on their own. The session cookie is then marked
`Secure`.

A proxy on the same machine as a server that runs outside Docker connects from `127.0.0.1`. List it
(`DOWNLOADMANAGER_TRUST_PROXY=127.0.0.1`): a proxy that adds no `X-Forwarded-For` would otherwise make every browser
look like one on the server's own machine, which sets the first password without the setup code. Setting the
password with `DOWNLOADMANAGER_PASSWORD_FILE` avoids the setup page altogether.

Caddy (`Caddyfile`), with both containers on one compose network:

```
downloads.example.org {
    reverse_proxy inbound:8080
}
```

Traefik, labels on the `inbound` service (with a Traefik that has a `websecure` entry point and a certificate
resolver named `le`):

```yaml
    labels:
      traefik.enable: "true"
      traefik.http.routers.inbound.rule: Host(`downloads.example.org`)
      traefik.http.routers.inbound.entrypoints: websecure
      traefik.http.routers.inbound.tls.certresolver: le
      traefik.http.services.inbound.loadbalancer.server.port: "8080"
```

Only one proxy hop is believed (the nearest one's `X-Forwarded-*`).

## Health check

The image checks `GET /health` every 30 seconds (`docker/healthcheck.sh`, with bash's `/dev/tcp`; the image has no
curl). It uses the port of the first address in `DOWNLOADMANAGER_URLS`. For an `https://` address it can only check
that the port accepts a connection; give compose a `healthcheck:` of your own for more.

## Adding FFmpeg

FFmpeg is needed only to join separate audio and video streams (DASH, and HLS with separate audio). It is not in the
image because Ubuntu's `ffmpeg` package adds far more than the rest of the image (measured below). To add it, build
a small image on top:

```Dockerfile
FROM ghcr.io/kaan88/inbound:latest
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
```

The server finds `ffmpeg` on `PATH`.

## Building the image

```sh
docker build -t inbound .
docker buildx build --platform linux/arm64 -t inbound .     # untested; needs QEMU/binfmt
```

The build context is an allowlist (`.dockerignore`): only `Directory.Build.props`, the server's projects, `webui/`
and the two scripts in `docker/` are sent, and build output, `node_modules`, keys, `.env` files and anything named
like credentials or secrets are left out even there. The Node and .NET stages run on the build machine's own
architecture and cross-publish, so only the last stage is the target's.

Pinned inputs, all in the `Dockerfile`:

- Base images by digest, written in the `FROM` lines (`node:24-alpine` twice, `dotnet/sdk:10.0`,
  `dotnet/aspnet:10.0`). Dependabot (`.github/dependabot.yml`) opens a pull request each week when a newer digest
  of the same tag exists; nothing merges by itself. To update one by hand, run
  `docker buildx imagetools inspect <image:tag>` and take the top `Digest` (the multi-architecture index).
- 7-Zip: `SEVENZIP_VERSION` and the two SHA-256 values, for `https://www.7-zip.org/a/7z<version>-linux-x64.tar.xz`
  and `-linux-arm64.tar.xz`. Compute them from the downloads and compare them with the `digest` GitHub shows for the
  same files on the official release (`https://github.com/ip7z/7zip/releases`). The build fails when a hash differs.

The Windows `7z.exe` and `7z.dll` are not in the Linux image: the build passes `-p:BundleWindows7Zip=false`, and
`third_party/` is not in the build context.

## Measured (2026-11-08, linux/amd64, image built from `webui`)

On the owner's Linux test VM (Docker 29, 20 cores, 8 GB), `docker stats` every 10 seconds for about 6 minutes after a
minute's settle, no browser connected:

| Container | Idle memory (min / average / max) | CPU |
| --- | --- | --- |
| Fresh, image defaults (Server GC with .NET 10's dynamic heap sizing) | 31.4 / 34.4 / 37.8 MiB | 0.02% |
| After the sign-in checks and a 1.5 MB download | 52.2 / 53.9 / 60.5 MiB | 0.01% |
| Fresh, `DOTNET_gcServer=0` | 28.6 / 30.0 / 31.6 MiB | 0.02% |
| Fresh, `DOTNET_gcServer=0`, `DOTNET_GCConserveMemory=7` | 28.8 / 29.9 / 31.6 MiB | 0.03% |

All are well under the 100 MB target, and the GC settings save about 4 MiB, so the image sets none. Image: 350 MB
as `docker images` shows it, of which the ASP.NET base is 342 MB; the server and web UI add 3.1 MB and 7-Zip 2.9 MB.
Ubuntu's `ffmpeg` with `--no-install-recommends` would add 425 MB.

## Not done in the container

- After an extraction's renames the folder itself is not flushed to disk (Windows does it; Linux skips it), so a
  power cut just then could lose the renames, though not the data.
- `docker kill` (SIGKILL) gives the server no chance to stop a running `7zz`; `docker stop` does.
