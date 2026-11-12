# inbound-torrent: the native torrent worker (T0)

A small C++ process against libtorrent 2.1, started by the Host only while a torrent is active. Plan, gates and
decisions: `docs/plans/native-torrent-roadmap.md`. Wire format: `PROTOCOL.md`. Host-side contract and tests:
`src/DownloadManager.Torrents`, `tests/DownloadManager.Torrents.Tests`.

| Path | What |
| --- | --- |
| `src/main.cpp` | the worker: stdin reader thread, one main loop woken by commands, libtorrent's alert notification and two timers (none while it has no torrent) |
| `src/framing.hpp`, `base64.hpp`, `paths.hpp` | frames, binary fields, the worker's own path check |
| `tests/selftest.cpp` | checks of those three without libtorrent; runs as part of every build |
| `vcpkg.json` | dependencies: libtorrent without default features (so WebTorrent is off), Boost.JSON; `builtin-baseline` pins every other port |
| `ports/libtorrent` | overlay copy of vcpkg's port: the libtorrent tag and the SHA-512 of its source archive are pinned here |
| `triplets/` | static, release-only triplets: Windows with the static CRT (`/MT`), Linux x64 and arm64 |
| `build-windows.cmd` | Windows x64 build into `artifacts\torrent-worker\win-x64` |
| `Dockerfile.build` | Linux x64 and arm64 builds (arm64 cross-compiled) |

## Windows build

Needs Visual Studio 2022 or later with the C++ tools, CMake 3.25+ and Ninja on PATH, and a vcpkg checkout (default
`..\idm-deps\vcpkg` next to the repository, or `INBOUND_VCPKG_ROOT`). The first build compiles Boost, OpenSSL and
libtorrent (about 5 minutes on a 28-thread PC); later builds reuse vcpkg's binary cache.

```bat
native\torrent-worker\build-windows.cmd
dotnet test tests\DownloadManager.Torrents.Tests -c Release
```

## Linux builds

```sh
docker build -f native/torrent-worker/Dockerfile.build --build-arg TARGET=x64   --output artifacts/torrent-worker/linux-x64   native/torrent-worker
docker build -f native/torrent-worker/Dockerfile.build --build-arg TARGET=arm64 --output artifacts/torrent-worker/linux-arm64 native/torrent-worker
```

Each takes about 5 to 6 minutes on a 20-core machine when nothing is cached. `--build-arg JOBS=<n>` sets the number of
parallel compiles (default 4); one per core made an 8 GB machine stop answering. The x64 build runs the selftest;
the arm64 build is not run during the build (no emulation).

To run the tests against a Linux build, build the test project anywhere and run it in an SDK container:
`docker run --rm -v <test bin>:/t -v <worker dir>:/w:ro -e INBOUND_TORRENT_WORKER=/w/inbound-torrent
mcr.microsoft.com/dotnet/sdk:10.0 dotnet test /t/DownloadManager.Torrents.Tests.dll`.

## Taking a new libtorrent release (gate 13)

1. In `ports/libtorrent/vcpkg.json` set `version`; in `ports/libtorrent/portfile.cmake` set the `SHA512` of
   `https://github.com/arvidn/libtorrent/archive/v<version>.tar.gz` (`sha512sum` of the download).
2. If vcpkg's own port changed its patches for that version, copy them from a newer vcpkg checkout.
3. Build and run the tests (Windows above; Linux with the Dockerfile).

Measured on Windows (2026-11-11): moving between 2.1.1 and 2.1.2 is those two edits, a 162 s rebuild when the version
is not in the binary cache, and the 12 tests in about 30 s.
