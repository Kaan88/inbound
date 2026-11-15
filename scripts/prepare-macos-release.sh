#!/bin/sh
# MAC.1: signs Inbound.app's zip for the in-app updater: writes manifest-macos-<arch>.json next to it, in
# artifacts/release/<tag>-macos-<arch>. Same key as the Windows and Linux manifests.
# usage: prepare-macos-release.sh <version> <zip> <arm64|x64> <private-key.pem> [owner/repo] [tag]
# The tag defaults to v<version>; another (a test tag) puts the zip's address under that tag's release.
set -eu
[ $# -ge 4 ] || { echo "usage: $0 <version> <zip> <arm64|x64> <private-key.pem> [owner/repo] [tag]" >&2; exit 2; }
version="$1"; zip="$2"; arch="$3"; key="$4"; repo="${5:-Kaan88/inbound}"; tag="${6:-v$1}"
case "$arch" in arm64|x64) ;; *) echo "arch must be arm64 or x64" >&2; exit 2 ;; esac
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
name="Inbound-$version-macos-$arch.zip"
manifest="manifest-macos-$arch.json"
out="$root/artifacts/release/$tag-macos-$arch"
[ -e "$out" ] && { echo "$out already exists; move it first." >&2; exit 1; }
mkdir -p "$out"
cp "$zip" "$out/$name"
# NO_NOTES=1 (a test version, which has no changelog entry): the manifest carries no notes.
notes="--notes-from $root/src/DownloadManager.Contracts/Changelog/changelog.json"
[ "${NO_NOTES:-0}" = 1 ] && notes=""
# shellcheck disable=SC2086 # $notes is two words or none
"$dotnet" run --project "$root/tools/UpdateSigner" -c Release -- "$out" "$version" \
  "https://github.com/$repo/releases/download/$tag/$name" "$out/$name" "$key" stable "$manifest" $notes
rm -f "$out/public.pem"
(cd "$out" && shasum -a 256 "$name" "$manifest" > "SHA256SUMS-macos-$arch")
echo "Upload: $out/$name, $out/$manifest, $out/SHA256SUMS-macos-$arch"
