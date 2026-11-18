"""Sign or verify Inbound's arm64 release APK without giving secrets to MSBuild.

The release certificate is public and pinned here. Verification needs no secrets.
Signing gives only apksigner a reference to the existing password file.
"""
import argparse
import hashlib
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

RELEASE_CERT_SHA256 = "3746bf5a8975e623e8acd8dee6ad2665c3a26ffbf6f34ac6f33380f65d8f0457"


def run(args, *, timeout=120):
    result = subprocess.run([str(arg) for arg in args], capture_output=True, timeout=timeout)
    if result.returncode:
        # Do not forward diagnostics from an operation involving a secret.
        raise ValueError(f"{Path(args[0]).name} failed (exit {result.returncode})")
    return "\n".join(result.stdout.decode("utf-8", errors="replace").splitlines())


def check_signer(output):
    lines = "\n".join(output.splitlines())
    count = re.findall(r"^Number of signers: (\d+)$", lines, re.M)
    digests = re.findall(
        r"^(?:Signer #\d+ certificate|V\d+(?:\.\d+)? Signer(?: #\d+)?: certificate) SHA-256 digest: ([0-9a-fA-F]{64})$",
        lines, re.M)
    if count != ["1"] or not digests or any(d.lower() != RELEASE_CERT_SHA256 for d in digests):
        raise ValueError("APK must have one signer matching Inbound's pinned release certificate")


def check_manifest(output):
    lines = "\n".join(output.splitlines())
    required = [r"^package: name='app\.inbound\.android' ", r"^(?:minSdkVersion|sdkVersion):'31'$",
                r"^targetSdkVersion:'35'$", r"^native-code: 'arm64-v8a'\s*$"]
    if any(not re.search(pattern, lines, re.M) for pattern in required):
        raise ValueError("Expected app.inbound.android, minimum API 31, target 35 and arm64-only")
    if re.search(r"^application-debuggable", lines, re.M):
        raise ValueError("Release APK must not be debuggable")


def find_tools(android_sdk, java_sdk):
    java = java_sdk / "bin/java.exe"
    tools = []
    for directory in (android_sdk / "build-tools").iterdir():
        if re.fullmatch(r"\d+\.\d+\.\d+", directory.name):
            jar = directory / "lib/apksigner.jar"
            aapt = directory / "aapt2.exe"
            if jar.is_file() and aapt.is_file():
                tools.append((tuple(map(int, directory.name.split("."))), jar, aapt))
    if not java.is_file() or not tools:
        raise ValueError("Java and a stable Android build-tools apksigner/aapt2 are required")
    _, jar, aapt = max(tools)
    return java, jar, aapt


def verify(apk, java, jar, aapt):
    check_signer(run([java, "-jar", jar, "verify", "--verbose", "--print-certs", apk]))
    check_manifest(run([aapt, "dump", "badging", apk]))
    checker = Path(__file__).with_name("check-apk.py")
    packaging = run([sys.executable, "-I", checker, apk, "--release"])
    print(packaging)
    print("APK signature verified; sole signer matches Inbound's pinned release certificate")
    print("Package, minimum/target SDK, arm64-only ABI and non-debuggable flag verified")
    print(f"Signer certificate SHA-256: {RELEASE_CERT_SHA256}")
    with apk.open("rb") as stream:
        print(f"APK SHA-256: {hashlib.file_digest(stream, 'sha256').hexdigest()}")


def sign_release(apk, source, keystore, password_file, java, jar, aapt):
    for path in (source, keystore, password_file):
        if path is None or not path.is_file():
            raise ValueError("sign requires existing --input, --keystore and --password-file")
    if apk.exists() or apk.resolve() == source.resolve():
        raise ValueError("Signing output must be a new file distinct from the input")
    # Check non-secret build output before accessing the release key.
    check_manifest(run([aapt, "dump", "badging", source]))
    run([sys.executable, "-I", Path(__file__).with_name("check-apk.py"), source, "--release"])
    descriptor, pending_name = tempfile.mkstemp(prefix=".inbound-", suffix=".apk.pending", dir=apk.parent)
    os.close(descriptor)
    pending = Path(pending_name)
    try:
        # This PKCS12 uses the same store/key password. Omit --key-pass so
        # apksigner reuses the store password instead of reading a second file line.
        run([java, "-jar", jar, "sign", "--ks", keystore, "--ks-key-alias", "inbound",
             "--ks-pass", f"file:{password_file.resolve()}", "--debuggable-apk-permitted", "false",
             "--v4-signing-enabled", "false", "--out", pending, source])
        verify(pending, java, jar, aapt)
        # Same-directory hard link publishes atomically and fails if the target
        # appeared meanwhile. It cannot overwrite a user's existing artifact.
        os.link(pending, apk)
    finally:
        pending.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["sign", "verify"])
    parser.add_argument("apk", type=Path, help="Output APK for sign; existing APK for verify")
    parser.add_argument("--java-sdk", required=True, type=Path)
    parser.add_argument("--android-sdk", required=True, type=Path)
    parser.add_argument("--input", type=Path, help="Fresh build APK to sign; never overwritten")
    parser.add_argument("--keystore", type=Path)
    parser.add_argument("--password-file", type=Path)
    args = parser.parse_args()
    java, jar, aapt = find_tools(args.android_sdk, args.java_sdk)
    if args.action == "sign":
        sign_release(args.apk, args.input, args.keystore, args.password_file, java, jar, aapt)
    else:
        if not args.apk.is_file():
            raise ValueError("APK not found")
        verify(args.apk, java, jar, aapt)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.TimeoutExpired) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
