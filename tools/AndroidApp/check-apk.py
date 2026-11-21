# Checks an Inbound APK before it goes to a phone or the owner (2026-11-18: a release APK from a stale obj folder had no
# net.dot.jni.nativeaot.NativeAotRuntimeProvider and crashed at start).
#   python -I tools/AndroidApp/check-apk.py <apk> [--release] [--bundled-rclone] [--bundled-7zip]
# Fails unless: the runtime provider class and our activity are in the dex; the native library exports the TLS trust
# callback; the launcher icon is there; with --release, the test hooks are not in the native library.
import hashlib, sys, zipfile
apk = sys.argv[1]; release = "--release" in sys.argv
z = zipfile.ZipFile(apk); names = z.namelist()
dex = b"".join(z.read(n) for n in names if n.endswith(".dex"))
so = z.read("lib/arm64-v8a/libDownloadManager.Android.so") if "lib/arm64-v8a/libDownloadManager.Android.so" in names \
    else z.read(next(n for n in names if n.endswith("libDownloadManager.Android.so")))
checks = {
    "runtime provider in dex": b"net/dot/jni/nativeaot/NativeAotRuntimeProvider" in dex,
    "TLS callback in the library": b"Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate" in so,
    "launcher icon": any(n.endswith("ic_launcher.png") for n in names) or b"ic_launcher" in z.read("resources.arsc"),
}
if release:
    hooks = [w for w in ("probe_url", "crash_now", "update_fixture", "update_prompt_preview",
                        "InboundRcloneProbe", "hold_remote_files_publication", "remote_files_fixture_port")
             if w.encode("utf-16-le") in so or w.encode() in so]
    checks["no test hooks"] = not hooks
if "--bundled-7zip" in sys.argv:
    seven = "lib/arm64-v8a/lib7zz.so"
    seven_license = "assets/licenses/7-zip.txt"
    checks["only one packaged 7-Zip, arm64"] = [n for n in names if n.endswith("/lib7zz.so")] == [seven]
    checks["pinned 7-Zip bytes"] = seven in names and hashlib.sha256(z.read(seven)).hexdigest() == (
        "41a3efa6bba849c70c1625d572507f34b42ef05c7530215e60afea74fa38de74")
    checks["7-Zip license"] = seven_license in names and b"GNU LGPL" in z.read(seven_license)
    checks["7-Zip unRAR license"] = "assets/licenses/7-zip-unrar.txt" in names
if "--bundled-rclone" in sys.argv:
    binary = "lib/arm64-v8a/librclone.so"
    license_path = "assets/licenses/rclone.txt"
    checks["only one packaged rclone, arm64"] = [n for n in names if n.endswith("/librclone.so")] == [binary]
    checks["pinned rclone bytes"] = binary in names and hashlib.sha256(z.read(binary)).hexdigest() == (
        "a68c72a4677e2ba27f1ed7c5b388eea24b1088ad2bc389e461cc7a47c6195191"
    )
    checks["rclone license"] = license_path in names and b"Permission is hereby granted" in z.read(license_path)
bad = [k for k, ok in checks.items() if not ok]
for k, ok in checks.items(): print(("ok   " if ok else "FAIL ") + k)
sys.exit(1 if bad else 0)
