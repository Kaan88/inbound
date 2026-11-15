# SITES.3 walk: hand-off (2026-11-15)

What was walked, fixed and left, plus how to run the walk again. Status lives in `PROGRESS.md` item H; this note is
for the next person picking it up.

## Where things stand

- SITES.1 to SITES.3 (Host `sites.*`, Options › Sites, the browse view) are on `main`, with the walk fixes in the
  commit after `9885b46`.
- The walk ran on Linux only: the Uno window and the Linux Host built from this tree, against an unprivileged
  OpenSSH `sshd` on 127.0.0.1, with rclone 1.76.0 as the user's own rclone. Dark mode only.
- Everything the walk found is fixed, except the minor points listed under "Left".

## Not walked yet

1. **Remove a site:** Options › Sites › Remove… should default to Cancel, then the sidebar entry goes and the view
   leaves.
2. **The site's folder changed while the view is shown:** edit "Folder on the server" while the site is open. The view
   should start again at the new folder (`SitesPanel.UpdateSite`, `ShowsSameStart`). Only the code path is done; the
   same case reached through the sidebar was seen working.
3. Light mode, and the Starred menu tooltip (the full path).
4. Windows: the WinUI XAML compiler and the real WinUI window. Watch the focus code there.
   `SitesPanel.HoldKeyboard` and `FocusListWhenReady` were written against Uno's behaviour (when the list is hidden,
   Uno gives the keyboard to the menu bar), and they should do no harm on WinUI.
5. A quick second click on Load more was lost twice. It may be the XTEST harness, or the button being disabled while a
   page loads; it hasn't been looked into.

Not reviewed: this round's changes were pushed when the owner asked to stop, without a second review. Haiku should
review the window focus changes, and Fable should review `SiteKeyText` and the save refusal (they handle secrets).

## Left (minor, seen in the walk, not fixed)

- The rclone line in Options is red while Sites is off, and the off message appears twice.
- "rclone (your own rclone)" shows before the version is known.
- "Update rclone automatically" stays enabled while an own rclone is used, which Inbound never updates.
- The private-key box widens the Add/Edit dialog, and the Port box sits oddly.
- After Test, the result line can be below the fold of the Options page.
- The sidebar has no "Sites" heading above the sites.
- Rows in the browse view are tall.

## Running the walk again (Linux)

None of this touches the user's own app data, keys or rclone.conf. Use a scratch folder (`W` below), keep its keys
there, and never commit screenshots or keys.

1. Build into `$W/bin`: `scripts/perf-seed.sh`'s `perf_build` with `PERF_OUT=$W`, or by hand:
   `dotnet publish src/DownloadManager.Host.Linux` and `src/DownloadManager.Cli` to `$W/bin/lib`, and
   `src/DownloadManager.Desktop.Uno` to `$W/bin/desktop`. Use
   `-c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none`.
2. Set up a private instance: `DOWNLOADMANAGER_INSTANCE=walk-…` and `DOWNLOADMANAGER_DATA=$W/inst/data`. Unset
   `INBOUND_RCLONE` and the `DOWNLOADMANAGER_HOST`/`UI`/`URLS`/`ROOTS`/`AUTH`/`PASSWORD*` variables. Start
   `$W/bin/lib/inbound-host --background`.
3. Display: `Xvfb :37 -screen 0 1600x1000x24 -nolisten tcp`. Rootless Xwayland drops XTEST, so it can't be used. Run
   with no window manager, and move windows with Xlib `configure`. Start the window with
   `DISPLAY=:37 DOWNLOADMANAGER_UNO_SOFTWARE=1 $W/bin/desktop/DownloadManager.Desktop.Uno`, with `WAYLAND_DISPLAY`
   unset.
4. Input: python-xlib `xtest.fake_input` for clicks and keys. Screenshots: `import -window <id>`. The scroll wheel
   doesn't scroll Uno pages, so resize the window or use the keyboard.
5. SFTP server: `ssh-keygen` a host key and a user key into `$W/sshd`. Copy `user.pub` to `authorized_keys`. Run
   `/usr/bin/sshd -f $W/sshd/sshd_config` with a free loopback port, `UsePAM no`, `StrictModes no`,
   `PasswordAuthentication no`, `AuthorizedKeysFile` pointing into `$W/sshd`, and
   `Subsystem sftp /usr/lib/ssh/sftp-server`. This is the same setup as
   `tests/DownloadManager.Integration.Tests/SitesSftpEndToEndTests.cs`. To check a changed key, restart it with another
   `HostKey`.
6. Served tree used: `served/pub/{readme.txt, one-mb.bin, docs/deep/note.txt, back\slash/inside.txt, big/` with 1,200
   files `}`.
7. In the window: Options › Sites › Use Sites. Put the path of an rclone ≥ 1.75.0 in "Use my own rclone", then Add a
   site (127.0.0.1, the port, your user, Private key, paste the throwaway key). Trust the key, Test, then set "Folder on
   the server" to `$W/served/pub`.

The automatic check of the same flows is the e2e test. Run it with `INBOUND_RCLONE=<rclone path>`, then
`dotnet test tests/DownloadManager.Integration.Tests -c Release --filter FullyQualifiedName~Sites`.

## Next phase

SITES.4 (transfers: download and upload batches, the Transfers bar) is planned and not started. See
`docs/plans/remote-sites.md` and ROADMAP SITES.4.
