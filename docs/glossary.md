# Glossary: one name per thing (UI.R39)

Every surface a user reads uses these names: the Windows window, the web UI, the browser extension (popup, Options,
save forms), the tray, notifications, the CLI's help and output, Setup and the user docs. A new string picks its word
from here. Code identifiers, IPC message types, settings keys, CLI command names and URL paths are not user words and
keep their names (see "Kept different on purpose" below).

Source: review 5 (W1, T4, X1, S2), and what the app already used most. 0.7.1 had already picked Stop All in the
tray, Delete in delete questions and "server" in the web UI's switcher.

| Thing | Use | Don't use |
|---|---|---|
| Halt a download and keep its data (verb) | **Stop** (Stop, Stop All, File > Stop) | Pause, Pause All, Stop Download |
| A download halted that way (state) | **Stopped** | Paused |
| Go on with a stopped or failed download | **Resume** | Download Now, Start (for a download) |
| A download that got all its data (state, view) | **Completed** (status, sidebar view, Delete Completed, the torrent view) | Finished, Complete, Done (as a state name) |
| A download that went wrong (state, view) | **Failed** (status, sidebar view, the torrent view) | Errored, Error (as a state name) |
| Take a download off the list (the file may also go: the question asks) | **Delete** (toolbar, menus, question titles and buttons, Delete Completed) | Remove, Remove from list |
| Take something out of a group or a list of settings (a queue, a package, a paired server, a row of per-site limits) | **Remove** (Remove from Queue, Remove from Package) | Delete |
| One entry in the list | **download** ("Delete the selected download") | job, row, task, item (in user text) |
| The window with the file, segments, peers and so on | **Progress Details** | progress window, details window (as its name) |
| The settings window or page | **Options** (desktop, web UI, extension); "Options saved." after a save | Settings, preferences, "Open Download Manager settings" |
| Add many links at once | **Add Links** | Batch download, Add Batch |
| Add one link | **Add URL** | Add Link, New download |
| The machine the window runs on | **This PC** as a label or a choice (the switcher, a server list); in a sentence "this PC" or "this computer" both read fine | Local, localhost; "this computer" as a label or choice |
| Another machine running Inbound | **server** (switcher, web UI, extension, CLI help) | host, remote, remote host |
| Inbound's own process that keeps running when the window closes | **Inbound's background program** where the separate process matters (it stopped, isn't connected, start it); plain **Inbound** as the one that answers or refuses ("Inbound didn't take the choice"); the status bar says **Connected** / **Not connected** | Host, background Host, the host, service |
| The app | **Inbound** (short) or **Inbound Download Manager** (full name: Setup, About, window title, CLI banner) | Download Manager alone |
| A browser download handed to Inbound by the extension | **send** ("Send downloads to Inbound automatically", "Send only these file types", "Accept downloads sent from your browser") | capture, catch, intercept |
| A named list of downloads that runs on its own | **queue** (Start Queue, Stop Queue) | list, schedule (a schedule is when a queue runs) |
| Where downloads of a file kind go | **category**, with its **folder** | type, group |
| Folders the user picks often | **favorite folders** | bookmarks, recent (recent is a separate list) |
| A torrent giving data back after it completed | **seeding** (Start Seeding, Stop Seeding, Seeding view) | sharing, uploading (as a state) |
| A debrid or cloud service such as TorBox | **provider**; by name when there is one (TorBox, "My TorBox") | debrid, service, remote |
| The browser add-on | **browser extension** (Chrome, Edge and Firefox alike) | add-on, plugin, bridge (Firefox's own store calls it an add-on; our words don't) |
| What runs shutdown, restart, log off or sleep after downloads | **computer action** | system action, system completion action |
| Show the file's folder in the file manager | **Open Folder** | Show in folder, Reveal |
| Pick the program to open a file with | **Open With…** | Open with… |

## Kept different on purpose

- **Pause sending** in the extension's popup (and "Sending paused, N min left"): this is not a download. It lets the
  browser keep its own downloads for five minutes and ends by itself, so it is a pause, not a stop.
- **Start Queue / Stop Queue / Start Now on TorBox**: a queue, or a provider's queue, is started, not resumed; it never
  had data of its own to keep.
- **Start download / Download later** in the save forms and Add URL: a new download starts; Resume is for one that ran.
- **Unfinished** stays the sidebar group over Active, Queued and Failed: "Uncompleted" reads badly. Its views use the
  names above.
- **Stop Seeding**: a seeding torrent has completed; Stop here ends the upload, and the download stays Completed.
- **Done** for one segment in Progress Details' segment list: a part of a download is not a download state.
- An extraction record's row and a provider item's row are rows of the list, so they are **Delete**d too (Delete
  Extraction Record…, "Delete from the list?"); per-site limit rows, favorite folders and saved servers in the web
  switcher keep **Remove** (they are settings, not downloads).
- **CLI command names** (`dm pause`, `dm remove`, `dm pause-all`, `dm host`) are a scripting interface and keep their
  names; the help text says what each is called in the window (pause = Stop, remove = Delete, host = the background
  program). The CLI's own output uses the names above.
- **`#/settings`** stays the web UI's address for Options, and settings keys keep their names (no protocol change).
- **Hoster** and **link container** stay in the TorBox/JDownloader menus: they are those communities' own terms.
- **Log lines** (the Host's and the window's trace files) keep "Host": they are for diagnosing, not for the user.
- **Developer scripts** (`scripts\*.ps1`) follow these names where they print to a person (install.ps1's shortcut, repair.ps1); their comments and parameter names are not user text.
- **Changelog entries already released** keep their wording; new entries use the names above.
- Torrent Info's short labels (**Down, Up, Connected, Info hash**) are torrent-client words for that audience.
