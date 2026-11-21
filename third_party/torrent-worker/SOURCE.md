# Torrent worker for Android (ANDROID.9)

`lib/arm64-v8a/libinbound-torrent.so` in the arm64 APK is `inbound-torrent` from `native/torrent-worker`, built for
Android with the NDK. Android runs a program from an app only out of its native library folder and under a `lib*.so`
name, and its app sandbox needs a bionic build, so the Linux worker cannot be reused.

- Build: `bash native/torrent-worker/build-android.sh` (Git Bash on Windows; NDK 28.2.13676358, API 29, arm64-v8a,
  triplet `native/torrent-worker/triplets/arm64-android-static-rel.cmake`, the vcpkg baseline in
  `native/torrent-worker/vcpkg.json`).
- Statically linked: libtorrent 2.1.2, OpenSSL 3.6.5, Boost 1.92.0, the NDK's libc++. Dynamic: only Android's libc,
  libm and libdl.
- SHA-256 `7258c4f920237e58ecb1be3d61d3bf20c52057012a57cbc98373777f56ce1565`, pinned in
  `src/DownloadManager.Android/DownloadManager.Android.csproj` and `tools/AndroidApp/check-apk.py`.
- Licenses shipped as APK assets from `android/` (copied from the vcpkg ports' copyright files): `licenses/libtorrent.txt`,
  `licenses/openssl.txt`, `licenses/boost.txt`. `THIRD-PARTY-NOTICES.md` lists all three.
