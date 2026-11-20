"""Capture C3/C4 Remote Files parity and hash-check two selected downloads."""

import argparse
import hashlib
import importlib.machinery
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[2]
MOCKUPS = ROOT / "docs" / "plans" / "android-mockups"
FIXTURE_PATH = Path(__file__).with_name("remote-files-parity-fixture.py")
SMOKE_PATH = Path(__file__).with_name("smoke-remote-files-ui.py")
EXPECTED_FILES = ("session-0412.flac", "session-0419.flac")
EXPECTED_C4_ORDER = (
    "masters", "stems", "session-0412.flac", "session-0419.flac",
    "session-0503.flac", "notes.pdf", "artwork.zip",
)
PACKAGE = "app.inbound.android.rcloneprobe"
ACTIVITY = PACKAGE + "/app.inbound.android.MainActivity"
AVD = "inbound33"
API = "33"


def load_module(name, path):
    return importlib.machinery.SourceFileLoader(name, str(path)).load_module()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def make_pair(reference, capture, output, label):
    """Create a clearly labelled side-by-side PNG; sources remain unchanged."""
    with Image.open(reference) as original, Image.open(capture) as actual:
        originals = original.convert("RGB")
        captures = actual.convert("RGB")
    common_height = 1200
    def fit(image):
        width = max(1, round(image.width * common_height / image.height))
        return image.resize((width, common_height), Image.Resampling.LANCZOS)

    left, right = fit(originals), fit(captures)
    header = 42
    canvas = Image.new("RGB", (left.width + right.width, common_height + header), "#171717")
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.load_default()
    draw.text((16, 13), f"{label} · approved reference", fill="white", font=font)
    draw.text((left.width + 16, 13), f"{label} · emulator capture", fill="white", font=font)
    canvas.paste(left, (0, header))
    canvas.paste(right, (left.width, header))
    canvas.save(output, format="PNG")
    return canvas.size


class Capture:
    def __init__(self, args):
        smoke_module = load_module("remote_files_existing_smoke", SMOKE_PATH)
        self.smoke = smoke_module.Smoke(args)
        self.args = args
        self.evidence = self.smoke.evidence
        self.fixture = None
        self.fixture_thread = None
        self.output_paths = []
        self.port = None
        self.s3_process = None
        self.s3_root = None

    def adb(self, *args, raw=False):
        return self.smoke.adb(*args, raw=raw)

    def shell(self, *args):
        return self.smoke.shell(*args)

    def tree(self):
        return self.smoke.tree()

    def wait_node(self, label, timeout=25, resource=None):
        return self.smoke.wait_node(label, timeout=timeout, resource=resource)[0]

    def wait_contains(self, fragment, timeout=25):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            tree = self.tree()
            if any(fragment in node.get("text", "") for node in tree.iter("node")):
                return tree
            time.sleep(0.4)
        raise RuntimeError(f"Timed out waiting for UI text containing {fragment!r}")

    def tap(self, label, timeout=25):
        self.smoke.tap(self.wait_node(label, timeout=timeout))

    def tap_card_prefix(self, prefix, timeout=25):
        node, _ = self.smoke.wait_content_description_prefix(prefix, timeout=timeout)
        self.smoke.tap(node)

    def tap_local_folder(self, name):
        card = next((node for node in self.tree().iter("node")
                     if node.get("content-desc", "").startswith(name + ", local folder.")), None)
        self.smoke.tap(card if card is not None else self.wait_node(name))

    def capture_screen(self, name):
        path = self.evidence / f"{name}.png"
        path.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        return path

    def file_exists(self, path):
        return self.shell(f"if [ -f '{path}' ]; then echo yes; fi").strip() == "yes"

    def preflight(self):
        require(self.args.serial.startswith("emulator-"), "Refusing a non-emulator serial")
        require(self.adb("emu", "avd", "name").splitlines()[0] == AVD, f"Expected dedicated AVD {AVD}")
        require(self.shell("getprop", "ro.build.version.sdk").strip() == API, f"Expected API {API}")
        self.smoke.preflight()
        standalone = (self.args.s3_only or getattr(self.args, "upload_host_only", False)
                      or getattr(self.args, "upload_source_only", False) or getattr(self.args, "s3_upload_ui", False))
        if not standalone:
            require(self.shell("test", "!", "-e", "/sdcard/Download/session-0412.flac").strip() == "",
                    "Refusing because session-0412.flac already exists in shared Downloads")
            require(self.shell("test", "!", "-e", "/sdcard/Download/session-0419.flac").strip() == "",
                    "Refusing because session-0419.flac already exists in shared Downloads")
            self.output_paths = [f"/sdcard/Download/{name}" for name in EXPECTED_FILES]
            if self.args.nested_destination:
                self.output_paths = [f"/sdcard/Download/Inbound/Music/{name}" for name in EXPECTED_FILES]
            (self.evidence / "fixture-hashes.txt").write_text(
                "\n".join(f"{name} {digest}" for name, digest in self.fixture_module.HASHES.items()) + "\n",
                encoding="ascii")
        if standalone:
            self.smoke.log("Preflight PASS: dedicated inbound33/API 33 emulator and isolated probe APK")
        else:
            self.smoke.log("Preflight PASS: dedicated inbound33/API 33 emulator, isolated probe APK, two parity destinations absent")

    def start_fixture(self):
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            self.port = sock.getsockname()[1]
        reverse = self.adb("reverse", "--list")
        require(not re.search(rf"\btcp:{self.port}\b", reverse), f"ADB reverse port {self.port} is already in use")
        self.fixture = self.fixture_module.FixtureServer(("127.0.0.1", self.port))
        import threading
        self.fixture_thread = threading.Thread(target=self.fixture.serve_forever, daemon=True)
        self.fixture_thread.start()
        self.adb("reverse", f"tcp:{self.port}", f"tcp:{self.port}")
        self.smoke.reversed = True
        self.smoke.port = self.port

    @staticmethod
    def s3_serve_command(rclone, root, port, access_key, secret_key):
        """Match SiteKindsEndToEndTests' real-rclone serve s3 invocation."""
        return [
            str(rclone), "serve", "s3", str(root), "--addr", f"127.0.0.1:{port}",
            "--auth-key", f"{access_key},{secret_key}",
            "--cache-dir", str(Path(root).parent / "server-cache"), "--config", "",
            "--dir-cache-time", "0s",
        ]

    def start_s3_fixture(self):
        rclone = Path(self.args.rclone).expanduser().resolve()
        require(rclone.is_file(), f"Passed --rclone executable does not exist: {rclone}")
        version = subprocess.run([str(rclone), "version"], capture_output=True, text=True,
                                 timeout=15, check=True).stdout
        require("rclone" in version.lower(), "--rclone did not report an rclone version")
        self.smoke.log("Preflight PASS: using the explicitly supplied Windows rclone executable")

        self.s3_temp = tempfile.TemporaryDirectory(prefix="inbound-android-s3-parity-")
        self.s3_root = Path(self.s3_temp.name) / "served"
        self.s3_root.mkdir()
        bucket = self.s3_root / "pub"
        bucket.mkdir()
        (bucket / "readme.txt").write_text("read me", encoding="ascii")
        # These are deliberately synthetic test credentials; never use account keys here.
        access_key = "INBOUNDTESTKEY"
        secret_key = "inbound-test-secret-8813"
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            self.port = sock.getsockname()[1]
        reverse = self.adb("reverse", "--list")
        require(not re.search(rf"\btcp:{self.port}\b", reverse),
                f"ADB reverse port {self.port} is already in use")
        command = self.s3_serve_command(rclone, self.s3_root, self.port, access_key, secret_key)
        # Suppress rclone output: it is unnecessary for evidence and must not expose keys.
        self.s3_process = subprocess.Popen(command, stdin=subprocess.DEVNULL,
                                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if self.s3_process.poll() is not None:
                raise RuntimeError("The supplied rclone exited before its S3 listener became ready")
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.25):
                    break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("rclone serve s3 did not become ready on loopback")
        try:
            self.adb("reverse", f"tcp:{self.port}", f"tcp:{self.port}")
            self.smoke.port = self.port
            self.smoke.reversed = True
        except Exception:
            self.stop_s3_fixture()
            raise

    def stop_s3_fixture(self):
        if self.s3_process is not None:
            if self.s3_process.poll() is None:
                self.s3_process.terminate()
                try:
                    self.s3_process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.s3_process.kill()
                    self.s3_process.wait(timeout=5)
            self.s3_process = None
        if getattr(self, "s3_temp", None) is not None:
            self.s3_temp.cleanup()
            self.s3_temp = None

    def verify_s3(self, atomic=False):
        self.tap("Add location", timeout=40)
        self.tap("Location type: WebDAV")
        self.tap("S3")
        self.smoke.enter("Location name", "Backups")
        provider, _ = self.smoke.wait_content_description(
            lambda value: value == "S3 provider", "S3 provider")
        self.smoke.tap(provider)
        self.tap("Other S3-compatible service")
        endpoint = f"http://127.0.0.1:{self.port}"
        self.smoke.enter("S3 endpoint (optional for Amazon S3)", endpoint)
        self.smoke.enter("Region (optional)", "eu-central")
        self.smoke.enter("Access key ID", "INBOUNDTESTKEY")
        self.shell("input", "keyevent", "4")
        self.smoke.enter("Secret access key", "inbound-test-secret-8813")
        # Dismiss the keyboard before saving; the final secret field is password-masked.
        self.shell("input", "keyevent", "4")
        self.tap("Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.")
        self.tap("Save location")
        self.smoke.wait_content_description_prefix("Backups. S3 · eu-central", timeout=40)
        self.capture_screen("s3-location")
        (self.evidence / "s3-location-ui.xml").write_bytes(ET.tostring(self.tree(), encoding="utf-8"))
        self.smoke.log("PASS S3 location card: Backups / S3 · eu-central")
        self.tap_card_prefix("Backups. S3 · eu-central")
        bucket = "bucket" if atomic else "pub"
        self.wait_node(bucket, timeout=40)
        self.tap_card_prefix(f"{bucket}, folder.", timeout=30)
        if atomic:
            self.tap_card_prefix("pub, folder.", timeout=30)
        self.wait_node("readme.txt", timeout=30)
        self.capture_screen("s3-browse")
        (self.evidence / "s3-browse-ui.xml").write_bytes(ET.tostring(self.tree(), encoding="utf-8"))
        self.smoke.log("PASS S3 browse: fixture bucket pub contains readme.txt")

    def verify_upload_host(self, s3=False):
        self.adb("install", str(Path(self.args.apk).resolve()))
        self.smoke.installed = True
        extra = "remote_files_s3_upload_port" if s3 else "remote_files_upload_port"
        self.adb("shell", "am", "start", "-W", "-n", ACTIVITY, "--ez", "skip_first_run", "true",
                 "--ei", extra, str(self.port))
        self.smoke.started = True
        pid = self.shell("pidof", PACKAGE).strip()
        require(pid.isdecimal(), "The upload probe did not start a single app process")
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            if s3 and self.fixture.fixture.race_arrived.is_set() and not self.fixture.fixture.race_release.is_set():
                self.fixture.fixture.competitor_write("race.bin", b"independent writer")
                self.fixture.fixture.race_release.set()
            logs = self.adb("logcat", "--pid", pid, "-d", "-s", "InboundRemoteFilesProbe")
            if "PASS: conditional upload committed; competing file refused" in logs or " E InboundRemoteFilesProbe" in logs:
                break
            time.sleep(0.5)
        (self.evidence / "upload-logcat.txt").write_text(logs, encoding="utf-8")
        require("PASS: conditional upload committed; competing file refused" in logs, "Host upload probe did not pass")
        if s3:
            state = self.fixture.fixture
            require(state.objects.get("inbound-upload-proof.bin") == b"abc" and state.objects.get("race.bin") == b"independent writer",
                    "The new object or competing object bytes did not match")
            require([(p["key"], p["if_none_match"], p["authorization_shape"], p["bytes"]) for p in state.puts]
                    == [("inbound-upload-proof.bin", "*", True, b"abc"), ("race.bin", "*", True, b"abc")],
                    "Expected exactly two signed conditional PUTs without staging writes or retries")
            self.smoke.log("PASS Android S3 commit race: new object contains abc; independent writer's bytes preserved; no fallback or retry")
            return
        base = "media/recordings/2026/"
        require(self.fixture.parity_uploads == {base + "inbound-upload-proof.bin": b"abc"},
                "Fixture received unexpected published bytes or replaced the competing file")
        require(not self.fixture.parity_upload_staging, "The fixture still holds a staged upload")
        self.smoke.log("PASS Android Host upload: fixture received abc; competing artwork.zip unchanged; staging empty")

    def verify_phone_upload_source(self, upload_ui=False):
        if not upload_ui:
            self.adb("install", str(Path(self.args.apk).resolve()))
            self.smoke.installed = True
            self.smoke.started = True
        component = ("shell", "am", "start", "-W", "-n", ACTIVITY, "--ez", "skip_first_run", "true")

        def wait_for(marker, failure_marker, *, none=False):
            pid = self.shell("pidof", PACKAGE).strip()
            require(pid.isdecimal(), "The source probe did not start a single app process")
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                logs = self.adb("logcat", "--pid", pid, "-d", "-s", "InboundRemoteFilesProbe")
                for line in logs.splitlines():
                    if failure_marker in line:
                        raise RuntimeError(line)
                    if marker in line or (none and "UPLOAD_SOURCE_CLEANUP_NONE" in line):
                        return line
                time.sleep(0.4)
            (self.evidence / f"{marker.lower()}-logcat.txt").write_text(logs, encoding="utf-8")
            raise RuntimeError(f"Timed out waiting for {marker}")

        try:
            self.adb(*component, "--ez", "media_upload_create", "true",
                     "--ez", "media_upload_nested", str(upload_ui).lower())
            line = wait_for("UPLOAD_SOURCE_READY", "UPLOAD_SOURCE_CREATE_FAILED")
            match = re.search(r"name=(inbound-c4-upload-probe-[0-9a-f]{32}\.txt) uri=(content://media/[^ ]+) bytes=(\d+) sha256=([0-9a-f]{64})", line)
            require(match is not None, "Probe did not identify its exact MediaStore file")
            name, uri, size, sha256 = match.groups()
            folder = "inbound-c4-" + name.removeprefix("inbound-c4-upload-probe-").removesuffix(".txt") if upload_ui else None
            file_path = f"/sdcard/Download/{folder}/{name}" if folder else f"/sdcard/Download/{name}"
            payload = b"Inbound C4 synthetic upload probe\n"
            require(int(size) == len(payload) and sha256 == hashlib.sha256(payload).hexdigest(), "Unexpected source contents")
            media = self.shell("content", "query", "--uri", uri, "--projection", "_display_name:_size:is_pending")
            require(f"_display_name={name}" in media and f"_size={size}" in media and "is_pending=0" in media,
                    "The source row was not published")
            require(self.shell("sha256sum", file_path).split()[0] == sha256, "Public file hash did not match")
            (self.evidence / "upload-source.txt").write_text(line + "\n" + media, encoding="utf-8")
            self.smoke.log(f"PASS Android upload source: published MediaStore row, {size} bytes, SHA-256 {sha256}")
            if upload_ui:
                self.adb(*component, "--ez", "media_upload_create", "true", "--ez", "media_upload_nested", "true")
                duplicate = wait_for("UPLOAD_SOURCE_CREATE_FAILED", "UPLOAD_SOURCE_CLEANUP_FAILED")
                require("already owned" in duplicate and self.file_exists(file_path),
                        "Duplicate create did not preserve the original owned source")
                self.smoke.log("PASS duplicate MediaStore create refused without replacing the owned row")
                self.tap("This phone")
                for system_back in (True, False):
                    self.tap_card_prefix(f"{folder}, local folder.")
                    self.tap_card_prefix(f"Select {name}, on this phone.")
                    if system_back:
                        self.shell("input", "keyevent", "4")
                    else:
                        self.tap("Back")
                    self.wait_node("0 files")
                    buttons = [n for n in self.tree().iter("node") if n.get("text") == "To site"]
                    require(len(buttons) == 1 and buttons[0].get("enabled") == "false",
                            "Folder back must clear selection and disable upload")
                self.smoke.log("PASS C4: both Back paths clear the nested phone-file selection")
                self.tap_card_prefix(f"{folder}, local folder.")
                self.tap_card_prefix(f"Select {name}, on this phone.")
                self.capture_screen("c4-this-phone-selection")
                self.tap("To site")
                self.wait_node("Upload file?")
                require(any(name in n.get("text", "") for n in self.tree().iter("node")),
                        "Confirmation must show the full source filename")
                self.capture_screen("c4-upload-confirmation")
                self.tap("Cancel")
                require(not self.fixture.fixture.puts, "Cancel must not send a PUT")
                self.tap("To site")
                self.tap("Upload")
                self.wait_node(name, timeout=40)
                state = self.fixture.fixture
                require(state.objects.get("pub/" + name) == payload and len(state.puts) == 1
                        and state.puts[0]["if_none_match"] == "*", "Expected one conditional upload with exact source bytes")
                self.capture_screen("c4-upload-completed")
                self.smoke.log("PASS C4 upload UI: This phone selection, Cancel made no request, Confirm uploaded the MediaStore bytes")
        finally:
            self.adb(*component, "--ez", "media_upload_cleanup", "true")
            cleaned = wait_for("UPLOAD_SOURCE_CLEANED", "UPLOAD_SOURCE_CLEANUP_FAILED", none="uri" not in locals())
            if "uri" in locals():
                require(f"uri={uri}" in cleaned and not self.file_exists(file_path),
                         "The owned upload source was not removed")
                if folder:
                    # Exact, nonce-named test folder only; rmdir refuses nonempty folders.
                    self.shell(f"if [ -d '/sdcard/Download/{folder}' ]; then rmdir '/sdcard/Download/{folder}'; fi")
            if "UPLOAD_SOURCE_CLEANED" in cleaned:
                self.smoke.log("PASS exact MediaStore source cleanup")

    def setup_location(self, populate=True):
        self.adb("install", str(Path(self.args.apk).resolve()))
        self.smoke.installed = True
        require(f"package:{PACKAGE}" in self.shell("pm", "list", "packages", PACKAGE),
                "Explicit probe APK did not install")
        self.adb("shell", "am", "start", "-W", "-n", ACTIVITY, "--ez", "skip_first_run", "true")
        self.smoke.started = True
        nav = self.wait_node("Remote Files", resource="nav_remote_files")
        self.smoke.tap(nav)
        self.tap("Enable Remote Files", timeout=35)
        if not populate:
            return
        self.location = "Home NAS"
        for name in ("Home NAS", "Work files", "Backups"):
            self.tap("Add location", timeout=50)
            self.smoke.enter("Location name", name)
            self.smoke.enter("WebDAV address (https://…)", f"http://127.0.0.1:{self.port}/")
            self.tap("Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.")
            self.tap("Save location")
            self.smoke.wait_content_description_prefix(name + ". ", timeout=40)
        capture = self.capture_screen("c3-capture")
        (self.evidence / "c3-ui.xml").write_bytes(ET.tostring(self.tree(), encoding="utf-8"))
        pair_size = make_pair(MOCKUPS / "c3-sites.png", capture, self.evidence / "c3-pair.png", "C3")
        self.smoke.log(f"Captured three saved C3 location cards and transfer summary; paired image {pair_size[0]}x{pair_size[1]}")

    def capture_selected_folder(self):
        self.tap_card_prefix(self.location + ". ")
        for folder in ("media", "recordings", "2026"):
            self.tap_card_prefix(folder + ", folder.", timeout=35)
        self.tap("This phone")
        self.wait_node("Downloads on this phone")
        if self.args.nested_destination:
            self.tap_local_folder("Inbound")
            self.wait_node("Downloads/Inbound")
            self.tap_local_folder("Music")
            self.wait_node("Downloads/Inbound/Music")
        self.tap("Use this download folder")
        self.wait_node("2026")
        for filename in EXPECTED_FILES:
            self.tap_card_prefix("Select " + filename + ",", timeout=25)
        self.tap("Site")
        tree = self.wait_contains("2 files ·", timeout=25)
        require(any(node.get("text") in ("To this phone", "↓  To this phone") for node in tree.iter("node")),
                "Two-file selection did not enable the To this phone action")
        require(all(any(node.get("content-desc", "").startswith("Selected " + name + ",")
                         for node in tree.iter("node"))
                    for name in EXPECTED_FILES), "The first selected C4 view does not show both selected files")
        self.tap("media", timeout=25)
        self.smoke.wait_content_description_prefix("recordings, folder.", timeout=20)
        # Re-enter the nested folder after checking that the ancestor breadcrumb
        # is an operable control; C4 evidence needs the deepest breadcrumb.
        for folder in ("recordings", "2026"):
            self.tap_card_prefix(folder + ", folder.", timeout=25)
        for filename in EXPECTED_FILES:
            self.tap_card_prefix("Select " + filename + ",", timeout=25)
        tree = self.wait_contains("2 files ·", timeout=25)
        self.wait_node("2026")
        if self.args.nested_destination:
            require(any(node.get("text") == "To Downloads/Inbound/Music" for node in tree.iter("node")),
                    "C4 did not show the chosen nested destination")
        require(all(any(node.get("content-desc", "").startswith("Selected " + name + ",")
                         for node in tree.iter("node"))
                    for name in EXPECTED_FILES), "The final C4 view does not show both selected files")
        visible_names = {node.get("text", "") for node in tree.iter("node")}
        actual_order = [node.get("text", "") for node in tree.iter("node")
                        if node.get("text", "") in EXPECTED_C4_ORDER]
        require(tuple(actual_order) == EXPECTED_C4_ORDER,
                f"C4 screenshot order mismatch: {actual_order}")
        for folder, count in (("masters", 12), ("stems", 48)):
            rows = [node for node in tree.iter("node") if node.get("content-desc", "").startswith(folder + ", folder.")]
            require(len(rows) == 1 and any(child.get("text") == f"Folder · {count} items" for child in rows[0].iter("node")),
                    f"C4 {folder} row does not show {count} direct children")
        self.smoke.log("PASS C4 listing: exact mockup row order; masters 12 items, stems 48 items")
        require(set(EXPECTED_C4_ORDER).issubset(visible_names),
                "C4 screenshot is missing one or more expected rows")
        capture = self.capture_screen("c4-capture")
        (self.evidence / "c4-ui.xml").write_bytes(ET.tostring(tree, encoding="utf-8"))
        pair_size = make_pair(MOCKUPS / "c4-site-browser.png", capture, self.evidence / "c4-pair.png", "C4")
        self.smoke.log(f"Captured C4 nested 2026 breadcrumb with both selected files; paired image {pair_size[0]}x{pair_size[1]}")

    def download_and_verify(self):
        tree = self.tree()
        self.tap("To this phone" if any(node.get("text") == "To this phone" for node in tree.iter("node"))
                 else "↓  To this phone")
        self.wait_node("Download 2 files?", timeout=20)
        self.tap("Download", timeout=20)
        self.wait_node("Transfers", timeout=35)
        deadlines = time.monotonic() + 90
        hashes = {}
        while time.monotonic() < deadlines:
            if all(self.file_exists(path) for path in self.output_paths):
                hashes = {}
                for name, path in zip(EXPECTED_FILES, self.output_paths):
                    line = self.shell("sha256sum", path).split()
                    if len(line) >= 1:
                        hashes[name] = line[0]
                if all(hashes.get(name) == self.fixture_module.HASHES[name] for name in EXPECTED_FILES):
                    break
            time.sleep(0.5)
        else:
            raise RuntimeError(f"Two selected downloads did not match fixture SHA-256 values: {hashes}")
        require(self.fixture.parity_gets == set(EXPECTED_FILES),
                f"Unexpected fixture GET set: {sorted(self.fixture.parity_gets)}")
        current = {line.rsplit("/", 1)[-1] for line in
                   self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()}
        expected_root = self.smoke.baseline_files if self.args.nested_destination else self.smoke.baseline_files | set(EXPECTED_FILES)
        require(current == expected_root, f"Unexpected shared Downloads changes: {current ^ expected_root}")
        if self.args.nested_destination:
            nested = set(self.shell("find", "/sdcard/Download/Inbound/Music", "-maxdepth", "1", "-type", "f").splitlines())
            require(nested == set(self.output_paths), "Nested destination contains something other than the selected files")
        (self.evidence / "download-hashes.txt").write_text(
            "\n".join(f"{name} {hashes[name]}" for name in EXPECTED_FILES) + "\n", encoding="ascii")
        self.smoke.log("PASS two selected files: " + ", ".join(f"{name} SHA-256 {hashes[name]}" for name in EXPECTED_FILES))

    def delete_and_verify(self):
        self.adb("shell", "input", "keyevent", "4")
        self.wait_node("2026")
        tree = self.tree()
        for node in tree.iter("node"):
            if any(node.get("content-desc", "").startswith("Selected " + name + ",") for name in EXPECTED_FILES):
                self.smoke.tap(node)
        self.tap_card_prefix("Select notes.pdf,")
        self.tap("Delete selected remote files")
        self.wait_node("Delete notes.pdf?")
        self.tap("Cancel")
        self.wait_node("2026")
        require(not getattr(self.fixture, "parity_notes_deleted", False), "Cancel deleted the remote file")
        self.tap("Delete selected remote files")
        self.wait_node("Delete notes.pdf?")
        self.tap("Delete")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if getattr(self.fixture, "parity_notes_deleted", False):
                tree = self.tree()
                labels = [node.get("text", "") + node.get("content-desc", "") for node in tree.iter("node")]
                if any("session-0503.flac" in label for label in labels) and not any("notes.pdf" in label for label in labels):
                    (self.evidence / "c4-delete-ui.xml").write_bytes(ET.tostring(tree, encoding="utf-8"))
                    self.smoke.log("PASS remote deletion: Cancel left the fixture unchanged; Confirm removed only notes.pdf")
                    return
            time.sleep(0.5)
        raise RuntimeError("Confirmed deletion was not verified against the fixture and refreshed listing")

    def verify_sftp(self):
        fixture_module = load_module("sftp_onboarding_fixture", Path(__file__).with_name("sftp-onboarding-fixture.py"))
        fixture = fixture_module.SftpFixture().start()
        owned_reverse = False
        port = fixture.port
        try:
            reverse = self.adb("reverse", "--list")
            require(not re.search(rf"\btcp:{port}\b", reverse), "SFTP fixture reverse already exists")
            self.adb("reverse", f"tcp:{port}", f"tcp:{port}")
            owned_reverse = True
            for _ in range(5):
                if any("Add location" in (node.get("text"), node.get("content-desc")) for node in self.tree().iter("node")):
                    break
                self.adb("shell", "input", "keyevent", "4")
            self.tap("Add location")
            self.tap("Location type: WebDAV")
            self.tap("SFTP")
            meta = fixture.metadata()
            for label, value in (("Location name", "Test SFTP"), ("SFTP server name or address", meta["host"]),
                                 ("Port", str(port)), ("User name (optional)", meta["username"]),
                                 ("Password (optional)", meta["password"])):
                if label == "Port":
                    self.tap(label)
                    self.shell("input", "keyevent", "123", "67", "67")  # End, then remove the default 22.
                self.smoke.enter(label, value)
            # Dismiss the soft keyboard so the Save action is not obscured or
            # intercepted by the IME on the final SFTP credential field.
            self.shell("input", "keyevent", "4")
            self.tap("Save location")
            self.tap_card_prefix("Test SFTP. ")
            self.wait_node("Trust this SFTP server key?")
            self.wait_contains(meta["hostKeyFingerprint"])
            self.tap("Cancel")
            self.wait_node("Add location")
            require(not fixture.server.authenticated.is_set(), "SFTP login occurred before trust")
            self.tap_card_prefix("Test SFTP. ")
            self.wait_node("Trust this SFTP server key?")
            self.wait_contains(meta["hostKeyFingerprint"])
            self.tap("Trust key")
            self.wait_node("Add location")
            self.smoke.wait_content_description_prefix("Test SFTP. SFTP ·")
            self.capture_screen("c3-sftp-location")
            self.tap_card_prefix("Test SFTP. ")
            self.wait_contains("welcome.txt")
            require(fixture.server.authenticated.is_set(), "No successful SFTP fixture authentication")
            self.capture_screen("c3-sftp-browse")
            self.smoke.log("PASS SFTP: fixture fingerprint matched; Cancel sent no login; Trust then browse authenticated")
            self.shell("input", "keyevent", "4")
            self.wait_node("Add location")
            expected_card = f"Test SFTP. SFTP · {meta['host']}:{port}. Open location."
            cards = [node.get("content-desc", "") for node in self.tree().iter("node")
                     if node.get("content-desc", "").startswith("Test SFTP. ")]
            require(cards == [expected_card], "Post-browse SFTP card did not show the expected unavailable-space detail")
            self.capture_screen("c3-sftp-location-after-browse")
            self.smoke.log("PASS SFTP unavailable space: post-browse location card shows no free-space figure")
        finally:
            try:
                if owned_reverse:
                    self.adb("reverse", "--remove", f"tcp:{port}")
            finally:
                fixture.close()

    def run(self):
        self.fixture_module = load_module("remote_files_parity_fixture", FIXTURE_PATH)
        self.preflight()
        try:
            if getattr(self.args, "upload_source_only", False):
                self.verify_phone_upload_source()
                return self.evidence
            if self.args.s3_only:
                self.start_s3_fixture()
            elif getattr(self.args, "s3_upload_host_only", False) or getattr(self.args, "s3_upload_ui", False):
                import threading
                module = load_module("s3_conditional_upload", Path(__file__).with_name("s3-conditional-upload-fixture.py"))
                self.fixture = module.serve()
                self.fixture.fixture.race_enabled = True
                self.port = self.fixture.server_port
                require(not re.search(rf"\btcp:{self.port}\b", self.adb("reverse", "--list")), "Fixture reverse already exists")
                self.fixture_thread = threading.Thread(target=self.fixture.serve_forever, daemon=True)
                self.fixture_thread.start()
                self.adb("reverse", f"tcp:{self.port}", f"tcp:{self.port}")
                self.smoke.reversed = True
                self.smoke.port = self.port
            else:
                self.start_fixture()
            if getattr(self.args, "s3_upload_ui", False):
                self.setup_location(populate=False)
                self.verify_s3(atomic=True)
                self.verify_phone_upload_source(upload_ui=True)
                return self.evidence
            if getattr(self.args, "upload_host_only", False):
                self.verify_upload_host(getattr(self.args, "s3_upload_host_only", False))
                return self.evidence
            if self.args.s3_only:
                self.setup_location(populate=False)
                self.verify_s3()
                return self.evidence
            if self.args.sftp_only:
                self.setup_location(populate=False)
                self.verify_sftp()
                return self.evidence
            if self.args.nested_destination:
                # Plain mkdir is exclusive; never claim or clean a pre-existing folder.
                self.shell("mkdir", "/sdcard/Download/Inbound")
                self.owns_inbound = True
                self.shell("mkdir", "/sdcard/Download/Inbound/Music")
                self.owns_music = True
            self.setup_location()
            self.capture_selected_folder()
            self.download_and_verify()
            self.delete_and_verify()
            if self.args.verify_sftp:
                self.verify_sftp()
        except Exception:
            if self.smoke.started:
                try:
                    ET.ElementTree(self.tree()).write(self.evidence / "failure-ui.xml", encoding="utf-8")
                except Exception as error:
                    self.smoke.log(f"Could not collect failure UI: {error}")
            raise
        finally:
            if self.fixture is not None:
                if self.fixture_thread is not None:
                    self.fixture.shutdown()
                self.fixture.server_close()
            if self.fixture_thread is not None:
                self.fixture_thread.join()
            if self.args.s3_only:
                self.stop_s3_fixture()
        return self.evidence

    def clean(self):
        errors = []
        if self.smoke.started:
            for name, path in zip(EXPECTED_FILES, self.output_paths):
                try:
                    if self.file_exists(path):
                        digest = self.shell("sha256sum", path).split()[0]
                        require(digest == self.fixture_module.HASHES[name],
                                f"Refusing cleanup of {name}: its hash is not the test fixture hash")
                        self.shell("rm", path)
                except Exception as exc:
                    errors.append(f"{name} cleanup: {exc}")
        for flag, path in (("owns_music", "/sdcard/Download/Inbound/Music"), ("owns_inbound", "/sdcard/Download/Inbound")):
            if getattr(self, flag, False):
                try:
                    self.shell("rmdir", path)
                except Exception as exc:
                    errors.append(f"{path} cleanup: {exc}")
        try:
            self.smoke.clean()
        except Exception as exc:
            errors.append(f"isolated smoke cleanup: {exc}")
        if errors:
            raise RuntimeError("; ".join(errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt", help="Android build-tools aapt(.exe); auto-detected by the existing smoke helper")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit app.inbound.android.rcloneprobe APK")
    parser.add_argument("--evidence", type=Path, required=True, help="New evidence directory")
    parser.add_argument("--verify-sftp", action="store_true", help="Additionally verify first-use SFTP trust with a loopback fixture (requires Paramiko)")
    parser.add_argument("--sftp-only", action="store_true", help="Run only first-use SFTP trust (requires Paramiko)")
    parser.add_argument("--s3-only", action="store_true",
                        help="Run only S3 location setup and bucket browsing against the passed --rclone executable")
    parser.add_argument("--rclone", help="Installed Windows rclone executable for --s3-only; no bundled/test substitute")
    parser.add_argument("--nested-destination", action="store_true", help="Verify Downloads/Inbound/Music (requires Downloads/Inbound to be absent)")
    parser.add_argument("--upload-host-only", action="store_true", help="Verify conditional upload through the isolated Android Host probe, not UI")
    parser.add_argument("--s3-upload-host-only", action="store_true", help="Verify S3 conditional commit with a competing writer, not UI")
    parser.add_argument("--upload-source-only", action="store_true", help="Verify isolated MediaStore source creation and exact cleanup, not UI")
    parser.add_argument("--s3-upload-ui", action="store_true", help="Verify C4 UI using only the loopback atomic S3 fixture")
    args = parser.parse_args()
    if args.s3_upload_host_only:
        args.upload_host_only = True
    if args.s3_upload_ui and (args.upload_source_only or args.upload_host_only or args.s3_only or args.sftp_only or args.verify_sftp or args.nested_destination):
        parser.error("--s3-upload-ui cannot be combined with other modes")
    if args.upload_source_only and (args.upload_host_only or args.s3_only or args.sftp_only or args.verify_sftp or args.nested_destination):
        parser.error("--upload-source-only cannot be combined with other modes")
    if args.s3_only and not args.rclone:
        parser.error("--s3-only requires --rclone with the installed Windows rclone executable")
    if args.s3_only and (args.sftp_only or args.verify_sftp or args.nested_destination):
        parser.error("--s3-only cannot be combined with SFTP or nested-destination modes")
    if args.upload_host_only and (args.s3_only or args.sftp_only or args.verify_sftp or args.nested_destination):
        parser.error("--upload-host-only cannot be combined with other modes")
    capture = Capture(args)
    try:
        result = capture.run()
        print(f"Evidence: {result}")
    finally:
        capture.clean()


if __name__ == "__main__":
    main()
