# Inbound web UI (D2)

The browser front end of the headless server (`src/DownloadManager.Server`). Svelte 5, Vite and Tailwind CSS, built
to static files the server serves from its `wwwroot`. Nothing here runs at the server's run time: there is no Node
in the image.

## Build

```powershell
cd webui
npm ci            # exact versions from package-lock.json; works with --offline once the npm cache has them
npm run check     # svelte-check (types and Svelte warnings)
npm test          # vitest: protocol client, model, views, add-dialog routing, formatting, store
npm run build     # dist/ (the app) and dist/signin/ (the sign-in page, the only page served before sign-in)
```

`scripts\build.ps1` does the same before the solution build. When `webui\dist\index.html` and
`webui\dist\signin\index.html` exist, `DownloadManager.Server.csproj` copies `dist` into the server's `wwwroot` on
build and publish; otherwise the labelled placeholder page in `src\DownloadManager.Server\wwwroot` is served. So the
order is always: build the web UI, then build or publish the server. The Docker build (D3) does the same in a Node
stage (`npm ci && npm run build` from the lockfile) before `dotnet publish`, and copies only the published server.

Two Vite builds, because the server answers only `/signin/`, `/favicon.svg`, `/theme.js`, `/health` and the
sign-in calls without a session: `vite.config.ts` builds the app into `dist/`, and `vite.signin.config.ts` builds
`signin/index.html` into `dist/signin/` with its own assets. `public/theme.js` applies a theme chosen in this browser
before the page draws; it is a file because the server's content policy allows no inline script.

## Run against a test server

Never point this at the owner's data. A test run uses a temporary data folder and a temporary download root, under
`artifacts\` (the repo's integrity label lets a built program write there):

```bash
# from the repo root, in Bash (programs are started from Bash here, not PowerShell)
dotnet build src/DownloadManager.Server/DownloadManager.Server.csproj -c Release
tools/TestServer/bin/Release/net10.0/TestServer.exe --port 18731 --torbox &   # downloads and a mock TorBox
DOWNLOADMANAGER_INSTANCE=webui-test \
DOWNLOADMANAGER_DATA="$(cygpath -w artifacts/webui-run/data)" \
DOWNLOADMANAGER_ROOTS="$(cygpath -w artifacts/webui-run/downloads)" \
DOWNLOADMANAGER_URLS=http://127.0.0.1:18732 \
DOWNLOADMANAGER_TORBOX_API=http://127.0.0.1:18731/torbox/v1/api/ \
  src/DownloadManager.Server/bin/Release/net10.0/DownloadManager.Server.exe &
```

`npm run dev` serves the UI with hot reload and sends `/api` (WebSocket included) to `INBOUND_SERVER`
(default `http://127.0.0.1:8080`).

## Screens check

`npm run screens` (`scripts/screens.mjs`, Playwright's Chromium) walks every screen against such a server and saves
screenshots at 1360 and 390 pixels wide, dark and light, into `artifacts\webui\`: the list (row menu, toolbar More menu,
delete and cancel-package dialogs, package and download details), the Add menu and every add dialog, Extractions, Queues,
every Options page, My TorBox and Statistics. It sets the test server's password on a first run, seeds downloads through
the Add URL and Add links dialogs, then does a few things end to end: enters an archive password and checks the
extraction, stops and resumes, deletes without the file, uploads a `.torrent` file and chooses its files, saves a site
login and a proxy password in one Options draft (Apply), checks that Cancel and Back ask before dropping a draft, pairs a
browser token and checks what it may do, revokes it, and signs out. It prints how many steps passed and failed, and any
console error, page error, sideways scroll or overlapping toolbar buttons. `INBOUND_ONLY=<regex>` walks only the screens
(or `e2e:` checks) whose name matches, against the state an earlier run left.

| Variable | Meaning |
| --- | --- |
| `INBOUND_TEST_PASSWORD` | the throwaway password for the test server (required) |
| `INBOUND_URL` | the server, default `http://127.0.0.1:18732` |
| `INBOUND_TEST_SERVER` | the TestServer, default `http://127.0.0.1:18731` |
| `INBOUND_DOWNLOADS` | the download root, set as the download folder |
| `INBOUND_MOCK_KEY` | the mock TorBox key the TestServer prints (invented) |
| `INBOUND_FILES` | a plain web server with `holiday-photos.7z` (a test archive, password `screens`) and `documents.zip` |
| `INBOUND_TORRENT` | a small `.torrent` file to upload |

## Rules the code keeps

- Colors only through the tokens in `src/app.css`, which are the desktop app's (`App.xaml`), light and dark.
- The protocol is the desktop's (`docs/ipc-protocol.md`): one WebSocket for the snapshot, events and quick commands,
  and `POST /api/ipc` for calls that wait on the network, which would hold up the socket's one-at-a-time queue.
- A provider link (magnet, hoster, folder link, `.torrent`) is never requested directly. A line nobody can classify
  goes directly only when its own box is ticked.
- Secrets are write-only: the TorBox key, site login and proxy passwords and archive passwords are sent, never shown.
