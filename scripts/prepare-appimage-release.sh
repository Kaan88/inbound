#!/bin/sh
# Signs an AppImage for the in-app updater: writes manifest-linux-x64.json next to it, in
# artifacts/release/v<version>-linux. Same key as the Windows manifest.
# usage: prepare-appimage-release.sh <version> <appimage> <private-key.pem> [releases-repo]
set -eu
[ $# -ge 3 ] || { echo "usage: $0 <version> <appimage> <private-key.pem> [owner/repo]" >&2; exit 2; }
version="$1"; appimage="$2"; key="$3"; repo="${4:-Kaan88/inbound}"
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
name="Inbound-$version-x86_64.AppImage"
out="$root/artifacts/release/v$version-linux"
[ -e "$out" ] && { echo "$out already exists; move it first." >&2; exit 1; }
mkdir -p "$out"
cp "$appimage" "$out/$name"
"$dotnet" run --project "$root/tools/UpdateSigner" -c Release -- "$out" "$version" \
  "https://github.com/$repo/releases/download/v$version/$name" "$out/$name" "$key" stable manifest-linux-x64.json \
  --notes-from "$root/src/DownloadManager.Contracts/Changelog/changelog.json"
rm -f "$out/public.pem"
(cd "$out" && sha256sum "$name" manifest-linux-x64.json > SHA256SUMS-linux)
echo "Upload: $out/$name, $out/manifest-linux-x64.json, $out/SHA256SUMS-linux"
