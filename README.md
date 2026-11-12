# Inbound Download Manager

A download manager for Windows, in three parts:

- a WinUI 3 desktop app;
- a background service that owns the downloads and keeps them running from the notification area when the window is closed;
- a browser extension for Chrome, Edge and Firefox that can send browser downloads to the app.

It splits downloads into segments, resumes them after interruptions and restarts, and supports queues, schedules, proxies and site logins.

## Install

Download the installer from the [latest release](https://github.com/Kaan88/inbound/releases/latest) and run it.

Program files install to `%LOCALAPPDATA%\Programs\Inbound Download Manager`; settings, the download list and logs live in `%LOCALAPPDATA%\Inbound Download Manager`. 0.3.0 is a fresh start: it does not read state or settings from earlier builds.

## Browser extension (Chrome, Edge, Firefox)

The extension needs the desktop app, which it reaches through a per-user native-messaging host. Setup registers that host for Chrome, Edge and Firefox. Automatic sending is on from a fresh install; the extension's Settings, which open on install, can turn it off. It can also pair with your own Inbound server.

- **Chrome:** install it from the [Chrome Web Store](https://chromewebstore.google.com/detail/olhbcdbjdnfoiinllnlpandggadfabfm).
- **Edge:** install it from [Microsoft Edge Add-ons](https://microsoftedge.microsoft.com/addons/detail/ifoagddcenmjpbklkhghbdmgplldcdci). The Chrome Web Store listing also works in Edge.
- **Firefox:** Firefox has its own build, signed by Mozilla and distributed here rather than on Firefox Add-ons. Setup offers to add it to Firefox (Firefox asks you to confirm). It is also on each [release](https://github.com/Kaan88/inbound/releases/latest) as `inbound-bridge-<version>-firefox.xpi`: drag it onto a Firefox window. Firefox then updates it on its own. It was checked in Firefox 159.

To load it from source, build it (see below), open `chrome://extensions` (or `edge://extensions`), turn on Developer mode, choose **Load unpacked** and select `extensions\chrome\dist`. The extension ID is pinned by the manifest key. For Firefox, run `npm run build:firefox` in `extensions\chrome`; it writes `extensions\chrome\dist-firefox`, which loads as a temporary add-on from `about:debugging`.

What the extension does with data is in its [privacy policy](docs/chrome-extension-privacy.md).

## Torrents

The app can download magnets and .torrent files on this PC. It is off until you tick **Options › Torrents › Download
torrents on this PC**; then **Tasks** has **Add Magnet…** and **Import Torrent File…**, and a magnet pasted into Add URL
goes the same way. A torrent is one row in the download list. For a torrent with several files the app asks which
files to download first (an option on the same page). With a TorBox account the app asks by default whether a torrent
goes to this PC or to TorBox, and says for every line whether TorBox has it cached.

The torrent engine (`inbound-torrent.exe`, built on libtorrent) runs only while a torrent downloads and closes about
30 seconds after the last one stops, so with nothing to do no torrent process runs and nothing listens on the network.
Torrents connect directly: the proxy in Options is not used for them. Setting the environment variable
`DOWNLOADMANAGER_TORRENTS=off` turns torrents off whatever Options says.

## Docker (headless server and web UI)

The same download engine also runs as a headless server with a web UI, in a container for a home server or NAS:

```sh
docker run -d --name inbound --init -p 8080:8080 \
  -e PUID=$(id -u) -e PGID=$(id -g) \
  -v ~/inbound/data:/data -v ~/downloads:/downloads \
  ghcr.io/kaan88/inbound:latest
```

Sign-in is on by default; the first visit sets the password, with a setup code from `docker logs`. Settings, compose,
backup (the data folder holds the key to the saved secrets) and a reverse proxy for HTTPS are in
[docs/docker.md](docs/docker.md); `docker/compose.example.yml` is a starting point. The image is published only when
a version tag is pushed, for linux/amd64 only.

## Build

Prerequisites:

- Windows 11 x64
- .NET SDK 10
- Node.js 20+ (for the extension and the web UI)
- Google Chrome, Microsoft Edge or Firefox (to try the extension)
- Windows App SDK runtime (the installer can bootstrap/self-contain it)
- Docker (only to build the server image)

```powershell
dotnet build DownloadManager.slnx -c Release
cd extensions\chrome; npm ci; npm run build; npm run build:firefox
```

Run the tests with `scripts\test.ps1`. To publish and install a development build:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

This publishes to `%LOCALAPPDATA%\Programs\Inbound Download Manager`, registers the per-user native-messaging host for Chrome, Edge and Firefox, and optionally enables launch at sign-in.

## Repository layout

```
src/
  DownloadManager.Core/            domain models, states, policies
  DownloadManager.Contracts/       versioned IPC contracts
  DownloadManager.Engine/          HTTP engine, resume, segmentation
  DownloadManager.Persistence/     durable jobs/settings/history
  DownloadManager.Runtime/         job orchestration, scheduling, dedupe
  DownloadManager.Host/            background host + tray + IPC
  DownloadManager.Tray/            notification-area library
  DownloadManager.Desktop.WinUI/   WinUI 3 frontend
  DownloadManager.NativeHost/      browser native-messaging bridge
  DownloadManager.Cli/             headless control
  DownloadManager.Server/          headless server for Linux/Docker
  DownloadManager.Archives/        archive extraction
  DownloadManager.Containers/      link container files (CCF, DLC)
  DownloadManager.Providers.TorBox/ TorBox provider
extensions/chrome/                 our MV3 TypeScript extension (Chrome, Edge, Firefox)
webui/                             web UI for the server
docker/                            Docker compose example and container scripts
installer/                         installer definition
assets/                            app icon sources
tools/TestServer/                  fault-injection HTTP server
tests/                             unit/integration/fault tests
scripts/                           build/test/install/package scripts
docs/                              architecture and operation docs
```

## Documentation

- `GOAL.md` — objective, milestones, completion criteria
- `ROADMAP.md` — executable work and acceptance conditions
- `PROGRESS.md` — session handoff and current state
- `CLAUDE.md` — agent onboarding
- `docs/architecture.md` — process boundaries and IPC
- `docs/docker.md` — running the server in Docker
- `docs/browser-integration.md` — handoff constraints and protocol
- `docs/chrome-extension-privacy.md` — extension privacy policy
