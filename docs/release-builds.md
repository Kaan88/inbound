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
- Tag each line with where it applies: `windows` (the Windows app), `webui` (the browser web UI), `server` and `docker`
  (the headless server), `linux` (the Linux app; shown nowhere yet), `extension` (the browser extension), or `all`.
  Add `torrents` for a line about torrents the app downloads (shown only while torrents are on) and `torbox` (the provider's id) for one
  about the TorBox account (shown only with an account set). The Windows app shows `windows` and `all` lines; the web UI
  `webui`, `server`, `docker` and `all`. The rest is folded under "N changes in the web UI and the browser extension".
- `dotnet test tests\DownloadManager.Core.Tests -c Release --filter ChangelogTests` checks the file: it parses, versions
  go newest first, every tag is known and each version fits a manifest.

`prepare-release.ps1` stops when the version has no dated entry, and passes the file to `UpdateSigner --notes-from`,
which writes the version's lines into `manifest.json` as `notes`. They are outside the signed text, so apps from before
0.7.2 still verify the manifest. They have their own signature, `notesSignature`, over the signed text and the notes, made
with the same key; the app shows notes only when it verifies, reads them with limits and shows them as plain text. UpdateSigner reads
`changelog.json` with the Contracts library, so rebuild it after pulling (`dotnet build tools\UpdateSigner -c Release`).
`prepare-appimage-release.sh` passes the same file for `manifest-linux-x64.json`.

## Preparing the release assets

For version `X.Y.Z`, prepare from the already-built artifacts:

```powershell
.\scripts\prepare-release.ps1 `
  -Version X.Y.Z `
  -PrivateKeyPath <path to update-signing.pem>
```

Replace `X.Y.Z` with the release version and use the existing private key. Keep a second, offline copy of it: no
installed app accepts an update signed by any other key.

The script expects `artifacts/installer/DownloadManagerSetup-X.Y.Z.exe`,
`artifacts/package/download-manager-X.Y.Z.zip`, and the already-built
`tools/UpdateSigner/bin/Release/net10.0/UpdateSigner.exe`. It validates package
contents/version, signs the update ZIP with the existing private key, and writes
six reviewable files under `artifacts/release/vX.Y.Z/`:

- `DownloadManagerSetup-X.Y.Z.exe`
- `download-manager-X.Y.Z.zip`
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
or set `TORRENT_WORKER` to a worker built earlier. Any Linux runner with that worker works; the project's own builds run on a
self-hosted Forgejo Actions runner, not on GitHub. Sign it with the same key as the Windows manifest:

```sh
scripts/prepare-appimage-release.sh X.Y.Z artifacts/linux/Inbound-X.Y.Z-x86_64.AppImage <path to update-signing.pem>
```

That writes `artifacts/release/vX.Y.Z-linux/` with the AppImage, `manifest-linux-x64.json` and `SHA256SUMS-linux`.
Upload all three with the Windows files. When a release does not rebuild the AppImage, still upload the previous
`manifest-linux-x64.json` (it names the earlier release's AppImage) so that
`releases/latest/download/manifest-linux-x64.json` keeps answering. The signed file must be the exact file uploaded:
the manifest binds its size and SHA-256.
