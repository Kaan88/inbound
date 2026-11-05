# Inbound Download Manager

Inbound is the app's display name. Existing DownloadManager install/data paths, native-host identity and update identity remain compatible with earlier builds.

A private-use Windows download manager built around a durable background engine,
a replaceable WinUI 3 frontend, and a Manifest V3 Chrome bridge. It implements
observed another download manager behavior without using reference app code, assets, or
licensing.

Reference: **another download manager 6.43.12.3** (see
`notes/reference-version.md`).

> Status: Windows installer package is built and ready for user installation
> testing; actual install lifecycle and sign-in behavior remain unverified.
> Parity claims are tracked honestly in
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
dotnet build DownloadManager.slnx -c Debug
cd extensions\chrome; npm install; npm run build
```

## Install

The current packaged installer is
`artifacts\installer\DownloadManagerSetup-0.1.0.exe` (71.9 MB; SHA-256
`A3D5ABCADE836177371CA8D3F5FD1981DE83CA47C8956D138C1C8920260D8363`). Publish
checks verified all staged extension files and hashes, including icons, with no
nested `dist`. The package is ready for user installation testing; actual
install/upgrade/uninstall lifecycle and sign-in behavior have not yet been
verified.

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
