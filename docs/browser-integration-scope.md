# Browser integration scope

The supported browsers are **Google Chrome** and **Microsoft Edge**, using our Manifest V3
extension and per-user native-messaging host. **Firefox** is required too (owner decision,
2026-11-06) and is built with the extension work (ROADMAP EXT.E1).

| Browser | Status | Reason |
| --- | --- | --- |
| Chrome | Supported | Native-messaging registration, automatic download interception, context menu, authenticated handoff, and Download All are verified |
| Microsoft Edge (Chromium) | Supported; real check pending (ROADMAP EXT.E1) | Edge users install the Chrome Web Store listing, so the same extension ID applies, and the native host is registered under Edge's own key. What has and has not been verified is in `PROGRESS.md`. A separate Edge Add-ons listing is parked (ROADMAP EXT.S1) |
| Firefox | Built and registered; not signed, not checked in a real Firefox (ROADMAP EXT.E1) | `npm run build:firefox` writes `dist-firefox` with the add-on ID `inbound-bridge@kaan88`; Setup and the scripts register a Firefox manifest (`allowed_extensions`) under `HKCU\Software\Mozilla\NativeMessagingHosts`. Release Firefox runs only add-ons signed by addons.mozilla.org, which needs the owner's account. See `docs/browser-integration.md`, Firefox |
| Other browsers | Not supported | No extension/host implementation |

In Chrome, Edge and Firefox the extension can also send downloads to the user's own Inbound server (Docker or
Linux) after pairing in its Settings; that path needs no native host (`docs/browser-integration.md`, Sending to the
user's own server).

Adding a browser requires its own extension package or a compatible store listing, a native-host
registration path, and handoff verification; the host IPC and protocol can be reused.
