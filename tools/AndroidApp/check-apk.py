# Checks an Inbound APK before it goes to a phone or the owner (2026-11-18: a release APK from a stale obj folder had no
# net.dot.jni.nativeaot.NativeAotRuntimeProvider and crashed at start).
#   python -I tools/AndroidApp/check-apk.py <apk> [--release] [--bundled-rclone] [--bundled-7zip] [--bundled-torrent]
# Fails unless: the runtime provider class and our activity are in the dex; the native library exports the TLS trust
# callback; the launcher icon is there; every bundled native library has 16 KiB-aligned (p_align >= 0x4000) PT_LOAD segments;
# with --release, the test hooks are not in the native library and the bundled 7-Zip, torrent worker and rclone are there
# (--release implies all three --bundled-* checks; the flags also work on their own).
import hashlib, struct, sys, zipfile

# Every Intent extra / marker the test-build hooks read (src/DownloadManager.Android, INBOUND_TEST_BUILD blocks). The very generic
# extras "seed", "real" and "server" are left out: they would match ordinary words in any native library.
HOOKS = ("probe_url", "crash_now", "update_fixture", "update_prompt_preview", "InboundRcloneProbe",
         "hold_remote_files_publication", "remote_files_fixture_port", "remote_files_upload_port",
         "remote_files_s3_upload_port", "metered_override", "wifi_only", "add_url", "log_state", "rclone_probe",
         "media_upload_create", "media_upload_cleanup", "media_upload_nested", "skip_first_run",
         "update_preview_kind", "update_preview_notes_scroll", "update_preview_outer_scroll")
PAGE_ALIGN = 0x4000


def elf_load_alignments(data):
    """p_align of every PT_LOAD program header of an ELF64 little-endian file; ValueError if it is not one."""
    if data[:4] != b"\x7fELF" or len(data) < 0x40 or data[4] != 2 or data[5] != 1:
        raise ValueError("not an ELF64 little-endian file")
    phoff = struct.unpack_from("<Q", data, 0x20)[0]
    phentsize, phnum = struct.unpack_from("<HH", data, 0x36)
    out = []
    for i in range(phnum):
        off = phoff + i * phentsize
        if off + 0x38 > len(data): raise ValueError("program header outside the file")
        if struct.unpack_from("<I", data, off)[0] == 1:
            out.append(struct.unpack_from("<Q", data, off + 0x30)[0])
    return out


def unaligned_libraries(z):
    """(name, smallest p_align or error text) for each lib/arm64-v8a/*.so that is not 16 KiB aligned."""
    bad = []
    for n in z.namelist():
        if n.startswith("lib/arm64-v8a/") and n.endswith(".so"):
            try:
                aligns = elf_load_alignments(z.read(n))
            except ValueError as e:
                bad.append((n, str(e))); continue
            if not aligns or min(aligns) < PAGE_ALIGN:
                bad.append((n, hex(min(aligns)) if aligns else "no PT_LOAD"))
    return bad


def main(argv):
    apk = argv[1]; release = "--release" in argv
    z = zipfile.ZipFile(apk); names = z.namelist()
    dex = b"".join(z.read(n) for n in names if n.endswith(".dex"))
    so = z.read("lib/arm64-v8a/libDownloadManager.Android.so") if "lib/arm64-v8a/libDownloadManager.Android.so" in names \
        else z.read(next(n for n in names if n.endswith("libDownloadManager.Android.so")))
    checks = {
        "runtime provider in dex": b"net/dot/jni/nativeaot/NativeAotRuntimeProvider" in dex,
        "TLS callback in the library": b"Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate" in so,
        "launcher icon": any(n.endswith("ic_launcher.png") for n in names) or b"ic_launcher" in z.read("resources.arsc"),
    }
    bad_align = unaligned_libraries(z)
    checks["native libraries 16 KiB aligned"] = not bad_align
    for n, found in bad_align: print(f"     {n}: p_align {found}, need >= {PAGE_ALIGN:#x}")
    if release:
        hooks = [w for w in HOOKS if w.encode("utf-16-le") in so or w.encode() in so]
        checks["no test hooks"] = not hooks
        if hooks: print("     test hooks found: " + ", ".join(hooks))
    if release or "--bundled-7zip" in argv:
        seven = "lib/arm64-v8a/lib7zz.so"
        seven_license = "assets/licenses/7-zip.txt"
        checks["only one packaged 7-Zip, arm64"] = [n for n in names if n.endswith("/lib7zz.so")] == [seven]
        checks["pinned 7-Zip bytes"] = seven in names and hashlib.sha256(z.read(seven)).hexdigest() == (
            "41a3efa6bba849c70c1625d572507f34b42ef05c7530215e60afea74fa38de74")
        checks["7-Zip license"] = seven_license in names and b"GNU LGPL" in z.read(seven_license)
        checks["7-Zip unRAR license"] = "assets/licenses/7-zip-unrar.txt" in names
    if release or "--bundled-torrent" in argv:
        worker = "lib/arm64-v8a/libinbound-torrent.so"
        checks["only one packaged torrent worker, arm64"] = [n for n in names if n.endswith("/libinbound-torrent.so")] == [worker]
        checks["pinned torrent worker bytes"] = worker in names and hashlib.sha256(z.read(worker)).hexdigest() == (
            "7258c4f920237e58ecb1be3d61d3bf20c52057012a57cbc98373777f56ce1565")
        checks["libtorrent license"] = "assets/licenses/libtorrent.txt" in names and b"Arvid Norberg" in z.read("assets/licenses/libtorrent.txt")
        checks["OpenSSL license"] = "assets/licenses/openssl.txt" in names and b"Apache License" in z.read("assets/licenses/openssl.txt")
        checks["Boost license"] = "assets/licenses/boost.txt" in names and b"Boost Software License" in z.read("assets/licenses/boost.txt")
    if release or "--bundled-rclone" in argv:
        binary = "lib/arm64-v8a/librclone.so"
        license_path = "assets/licenses/rclone.txt"
        checks["only one packaged rclone, arm64"] = [n for n in names if n.endswith("/librclone.so")] == [binary]
        checks["pinned rclone bytes"] = binary in names and hashlib.sha256(z.read(binary)).hexdigest() == (
            "a68c72a4677e2ba27f1ed7c5b388eea24b1088ad2bc389e461cc7a47c6195191"
        )
        checks["rclone license"] = license_path in names and b"Permission is hereby granted" in z.read(license_path)
    bad = [k for k, ok in checks.items() if not ok]
    for k, ok in checks.items(): print(("ok   " if ok else "FAIL ") + k)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
