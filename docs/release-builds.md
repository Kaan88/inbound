# Public builds repository

Source, installers and update-feed assets live in the public `Kaan88/inbound`
repository; releases are GitHub release assets there. Preparing assets
is a local, reviewable step; `scripts/prepare-release.ps1` does not build the
application, create a GitHub release, or upload files.

## Tools needed to build

Build the native torrent worker first. `scripts/publish.ps1` (and so `package.ps1` and `build-installer.ps1`) stops before
building anything when `artifacts\torrent-worker\win-x64\inbound-torrent.exe` is missing. Build it with
`native\torrent-worker\build-windows.cmd` (see `native/torrent-worker/README.md` for what it needs),
or pass another copy with `-TorrentWorker <path>` to any of the three scripts.

Node and npm are needed for the browser extension and now also for the web UI: `scripts/publish.ps1` runs `npm ci` (when `webui\node_modules` is missing) and `npm run build` in `webui`, then publishes `DownloadManager.Server` (the web UI bridge the
Host starts when Options > Web UI is on) into the same app folder with `wwwroot\index.html` and `wwwroot\signin\index.html`.
It stops with an error when either page is missing. `scripts/package.ps1` and `scripts/build-installer.ps1` publish through it,
so the zip and Setup carry the bridge; the installer copies the app folder recursively and needs no extra file entry.
The bridge needs the ASP.NET Core runtime, which the other apps do not: a `-SelfContained` build (every release build)
carries it, while a plain build needs the ASP.NET Core 10 runtime installed for the web UI to start (publishing the bridge
self-contained into a framework-dependent folder would stop the other apps from starting, so the script does not). It adds about 29 MB to the install folder and about 9 MB to Setup (ASP.NET Core); unused
framework assemblies are not trimmed yet, and the increase is accepted for now. Setup and the uninstall scripts also close a running
`DownloadManager.Server.exe` of that install. Setup never adds or removes the firewall rule; `INSTALL.txt` tells how to remove
"Inbound Download Manager web UI" after uninstalling.

First have the Firefox add-on signed (EXT.S4). This writes the signed XPI and
`extensions/firefox-updates.json`, which points Firefox at this release:

```powershell
.\scripts\sign-extension-firefox.ps1 -ReleaseTag vX.Y.Z -ApiKeysPath <AMO API key file>
```

The key file holds `{ "issuer": "...", "secret": "..." }` from addons.mozilla.org,
Developer Hub, Manage API Keys, and stays outside the repository. AMO signs a
version number only once: if signing stops after the upload, run the same command
again (it resumes from `artifacts\firefox-addon\.sign-<version>`), or download the
signed file from the Developer Hub and pass `-SignedXpi <file>`. For an app release
that keeps the same extension version, re-point the update file at the new release
with the XPI already signed:
`-ReleaseTag vX.Y.Z -SignedXpi artifacts\firefox-addon\inbound-bridge-<version>-firefox.xpi`.
Then build Setup (`scripts/build-installer.ps1`, which bundles that XPI).

The Chrome Web Store (Chrome) is published with the Chrome Web Store API v2. Keep a second key
file outside the repository, `{ "client_id", "client_secret", "publisher_id", "refresh_token" }`: the OAuth
client from Google Cloud (Chrome Web Store API enabled), the publisher ID from the Developer Dashboard
(Publisher, Settings), and a refresh token that `scripts/chrome-store-auth.ps1 -KeysPath <file>` saves after
a one-time sign-in in the browser (a "Web application" client needs `http://127.0.0.1:53682/` as a redirect
URI; a "Desktop app" client needs nothing). Then, after `scripts/package-extension-store.ps1`:

```powershell
.\scripts\publish-extension-chrome.ps1 -KeysPath <file>             # upload and submit for review
.\scripts\publish-extension-chrome.ps1 -KeysPath <file> -StatusOnly # review state
```

The store listing (screenshots, texts) is still edited in the dashboard.

Edge has its own listing on Microsoft Edge Add-ons (Partner Center; store ID `0RDCKGXFWJ1N`). Edge assigns the
extension its own ID (`ifoagddcenmjpbklkhghbdmgplldcdci`), which differs from Chrome's; the app trusts it
(`BrowserBridge.EdgeStoreExtensionId`) from 0.6.7, so a release carrying that ID must be out before a listing goes live.
Submit the zip from `scripts/package-extension-store.ps1` in Partner Center. There is no publish script for Edge yet
(Microsoft's Edge Add-ons API can only update an existing product; keep any API credentials in environment
variables or a file outside the repository).

## Changelog: What's new (UPD.N1)

Before building a release, write its lines in `src/DownloadManager.Contracts/Changelog/changelog.json`. That one file
feeds What's new in the Windows app (opened once after an update, and from Help > What's New…), What's new in the web UI
(the server's `changelog.get`), and the notes in the Windows app's Update available question (the update manifest's
`notes`). The file is built into `DownloadManager.Contracts`, so the release build must contain the final text.

- Never write what was or wasn't tried, tested or verified ("not tried by hand", "automatic tests only") in
  `changelog.json` or the GitHub release notes; that belongs in PROGRESS.md only (owner, 2026-11-12). Known issues
  list real problems.
- Add the version at the top (newest first) with its release date (`"date": "yyyy-MM-dd"`; `null` until then) and
  sections titled `New`, `Fixes` and `Upgrade notes`. Leave known issues to the GitHub release notes.
- One or two plain sentences per line, as the app talks (`docs/design-guidelines.md`). Plain text only: no Markdown or
  HTML, which would show as typed. At most 400 characters a line and 40 lines a version, since the manifest carries no
  more.
- Tag each line with where it applies: `desktop` (the desktop window, which is one code base for Windows and Linux:
  the list, menus, dialogs, Options; use this unless the line is about one system only), `windows` (the Windows app
  only: Setup, the tray, Windows itself), `linux` (the Linux app only: the AppImage, packages), `macos` (the macOS app
  only: Inbound.app, the menu-bar icon, macOS itself), `webui` (the browser
  web UI), `server` and `docker` (the headless server), `extension` (the browser extension), or `all`.
  Add `torrents` for a line about torrents the app downloads (shown only while torrents are on) and `torbox` (the provider's id) for one
  about the TorBox account (shown only with an account set). The Windows app shows `desktop`, `windows` and `all` lines; the Linux
  window `desktop`, `linux` and `all`; the macOS window `desktop`, `macos` and `all`; the web UI `webui`, `server`, `docker` and `all`. In the manifest a `desktop` tag
  is written as `windows` and `linux`, because apps from before the tag (0.7.7 or older) would hide the line; the macOS
  manifests (`manifest-macos-<arch>.json`) keep `desktop`, as only newer apps read them. This can go once no
  such install updates directly (ROADMAP UPD.N3); `changelog.json` itself uses `desktop`. The rest is folded under "N changes in the web UI and the browser extension".
- `dotnet test tests\DownloadManager.Core.Tests -c Release --filter ChangelogTests` checks the file: it parses, versions
  go newest first, every tag is known and each version fits a manifest.

`prepare-release.ps1` stops when the version has no dated entry, and passes the file to `UpdateSigner --notes-from`,
which writes the version's lines into `manifest.json` as `notes`. They are outside the signed text, so apps from before
0.7.2 still verify the manifest. They have their own signature, `notesSignature`, over the signed text and the notes, made
with the same key; the app shows notes only when it verifies, reads them with limits and shows them as plain text. UpdateSigner reads
`changelog.json` with the Contracts library, so rebuild it after pulling (`dotnet build tools\UpdateSigner -c Release`).
`prepare-appimage-release.sh` passes the same file for `manifest-linux-x64.json`, and `prepare-macos-release.sh` for
`manifest-macos-arm64.json` and `manifest-macos-x64.json`.

## Preparing the release assets

For version `X.Y.Z`, prepare from the already-built artifacts:

```powershell
.\scripts\prepare-release.ps1 `
  -Version X.Y.Z `
  -PrivateKeyPath <path to update-signing.pem>
```

Replace `X.Y.Z` with the release version and use the existing private key. Keep a second, offline copy of it: no
installed app accepts an update signed by any other key.

The script expects `artifacts/installer/InboundSetup-X.Y.Z.exe`,
`artifacts/package/Inbound-X.Y.Z-windows-x64.zip`, and the already-built
`tools/UpdateSigner/bin/Release/net10.0/UpdateSigner.exe`. It validates package
contents/version, signs the update ZIP with the existing private key, and writes
six reviewable files under `artifacts/release/vX.Y.Z/`:

- `InboundSetup-X.Y.Z.exe` (up to 0.7.4: `DownloadManagerSetup-X.Y.Z.exe` and `download-manager-X.Y.Z.zip`; the
  updater takes the zip's address from the signed `manifest.json`, so the names are free)
- `Inbound-X.Y.Z-windows-x64.zip`
- `manifest.json`
- `public.pem`
- `inbound-bridge-<extension version>-firefox.xpi`, after checking that
  `extensions/firefox-updates.json` names it at this release with its hash
- `SHA256SUMS`

Review the manifest URL, version, archive contents, public key, and hashes. Then
create a release tagged `vX.Y.Z` in the builds repository and upload those six
files explicitly. Firefox follows `extensions/firefox-updates.json` on `main` as soon
as it is there, so push in this order: the tag alone (`git push origin vX.Y.Z`, on the
release squash commit), then create the release on that tag and upload the six
files, then push `main`. Pushed the other way, Firefox only meets a short window of
failed update checks (the hash rules out a wrong file) and tries again later. The manifest points at the versioned ZIP release asset.
From 0.7.3 the app fetches that asset in segments (up to 8 connections, following GitHub's redirect for each), and in
one stream when the host answers no ranges or no length; the signed size and SHA-256 decide either way. Do not
upload the private signing key or any staging directory. Keep using the same
private key for all releases because installed clients trust its public key.

The output folder must not already exist. Failed preparation leaves its uniquely
named `.vX.Y.Z.staging-*` folder in the scoped `artifacts/release` directory
for review; remove only that exact staging folder after inspecting it before
retrying.

## Linux AppImage

The AppImage is built on Linux x86_64 with the .NET 10 SDK and `appimagetool`: `scripts/package-appimage.sh` writes
`artifacts/linux/Inbound-<version>-x86_64.AppImage`. It needs the Linux torrent worker first, and stops without it:
`docker build -f native/torrent-worker/Dockerfile.build --build-arg TARGET=x64 --output artifacts/torrent-worker/linux-x64 native/torrent-worker`,
or set `TORRENT_WORKER` to a worker built earlier. Any Linux runner with that worker works; the release's own are built
on GitHub (see "GitHub workflows" below). Sign it with the same key as the Windows manifest:

```sh
scripts/prepare-appimage-release.sh X.Y.Z artifacts/linux/Inbound-X.Y.Z-x86_64.AppImage <path to update-signing.pem>
```

That writes `artifacts/release/vX.Y.Z-linux/` with the AppImage, `manifest-linux-x64.json` and `SHA256SUMS-linux`.
Upload all three with the Windows files. When a release does not rebuild the AppImage, still upload the previous
`manifest-linux-x64.json` (it names the earlier release's AppImage) so that
`releases/latest/download/manifest-linux-x64.json` keeps answering. The signed file must be the exact file uploaded:
the manifest binds its size and SHA-256.

### The other Linux packages (X.8)

`scripts/package-linux.sh` makes four more files from the AppDir that `package-appimage.sh` leaves in
`artifacts/linux/AppDir`, so run that first:

| File | For |
| --- | --- |
| `Inbound-<version>-x86_64.tar.gz` | Any distribution: unpack and run `./inbound` |
| `inbound_<version>-1_amd64.deb` | Debian, Ubuntu, Mint |
| `inbound-<version>-1.x86_64.rpm` | Fedora, openSUSE |
| `inbound-<version>-1-x86_64.pkg.tar.zst` | Arch, CachyOS, Manjaro (`pacman -U`) |

and `SHA256SUMS-linux-packages`. The three packages need [nfpm](https://nfpm.goreleaser.com) on PATH or named by
`NFPM`; without it only the tar.gz is made. A package installs the same files as the AppImage under
`/usr/lib/inbound`, the launcher `/usr/bin/inbound`, a menu entry and an icon. None of them updates itself: the
in-app updater is for the AppImage, so they are not in `manifest-linux-x64.json` and are not signed.

## macOS app (MAC.1)

`scripts/package-macos.sh osx-arm64` (or `osx-x64`) builds `Inbound.app` on a Mac with the .NET 10 SDK and Xcode's
command-line tools, and packs `artifacts/macos/Inbound-<version>-macos-<arch>.zip` and `.dmg` with
`SHA256SUMS-macos-<arch>`. It uses the macOS torrent worker from `artifacts/torrent-worker/<rid>/inbound-torrent` when
there is one (built with vcpkg and the `*-osx-static-rel` triplets in `native/torrent-worker/triplets`); without it the
app runs with torrents off.

| Inside Inbound.app | What |
| --- | --- |
| `Contents/MacOS/InboundLauncher` | The bundle's program: starts the Host, then becomes the window (`--cli`, `--host`, `--background` as AppRun) |
| `Contents/Resources/window/` | The Uno window. `Resources -> .` beside it, as Uno's macOS host reads its files from there when started from Finder |
| `Contents/Resources/host/` | `inbound-host` (the Linux Host's project, built for macOS), the CLI, the native host, the server and the torrent worker |
| `Contents/Helpers/Inbound Menu.app` | `inbound-menu` (the menu-bar icon, `native/macos-menu`) as its own LSUIElement app (`local.inbound.menu`), so its notifications are Inbound's and it offers Services > Download with Inbound; signed before the outer bundle |

The app is signed ad hoc only, not with a Developer ID, and not notarized: Gatekeeper asks on the first open
(open it once, then System Settings > Privacy & Security > Open Anyway; macOS 15 dropped the right-click > Open way.
Or `xattr -dr com.apple.quarantine /Applications/Inbound.app`).

It updates itself as the AppImage does: it reads `manifest-macos-arm64.json` or `manifest-macos-x64.json` beside the
Windows `manifest.json`, downloads the signed zip, unpacks it with `ditto` beside the running bundle, swaps the bundles
with two renames and restarts (`MacAppRestarter`). Sign a release's zip with the same key:

```sh
scripts/prepare-macos-release.sh X.Y.Z artifacts/macos/Inbound-X.Y.Z-macos-arm64.zip arm64 <path to update-signing.pem>
```

The macOS workflow does this for both architectures when a release is published. To try the updater without a
release, run the workflow by hand with `test_version` (higher than the installed one, e.g. `0.8.1`): it publishes that
build to the pre-release `macos-update-test`, which never becomes "latest". On a test Mac:
`/Applications/Inbound.app/Contents/MacOS/InboundLauncher --cli settings set updatemanifest
https://github.com/Kaan88/inbound/releases/download/macos-update-test/manifest.json`, then Help > Check for Updates.
Put it back afterwards with `--cli settings set updatemanifest https://github.com/Kaan88/inbound/releases/latest/download/manifest.json`.

A copy of the app put in place of an older one by hand stops the older Host when it starts (MAC.3); to stop
everything anyway: `pkill -f 'Inbound.app/Contents/'`. Start Inbound when I sign in writes the LaunchAgent
`~/Library/LaunchAgents/local.inbound.host.plist`.

## GitHub workflows (CI.1)

| Workflow | Starts | Does |
| --- | --- | --- |
| `ci.yml` (CI) | Every push to main and every pull request, except Markdown and docs only | Windows: `scripts\build.ps1` and `scripts\test.ps1`. Linux: the web UI, the extension, the programs the Linux build ships, and the tests that pass on Linux |
| `appimage.yml` (Linux packages) | A published release, or by hand | With a tag: builds the AppImage and the four packages from the tag, signs `manifest-linux-x64.json` and attaches everything to the release. By hand without a tag: builds the chosen branch and keeps the files as run artifacts |
| `macos.yml` (macOS packages) | Every push to main or a `macos-*` branch and every pull request, except Markdown and docs only; or by hand | Builds the torrent worker and `Inbound.app` for arm64 and x64 as run artifacts (14 days), starts the Host and the CLI, has Sites download rclone and runs it, and drives the window through `DOWNLOADMANAGER_UNO_SELFTEST` (About, Progress Details, Options, closing and reopening the main window) with screenshots and the trace in the log. Nothing is attached to a release |
| `docker.yml` (Docker image) | A published release, or by hand from a tag | Builds and pushes the server image |
| `windows.yml` (Windows build) | A pushed version tag, or by hand | With a tag: builds Setup (with the signed Firefox add-on) and the zip, signs `manifest.json`, and puts the six files in a **draft** release with notes drafted from `changelog.json` (CI.2). By hand without a tag: a test Setup (no Firefox add-on) and zip of the chosen branch as run artifacts, nothing signed |

### A release built by GitHub (CI.2)

1. Locally, as before: write `changelog.json`, set the version, and when the extension's version changed, sign the
   Firefox add-on (`scripts\sign-extension-firefox.ps1 -ReleaseTag vX.Y.Z`; for an unchanged extension the same
   command with `-SignedXpi`), so that `extensions/firefox-updates.json` names the file at this release with its hash.
   Commit. Do not put `[skip ci]` in the release commit's message: GitHub starts nothing for such a push, tags
   included, and that is how 0.7.3 and 0.7.4 got no AppImage and no Docker image.
2. For a **new** extension version only: make a draft release for the tag by hand and attach the signed XPI to it,
   since no earlier release has that file. Otherwise the workflow takes it from the newest earlier release that has
   it. Either way it is checked against the hash in `extensions/firefox-updates.json`.
3. Push the tag alone (`git push origin vX.Y.Z`). The Windows build runs: Setup, the zip, `manifest.json` signed with
   the `UPDATE_SIGNING_KEY` secret (and refused unless its `public.pem` equals the latest release's), `SHA256SUMS`, all
   six files in a draft release. If the tag's commit did have `[skip ci]`, or to build again: Actions, Windows build,
   Run workflow, and give the tag.
4. Read the draft: the files, the manifest, the notes. The notes come from `changelog.json`; add the summary
   paragraph, the browser extension line and Known issues by hand, as in earlier releases.
5. Publish the draft. That starts the Linux packages and the Docker image, which attach their files to the release
   a few minutes later. (A release that GitHub's own token publishes would start neither, which is one reason the
   publishing stays a person's step.)
6. Push `main`, so Firefox finds `extensions/firefox-updates.json`.

The local way above (`build-installer.ps1`, `package.ps1`, `prepare-release.ps1`, then creating the release by
hand) still works and gives the same six files. The Chrome Web Store and Edge Add-ons uploads stay local either way.

To build for a tag whose release exists already: Actions, Linux packages, Run workflow, and give the tag; for the
image, Actions, Docker image, Run workflow, and choose the tag under "Use workflow from".
