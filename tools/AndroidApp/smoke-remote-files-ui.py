"""Exercise Remote Files end-to-end on a dedicated API 33 emulator."""

import argparse
import hashlib
import os
import re
import socket
import subprocess
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path

from http.server import ThreadingHTTPServer
from importlib.machinery import SourceFileLoader


PACKAGE = "app.inbound.android.rcloneprobe"
ACTIVITY = PACKAGE + "/app.inbound.android.MainActivity"
AVD = "inbound33"
API = "33"
FILE = "inbound-remote-files.txt"
PAYLOAD = b"abc"
SHA256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"


def find_aapt():
    sdk = os.environ.get("ANDROID_SDK_ROOT") or os.environ.get("ANDROID_HOME")
    if not sdk and os.environ.get("LOCALAPPDATA"):
        sdk = str(Path(os.environ["LOCALAPPDATA"]) / "Android" / "Sdk")
    if not sdk:
        return None
    candidates = []
    for path in (Path(sdk) / "build-tools").glob("*/aapt*"):
        if path.is_file() and path.name.lower() in ("aapt", "aapt.exe"):
            candidates.append(path)
    return str(sorted(candidates, key=lambda p: tuple(
        int(x) if x.isdigit() else 0 for x in re.findall(r"\d+|\D+", p.parent.name)), reverse=True)[0]) if candidates else None


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def center(node):
    left, top, right, bottom = map(int, re.findall(r"\d+", node.get("bounds", "")))
    return (left + right) // 2, (top + bottom) // 2


class Smoke:
    def __init__(self, args):
        self.args = args
        self.nonce = uuid.uuid4().hex[:8]
        self.location = "Remote UI " + self.nonce
        self.xml_path = f"/data/local/tmp/remote-files-ui-{self.nonce}.xml"
        self.evidence = args.evidence.resolve()
        self.evidence.mkdir(parents=True, exist_ok=False)
        self.installed = self.reversed = self.started = False
        self.port = None

    def log(self, text):
        print(text, flush=True)
        with (self.evidence / "result.txt").open("a", encoding="utf-8") as output:
            output.write(text + "\n")

    def adb(self, *args, raw=False):
        result = subprocess.run([self.args.adb, "-s", self.args.serial, *map(str, args)],
                                capture_output=True, timeout=120 if args and args[0] == "install" else 25, check=True)
        return result.stdout if raw else result.stdout.decode("utf-8", errors="replace")

    def shell(self, *args):
        return self.adb("shell", *args)

    def tree(self):
        for _ in range(2):
            result = self.shell("uiautomator", "dump", self.xml_path)
            if f"dumped to: {self.xml_path}" in result:
                return ET.fromstring(self.adb("exec-out", "cat", self.xml_path, raw=True))
        raise RuntimeError("Android did not write a fresh UI hierarchy")

    @staticmethod
    def matches(tree, label):
        return [n for n in tree.iter("node")
                if label in (n.get("text"), n.get("content-desc"))]

    def wait_node(self, label, timeout=25, resource=None):
        deadline = time.monotonic() + timeout
        latest = None
        while time.monotonic() < deadline:
            latest = self.tree()
            found = self.matches(latest, label)
            if resource:
                found = [node for node in found if node.get("resource-id", "").endswith(":id/" + resource)]
            if found:
                require(len(found) == 1, f"Expected one UI control {label!r}, got {len(found)}")
                return found[0], latest
            time.sleep(0.4)
        raise RuntimeError(f"Timed out waiting for {label!r}")

    def wait_content_description(self, predicate, description, timeout=25):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            latest = self.tree()
            found = [node for node in latest.iter("node")
                     if predicate(node.get("content-desc", ""))]
            if found:
                require(len(found) == 1,
                        f"Expected one UI control {description!r}, got {len(found)}")
                return found[0], latest
            time.sleep(0.4)
        raise RuntimeError(f"Timed out waiting for {description!r}")

    def wait_content_description_prefix(self, prefix, timeout=25):
        return self.wait_content_description(
            lambda value: value.startswith(prefix), prefix + "*", timeout)

    def wait_content_description_suffix(self, suffix, timeout=25):
        return self.wait_content_description(
            lambda value: value.endswith(suffix), "*" + suffix, timeout)

    def download_selected_files(self):
        selected = [node for node in self.tree().iter("node")
                    if node.get("content-desc", "").startswith("Selected " + FILE + ",")]
        if not selected:
            file_card, _ = self.wait_content_description_prefix("Select " + FILE + ",")
            self.tap(file_card)
        else:
            require(len(selected) == 1, "Expected one selected fixture file card")
        transfer, _ = self.wait_node("↓  To this phone")
        self.tap(transfer)
        confirm, _ = self.wait_node("Download")
        self.tap(confirm)

    def target_exists(self, target):
        return self.shell(f"if [ -f '{target}' ]; then echo yes; fi").strip() == "yes"

    def tap(self, node):
        self.shell("input", "tap", *center(node))

    def enter(self, hint, value):
        node, _ = self.wait_node(hint)
        self.tap(node)
        self.shell("input", "text", value.replace(" ", "%s"))

    def preflight(self):
        require(self.args.serial.startswith("emulator-"), "Refusing a non-emulator serial")
        require(self.adb("emu", "avd", "name").splitlines()[0] == AVD, f"Expected dedicated AVD {AVD}")
        require(self.shell("getprop", "ro.build.version.sdk").strip() == API, f"Expected API {API}")
        self.validated_emulator = True
        require(Path(self.args.apk).is_file(), "Explicit probe APK does not exist")
        aapt = self.args.aapt or find_aapt()
        require(aapt is not None, "Android build-tools aapt.exe is required to validate the APK package")
        badging = subprocess.run([aapt, "dump", "badging", str(Path(self.args.apk).resolve())],
                                 capture_output=True, text=True, timeout=25, check=True).stdout
        require(re.search(r"^package: name=['\"]" + re.escape(PACKAGE) + r"['\"](?:\s|$)",
                          badging, re.MULTILINE) is not None,
                f"Refusing an APK that is not {PACKAGE}")
        packages = self.shell("pm", "list", "packages", PACKAGE)
        require(f"package:{PACKAGE}" not in packages, "Probe app is already installed; refusing non-fresh run")
        require(self.shell("test", "!", "-e", f"/sdcard/Download/{FILE}").strip() == "",
                f"Refusing because /sdcard/Download/{FILE} already exists")
        inventory = self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f")
        self.baseline_files = {line.rsplit("/", 1)[-1] for line in inventory.splitlines()}
        (self.evidence / "apk.sha256").write_text(
            hashlib.sha256(Path(self.args.apk).read_bytes()).hexdigest() + "\n", encoding="ascii")
        self.log(f"Preflight PASS: {AVD}, API {API}; probe absent; Download files={len(self.baseline_files)}")

    def run(self):
        self.preflight()
        module = SourceFileLoader("remote_files_fixture", str(
            Path(__file__).with_name("remote-files-fixture.py"))).load_module()
        require(module.PAYLOAD == PAYLOAD and hashlib.sha256(module.PAYLOAD).hexdigest() == SHA256,
                "The reusable WebDAV fixture payload/hash changed")
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        reverse = self.adb("reverse", "--list")
        require(not re.search(rf"\btcp:{port}\b", reverse), f"ADB reverse port {port} is already in use")
        class FixtureHandler(module.Handler):
            def do_GET(handler):
                server.smoke_requests.append(handler.path)
                if server.hold_get and handler.path == "/" + FILE:
                    server.get_arrived.set()
                    require(server.release_get.wait(20), "Timed out waiting to place the competing file")
                super().do_GET()

        server = ThreadingHTTPServer(("127.0.0.1", port), FixtureHandler)
        server.smoke_requests = []
        server.hold_get = False
        server.get_arrived = threading.Event()
        server.release_get = threading.Event()
        self.port = port
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            self.adb("reverse", f"tcp:{port}", f"tcp:{port}")
            self.reversed = True
            self.adb("install", str(Path(self.args.apk).resolve()))
            self.installed = True
            require(f"package:{PACKAGE}" in self.shell("pm", "list", "packages", PACKAGE),
                    "Explicit APK did not install the isolated probe package")
            self.adb("shell", "am", "start", "-W", "-n", ACTIVITY, "--ez", "skip_first_run", "true")
            self.started = True
            nav, tree = self.wait_node("Remote Files", resource="nav_remote_files")
            self.tap(nav)
            setup, _ = self.wait_node("Enable Remote Files", timeout=35)
            self.tap(setup)
            self.wait_node("Add location", timeout=50)
            add, _ = self.wait_node("Add location")
            self.tap(add)
            self.wait_node("Add WebDAV location")
            self.enter("Location name", self.location)
            self.enter("WebDAV address (https://…)", f"http://127.0.0.1:{port}/")
            allow, _ = self.wait_node(
                "Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.")
            self.tap(allow)
            save, _ = self.wait_node("Save location")
            self.tap(save)
            card, _ = self.wait_node(self.location, timeout=40)
            self.tap(card)
            self.download_selected_files()

            target = f"/sdcard/Download/{FILE}"
            deadline = time.monotonic() + 75
            digest = ""
            while time.monotonic() < deadline:
                if self.target_exists(target):
                    digest = self.shell("sha256sum", target)
                    if SHA256 in digest:
                        break
                time.sleep(0.5)
            require(SHA256 in digest, "Downloaded file did not match fixture SHA-256")
            require("/" + FILE in server.smoke_requests, "Fixture did not record the file GET")
            current = {line.rsplit("/", 1)[-1] for line in
                       self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()}
            require(current == self.baseline_files | {FILE},
                    f"Unexpected Download inventory: {len(current - self.baseline_files - {FILE})} extra, "
                    f"{len((self.baseline_files | {FILE}) - current)} missing")
            self.log(f"PASS browse and download: {target}; SHA-256 {SHA256}; no extra files")
            (self.evidence / "download.sha256").write_text(digest, encoding="utf-8")
            self.shell("rm", target)
            server.hold_get = True
            back, _ = self.wait_node("‹  Back")
            self.tap(back)
            self.download_selected_files()
            require(server.get_arrived.wait(20), "Second download did not reach the fixture")
            competing = b"keep the existing file\n"
            self.shell(f"set -C; echo 'keep the existing file' > '{target}'")
            self.competing_hash = hashlib.sha256(competing).hexdigest()
            server.release_get.set()
            deadline = time.monotonic() + 35
            while time.monotonic() < deadline:
                if any("1 skipped, 0 failed of 1" in node.get("text", "") for node in self.tree().iter("node")):
                    break
                refresh, _ = self.wait_node("Refresh transfers")
                self.tap(refresh)
            else:
                raise RuntimeError("Second transfer was not reported skipped")
            require(self.shell("sha256sum", target).split()[0] == self.competing_hash,
                    "The competing file changed")
            self.log("PASS publication conflict: one skipped, competing file unchanged")
            self.shell("rm", target)
            server.hold_get = False
            self.adb("shell", "am", "start", "-W", "-n", ACTIVITY, "--ez", "hold_remote_files_publication", "true")
            pid = self.shell("pidof", PACKAGE).strip()
            require(pid.isdigit(), "Could not identify the isolated probe process")
            back, _ = self.wait_node("‹  Back")
            self.tap(back)
            self.download_selected_files()
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                logs = self.adb("logcat", "-d", f"--pid={pid}", "-s", "InboundTest")
                if "Remote Files exclusive destination opened; waiting before copy" in logs:
                    (self.evidence / "publication-before-stop.log").write_text(logs, encoding="utf-8")
                    break
                time.sleep(0.3)
            else:
                raise RuntimeError("The transfer did not reach the Android exclusive-copy hook")
            self.adb("shell", "am", "force-stop", PACKAGE)
            gone = subprocess.run([self.args.adb, "-s", self.args.serial, "shell", "pidof", PACKAGE],
                                  capture_output=True, timeout=25)
            require(gone.returncode == 1 and not gone.stdout.strip() and not gone.stderr.strip(),
                    "The probe process survived the force-stop or ADB failed")
            self.interrupted_hash = hashlib.sha256(b"").hexdigest()
            require(self.shell("sha256sum", target).split()[0] == self.interrupted_hash,
                    "The force-stop did not leave the opened, still-empty destination")
            self.adb("shell", "am", "start", "-W", "-n", ACTIVITY, "--ez", "skip_first_run", "true")
            nav, _ = self.wait_node("Remote Files", resource="nav_remote_files")
            self.tap(nav)
            card, _ = self.wait_node(self.location)
            self.tap(card)
            transfer_summary, _ = self.wait_content_description_suffix(". Open transfers.")
            self.tap(transfer_summary)
            transfers, _ = self.wait_node("Transfers")
            self.tap(transfers)
            deadline = time.monotonic() + 35
            while time.monotonic() < deadline:
                tree = self.tree()
                if any("0 skipped, 1 failed of 1" in (text := node.get("text", "")) and
                       f"{FILE}: Saving this file was interrupted. The destination may be incomplete" in text
                       for node in tree.iter("node")):
                    ET.ElementTree(tree).write(self.evidence / "publication-after-restart.xml", encoding="utf-8")
                    break
                refresh, _ = self.wait_node("Refresh transfers")
                self.tap(refresh)
            else:
                raise RuntimeError("Interrupted transfer did not show failure and a filename-specific warning")
            require(self.shell("sha256sum", target).split()[0] == self.interrupted_hash,
                    "Recovery modified the interrupted public file")
            self.log("PASS process kill: recovery left the incomplete file unchanged and showed one failed, zero skipped")
        finally:
            server.release_get.set()
            server.shutdown()
            thread.join()

    def clean(self):
        errors = []
        try:
            if self.installed:
                try:
                    self.adb("uninstall", PACKAGE)
                    require(f"package:{PACKAGE}" not in self.shell("pm", "list", "packages", PACKAGE),
                            "Probe package remains installed after cleanup")
                except Exception as exc:
                    errors.append(f"uninstall: {exc}")
            target = f"/sdcard/Download/{FILE}"
            try:
                if self.started and self.target_exists(target):
                    digest = self.shell("sha256sum", target)
                    require(digest.split()[0] in (SHA256, getattr(self, "competing_hash", None),
                                                  getattr(self, "interrupted_hash", None)),
                            "Refusing to delete unexpected file at fixture output path")
                    self.shell("rm", "-f", target)
                if hasattr(self, "baseline_files"):
                    current = {line.rsplit("/", 1)[-1] for line in
                               self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()}
                    require(current == self.baseline_files,
                            f"Download directory differs after cleanup: {len(current - self.baseline_files)} added, "
                            f"{len(self.baseline_files - current)} removed")
            except Exception as exc:
                errors.append(f"payload cleanup: {exc}")
            if self.reversed:
                try:
                    self.adb("reverse", "--remove", f"tcp:{self.port}")
                except Exception as exc:
                    errors.append(f"reverse cleanup: {exc}")
        finally:
            if getattr(self, "validated_emulator", False):
                try:
                    self.shell("rm", "-f", self.xml_path)
                except Exception as exc:
                    errors.append(f"UI dump cleanup: {exc}")
        if errors:
            raise RuntimeError("; ".join(errors))
        if self.started:
            self.log("PASS cleanup: isolated probe uninstalled; owned ADB reverse removed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt", help="Path to Android build-tools aapt(.exe); auto-detected from the SDK by default")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit app.inbound.android.rcloneprobe APK")
    parser.add_argument("--evidence", type=Path, required=True, help="New evidence directory")
    smoke = Smoke(parser.parse_args())
    try:
        smoke.run()
    except Exception:
        if smoke.started:
            try:
                ET.ElementTree(smoke.tree()).write(smoke.evidence / "failure-ui.xml", encoding="utf-8")
                logs = smoke.adb("logcat", "-d", "-s", "Inbound", "InboundRemoteFilesProbe")
                (smoke.evidence / "failure-logcat.txt").write_text(logs, encoding="utf-8")
            except Exception as error:
                smoke.log(f"Could not collect failure evidence: {error}")
        raise
    finally:
        smoke.clean()


if __name__ == "__main__":
    main()
