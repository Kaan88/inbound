# Inbound Download Manager

A download manager for Windows, in three parts:

- a WinUI 3 desktop app;
- a background service that owns the downloads and keeps them running from the notification area when the window is closed;
- a Chrome extension that can send browser downloads to the app.

It splits downloads into segments, resumes them after interruptions and restarts, and supports queues, schedules, proxies and site logins.

## Install

Download the installer from the [latest release](https://github.com/Kaan88/inbound/releases/latest) and run it.

Program files install to `%LOCALAPPDATA%\Programs\Inbound Download Manager`; settings, the download list and logs live in `%LOCALAPPDATA%\Inbound Download Manager`. 0.3.0 is a fresh start: it does not read state or settings from earlier builds.

## Chrome extension

The extension needs the desktop app, which it reaches through a per-user native-messaging host. Automatic sending is off until you turn it on in the extension's Settings.

To load it from source, build it (see below), open `chrome://extensions`, turn on Developer mode, choose **Load unpacked** and select `extensions\chrome\dist`. The extension ID is pinned by the manifest key.

What the extension does with data is in its [privacy policy](docs/chrome-extension-privacy.md).

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
a version tag is pushed.

## Build

Prerequisites:

- Windows 11 x64
- .NET SDK 10
- Node.js 20+ (for the extension)
- Google Chrome
- Windows App SDK runtime (the installer can bootstrap/self-contain it)

```powershell
dotnet build DownloadManager.slnx -c Release
cd extensions\chrome; npm ci; npm run build
```

Run the tests with `scripts\test.ps1`. To publish and install a development build:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

This publishes to `%LOCALAPPDATA%\Programs\Inbound Download Manager`, registers the per-user Chrome native-messaging host, and optionally enables launch at sign-in.

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
  DownloadManager.NativeHost/      Chrome native-messaging bridge
  DownloadManager.Cli/             headless control
extensions/chrome/                 our MV3 TypeScript extension
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
