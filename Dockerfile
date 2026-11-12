# Inbound Download Manager: the headless server (src/DownloadManager.Server) with its web UI (webui/), D3.
#
#   docker build -t inbound .                        (this machine's architecture)
#   docker buildx build --platform linux/amd64,linux/arm64 -t inbound .
#
# Four build stages run on the build machine's own architecture (BUILDPLATFORM): Node builds the web UI from its
# lockfile, a small stage fetches the official 7-Zip for Linux (7zz) and checks its SHA-256, a C++ stage builds the
# libtorrent worker (inbound-torrent), and the .NET SDK publishes the server for the target architecture (TARGETARCH -> linux-x64 / linux-arm64). Only the last stage is the target's
# own, and it runs no command, so a cross build needs no emulation. Nothing in the image needs Node at run time.
#
# Base images are pinned by digest (the multi-architecture index), written out in each FROM line so Dependabot
# (.github/dependabot.yml) can propose new digests. Each image is named once: the two Node stages start from the
# node-base stage, so one digest serves both and a Dependabot PR can't leave them apart. To update one by hand: docker buildx imagetools inspect <image:tag>, take the top "Digest", and change it here. docs/docker.md
# has the list and how to update 7-Zip.

# ---- Node, for the two stages below ---------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:24-alpine@sha256:a34ab6d7f6f4813f4d0c5469d74685604b962fbdbf5793a7bc7b4998ecb867c8 AS node-base

# ---- Web UI: static files only ---------------------------------------------------------------------------------------
FROM node-base AS webui
WORKDIR /webui
COPY webui/package.json webui/package-lock.json ./
# Exactly the lockfile; no install scripts run (none of these packages needs one: the native parts are prebuilt).
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY webui/ ./
RUN npm run build && test -f dist/index.html && test -f dist/signin/index.html

# ---- 7-Zip for Linux: the official build, checked against its published SHA-256 ------------------------------------
# Source: https://www.7-zip.org/download.html, files https://www.7-zip.org/a/7z2604-linux-<arch>.tar.xz (7-Zip 26.04).
# The hashes below were computed from those downloads on 2026-11-08 and equal the "digest" GitHub lists for the same
# files on the official release https://github.com/ip7z/7zip/releases/tag/26.04.
FROM node-base AS sevenzip
ARG TARGETARCH
ARG SEVENZIP_VERSION=2604
ARG SEVENZIP_SHA256_X64=fc0327ba27e89bd086cf426dff17d77de582953cdbbc10a6576540a06853ffcd
ARG SEVENZIP_SHA256_ARM64=5b0ac3aa91c1e3499f011d6af69d7f6b4de3ef2f011d9e2ff44c58ebd04aa388
# busybox tar can't read 7-Zip's xz filters, so GNU xz unpacks it (the archive itself is checked first).
RUN apk add --no-cache xz
RUN set -eu; \
    case "$TARGETARCH" in \
      amd64) arch=x64;   sum="$SEVENZIP_SHA256_X64" ;; \
      arm64) arch=arm64; sum="$SEVENZIP_SHA256_ARM64" ;; \
      *) echo "No 7-Zip build is pinned for $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    wget -q -O /tmp/7z.tar.xz "https://www.7-zip.org/a/7z${SEVENZIP_VERSION}-linux-${arch}.tar.xz"; \
    echo "$sum  /tmp/7z.tar.xz" | sha256sum -c -; \
    mkdir -p /out/tools/7-Zip /tmp/7z; \
    xz -dc /tmp/7z.tar.xz | tar -x -C /tmp/7z; \
    cp /tmp/7z/7zz /tmp/7z/License.txt /tmp/7z/readme.txt /out/tools/7-Zip/; \
    chmod 755 /out/tools/7-Zip/7zz; chmod 644 /out/tools/7-Zip/License.txt /out/tools/7-Zip/readme.txt

# ---- The server ---------------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS server
ARG TARGETARCH
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ src/
COPY --from=webui /webui/dist/ webui/dist/
RUN set -eu; \
    case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "Unsupported $TARGETARCH" >&2; exit 1 ;; esac; \
    dotnet publish src/DownloadManager.Server/DownloadManager.Server.csproj -c Release -r "$rid" --self-contained false \
      -p:BundleWindows7Zip=false -p:UseAppHost=false -o /out; \
    test -f /out/wwwroot/index.html && test -f /out/wwwroot/signin/index.html; \
    if find /out \( -iname '*.exe' -o -iname '7z.dll' \) | grep -q .; then echo "Windows files in the Linux publish" >&2; exit 1; fi; \
    mkdir -p /src/docker-empty

# ---- The torrent worker (T1): libtorrent, built from native/torrent-worker ------------------------------------------
# The same build as native/torrent-worker/Dockerfile.build (keep the two in step): Ubuntu 22.04, vcpkg at the pinned
# commit, libtorrent from the overlay port. It runs on the build machine and cross-compiles for arm64, so no emulation.
# It takes a while (Boost, OpenSSL and libtorrent from source). A worker built before can stand in for this stage:
#   docker build --build-context torrent-worker=artifacts/torrent-worker/linux-x64 -t inbound .
FROM --platform=$BUILDPLATFORM ubuntu:22.04@sha256:076e7bf82319619f28dd0aa9c02ab34b055ad37100395b4f49a2ffba04fb5e11 AS torrent-worker-build
ARG TARGETARCH
ARG VCPKG_COMMIT=e54ef0fcc9a82033a61e51002552a910a5d57282
ARG TORRENT_JOBS=4
ENV DEBIAN_FRONTEND=noninteractive VCPKG_DISABLE_METRICS=1 VCPKG_MAX_CONCURRENCY=$TORRENT_JOBS
RUN apt-get update && apt-get install -y --no-install-recommends \
      build-essential ca-certificates curl git ninja-build pkg-config python3 tar unzip zip perl linux-libc-dev \
    && if [ "$TARGETARCH" = arm64 ]; then apt-get install -y --no-install-recommends g++-aarch64-linux-gnu binutils-aarch64-linux-gnu; fi \
    && rm -rf /var/lib/apt/lists/*
RUN git clone --filter=blob:none https://github.com/microsoft/vcpkg.git /vcpkg \
    && git -C /vcpkg checkout --detach "$VCPKG_COMMIT" \
    && /vcpkg/bootstrap-vcpkg.sh -disableMetrics
WORKDIR /src
COPY native/torrent-worker/ ./
RUN set -eu; \
    XC=""; \
    case "$TARGETARCH" in \
      amd64) TRIPLET=x64-linux-static-rel; STRIP=strip ;; \
      arm64) TRIPLET=arm64-linux-static-rel; STRIP=aarch64-linux-gnu-strip; \
        XC="-DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++" ;; \
      *) echo "No torrent worker build for $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    CMAKE=$(/vcpkg/vcpkg fetch cmake --x-stderr-status | tail -n1); [ -x "$CMAKE" ]; \
    $CMAKE -S . -B /build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE=/vcpkg/scripts/buildsystems/vcpkg.cmake \
      -DVCPKG_TARGET_TRIPLET=$TRIPLET -DVCPKG_OVERLAY_TRIPLETS=/src/triplets $XC; \
    $CMAKE --build /build --parallel $TORRENT_JOBS; \
    if [ "$TARGETARCH" = amd64 ] && [ "$(uname -m)" = x86_64 ]; then /build/inbound-torrent-selftest && /build/inbound-torrent --version; fi; \
    $STRIP /build/inbound-torrent; \
    mkdir /out && cp /build/inbound-torrent /out/

# Laid out like Dockerfile.build's output (the worker at the root), so --build-context torrent-worker=<folder> fits.
FROM scratch AS torrent-worker
COPY --from=torrent-worker-build /out/ /

# ---- Runtime ------------------------------------------------------------------------------------------------------
# The regular ASP.NET image (Ubuntu), not the chiseled one: the entrypoint needs a shell, chown and setpriv to give
# /data to PUID:PGID and then drop root, which a chiseled image (no shell, fixed user) can't do.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="Inbound Download Manager" \
      org.opencontainers.image.description="Headless download server with a web UI: segmented downloads, queues, extraction and a TorBox account." \
      org.opencontainers.image.source="https://github.com/Kaan88/inbound" \
      org.opencontainers.image.url="https://github.com/Kaan88/inbound" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
# No org.opencontainers.image.licenses: the repository names no license (Directory.Build.props says "Private use").
# The bundled 7-Zip keeps its own terms in /app/tools/7-Zip/License.txt (GNU LGPL with the unRAR restriction).

ENV DOWNLOADMANAGER_DATA=/data \
    DOWNLOADMANAGER_ROOTS=/downloads \
    DOWNLOADMANAGER_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_HTTP_PORTS= \
    PUID=1000 \
    PGID=1000 \
    TZ=Etc/UTC \
    DOWNLOADMANAGER_TORRENT_PORT=6881 \
    DOWNLOADMANAGER_TORRENT_UPNP=off

COPY --from=server /out/ /app/
COPY --from=sevenzip /out/tools/ /app/tools/
# The Server looks for the torrent worker next to itself (TorrentSetup.FindWorker). DOWNLOADMANAGER_TORRENTS=off turns
# torrents off without it ever starting.
COPY --from=torrent-worker --chmod=755 /inbound-torrent /app/inbound-torrent
COPY --chmod=755 docker/entrypoint.sh docker/healthcheck.sh /usr/local/bin/
# The two volumes, owned by the default PUID:PGID (1000, owner decision 2026-11-08), so the default needs no chown; the
# entrypoint hands /data to another PUID:PGID. Only numbers are used, never an account name: whether the base image
# has an account with that id (its own 'app' is 1654) makes no difference, and no account is added.
COPY --from=server --chown=1000:1000 /src/docker-empty/ /data/
COPY --from=server --chown=1000:1000 /src/docker-empty/ /downloads/

VOLUME ["/data", "/downloads"]
# The torrent port listens only while a torrent runs (docs/docker.md, "Torrents").
EXPOSE 8080 6881/tcp 6881/udp
WORKDIR /app
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["/usr/local/bin/healthcheck.sh"]
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "/app/DownloadManager.Server.dll"]
