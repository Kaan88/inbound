# Chrome Web Store listing kit

Everything to paste into the Developer Dashboard, for `artifacts\chrome-store\inbound-bridge-<version>-chrome.zip`.
Build the zip with `scripts\package-extension-store.ps1`. Images are in `images\`.

## Do these in this order

The store gives the extension a new ID, and the desktop app refuses any extension whose ID it does not
know. So the app has to learn the ID before anyone can test the listing.

1. Upload the zip as a new item. **Do not submit it yet.** The Item ID appears at the top of the item.
2. Tell the app about that ID, and publish a desktop release that accepts it. The ID goes in
   `BrowserBridge.cs` (read by `NativeHost\Program.cs` and `HostPipeServer.cs`, which both compare against
   a single value today), `installer\DownloadManager.iss`, `scripts\register-native-host.ps1` and
   `scripts\install.ps1`. Keep the old ID too, if unpacked builds should keep working.
3. Install that release and the extension together, and check they connect.
4. Put the privacy policy online, and fill in the tabs below.
5. Submit.

The ID stays the same when you upload new versions of this item. Deleting the item and adding it again
gives a new one.

## Account

- One-time registration fee; the payment screen shows the amount. 2-Step Verification is required.
- The developer email cannot be changed later.
- **Trader or non-trader:** an EU legal declaration only you can make. A free project unrelated to any
  business points to non-trader, but the decision is yours.

## Store listing tab

**Name** (comes from the zip): Inbound Download Manager Bridge

**Summary** (comes from the zip):
Sends browser downloads to the Inbound Download Manager desktop app for Windows, which must be installed separately.

**Category:** Productivity, Tools

**Language:** English

**Icon:** comes from the zip.

**Screenshots:** `images\screenshot-1-popup.png` to `screenshot-5-app.png`, all 1280 x 800.

**Small promo tile:** `images\promo-small-440x280.png`. The marquee image is optional and not included.

**Homepage URL:** `https://github.com/Kaan88/inbound`

**Support URL:** `https://github.com/Kaan88/inbound/issues`

**Description:**

```text
Sends your Chrome downloads to the Inbound Download Manager desktop app, which keeps the download list and, on sites that allow it, can resume interrupted transfers and speed up large files.

THE DESKTOP APP IS REQUIRED
This extension works with the Inbound Download Manager app for Windows, which you install separately. Without the app it cannot send anything: your downloads stay in Chrome and the extension tells you what is missing.

WHAT IT DOES
- Right-click a link, image, video or audio file and choose "Download with Inbound Download Manager". You can name the file and choose a folder first.
- "Download all links" lists the links on the current page, so you can filter them and send the ones you pick.
- Automatic sending, which you turn on in Settings: downloads that match your rules stop in Chrome and start again in the app, and they leave Chrome's download list. If the app is not installed or cannot be reached, or it refuses a download, the download stays in Chrome. Once the app has accepted one, Chrome's copy is gone, so a file from a submitted form or a link that works only once can fail in the app. Pause catching from the toolbar popup before downloading those.
- Rules for which file types and sites to catch. Hold Delete while clicking an ordinary link to keep that one download in Chrome, or pause catching for five minutes from the toolbar popup. Use the pause for buttons and for links inside embedded frames, where holding Delete does not work.

YouTube is not supported.

WHAT IT SHARES WITH THE APP
For each download you send, the extension gives the desktop app, on the same computer:
- the file's address and the address of the page it came from
- your browser's user agent
- your cookies for that site, which can include the ones that keep you signed in, so the app can download files that need your own login

The app keeps these on your computer with the download, the cookies encrypted, until you remove it, and it sends the cookies only to that site. The address can also stay in the app's list of recent addresses. Nothing is sent to the developer: there is no analytics, advertising or tracking. See the privacy policy for the full details.

The first time you install it, Settings opens so you can decide whether to turn on automatic sending. It stays off until you do.
```

## Privacy tab

**Single purpose:**

```text
Sends downloads from Chrome to the Inbound Download Manager desktop app installed on the same computer. It can catch matching downloads automatically once the user turns this on, and it lets the user send an item from the right-click menu or links chosen from the current page. Without the desktop app, downloads stay in Chrome.
```

**Permission justifications:**

| Permission | Justification |
|---|---|
| `downloads` | Detects when Chrome starts a download and reads its address, file name, type and size to apply the user's rules. If the user has turned on automatic sending, a matching download is paused while the desktop app is asked to take it. Chrome's copy is cancelled and removed from Chrome's list only after the app accepts; otherwise the download resumes in Chrome. |
| `contextMenus` | Adds one command, "Download with Inbound Download Manager", to the right-click menu on links, images, video and audio, so the user can send that item to the desktop app and choose its name and folder first. |
| `storage` | Saves the user's settings and the temporary "pause catching" timer on this device. Keeps a right-click download waiting for confirmation (its address, page address, referrer, browser user agent and file name) in session storage, because the background worker can stop in between. Sync storage is not used and cookie values are not kept here. |
| `cookies` | Many downloads work only with the user's login for that site. When a download is sent, the extension reads the cookies for that download's address and passes them through native messaging to the desktop app on the same computer, so the app can make the request as the user. The extension has no server and sends them nowhere else. The app stores them encrypted on the device with that download and sends them only to that download's own site. |
| `nativeMessaging` | The only channel to the Inbound Download Manager desktop app on the same computer, which the user installs separately. Used to send downloads, check the connection, show recent downloads, open the app and show a folder picker. Connecting starts the app's background process if it is not already running. |
| `notifications` | Shows a desktop notification with the result of a download being sent (sent to the app, already in the app, rejected, or app unavailable) and says why a right-clicked link cannot be sent. Without it, a caught download would disappear from Chrome with no explanation. The user can turn these off in Settings, apart from the one saying a site is not supported. |
| Host permission (`http://*/*`, `https://*/*`) | Downloads can start from any website, so the sites cannot be listed in advance. Access is used to read the cookies for an address being sent, and to read a tab's address and title for the source page and package name. The extension makes no network requests of its own. Page contents are not collected through this permission and nothing is sent to the developer. |
| Content script (all http and https pages, top frame) | Lists the addresses of the page's links, images, video and audio when the user opens Download All. Only while automatic sending is on, checks whether the Delete key is held during a click, so that one download can stay in Chrome; it keeps no record of keystrokes. It adds nothing to the page. Page data goes only to the extension and, for downloads the user picks, to the desktop app. |

**Are you using remote code?** No.

**Data usage.** Tick these, and check the labels against the form:

| Category | Tick | Why |
|---|---|---|
| Authentication information | Yes | Cookies for the download's address, which can include sign-in cookies. |
| Web history | Yes | Addresses of downloads and the pages they came from. |
| User activity | Yes | While automatic sending is on: watches new browser downloads, and whether Delete is held during a click. |
| Website content | Yes | Link addresses and labels on a page, when the user opens Download All. |
| Personally identifiable information | No | Nothing targets it. It could appear only by accident inside an address or file name. |
| Health, financial, personal communications, location | No | Nothing reads these. |

**Certifications:** tick all three (not sold or transferred outside the approved uses; not used for
unrelated purposes; not used for creditworthiness or lending). The code supports each one.

**Privacy policy URL:**
`https://github.com/Kaan88/inbound/blob/main/docs/chrome-extension-privacy.md`

It must be online before you submit, so `docs\chrome-extension-privacy.md` has to be pushed first.

## Distribution tab

- **Payments:** free.
- **Visibility:** all three options get the same review, so this only decides who can install it.
  **Private** with your own email as a tester is closest to private use. **Unlisted** lets anyone with the
  link install it. **Public** lists it in the store.
- **Regions:** all, or just your own.

## Test instructions tab

Fill in `<version>` and `<sha256>` from the release made in step 2.

```text
This extension is the browser half of a Windows desktop app. It cannot be fully tested without the app.

Tested on Windows 11 x64; the installer also accepts 64-bit Windows 10 version 1809 or later. No account is needed.

Install the desktop app:
https://github.com/Kaan88/inbound/releases/download/v<version>/DownloadManagerSetup-<version>.exe
SHA-256: <sha256>
The installer is not code-signed, so Windows SmartScreen may warn about it (More info, then Run anyway).

Then:
1. Install the extension. Settings opens by itself. Tick "Automatically send downloads to the desktop app" and click Save settings.
2. Click the extension's toolbar icon (under the puzzle-piece icon until it is pinned). It should say "Connected to the desktop app".
3. Download any direct link to a .zip file. The download should leave Chrome's list and appear in the Inbound Download Manager window (click Open app in the popup if the window is closed).
4. Right-click any link and choose "Download with Inbound Download Manager". A window opens to confirm the name and folder.
5. In the toolbar popup, click "Download all links..." on a page with links. Pick some and click "Download selected".

Without the desktop app: the popup says the app is required and links to it, and downloads stay in Chrome. That is the expected behaviour, not a fault.

YouTube is deliberately not supported.
```
