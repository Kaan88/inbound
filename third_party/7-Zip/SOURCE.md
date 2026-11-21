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

The phone app extracts with the official static Linux build for arm64, `7zzs`, unmodified, from 7-Zip 26.04's
`https://www.7-zip.org/a/7z2604-linux-arm64.tar.xz` (archive SHA-256
`5b0ac3aa91c1e3499f011d6af69d7f6b4de3ef2f011d9e2ff44c58ebd04aa388`, the same file the Docker image pins). The
dynamically linked `7zz` in the same archive needs glibc, which Android doesn't have; `7zzs` ran on the owner's Galaxy
S24 FE (Android 16) on 2026-11-21. The APK carries it as `lib/arm64-v8a/lib7zz.so`, because Android only runs programs
from the app's native library folder. The build takes it from `artifacts/android-app/7zip/arm64-v8a/lib7zz.so`
(ignored by git) and refuses any other bytes.

| File | SHA-256 |
| --- | --- |
| 7zzs (shipped as lib7zz.so) | 8f34c72fa4a9a79282b2364308f75d77a928ddf571292a7d030825e53d60e3a7 |
| android/License.txt (the Linux build's own, shipped as `assets/licenses/7-zip.txt`) | 1790374e5352329cedb46ee3808930a88e9ca2f08b82b10fcf5cf605d2c301b1 |
