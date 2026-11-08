# Inbound Download Manager: the headless server (src/DownloadManager.Server) with its web UI (webui/), D3.
#
#   docker build -t inbound .                        (this machine's architecture)
#   docker buildx build --platform linux/amd64,linux/arm64 -t inbound .
#
# Three build stages run on the build machine's own architecture (BUILDPLATFORM): Node builds the web UI from its
# lockfile, a small stage fetches the official 7-Zip for Linux (7zz) and checks its SHA-256, and the .NET SDK publishes
# the server for the target architecture (TARGETARCH -> linux-x64 / linux-arm64). Only the last stage is the target's
# own, and it runs no command, so a cross build needs no emulation. Nothing in the image needs Node at run time.
#
# Base images are pinned by digest (the multi-architecture index), written out in each FROM line so Dependabot
# (.github/dependabot.yml) can propose new digests; node appears twice and both lines must stay the same. To update
# one by hand: docker buildx imagetools inspect <image:tag>, take the top "Digest", and change it here. docs/docker.md
# has the list and how to update 7-Zip.

# ---- Web UI: static files only ---------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:24-alpine@sha256:a34ab6d7f6f4813f4d0c5469d74685604b962fbdbf5793a7bc7b4998ecb867c8 AS webui
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
FROM --platform=$BUILDPLATFORM node:24-alpine@sha256:a34ab6d7f6f4813f4d0c5469d74685604b962fbdbf5793a7bc7b4998ecb867c8 AS sevenzip
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
    TZ=Etc/UTC

COPY --from=server /out/ /app/
COPY --from=sevenzip /out/tools/ /app/tools/
COPY --chmod=755 docker/entrypoint.sh docker/healthcheck.sh /usr/local/bin/
# The two volumes, owned by the default PUID:PGID (1000, owner decision 2026-11-08), so the default needs no chown; the
# entrypoint hands /data to another PUID:PGID. Only numbers are used, never an account name: whether the base image
# has an account with that id (its own 'app' is 1654) makes no difference, and no account is added.
COPY --from=server --chown=1000:1000 /src/docker-empty/ /data/
COPY --from=server --chown=1000:1000 /src/docker-empty/ /downloads/

VOLUME ["/data", "/downloads"]
EXPOSE 8080
WORKDIR /app
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["/usr/local/bin/healthcheck.sh"]
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "/app/DownloadManager.Server.dll"]
