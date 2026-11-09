# Privacy policy: Inbound Download Manager Bridge

This policy covers the Inbound Download Manager Bridge extension for Chrome (also used in Edge, and built
for Firefox), and what the Inbound Download Manager desktop app does with the data the extension gives it.
Where it says Chrome, the same holds for the other browsers.

**No data about you reaches the developer.** The extension has no analytics, telemetry, advertising or
crash reporting, and the developer runs no server that receives data from it or from the desktop app. If you
pair the extension with an Inbound server of your own (the Docker or Linux version), it also sends downloads
there; that server is yours, not the developer's (see "Sending to your own server").
GitHub, which hosts the app's updates, shows the developer only a total count of how many times each
release file was downloaded, and that count includes update checks.

## What the extension does

It sends downloads from Chrome to the Inbound Download Manager desktop app for Windows, on the same
computer, using Chrome's native messaging. The app must be installed separately. Without it, downloads
stay in Chrome. The toolbar popup also lists the app's newest 20 downloads and can pause, resume, retry or
delete them (see "What the app gives the extension"). The extension makes no network requests of its own, except to an Inbound server you paired
it with: the app or the server does the downloading.

## What it reads, and when

**Each time a download is sent to the app:**

- the file's address, and its final address after any redirects
- the address of the page it came from, and the referrer
- the suggested file name, file type and size
- your browser's user agent
- the cookies Chrome holds for the file's address, and for its final address when that is on the same
  site (the same origin). These can include the cookies that keep you signed in to that site. They are
  used so the app can download files that need your own login. Cookies for a final address on another
  site are not read.

The extension reads cookie values only at that moment, not while you browse. A download is sent in one
of these ways:

- **Automatically**, while "Automatically send downloads to the desktop app" is on in Settings. It is on
  from a fresh install (versions 0.4.0 to 0.4.x started with it off; an update keeps whatever you chose),
  and Settings opens on install with this explanation, so you can turn it off before your first download.
  Downloads that match your rules then stop in Chrome and start again in the app, and
  they leave Chrome's download list.
- **From the right-click menu**, when you choose "Download with Inbound Download Manager" and confirm.
  At the click, the extension notes which cookie store and top-level site the link belongs to, so it can
  read the right cookies when you confirm.
- **From Download All**, for the links left ticked.

**Downloads you do not send:** while automatic sending is on and not paused, the extension looks at each
new Chrome download's address, final address, referrer, file name and type to apply your rules.
Downloads that do not match stay in Chrome, and cookies are not read for them. A download from a site the
extension has no access to (for example after you withdrew its site access, or set it to "on click") also
stays in Chrome, with a note, because its cookies cannot be read; nothing is sent for it. So that the toolbar popup
can say why a download stayed in Chrome, the extension keeps the file name and the reason (for example a
site on your exclude list, or automatic sending being off) for the last five such downloads in Chrome's
session storage, which Chrome clears when it closes. None of this is sent to the app. To find out whether you held Delete, the extension first asks its script in each open tab about
the download's address; the script compares it with the link you clicked and keeps nothing.

**In Firefox only, page and frame loads:** Firefox shows its own Save dialog before an extension hears of a
download, so the Firefox version looks at the response to each page or frame you load over http or https,
whether automatic sending is on or off. It reads the address and these response headers: the file type,
`Content-Disposition` (the file name), the size and whether the response is compressed. From them it decides
only whether Firefox would save the response rather than show it. For a normal page it stops there, keeps
nothing and sends nothing. A response Firefox would save is treated like any other new download: while automatic
sending is on, it goes through your rules, and if they say to catch it, it is sent with the details listed
above, including the cookies for its site. Otherwise Firefox goes on as it would have. The Chrome and Edge
versions don't do this.

**Links on a page:** when you open Download All, the extension reads the addresses of the current page's
links, images, video and audio, and their labels. It does not look at them until you open it. They stay in
that window. Links that point to files start out ticked, and you can untick any of them. Only the addresses
left ticked go to the app, each with the details listed above, including your cookies for that address's
site, which may not be the site of the page you are on. If you send two or more, the page's title becomes
the name of the package.

**The Delete key:** while automatic sending is on, the extension's script on http and https pages checks
each key press only to see whether it is Delete, and notes the link if you click one while Delete is held.
That keeps that one download in Chrome. It does not store or send any key, and it reads no form contents.
It acts on the link only for the next 15 seconds. The link's address stays in the page's memory until your
next click or until the page closes, and goes nowhere else. While automatic sending is off, it does not
listen for keys or clicks at all.

**Settings:** the extension stores your settings, including any sites you exclude, in Chrome's local
storage on this computer. A download sent from the right-click menu is kept in Chrome's session storage
until the app accepts it: its address, page address, referrer, user agent, file name and which cookie store
to use, but no cookie values. If you cancel, it stays there until you close Chrome. Chrome's sync storage is
not used.

**What the app gives the extension:** to show downloads in the toolbar popup, the app sends the extension
its download list, including downloads you did not send from Chrome: addresses, page addresses, file
names, folders and progress. The popup shows the newest 20 and lets you pause, resume, retry or delete them in the
app, and, for a finished download only if you tick "Delete the file too", delete the file. Deleting an unfinished
download always deletes its partial data, and never a file it did not write. When the folder window opens, the app
sends the default download folder and your favorite folders. The app's other settings, passwords and
cookies are not sent to the extension. The extension keeps the list only in memory and sends it nowhere
else.

**Asking for a folder:** if you choose "Ask each time" in Settings, a caught download waits, paused, in
Chrome while its window is open. Its address, page address, referrer, user agent, file name, size and type
are kept in Chrome's session storage until you choose Download or Keep in browser, or close the window.

The extension does not run in Incognito windows. YouTube is not supported.

## Sending to your own server (optional)

In Settings, under Inbound server, you can pair the extension with an Inbound server you run, by typing its
address and a one-time code from the server's web UI. Until you do, nothing below happens.

- **What the extension keeps:** the server's address, the name it was paired under, and a token the server
  gave it, in Chrome's local storage on this computer (not synced). The token lets the extension send
  downloads and use the popup's actions on that server, and nothing else. It is not encrypted by the
  extension; anyone who can read your browser profile can read it. In Chrome and Edge the extension's
  scripts on web pages cannot read it; in Firefox they can (the extension's own script never does). Unpair deletes it from the extension
  and sends the token to the server once more, only to ask it to remove this pairing, so the token stops
  working there too. If the server can't be reached, Settings says so: then remove the browser from the
  web UI's Paired apps list yourself.
- **What goes to the server:** for each download you send there (automatically, if you chose the server
  for automatic downloads, or by choosing Server in the folder window), the same details as for the
  desktop app, listed above, your cookies for that site included. The extension asks Chrome for access to
  the server's address when you pair. Its requests carry the token, never Chrome's cookies for the
  server. If the address starts with http://, the token and these details cross your network
  unencrypted; use https:// when the network is not only yours.
- **What the server gives the extension:** its download list for the popup (the newest 20, labelled
  Server), its default folder and favorite folders for the folder window, and the names of its folders
  under its download folders when you choose Choose… there.
- **What the server keeps:** like the desktop app, a record of each download in its data folder until you
  remove it there, the cookies encrypted with a key kept in the same data folder (anyone who can read the
  whole data folder can read them). Its logs do not contain cookies or the token.
- A download meant for the server that the server does not take stays in Chrome. It is never sent to the
  desktop app instead, and the other way round.

## What the desktop app keeps

The app stores each download's record on your computer, in
`%LOCALAPPDATA%\Inbound Download Manager`, until you remove it from the list:

- Cookies are encrypted with Windows DPAPI for your Windows user. They stay with the download until you
  remove it, including after it finishes. An unfinished download uses them when it continues. A finished
  one uses them again only if its queue has a repeating schedule, which checks the file with your cookies
  and may download it again, or if its file is missing when the app next starts and the download is then
  started again. Otherwise they stay stored and unused.
- Everything else is not encrypted: the file's address and final address, the page address and referrer,
  the file name and folder, the user agent, the file's size and type, dates, error messages and, for a
  package, the page's title. An address can contain a private link or access token if the site puts one
  there.

The app also keeps the 20 most recently added addresses in its settings file, which can remain after a
download is removed, and local log files, which are not deleted automatically. The logs can contain file
names, folder paths and error messages and, if you turn on clipboard monitoring in the app, addresses. The
app does not write cookies to them.

Removing the extension or uninstalling the app does not delete this data. Removing a download from the
list deletes its record and its cookies. It does not delete the recent-addresses list, the logs, or the
size, status and dates kept for the app's statistics. If an unfinished download is taken off the list
with its data kept (the command-line `remove --keep-file`), its partial file and a small progress file
containing its addresses also stay behind. To erase everything, exit the app from its
tray icon, then delete that folder.

## Who else sees anything

- **The site hosting the file**, and any other site the download moves to, such as after a redirect or for
  the parts of a video stream. The app connects directly, as your browser would have. Each site sees the
  address it is asked for, the user agent and your IP address. Cookies and the referrer go only to the site
  at the file's original address, and not to any other site, including after a redirect to one. If you point
  a download at another site with the app's Refresh Download Address command, its saved cookies and referrer
  are deleted first. If an address uses plain http, anyone on the network path can read what is sent to it,
  as with any plain http request.
- **A proxy**, if you set one up in the app or choose its option to use the Windows proxy settings. For an
  https download it sees the site's name. For an http download it can also read the address, cookies and
  referrer. The update check does not use the app's proxy setting; it follows the Windows proxy settings.
- **GitHub.** The installed app contacts GitHub each time its background process starts, and then every 24
  hours by default, to check for updates. Using the extension can start that process. If a newer version is
  available, the app downloads it from GitHub and installs it automatically once no downloads are running,
  and until then it checks again every minute. You can turn this off or change the interval in the app's
  Options. These requests contain no data about you or your downloads beyond what any web request reveals,
  such as your IP address.

- **Your own Inbound server**, if you paired one, for the downloads you send there (see "Sending to your own
  server"). Anything between this computer and the server, such as a reverse proxy, sees what the server sees
  unless the connection uses https.

Your data is not sold, not used for advertising or credit decisions, and not read by the developer. It is
not transferred except as described above.

The use of information received from Google APIs will adhere to the
[Chrome Web Store User Data Policy](https://developer.chrome.com/docs/webstore/program-policies/policies),
including the Limited Use requirements.

## Your choices

In the extension's Settings you can turn automatic sending on or off, limit it to certain file types (a file
whose name has no extension is still sent, unless it is a web page), and exclude sites. You can pause catching
for five minutes from the toolbar popup. You can pair or unpair your own server, and choose whether automatic
downloads go to this computer or to it. Removing the extension stops it reading or sending anything. Downloads
already in the app stay there, keep running, and keep their saved cookies until you remove them.

## Changes and contact

Changes to this policy are published at this address. For questions, open an issue at
<https://github.com/Kaan88/inbound/issues>.
