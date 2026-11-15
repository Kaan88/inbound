# Third-party notices

Inbound Download Manager is built with, and in places ships, software made by others. This file lists what is
included in Setup, the update zip, the Linux AppImage and the Docker image, under which licence, and where its own
licence text is. Inbound's own licence is in `LICENSE`.

## Shipped next to Inbound

| Component | Version | Licence | Where | Licence text |
| --- | --- | --- | --- | --- |
| 7-Zip, by Igor Pavlov | 26.04 | GNU LGPL 2.1 or later, with the unRAR licence restriction for its RAR decoder | Setup and the update zip (`tools\7-Zip`), the Docker image (`/app/tools/7-Zip`): extracting archives | `tools/7-Zip/License.txt`; source at https://www.7-zip.org |
| libtorrent, by Arvid Norberg and contributors | 2.1.2 | BSD-3-Clause (with Zlib, BSL-1.0, APSL-2.0 and BSD-4-Clause parts) | built into `inbound-torrent` (the torrent worker) | https://github.com/arvidn/libtorrent/blob/v2.1.2/LICENSE |
| Boost (the libtorrent dependencies: Asio, JSON and others) | as pinned by the vcpkg baseline in `native/torrent-worker` | Boost Software License 1.0 | built into `inbound-torrent` | https://www.boost.org/LICENSE_1_0.txt |
| OpenSSL | as pinned by the vcpkg baseline in `native/torrent-worker` | Apache License 2.0 | built into `inbound-torrent` (encrypted torrent connections) | https://www.openssl.org/source/license.html |
| .NET runtime and ASP.NET Core, by Microsoft and the .NET Foundation | 10.0 | MIT | every program (self-contained where it is published that way), the Docker base image | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Windows App SDK (WinUI 3), by Microsoft | 2.6.5 | MIT | the Windows window | https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE |
| Uno Platform, with SkiaSharp and HarfBuzzSharp | Uno.Sdk 6.7.45 | Apache License 2.0 (Uno); MIT (SkiaSharp, HarfBuzzSharp); BSD-3-Clause (Skia) | the Linux window (AppImage) | https://github.com/unoplatform/uno/blob/master/License.md |
| Tmds.DBus.Protocol, by Tom Deseyn | 0.95.2 | MIT | the Linux window and Host (tray, key store, desktop settings) | https://github.com/tmds/Tmds.DBus/blob/main/LICENSE |
| Svelte, by the Svelte contributors | 5.57.3 | MIT | the web UI's script (`wwwroot`) | https://github.com/sveltejs/svelte/blob/main/LICENSE.md |
| Tailwind CSS, by Tailwind Labs | 4.3.5 | MIT | the web UI's style sheet | https://github.com/tailwindlabs/tailwindcss/blob/main/LICENSE |

## Downloaded when you use it

| Component | Version | Licence | Where | Licence text |
| --- | --- | --- | --- | --- |
| rclone, by Nick Craig-Wood and contributors | 1.76.0 or a newer release Inbound checked | MIT | Sites. Not in Setup or the update zip: the desktop apps download the official release from Options › Sites (the page names its licence first) into the app's data folder; only the program is kept. The Docker image ships it in `/app/tools/rclone`, with its `COPYING`. | `third_party/rclone/COPYING`; source at https://github.com/rclone/rclone |

`third_party/7-Zip/SOURCE.md` and `third_party/rclone/SOURCE.md` give the official files these come from and their
SHA-256 values.

## Used only to build or test

These are not part of what you install: Node.js, Vite, TypeScript, svelte-check, Vitest and Playwright (building and
testing the web UI and the browser extension), vcpkg and CMake (building the torrent worker), xUnit (tests), Inno Setup
(building Setup), and the Uno Platform's own build tools. LibVLCSharp and CommonServiceLocator come in through Uno's
packages but are left out of the build. The Docker image's base layers (Debian packages under
`mcr.microsoft.com/dotnet/aspnet`) carry their own notices in `/usr/share/doc`.

Icons, logos and other artwork are Inbound's own.
