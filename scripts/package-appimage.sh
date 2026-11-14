#!/usr/bin/env bash
# D7: builds the Linux AppImage (x86_64). The AppDir is always built; it is packed only when appimagetool is on PATH
# (or APPIMAGETOOL names it). Output: artifacts/linux/Inbound-<version>-x86_64.AppImage
#
# Layout inside the AppImage:
#   usr/lib/inbound/          inbound-host, DownloadManager.Cli, DownloadManager.NativeHost, DownloadManager.Server,
#                             inbound-torrent (the torrent worker)
#   usr/lib/inbound/desktop/  the Uno window (the Host looks for the window here)
#   AppRun                    --native-host | --cli | --host | --background (Host only) | (nothing: Host, then the window)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/linux"
appdir="$out/AppDir"
lib="$appdir/usr/lib/inbound"
config="${CONFIGURATION:-Release}"
version="$(grep -oPm1 '(?<=<Version>)[^<]+' "$root/Directory.Build.props" 2>/dev/null || echo 0.0.0)"

# T1: the native torrent worker (native/torrent-worker/Dockerfile.build), next to inbound-host, which looks for it there.
worker="${TORRENT_WORKER:-$root/artifacts/torrent-worker/linux-x64/inbound-torrent}"
if [ ! -f "$worker" ]; then
  echo "The torrent worker $worker is missing. Build it with native/torrent-worker/Dockerfile.build, or set TORRENT_WORKER." >&2
  exit 1
fi

rm -rf "$appdir"
mkdir -p "$lib/desktop"
publish() {
  dotnet publish "$root/$1" -c "$config" -r linux-x64 --self-contained true -p:PublishSingleFile=false \
    -p:DebugType=none -o "$2" -nologo -v q
}
publish src/DownloadManager.Host.Linux "$lib"
publish src/DownloadManager.Cli "$lib"
publish src/DownloadManager.NativeHost "$lib"
publish src/DownloadManager.Server "$lib"
publish src/DownloadManager.Desktop.Uno "$lib/desktop"
install -m 755 "$worker" "$lib/inbound-torrent"

cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
lib="$here/usr/lib/inbound"
export DOWNLOADMANAGER_UI="$lib/desktop/DownloadManager.Desktop.Uno"
case "$1" in
  --native-host) shift; exec "$lib/DownloadManager.NativeHost" "$@" ;;
  --cli) shift; exec "$lib/DownloadManager.Cli" "$@" ;;
  --host) shift; exec "$lib/inbound-host" "$@" ;;
  # The sign-in start (the autostart entry's "<AppImage> --background"): only the Host, no window. exec keeps this the
  # started process and keeps the AppImage mount alive for as long as the Host runs.
  --background) exec "$lib/inbound-host" "$@" ;;
esac
# A second Host asks the running one and exits, so this is safe when it already runs.
setsid -f "$lib/inbound-host" >/dev/null 2>&1 </dev/null
exec "$DOWNLOADMANAGER_UI" "$@"
EOF
chmod 755 "$appdir/AppRun"

cp "$root/assets/inbound-icon.png" "$appdir/inbound.png"
cat > "$appdir/inbound.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Inbound Download Manager
Comment=Download manager
Exec=inbound %U
Icon=inbound
Categories=Network;FileTransfer;
StartupWMClass=DownloadManager.Desktop.Uno
EOF

tool="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [ -z "$tool" ]; then
  echo "AppDir built at $appdir; appimagetool not found, so no AppImage was packed."
  exit 0
fi
ARCH=x86_64 "$tool" --no-appstream "$appdir" "$out/Inbound-$version-x86_64.AppImage"
echo "Built $out/Inbound-$version-x86_64.AppImage"
