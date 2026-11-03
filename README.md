# Download Manager

A private-use Windows download manager built around a durable background engine,
a replaceable WinUI 3 frontend, and a Manifest V3 Chrome bridge. It implements
observed another download manager behavior without using reference app code, assets, or
licensing.

Reference: **another download manager 6.43.12.3** (see
`notes/reference-version.md`).

> Status: Phase 0/1 in development. Parity claims are tracked honestly in
> `reference notes`; unimplemented features are labelled.

## Repository layout

```
src/
  DownloadManager.Core/            domain models, states, policies
  DownloadManager.Contracts/       versioned IPC contracts
  DownloadManager.Engine/          HTTP engine, resume, segmentation
  DownloadManager.Persistence/     durable jobs/settings/history
  DownloadManager.Host/            background host + tray + IPC
  DownloadManager.Tray/            notification-area library
  DownloadManager.Desktop.WinUI/   WinUI 3 frontend
  DownloadManager.NativeHost/      Chrome native-messaging bridge
  DownloadManager.Cli/             headless control
extensions/chrome/                 our MV3 TypeScript extension
tools/TestServer/                  fault-injection HTTP server
tests/                             unit/integration/fault tests
notes/               analysis notes and evidence
scripts/                           build/test/install/package scripts
docs/                              architecture and operation docs
```

## Prerequisites

- Windows 11 x64
- .NET SDK 10
- Node.js 20+ (for the extension)
- Google Chrome
- Windows App SDK runtime (installer can bootstrap/self-contain)

## Build

```powershell
dotnet build DownloadManager.sln -c Debug
cd extensions\chrome; npm install; npm run build
```

## Install (development)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

The installer publishes to `%LOCALAPPDATA%\DownloadManager`, registers the
per-user Chrome native-messaging host, and optionally enables launch at sign-in.
Then load `extensions\chrome\dist` as an unpacked extension in
`chrome://extensions`. The extension ID is pinned by the manifest key.

## Documentation

- `GOAL.md` — objective, pinned reference, milestones, completion criteria
- `ROADMAP.md` — executable work and acceptance conditions
- `reference notes` — feature inventory with evidence and status
- `PROGRESS.md` — session handoff and current state
- `CLAUDE.md` — agent onboarding
- `docs/architecture.md` — process boundaries and IPC
- `docs/browser-integration.md` — handoff constraints and protocol
