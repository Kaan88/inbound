#!/usr/bin/env bash
# Builds the torrent worker for the phone (ANDROID.9): arm64-v8a, API 29, static libtorrent/OpenSSL/Boost and libc++,
# into artifacts/torrent-worker/android-arm64/libinbound-torrent.so (Android only runs programs from the app's native
# library folder, so the APK carries it under a lib*.so name). Runs from Git Bash on Windows.
#   bash native/torrent-worker/build-android.sh [ndk dir]
# INBOUND_VCPKG_ROOT points at a vcpkg checkout (default: idm-deps/vcpkg next to the main checkout).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
ndk=${1:-"$LOCALAPPDATA/Android/Sdk/ndk/28.2.13676358"}
vcpkg=${INBOUND_VCPKG_ROOT:-"$USERPROFILE/Downloads/idm-deps/vcpkg"}
[ -x "$vcpkg/vcpkg.exe" ] || [ -x "$vcpkg/vcpkg" ] || { echo "vcpkg not found at $vcpkg" >&2; exit 1; }
export ANDROID_NDK_HOME="$ndk"
build="$repo/artifacts/torrent-worker/build-android-arm64"
cmake -S "$here" -B "$build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE="$vcpkg/scripts/buildsystems/vcpkg.cmake" \
  -DVCPKG_CHAINLOAD_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=29 -DANDROID_STL=c++_static \
  -DVCPKG_TARGET_TRIPLET=arm64-android-static-rel -DVCPKG_OVERLAY_TRIPLETS="$here/triplets" \
  -DVCPKG_INSTALLED_DIR="$vcpkg/../installed"
cmake --build "$build"
mkdir -p "$repo/artifacts/torrent-worker/android-arm64"
cp "$build/inbound-torrent" "$repo/artifacts/torrent-worker/android-arm64/libinbound-torrent.so"
sha256sum "$repo/artifacts/torrent-worker/android-arm64/libinbound-torrent.so"
