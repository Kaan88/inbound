#!/usr/bin/env bash
# Builds Inbound.app for macOS (arm64 or x64) and packs it as a .zip and a .dmg. Run on a Mac (the GitHub macOS
# runner): codesign and hdiutil are macOS tools. Usage: scripts/package-macos.sh [osx-arm64|osx-x64]
# Output: artifacts/macos/Inbound-<version>-macos-<arch>.zip and .dmg
#
# Not signed with a Developer ID and not notarized: every Mach-O file is signed ad hoc (Apple Silicon runs nothing
# unsigned), so Gatekeeper asks on first open. Right-click > Open, or: xattr -dr com.apple.quarantine /Applications/Inbound.app
#
# Layout:
#   Contents/MacOS/InboundLauncher       the bundle's program: starts the Host, then becomes the window. Only Mach-O
#                                        files and scripts may sit in Contents/MacOS, or codesign refuses the bundle.
#   Contents/Resources/window/           the Uno window (DownloadManager.Desktop.Uno)
#   Contents/Resources/host/             inbound-host, DownloadManager.Cli, DownloadManager.NativeHost,
#                                        DownloadManager.Server, inbound-menu (the menu-bar icon),
#                                        inbound-torrent (when built)
set -euo pipefail
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64) arch=arm64 ;; osx-x64) arch=x64 ;; *) echo "Unknown runtime $rid" >&2; exit 1 ;; esac
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/macos"
config="${CONFIGURATION:-Release}"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n1)"
app="$out/$arch/Inbound.app"
macos="$app/Contents/MacOS"
host="$app/Contents/Resources/host"
window="$app/Contents/Resources/window"

rm -rf "$out/$arch"
mkdir -p "$macos" "$host" "$window"
publish() {
  dotnet publish "$root/$1" -c "$config" -r "$rid" --self-contained true -p:PublishSingleFile=false \
    -p:DebugType=none -p:UseAppHost=true -o "$2" -nologo -v q
}
publish src/DownloadManager.Host.Linux "$host"
publish src/DownloadManager.Cli "$host"
publish src/DownloadManager.NativeHost "$host"
publish src/DownloadManager.Server "$host"
publish src/DownloadManager.Desktop.Uno "$window"
# Started from Finder the window has the bundle's identity, and Uno's macOS host then reads its ms-appx files (the
# Fluent icon font, app.png) from Resources/ beside the program (MacOSHost.StartApp). They are beside the program itself.
ln -s . "$window/Resources"

# The torrent worker (native/torrent-worker) is optional here: without it the app runs with torrents turned off.
worker="${TORRENT_WORKER:-$root/artifacts/torrent-worker/$rid/inbound-torrent}"
if [ -f "$worker" ]; then
  install -m 755 "$worker" "$host/inbound-torrent"
else
  echo "No torrent worker at $worker: packing without torrents."
fi

# The menu-bar icon (native/macos-menu): the tray's counterpart, started by the Host from beside it.
case "$arch" in arm64) swift_target=arm64-apple-macos12 ;; *) swift_target=x86_64-apple-macos12 ;; esac
swiftc -O -target "$swift_target" -o "$host/inbound-menu" "$root/native/macos-menu/main.swift"

cat > "$macos/InboundLauncher" <<'EOF'
#!/bin/sh
# Inbound.app's program. A second Host asks the running one and exits, so starting it every time is safe.
macos="$(cd "$(dirname "$0")" && pwd)"
host="$macos/../Resources/host"
export DOWNLOADMANAGER_UI="$macos/../Resources/window/DownloadManager.Desktop.Uno"
export DOWNLOADMANAGER_HOST="$host/inbound-host"
case "$1" in
  --cli) shift; exec "$host/DownloadManager.Cli" "$@" ;;
  --host) shift; exec "$host/inbound-host" "$@" ;;
  --background) exec "$host/inbound-host" "$@" ;;
esac
# Finder adds -psn_... on old systems; the window doesn't want it.
case "$1" in -psn_*) shift ;; esac
"$host/inbound-host" --background >/dev/null 2>&1 </dev/null &
exec "$DOWNLOADMANAGER_UI" "$@"
EOF
chmod 755 "$macos/InboundLauncher"

# The icon: assets/inbound-icon.png -> AppIcon.icns.
iconset="$out/$arch/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z $size $size "$root/assets/inbound-icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$root/assets/inbound-icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
rm -rf "$iconset"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Inbound</string>
  <key>CFBundleDisplayName</key><string>Inbound Download Manager</string>
  <key>CFBundleIdentifier</key><string>local.inbound.desktop</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>InboundLauncher</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
EOF

# Ad-hoc signatures: every Mach-O file inside out, then the bundle. The .NET runtime needs JIT; with no hardened runtime
# an ad-hoc signature needs no entitlements.
find "$app/Contents" -type f | while read -r f; do
  if file -b "$f" | grep -q 'Mach-O'; then codesign --force --sign - "$f"; fi
done
codesign --force --sign - "$app"
codesign --verify --strict --verbose=2 "$app"

name="Inbound-$version-macos-$arch"
(cd "$out/$arch" && ditto -c -k --keepParent Inbound.app "$out/$name.zip")
staging="$out/$arch/dmg"
mkdir -p "$staging"
ditto "$app" "$staging/Inbound.app"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "Inbound $version" -srcfolder "$staging" -ov -format UDZO "$out/$name.dmg" >/dev/null
rm -rf "$staging"
(cd "$out" && shasum -a 256 "$name.zip" "$name.dmg" > "SHA256SUMS-macos-$arch")
cat "$out/SHA256SUMS-macos-$arch"
