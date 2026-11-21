# 7-Zip, bundled for extraction

Inbound extracts archives with 7-Zip's console program. 7-Zip is free software by Igor Pavlov,
licensed under the GNU LGPL, with the unRAR license restriction for its RAR decoder.
`License.txt` here is 7-Zip's own license text. Source code and more information:
https://www.7-zip.org.

These files are unmodified, from 7-Zip 26.04 x64, taken from the official release
`https://github.com/ip7z/7zip/releases/download/26.04/7z2604-x64.msi`.

| File | SHA-256 |
| --- | --- |
| 7z.exe | 95fff19b73eff77c87c0d7bd32822e43fb90dd2932459f962eb532d8dfb6ba53 |
| 7z.dll | 899cd73d7fc60e1c1c35c7de0f5bed3dc69bfe2cecbd48014d868a347970a0b1 |
| License.txt | 519ac0a4bded9c18ea02e0afb71f663d8c47373bd9facd3ac96a79f51d77765d |
| readme.txt | 6929cb624c82d5d1c55a146b5dcb4542445628e4ad8bcfe6272a0814e1383618 |

To update: replace the four files from a newer official release, update this table, and run
the Archives tests. The build copies them to `tools\7-Zip` next to every program that
references `DownloadManager.Archives`.

## Android (ANDROID.9)

The phone app extracts with `7zz` built from the official 7-Zip 26.04 source, unmodified, with the Android NDK
(28.2.13676358, API 29, arm64) against Android's own libc. The official Linux builds can't run in an app: `7zz` needs
glibc, and the static `7zzs` is killed by Android's app sandbox (SIGSYS, exit 159, seen on the owner's Galaxy S24 FE on
2026-11-21; it had run from adb's shell, which that sandbox doesn't cover). `tools/AndroidApp/build-7zip-android.sh`
builds it from `https://www.7-zip.org/a/7z2604-src.tar.xz` and gives the same bytes each time. The APK carries it as
`lib/arm64-v8a/lib7zz.so`, because Android only runs programs from the app's native library folder. The build takes it
from `artifacts/android-app/7zip/arm64-v8a/lib7zz.so` (ignored by git) and refuses any other bytes.

| File | SHA-256 |
| --- | --- |
| 7z2604-src.tar.xz (source, checked by the script) | 9691944c0fe0d01bb49373a704fb983fd33bc98b1738695179dfbf99ac1734f6 |
| 7zz, stripped (shipped as lib7zz.so) | 41a3efa6bba849c70c1625d572507f34b42ef05c7530215e60afea74fa38de74 |
| android/License.txt (the source's DOC/License.txt, shipped as `assets/licenses/7-zip.txt`) | 9ac2b4a97ab5d523965534d8b2d5868e511b39096d51fff458ab72c38b80fccc |
| android/unRarLicense.txt (the source's DOC/unRarLicense.txt, shipped as `assets/licenses/7-zip-unrar.txt`) | 17bd9fa4399092c777536fff045b41df76ec9d2ac4c9b8e7345d3b8b6ccc7976 |
