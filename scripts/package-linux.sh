#!/usr/bin/env bash
# X.8: the Linux packages other than the AppImage, made from the AppDir that scripts/package-appimage.sh builds
# (run that first). Output, in artifacts/linux:
#   Inbound-<version>-x86_64.tar.gz           unpack anywhere and run ./inbound
#   inbound_<version>-1_amd64.deb             Debian, Ubuntu, Mint
#   inbound-<version>-1.x86_64.rpm            Fedora, openSUSE
#   inbound-<version>-1-x86_64.pkg.tar.zst    Arch, CachyOS, Manjaro
#   SHA256SUMS-linux-packages
#
# The tar.gz needs no tool. The three packages are made by nfpm (https://nfpm.goreleaser.com), named by NFPM or found
# on PATH; without it only the tar.gz is made, and the script says so.
#
# What a package installs:
#   /usr/lib/inbound/                        the same files as in the AppImage
#   /usr/bin/inbound                         the AppImage's AppRun, pointed at /usr/lib/inbound
#   /usr/share/applications/inbound.desktop  the menu entry (the Host writes one only for an AppImage, X.7)
#   /usr/share/pixmaps/inbound.png           its icon
# None of them updates itself: the in-app updater is for the AppImage only (InstalledBuild.AppImage), so a package is
# updated by installing the next one.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/linux"
appdir="$out/AppDir"
version="$(grep -oPm1 '(?<=<Version>)[^<]+' "$root/Directory.Build.props")"
fail() { echo "$*" >&2; exit 1; }

[ -x "$appdir/AppRun" ] || fail "No AppDir at $appdir. Run scripts/package-appimage.sh first."
[ -x "$appdir/usr/lib/inbound/inbound-host" ] || fail "The AppDir has no inbound-host."
[ -f "$appdir/inbound.desktop" ] && [ -f "$appdir/inbound.png" ] || fail "The AppDir has no inbound.desktop or inbound.png."

# ---- tar.gz: the AppDir as a folder, with AppRun named inbound (it finds its files beside itself) ----
portable="Inbound-$version-x86_64"
work="$out/portable"
rm -rf "$work"
mkdir -p "$work/$portable"
cp -a "$appdir/usr" "$work/$portable/usr"
install -m 755 "$appdir/AppRun" "$work/$portable/inbound"
install -m 644 "$appdir/inbound.png" "$work/$portable/inbound.png"
install -m 644 "$root/LICENSE" "$work/$portable/LICENSE"
cat > "$work/$portable/README.txt" <<EOF
Inbound Download Manager $version (Linux x86_64)

Run ./inbound from this folder. Keep the folder together: the program finds its files beside itself.
This build does not update itself and adds no menu entry. For those, use the AppImage or your distribution's
package from https://github.com/Kaan88/inbound/releases
EOF
tar -C "$work" --owner=0 --group=0 --numeric-owner -czf "$out/$portable.tar.gz" "$portable"
rm -rf "$work"
made=("$portable.tar.gz")
echo "Built $out/$portable.tar.gz"

# ---- deb, rpm, pacman: one staging tree, three packages ----
nfpm="${NFPM:-$(command -v nfpm || true)}"
if [ -z "$nfpm" ]; then
  echo "nfpm not found (set NFPM or put it on PATH): no .deb, .rpm or .pkg.tar.zst was made."
else
  stage="$out/pkgroot"
  rm -rf "$stage"
  mkdir -p "$stage/usr/lib" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/pixmaps"
  cp -a "$appdir/usr/lib/inbound" "$stage/usr/lib/inbound"
  # The launcher is AppRun itself with one line changed, so the two cannot drift apart.
  sed 's|^lib="\$here/usr/lib/inbound"$|lib="/usr/lib/inbound"|' "$appdir/AppRun" > "$stage/usr/bin/inbound"
  grep -qx 'lib="/usr/lib/inbound"' "$stage/usr/bin/inbound" \
    || fail "AppRun no longer has the lib= line this script rewrites; update scripts/package-linux.sh."
  chmod 755 "$stage/usr/bin/inbound"
  install -m 644 "$appdir/inbound.desktop" "$stage/usr/share/applications/inbound.desktop"
  grep -qx 'Exec=inbound %U' "$stage/usr/share/applications/inbound.desktop" \
    || fail "inbound.desktop no longer starts 'inbound'; the packages' menu entry would not work."
  install -m 644 "$appdir/inbound.png" "$stage/usr/share/pixmaps/inbound.png"

  config="$out/nfpm.yaml"
  cat > "$config" <<EOF
name: inbound
arch: amd64
platform: linux
version: "$version"
release: "1"
section: net
priority: optional
maintainer: Kaan88 <Kaan88@users.noreply.github.com>
vendor: Inbound
homepage: https://github.com/Kaan88/inbound
license: MIT
description: |
  Inbound Download Manager
  A download manager with segmented and resumable downloads, queues and a scheduler,
  browser integration, torrents and a web UI.
contents:
  - src: $stage/usr/lib/inbound
    dst: /usr/lib/inbound
    type: tree
  - src: $stage/usr/bin/inbound
    dst: /usr/bin/inbound
    file_info:
      mode: 0755
  - src: $stage/usr/share/applications/inbound.desktop
    dst: /usr/share/applications/inbound.desktop
  - src: $stage/usr/share/pixmaps/inbound.png
    dst: /usr/share/pixmaps/inbound.png
  - src: $root/LICENSE
    dst: /usr/share/licenses/inbound/LICENSE
# What the self-contained .NET programs and the window load from the system: ICU, OpenSSL, X11 and fontconfig.
# Listed from what .NET and the window need in general; not yet checked package by package on each distribution.
overrides:
  deb:
    depends:
      - libc6
      - libgcc-s1
      - libstdc++6
      - zlib1g
      - libssl3t64 | libssl3
      - libicu76 | libicu75 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67
      - libx11-6
      - libfontconfig1
    recommends:
      - xdg-utils
      - libgl1
  rpm:
    depends:
      - glibc
      - libgcc
      - libstdc++
      - zlib
      - openssl-libs
      - libicu
      - libX11
      - fontconfig
    recommends:
      - xdg-utils
  archlinux:
    depends:
      - glibc
      - gcc-libs
      - zlib
      - openssl
      - icu
      - libx11
      - fontconfig
EOF
  for kind in deb rpm archlinux; do
    before="$(ls -1 "$out")"
    "$nfpm" package --config "$config" --packager "$kind" --target "$out/"
    file="$(comm -13 <(printf '%s\n' "$before") <(ls -1 "$out") | head -n1)"
    [ -n "$file" ] || fail "nfpm made no $kind package."
    made+=("$file")
    echo "Built $out/$file"
  done
  rm -rf "$stage" "$config"
fi

(cd "$out" && sha256sum "${made[@]}" > SHA256SUMS-linux-packages)
echo "Wrote $out/SHA256SUMS-linux-packages"
