#!/usr/bin/env bash
# Builds Inbound.app for macOS (arm64 or x64) and packs it as a .zip and a .dmg. Run on a Mac (the GitHub macOS
# runner): codesign and hdiutil are macOS tools. Usage: scripts/package-macos.sh [osx-arm64|osx-x64]
# Output: artifacts/macos/Inbound-<version>-macos-<arch>.zip and .dmg
#
# Not signed with a Developer ID and not notarized: every Mach-O file is signed ad hoc (Apple Silicon runs nothing
# unsigned), so Gatekeeper refuses the first open: then System Settings > Privacy & Security > Open Anyway, or:
# xattr -dr com.apple.quarantine /Applications/Inbound.app
#
# Layout:
#   Contents/MacOS/InboundLauncher       the bundle's program: starts the Host, then becomes the window. Only Mach-O
#                                        files and scripts may sit in Contents/MacOS, or codesign refuses the bundle.
#   Contents/Resources/window/           the Uno window (DownloadManager.Desktop.Uno)
#   Contents/Resources/host/             inbound-host, DownloadManager.Cli, DownloadManager.NativeHost,
#                                        DownloadManager.Server, inbound-torrent (when built)
#   Contents/Helpers/Inbound Menu.app    the menu-bar icon (native/macos-menu), a bundle of its own so its
#                                        notifications are Inbound's and it offers Services > Download with Inbound
set -euo pipefail
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64) arch=arm64 ;; osx-x64) arch=x64 ;; *) echo "Unknown runtime $rid" >&2; exit 1 ;; esac
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/macos"
config="${CONFIGURATION:-Release}"
# VERSION_OVERRIDE: build as another version (the workflow's test updates); else Directory.Build.props's.
version="${VERSION_OVERRIDE:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n1)}"
app="$out/$arch/Inbound.app"
macos="$app/Contents/MacOS"
host="$app/Contents/Resources/host"
window="$app/Contents/Resources/window"

rm -rf "$out/$arch"
mkdir -p "$macos" "$host" "$window"
publish() {
  dotnet publish "$root/$1" -c "$config" -r "$rid" --self-contained true -p:PublishSingleFile=false \
    -p:DebugType=none -p:UseAppHost=true -p:Version="$version" -o "$2" -nologo -v q
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

# The menu-bar icon (native/macos-menu): the tray's counterpart, started by the Host (MacMenuBar.cs).
case "$arch" in arm64) swift_target=arm64-apple-macos12 ;; *) swift_target=x86_64-apple-macos12 ;; esac
menu_app="$app/Contents/Helpers/Inbound Menu.app"
mkdir -p "$menu_app/Contents/MacOS" "$menu_app/Contents/Resources"
swiftc -O -target "$swift_target" -o "$menu_app/Contents/MacOS/inbound-menu" "$root/native/macos-menu/main.swift"
cat > "$menu_app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Inbound</string>
  <key>CFBundleDisplayName</key><string>Inbound</string>
  <key>CFBundleIdentifier</key><string>local.inbound.menu</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>inbound-menu</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
  <key>NSServices</key>
  <array>
    <dict>
      <key>NSMenuItem</key><dict><key>default</key><string>Download with Inbound</string></dict>
      <key>NSMessage</key><string>downloadWithInbound</string>
      <key>NSPortName</key><string>Inbound</string>
      <key>NSSendTypes</key><array><string>public.url</string><string>public.utf8-plain-text</string></array>
      <key>NSRequiredContext</key><dict/>
    </dict>
  </array>
</dict>
</plist>
EOF

cat > "$macos/InboundLauncher" <<'EOF'
#!/bin/sh
# Inbound.app's program. A second Host asks the running one and exits, so starting it every time is safe.
macos="$(cd "$(dirname "$0")" && pwd)"
# Plain paths, without MacOS/..: the updater finds the window by its path under the bundle.
contents="$(cd "$macos/.." && pwd)"
host="$contents/Resources/host"
export DOWNLOADMANAGER_UI="$contents/Resources/window/DownloadManager.Desktop.Uno"
export DOWNLOADMANAGER_HOST="$host/inbound-host"
case "$1" in
  --cli) shift; exec "$host/DownloadManager.Cli" "$@" ;;
  --host) shift; exec "$host/inbound-host" "$@" ;;
  --background) exec "$host/inbound-host" "$@" ;;
esac
# Finder adds -psn_... on old systems; the window doesn't want it.
case "$1" in -psn_*) shift ;; esac
# MAC.3: the Host keeps running after its window closes, so a copy of Inbound.app put in place of an older one would
# be served by the older Host. One of another version is asked to stop first (its downloads go on with the new one).
mine="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$contents/Info.plist" 2>/dev/null)"
running="$("$host/DownloadManager.Cli" host status --no-start 2>/dev/null | sed -n 's/^host \([^ ]*\) .*/\1/p')"
if [ -n "$mine" ] && [ -n "$running" ] && [ "$running" != "$mine" ]; then
  "$host/DownloadManager.Cli" host shutdown --no-start >/dev/null 2>&1
  i=0
  while [ $i -lt 50 ] && "$host/DownloadManager.Cli" host status --no-start >/dev/null 2>&1; do sleep 0.2; i=$((i + 1)); done
fi
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
cp "$app/Contents/Resources/AppIcon.icns" "$menu_app/Contents/Resources/AppIcon.icns"
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
codesign --force --sign - "$menu_app" # a nested bundle before the one around it
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
