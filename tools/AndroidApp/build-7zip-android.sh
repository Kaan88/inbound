#!/usr/bin/env bash
# Builds the phone's 7-Zip (lib7zz.so, ANDROID.9) from the official 7-Zip 26.04 source with the Android NDK.
# The official Linux builds can't run in an app: 7zz needs glibc, and the static 7zzs is killed by Android's
# app sandbox (SIGSYS, exit 159). This is the same 7zz, unmodified, linked against Android's own libc.
#
#   bash tools/AndroidApp/build-7zip-android.sh <7z2604-src.tar.xz> <out/lib7zz.so> [ndk dir]
#
# The source comes from https://www.7-zip.org/a/7z2604-src.tar.xz; it is refused unless its SHA-256 matches.
# Runs from Git Bash on Windows with the NDK's own make.exe. The output's hash is pinned in
# DownloadManager.Android.csproj and tools/AndroidApp/check-apk.py; third_party/7-Zip/SOURCE.md records it.
set -euo pipefail

src=${1:?source tarball}
out=${2:?output file}
ndk=${3:-"$LOCALAPPDATA/Android/Sdk/ndk/28.2.13676358"}
expected=9691944c0fe0d01bb49373a704fb983fd33bc98b1738695179dfbf99ac1734f6

actual=$(sha256sum "$src" | cut -d' ' -f1)
[ "$actual" = "$expected" ] || { echo "Unexpected source archive ($actual)" >&2; exit 1; }

case "$(uname -s)" in
  MINGW*|MSYS*) host=windows-x86_64; exe=.exe; make="$ndk/prebuilt/windows-x86_64/bin/make.exe" ;;
  Linux) host=linux-x86_64; exe=; make=make ;;
  *) echo "Unsupported build host" >&2; exit 1 ;;
esac
tc="$ndk/toolchains/llvm/prebuilt/$host/bin"
# API 29: the app's minimum is higher, and nothing newer is needed.
target=--target=aarch64-linux-android29

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
tar --force-local -xf "$src" -C "$work"
(
  cd "$work/CPP/7zip/Bundles/Alone2"
  # IS_MINGW= : the makefile takes a Windows build host for a MinGW target. LIB2: bionic has pthreads in libc.
  # -static-libstdc++: no libc++_shared.so to ship next to it.
  "$make" -j8 -f ../../cmpl_clang_arm64.mak IS_MINGW= \
    CC="$tc/clang$exe $target" CXX="$tc/clang++$exe $target" \
    LIB2=-ldl LDFLAGS_STATIC_2=-static-libstdc++ >/dev/null
)
mkdir -p "$(dirname "$out")"
"$tc/llvm-strip$exe" -o "$out" "$work/CPP/7zip/Bundles/Alone2/b/c_arm64/7zz"
sha256sum "$out"
