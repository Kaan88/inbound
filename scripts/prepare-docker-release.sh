#!/bin/sh
# Signs the Docker server's update file: writes manifest-docker.json in artifacts/release/v<version>-docker.
# There is no package to hash: the server's check reads only the version and the changelog lines, and never installs.
# Same key as the Windows manifest.
# usage: prepare-docker-release.sh <version> <private-key.pem> [owner/repo]
set -eu
[ $# -ge 2 ] || { echo "usage: $0 <version> <private-key.pem> [owner/repo]" >&2; exit 2; }
version="$1"; key="$2"; repo="${3:-Kaan88/inbound}"
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
out="$root/artifacts/release/v$version-docker"
[ -e "$out" ] && { echo "$out already exists; move it first." >&2; exit 1; }
mkdir -p "$out"
# ghcr.io lowercases the owner and the repository in the image path.
image="ghcr.io/$(printf '%s' "$repo" | tr '[:upper:]' '[:lower:]'):latest"
"$dotnet" run --project "$root/tools/UpdateSigner" -c Release -- --docker "$out" "$version" \
  "$image" "$key" stable manifest-docker.json \
  --notes-from "$root/src/DownloadManager.Contracts/Changelog/changelog.json"
rm -f "$out/public.pem"
(cd "$out" && sha256sum manifest-docker.json > SHA256SUMS-docker)
echo "Upload: $out/manifest-docker.json, $out/SHA256SUMS-docker"
