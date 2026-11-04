# Browser integration scope

The supported browser integration is **Google Chrome** using our Manifest V3
extension and per-user native-messaging host.

| Browser | Status | Reason |
| --- | --- | --- |
| Chrome | Supported | Native-messaging registration, automatic download interception, context menu, authenticated handoff, media panel, and Download All are verified |
| Microsoft Edge (Chromium) | Not supported | We do not register the Edge native-messaging key or publish an Edge package; the extension/native-host identity has not been verified there |
| Firefox | Not supported | Firefox uses WebExtensions with a different native-messaging manifest/protocol and `browser.downloads` semantics |
| Other browsers | Not supported | No extension/host implementation |

This is an intentional scope decision, not a claim of parity with reference app's Edge or
Firefox integrations. Adding a browser later requires its own extension package,
native-host registration path, and handoff verification; the host IPC and
protocol can be reused.
