# Browser integration scope

The supported browsers are **Google Chrome** and **Microsoft Edge**, using our Manifest V3
extension and per-user native-messaging host. **Firefox** is required too and comes last, after
everything else (owner decision, 2026-11-06).

| Browser | Status | Reason |
| --- | --- | --- |
| Chrome | Supported | Native-messaging registration, automatic download interception, context menu, authenticated handoff, and Download All are verified |
| Microsoft Edge (Chromium) | Supported; real check pending (ROADMAP EDGE.1) | Edge users install the Chrome Web Store listing, so the same extension ID applies, and the native host is registered under Edge's own key. What has and has not been verified is in `PROGRESS.md`. A separate Edge Add-ons listing is parked (ROADMAP EXT.S1) |
| Firefox | Required; pending, comes last (ROADMAP FF.1) | No work started. Firefox uses WebExtensions with a different native-messaging manifest/protocol and `browser.downloads` semantics |
| Other browsers | Not supported | No extension/host implementation |

Adding a browser requires its own extension package or a compatible store listing, a native-host
registration path, and handoff verification; the host IPC and protocol can be reused.
