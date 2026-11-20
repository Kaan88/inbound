"""Guided A2/A3/A6/A7 captures using a disposable, throttled download fixture."""

import argparse
import hashlib
import importlib.machinery
import json
import re
import socket
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).parent
MOCKUPS = ROOT / "docs" / "plans" / "android-mockups"
SMOKE_PATH = HERE / "smoke-remote-files-ui.py"
PAIR_HELPER_PATH = HERE / "capture-remote-files-parity.py"
PACKAGE = "app.inbound.android.rcloneprobe"
ACTIVITY = PACKAGE + "/app.inbound.android.MainActivity"
AVD = "inbound33"
API = "33"
FILE_SIZE = 16 * 1024 * 1024
CHUNK_SIZE = 16 * 1024
CHUNK_DELAY_SECONDS = 0.1
B5_KEYS = ("selected-active", "selected-paused", "selected-queued",
           "other-active", "other-finished")
CAPTURES = (
    ("a2-progress", "a2-downloads-later.png", "Downloads"),
    ("a2-queued", "a2-downloads-later.png", "Downloads"),
    ("a6-active-details", "a6-details.png", "Details"),
    ("a7-notification", "a7-background.png", None),
    ("a3-finished-snackbar", "a3-downloads-finished.png", "Downloads"),
    ("a6-finished-details", "a6-details.png", "Details"),
)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def load_module(name, path):
    return importlib.machinery.SourceFileLoader(name, str(path)).load_module()


def fixture_bytes(key=None):
    """Deterministic fixture body; generated lazily, never checked into evidence."""
    if key is None:
        return bytes(range(256)) * (FILE_SIZE // 256)
    # B5 endpoints carry distinct bytes as well as distinct paths/names.
    marker = hashlib.sha256(key.encode("ascii")).digest()
    block = bytes(value ^ marker[index % len(marker)]
                  for index, value in enumerate(range(256)))
    return block * (FILE_SIZE // len(block))


class Capture:
    def __init__(self, args):
        self.args = args
        self.smoke_module = load_module("download_states_existing_smoke", SMOKE_PATH)
        self.smoke = self.smoke_module.Smoke(args)
        self.evidence = self.smoke.evidence
        self.installed = False
        self.started = False
        self.reversed = False
        self.fixture = None
        self.fixture_thread = None
        self.fixture_bytes = fixture_bytes()
        self.nonce = uuid.uuid4().hex[:10]
        self.names = ({key: f"inbound-b5-{key}-{self.nonce}.bin" for key in B5_KEYS}
                      if getattr(args, "unattended_b5", False) else {
                          "active": f"inbound-a2-active-{self.nonce}.bin",
                          "queued": f"inbound-a2-queued-{self.nonce}.bin",
                          "paused": f"inbound-a2-paused-{self.nonce}.bin",
                          "finished": f"inbound-a2-finished-{self.nonce}.bin",
                      })
        if getattr(args, "c8_typed_a1_failed", False):
            self.names = {
                "selected-active": f"Disc-{self.nonce}.iso",
                "selected-paused": f"Archive-{self.nonce}.zip",
                "selected-queued": f"Clip-{self.nonce}.mp4",
                "other-active": f"Photo-{self.nonce}.png",
                "other-finished": f"Notes-{self.nonce}.pdf",
                "owned-failed": f"Missing-{self.nonce}.bin",
            }
        self.retry_armed = threading.Event()
        self.retry_allowed = threading.Event()
        self.retry_output_owned = False
        self.requests = []
        self.sent = {key: 0 for key in self.names}
        self.b5_total_sent = {key: 0 for key in self.names}
        self.b5_release = {}
        self.b5_arrived = {}
        self.b5_disconnects = {}
        self.xml_path = f"/data/local/tmp/download-states-{self.nonce}.xml"
        self.smoke.xml_path = self.xml_path
        self.capture_gate = threading.Event()
        self.a3_output_owned = False

    def adb(self, *args, raw=False):
        return self.smoke.adb(*args, raw=raw)

    def shell(self, *args):
        return self.smoke.shell(*args)

    def log(self, message):
        print(message, flush=True)
        with (self.evidence / "flow.txt").open("a", encoding="utf-8") as stream:
            stream.write(message + "\n")

    def preflight(self):
        require(self.args.serial.startswith("emulator-"),
                "Refusing a physical device; only the dedicated emulator is allowed")
        require(self.adb("emu", "avd", "name").splitlines()[0] == AVD,
                f"Refusing anything other than the dedicated {AVD} AVD")
        require(self.shell("getprop", "ro.build.version.sdk").strip() == API,
                f"Refusing anything other than API {API}")
        apk = Path(self.args.apk).resolve()
        require(apk.is_file(), "Explicit isolated probe APK does not exist")
        aapt = self.args.aapt or self.smoke_module.find_aapt()
        require(aapt, "Android build-tools aapt(.exe) is required")
        badging = subprocess.run(
            [aapt, "dump", "badging", str(apk)], capture_output=True, text=True,
            timeout=25, check=True).stdout
        require(re.search(r"^package: name=['\"]" + re.escape(PACKAGE) + r"['\"](?:\s|$)",
                          badging, re.MULTILINE),
                f"APK package must be exactly {PACKAGE}")
        require(f"package:{PACKAGE}" not in self.shell("pm", "list", "packages", PACKAGE),
                "Probe package is already installed; refusing to replace or clear it")
        if self.args.capture_mixed_a3:
            target = "/sdcard/Download/" + self.names["other-active"]
            require(self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                    "A3 exact fixture destination already exists")
        if getattr(self.args, "c8_typed_a1_failed", False):
            for name in self.names.values():
                path = "/sdcard/Download/" + name
                require(self.shell(f"if [ -e '{path}' ] || [ -L '{path}' ]; then echo present; fi").strip() != "present",
                        "An exact fixture destination already exists")
        initial_files = self.shell(
            "find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()
        (self.evidence / "apk.sha256").write_text(
            hashlib.sha256(apk.read_bytes()).hexdigest() + "\n", encoding="ascii")
        (self.evidence / "preflight.json").write_text(json.dumps({
            "avd": AVD,
            "api": API,
            "serial": self.args.serial,
            "package": PACKAGE,
            "apk": str(apk),
            "initial_download_count": len(initial_files),
            "fixture_bytes": FILE_SIZE,
            "chunk_bytes": CHUNK_SIZE,
            "chunk_delay_seconds": CHUNK_DELAY_SECONDS,
            "fixture_names": self.names,
        }, indent=2) + "\n", encoding="utf-8")
        self.log("Preflight PASS: inbound33/API 33, explicit rcloneprobe APK, probe package absent.")

    def start_fixture(self):
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            self.port = sock.getsockname()[1]
        require(not re.search(rf"\btcp:{self.port}\b", self.adb("reverse", "--list")),
                f"ADB reverse port {self.port} is already in use")
        capture = self
        if getattr(self.args, "unattended_b5", False):
            self.b5_release = {key: threading.Event() for key in self.names
                               if key != "other-finished"}
            self.b5_arrived = {key: threading.Event() for key in self.b5_release}
        self.b5_disconnects = dict.fromkeys(self.b5_arrived, 0)

        class Handler(BaseHTTPRequestHandler):
            def do_HEAD(self):
                key = self.path.strip("/")
                if getattr(capture.args, "verify_a1_retry", False) and key == "owned-failed-" + capture.nonce:
                    key = "owned-failed"
                if key not in capture.names:
                    self.send_error(404)
                    return
                if key == "owned-failed":
                    capture.requests.append({"file": key, "method": "HEAD", "at": time.time()})
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(FILE_SIZE))
                self.send_header("Content-Disposition",
                                 f'attachment; filename="{capture.names[key]}"')
                self.end_headers()

            def do_GET(self):
                key = self.path.strip("/")
                if getattr(capture.args, "verify_a1_retry", False) and key == "owned-failed-" + capture.nonce:
                    key = "owned-failed"
                if key not in capture.names:
                    self.send_error(404)
                    return
                capture.requests.append({"file": key, "method": "GET", "at": time.time(), "monotonic": time.monotonic()})
                if key == "owned-failed":
                    # A request racing the tap waits for its explicit acknowledgement.
                    if capture.retry_armed.is_set():
                        capture.retry_allowed.wait(8)
                    if not capture.retry_allowed.is_set():
                        self.send_error(404)
                        return
                    payload = fixture_bytes(key)
                    self.send_response(200)
                    self.send_header("Content-Type", "application/octet-stream")
                    self.send_header("Content-Disposition", f'attachment; filename="{capture.names[key]}"')
                    self.send_header("Content-Length", str(len(payload)))
                    self.end_headers()
                    try:
                        self.wfile.write(payload); self.wfile.flush()
                    except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass
                    return
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(FILE_SIZE))
                self.send_header("Content-Disposition",
                                 f'attachment; filename="{capture.names[key]}"')
                self.end_headers()
                try:
                    body = (fixture_bytes(key) if capture.args.unattended_b5
                            else capture.fixture_bytes)
                    for offset in range(0, FILE_SIZE, CHUNK_SIZE):
                        self.wfile.write(body[offset:offset + CHUNK_SIZE])
                        self.wfile.flush()
                        capture.sent[key] = offset + CHUNK_SIZE
                        capture.b5_total_sent[key] += CHUNK_SIZE
                        if capture.args.active_only and key == "active" and capture.sent[key] == 512 * 1024:
                            if not capture.capture_gate.wait(150 if capture.args.details_flow else 40):
                                return
                        if capture.args.unattended_b5 and key in capture.b5_release:
                            if capture.sent[key] >= 256 * 1024:
                                capture.b5_arrived[key].set()
                            if capture.sent[key] % (128 * 1024) == 0:
                                capture.b5_release[key].wait(3)
                        elif capture.args.details_flow:
                            capture.capture_gate.wait(0.25)
                        elif not (key == "other-finished" and
                                  (getattr(capture.args, "verify_b5_bulk_pause", False)
                                   or getattr(capture.args, "c8_typed_a1_failed", False))):
                            time.sleep(CHUNK_DELAY_SECONDS)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    if key in capture.b5_disconnects:
                        capture.b5_disconnects[key] += 1
                    capture.log(f"Fixture client disconnected: {key} at {capture.sent[key]} bytes.")

            def log_message(self, *_):
                pass

        self.fixture = ThreadingHTTPServer(("127.0.0.1", self.port), Handler)
        self.fixture_thread = threading.Thread(target=self.fixture.serve_forever, daemon=True)
        self.fixture_thread.start()
        self.adb("reverse", "--no-rebind", f"tcp:{self.port}", f"tcp:{self.port}")
        self.reversed = True
        self.log("Fixture ready. Add only the printed http://127.0.0.1 URLs in the probe.")
        keys = ("active", "queued", "paused", "finished") if self.args.mixed_a2 else ("active", "queued")
        if getattr(self.args, "unattended_b5", False):
            keys = B5_KEYS
        for key in keys:
            self.log(f"{key}: http://127.0.0.1:{self.port}/{key}")
        self.log("Suggested names: " + ", ".join(self.names[key] for key in keys))

    def install_and_launch(self):
        try:
            self.adb("install", str(Path(self.args.apk).resolve()))
        except subprocess.TimeoutExpired:
            # Preflight proved this package absent. Only claim a timed-out install
            # when the one installed base APK is byte-for-byte our probe.
            try:
                paths = self.shell("pm", "path", PACKAGE).splitlines()
                match = (re.fullmatch(r"package:(/data/app/[A-Za-z0-9_./+=~-]+/base\.apk)", paths[0])
                         if len(paths) == 1 else None)
                if match:
                    expected = hashlib.sha256(Path(self.args.apk).resolve().read_bytes()).hexdigest()
                    result = self.shell("sha256sum", match.group(1)).strip().split()
                    if len(result) == 2 and result[0].lower() == expected and result[1] == match.group(1):
                        self.installed = True
                        self.log("Timed-out install: exact owned APK identified for scoped cleanup.")
                    else:
                        self.log("Timed-out install: APK hash or path mismatch; package left untouched.")
                else:
                    self.log("Timed-out install: package path missing or ambiguous; package left untouched.")
            except Exception as exc:
                self.log(f"Timed-out install: ownership could not be verified; package left untouched: {exc}")
            raise
        self.installed = True
        require(f"package:{PACKAGE}" in self.shell("pm", "list", "packages", PACKAGE),
                "Probe APK did not install")
        if self.args.details_flow:
            self.shell("pm", "grant", PACKAGE, "android.permission.POST_NOTIFICATIONS")
        if self.args.active_only:
            return  # The A2 hook launches the fresh probe with its explicit setup flag.
        self.adb("shell", "am", "start", "-W", "-n", ACTIVITY)
        self.started = True
        self.log("Launched normally without skip_first_run; complete first-run setup manually if shown.")

    def capture(self, key, reference, anchor, *, prompt=True):
        self.log(f"Checkpoint {key}: {self.args.instructions[key]}")
        if key == "a7-notification" and self.args.unattended_a7_navigation:
            self.log("Opt-in A7 navigation: press Home, then expand the notification shade.")
            self.adb("shell", "input", "keyevent", "KEYCODE_HOME")
            self.adb("shell", "cmd", "statusbar", "expand-notifications")
        elif prompt:
            input(f"\nPrepare {key} on the emulator, then press Enter to capture "
                  "(Ctrl+C aborts): ")
        deadline = time.monotonic() + 25
        tree = None
        while time.monotonic() < deadline:
            if anchor is None:
                focus = self.shell("dumpsys", "window")
                if "NotificationShade" in focus:
                    tree = self.smoke.tree()
                    break
            else:
                try:
                    tree = self.smoke.tree()
                except (ET.ParseError, subprocess.CalledProcessError):
                    time.sleep(0.5)
                    continue
                labels = [node.get("text", "") + " " + node.get("content-desc", "")
                          for node in tree.iter("node")]
                if any(anchor.casefold() in label.casefold() for label in labels):
                    break
            time.sleep(0.4)
        else:
            raise RuntimeError(f"{key} did not show expected UI anchor {anchor!r}")
        screenshot = self.evidence / f"{key}-capture.png"
        screenshot.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        hierarchy = self.evidence / f"{key}-ui.xml"
        ET.ElementTree(tree).write(hierarchy, encoding="utf-8", xml_declaration=True)
        logs = self.adb("logcat", "-d", "-s", "Inbound", "InboundRemoteFilesProbe")
        (self.evidence / f"{key}-logcat.txt").write_text(logs, encoding="utf-8")
        pairer = load_module("download_states_pair_helper", PAIR_HELPER_PATH)
        pair = self.evidence / f"{key}-pair.png"
        size = pairer.make_pair(MOCKUPS / reference, screenshot, pair, key.upper())
        self.log(f"Captured {key}: {screenshot.name}, {hierarchy.name}, "
                 f"{pair.name} ({size[0]}x{size[1]}).")

    def capture_active_only(self):
        self.started = True
        self.adb("shell", "am", "start", "-W", "-n", ACTIVITY, "--ez", "skip_first_run", "true",
                 "--es", "add_url", f"http://127.0.0.1:{self.port}/active")
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            before = self.sent["active"]
            time.sleep(1)
            if 0 < before < self.sent["active"] < FILE_SIZE:
                break
        else:
            raise RuntimeError("A2 did not show two increasing nonterminal fixture-byte samples")
        self.log(f"PASS A2 fixture progress: {before} -> {self.sent['active']} bytes")
        if self.args.details_flow:
            self.capture_details_flow()
            return
        try:
            self.capture("a2-progress", "a2-downloads-later.png", self.names["active"], prompt=False)
        finally:
            self.capture_gate.set()
        self.log("UNVERIFIED in A2-only mode: queued A2, A3, A6, A7.")
        self.wait_for_completion("active")
        self.verify_files(("active",))

    def capture_details_flow(self):
        name = self.names["active"]

        def open_row(action):
            row, _ = self.smoke.wait_content_description(
                lambda desc: desc.startswith(name + ". "), "exact fixture download")
            self.smoke.tap(row)
            self.smoke.wait_node(name, resource="details_title")
            self.smoke.wait_node(action, resource="hero_main")

        def record(key, anchor):
            reference = next(ref for k, ref, _ in CAPTURES if k == key)
            self.capture(key, reference, anchor, prompt=False)

        record("a2-progress", name)
        open_row("Pause")
        stop, _ = self.smoke.wait_node("Stop", resource="hero_stop")
        require(stop.get("enabled") == "true", "Active Details Stop is not enabled")
        record("a6-active-details", name)
        self.shell("input", "keyevent", "4")
        self.smoke.wait_node("Search downloads")
        self.shell("input", "keyevent", "KEYCODE_HOME")
        self.shell("cmd", "statusbar", "expand-notifications")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            tree = self.smoke.tree()
            if not any(n.get("package") == "com.android.systemui" for n in tree.iter("node")):
                self.shell("cmd", "statusbar", "expand-notifications")
                time.sleep(.5)
                continue
            cards = [node for node in tree.iter("node")
                     if node.get("resource-id", "").endswith(":id/expandableNotificationRow")
                     and any(n.get("resource-id") == "android:id/title" and n.get("text", "").startswith("1 download")
                             for n in node.iter("node"))]
            for card in cards:
                labels = {n.get("text", "").lower() for n in card.iter("node")}
                if {"pause all", "open"} <= labels:
                    break
                expand = [n for n in card.iter("node") if n.get("resource-id", "").endswith(":id/expand_button")
                          and n.get("clickable") == "true"]
                if len(expand) == 1 and expand[0].get("content-desc", "").lower() == "expand":
                    self.smoke.tap(expand[0])
                    time.sleep(.5)
            else:
                continue
            break
        else:
            ET.ElementTree(tree).write(self.evidence / "unmatched-shade.xml", encoding="utf-8")
            raise RuntimeError("Inbound notification did not expose Pause all and Open")
        require(any(line.startswith("0|" + PACKAGE + "|") for line in self.shell("cmd", "notification", "list").splitlines()),
                "The test package did not post a notification")
        record("a7-notification", "Pause all")
        self.shell("cmd", "statusbar", "collapse")
        self.shell("am", "start", "-W", "-n", ACTIVITY)
        self.smoke.wait_node("Search downloads")
        self.capture_gate.set()
        record("a3-finished-snackbar", name + " finished")
        open_row("Open")
        tree = self.smoke.tree()
        require(not any(n.get("resource-id", "").endswith(":id/hero_stop")
                        for n in tree.iter("node")),
                "Finished Details unexpectedly exposes Stop")
        record("a6-finished-details", name)
        self.verify_files(("active",))

    def wait_for_completion(self, key):
        self.log(f"Waiting for the real fixture transfer to finish: {key}.")
        deadline = time.monotonic() + 600
        while time.monotonic() < deadline:
            if self.sent[key] >= FILE_SIZE:
                return
            time.sleep(0.5)
        raise RuntimeError(f"Fixture transfer {key} did not finish within 10 minutes")

    def verify_files(self, keys=("active", "queued")):
        expected = hashlib.sha256(self.fixture_bytes).hexdigest()
        records = []
        for key in keys:
            name = self.names[key]
            result = ""
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                result = self.shell(
                    f"if [ -f '/sdcard/Download/{name}' ]; then "
                    f"sha256sum '/sdcard/Download/{name}'; else echo MISSING:{name}; fi").strip()
                if result.split()[:1] == [expected]:
                    break
                time.sleep(0.4)
            records.append(f"{name}: {result}")
            require(result.split()[:1] == [expected],
                    f"Expected hash-matched shared Download file {name}; got {result!r}")
        (self.evidence / "fixture-integrity.txt").write_text(
            f"Expected SHA-256: {expected}\n" + "\n".join(records) + "\n",
            encoding="utf-8")
        (self.evidence / "fixture-requests.json").write_text(
            json.dumps({"requests": self.requests, "bytes_sent": self.sent},
                       indent=2) + "\n", encoding="utf-8")
        self.log(f"PASS {len(keys)} fixture payload(s): {expected}; shared files are left for operator review.")

    def run(self):
        self.preflight()
        try:
            self.start_fixture()
            self.install_and_launch()
            if self.args.active_only:
                self.capture_active_only()
                return self.evidence
            if self.args.mixed_a2:
                self.capture_mixed_a2()
                return self.evidence
            if self.args.unattended_b5:
                self.capture_b5_unattended()
                self.log("B5 fixture payloads are retained; cleanup must use exact name+SHA-256 verification.")
                return self.evidence
            for key in ("a2-progress", "a2-queued", "a6-active-details", "a7-notification"):
                self.capture(*next(item for item in CAPTURES if item[0] == key))
            self.wait_for_completion("active")
            self.log("Active transfer finished. Show the resulting transient completion snackbar now.")
            self.capture(*next(item for item in CAPTURES if item[0] == "a3-finished-snackbar"))
            self.wait_for_completion("queued")
            self.capture(*next(item for item in CAPTURES if item[0] == "a6-finished-details"))
            self.verify_files()
            return self.evidence
        finally:
            pending = sys.exc_info()[0] is not None
            try:
                self.clean()
            except Exception as exc:
                if not pending:
                    raise
                self.log(f"Cleanup WARNING while preserving original failure: {exc}")

    def capture_mixed_a2(self):
        self.log("Opt-in mixed A2: use the app's Add action for all four fixture URLs; no jobs or states are synthesized.")
        input("\nAdd all four printed fixture URLs in the probe, then press Enter: ")
        for key in ("active", "queued", "paused", "finished"):
            input(f"\nArrange the real {key} job state using app controls, then press Enter: ")
            self.capture(f"a2-mixed-{key}", "a2-downloads-later.png", "Downloads", prompt=False)
        self.log("Capture the graph history with the graph visible in the Downloads screen if available.")
        input("\nShow the download speed graph/history, then press Enter to capture it: ")
        self.capture("a2-mixed-graph", "a2-downloads-later.png", "Downloads", prompt=False)
        self.log("Waiting for all four real fixture transfers to finish; resume the paused job in the app.")
        for key in self.names:
            self.wait_for_completion(key)
        self.verify_files(tuple(self.names))
        self.log("Retention: uniquely named shared Download payloads are retained for operator review; delete only these names through the app when authorized.")
        if self.args.capture_b5:
            self.log("Optional B5: select three real fixture rows in Downloads and open the bulk overflow menu.")
            input("\nArrange three selected real rows with the bulk overflow menu open, then press Enter: ")
            self.capture("b5-select", "b5-select.png", "Downloads", prompt=False)

    def select_theme(self, theme):
        """Use only the probe UI; settings changes may recreate the activity."""
        settings, _ = self.smoke.wait_node("Settings", resource="nav_settings")
        self.smoke.tap(settings)
        self.smoke.wait_node("Theme")
        def theme_row():
            nodes = [n for n in self.smoke.tree().iter("node")
                     if n.get("resource-id", "").endswith(":id/setting_theme")]
            require(len(nodes) == 1, "Expected exactly one Theme setting")
            return nodes[0]
        self.smoke.tap(theme_row())
        choice, _ = self.smoke.wait_node(theme)
        self.smoke.tap(choice)
        # AppCompat can recreate the activity; wait for the restored Settings tab.
        self.smoke.wait_node(theme, resource="theme_text", timeout=30)
        switch, _ = self.smoke.wait_node("Dynamic colour", resource="dynamic_switch")
        if switch.get("checked") == "true":
            require(switch.get("enabled") == "true", "Dynamic colour cannot be turned off")
            self.smoke.tap(switch)
            self.smoke.wait_node(theme, resource="theme_text", timeout=30)
        switch, _ = self.smoke.wait_node("Dynamic colour", resource="dynamic_switch", timeout=30)
        require(switch.get("checked") == "false", "Dynamic colour remained enabled")
        # Reopen the real dialog to verify the stored choice after any recreation.
        self.smoke.tap(theme_row())
        choice, tree = self.smoke.wait_node(theme)
        require(choice.get("checked") == "true", f"Theme dialog did not confirm {theme} selected")
        stem = "c8" if theme == "Light" else "a1-a2-dark"
        ET.ElementTree(tree).write(self.evidence / f"{stem}-theme-confirmation.xml", encoding="utf-8", xml_declaration=True)
        self.smoke.tap(self.smoke.wait_node("Cancel")[0])
        downloads, _ = self.smoke.wait_node("Downloads", resource="nav_downloads")
        self.smoke.tap(downloads)
        self.smoke.wait_node("Search downloads", timeout=30)
        label = "C8" if theme == "Light" else "A1/A2 dark"
        self.log(f"PASS {label} setup: {theme} selected and dynamic colour off in the probe UI")

    def capture_b5_unattended(self):
        keys = B5_KEYS
        setup, _ = self.smoke.wait_node("Start downloading", resource="fr_start")
        self.smoke.tap(setup)
        if self.args.capture_c8_light:
            self.select_theme("Light")
        elif self.args.capture_dark_mixed:
            self.select_theme("Dark")
        self.log("B5: complete the real finished fixture first; then stage active rows one at a time and admit the queued row with New download, then Later.")
        expected = [self.names[key] for key in keys]
        def wait_tree(predicate, description, *, return_tree=False):
            deadline = time.monotonic() + 60
            tree = None
            while time.monotonic() < deadline:
                try:
                    tree = self.smoke.tree()
                    nodes = list(tree.iter("node"))
                except (ET.ParseError, subprocess.CalledProcessError):
                    time.sleep(.25)
                    continue  # Active progress can interrupt uiautomator.
                if predicate(nodes):
                    return tree if return_tree else nodes
                time.sleep(.25)
            if tree is not None:
                ET.ElementTree(tree).write(self.evidence / "b5-unmatched-ui.xml", encoding="utf-8")
            raise RuntimeError(f"B5 did not expose {description}")

        def matching(nodes, name):
            return [n for n in nodes if n.get("clickable") == "true"
                    and n.get("content-desc", "").startswith(name + ". ")]

        def center(bounds, quarter=False):
            match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", bounds or "")
            require(match, f"Missing or invalid bounds for identified row: {bounds!r}")
            left, top, right, bottom = map(int, match.groups())
            return left + (right - left) // (4 if quarter else 2), (top + bottom) // 2
        def add_hook_job(key, endpoint=None):
            self.adb("shell", "am", "start", "-W", "-n", ACTIVITY,
                     "--es", "add_url", f"http://127.0.0.1:{self.port}/{endpoint or key}")

        def wait_arrived(key):
            require(self.b5_arrived[key].wait(30), f"B5 fixture did not start {key}")

        # The completed job is established first, with its endpoint-specific
        # payload verified before any active transfer can consume capacity.
        add_hook_job("other-finished")
        self.wait_for_completion("other-finished")
        self.verify_b5_finished_payload()
        for key in ("selected-active", "selected-paused", "other-active"):
            add_hook_job(key)
            wait_arrived(key)
        # A real Later admission is stable regardless of queue concurrency and
        # is deliberately not inferred from a job that was paused afterward.
        self.add_b5_later_job("selected-queued")
        expected = [self.names[key] for key in keys]
        nodes = wait_tree(lambda nodes: all(len(matching(nodes, name)) == 1 for name in expected), "five unique rows")
        def row_state(nodes, name):
            row = matching(nodes, name)
            return row[0].get("content-desc", "")[len(name) + 2:].casefold() if len(row) == 1 else ""

        def has_state(nodes, name, state):
            description = row_state(nodes, name)
            if state == "downloading":
                return any(n.get("resource-id", "").endswith(":id/action")
                           and n.get("content-desc", "").casefold() == f"Pause {name}".casefold()
                           for n in nodes)
            return state in description

        # Hold four genuine HTTP responses after their first chunk; the fifth
        # response is unthrottled and must finish. Pause one active row through
        # its ordinary row action, allowing any capacity waiter to remain queued.
        nodes = wait_tree(lambda ns: all(row_state(ns, self.names[k]) for k in keys),
                          "all five row states")
        def action_for(nodes, name, action):
            found = [n for n in nodes if n.get("resource-id", "").endswith(":id/action")
                     and n.get("content-desc", "").casefold() == f"{action} {name}".casefold()
                     and n.get("clickable") == "true" and n.get("enabled") == "true"]
            require(len(found) == 1, f"Could not uniquely identify {action} action for {name}")
            return found[0]

        # Prefer pausing the intended target if it is active; otherwise the
        # fixture cannot promise the required reference mix on this Host.
        try:
            pause = action_for(nodes, self.names["selected-paused"], "Pause")
        except RuntimeError as exc:
            raise RuntimeError("B5 could not establish an active selected-paused job to pause") from exc
        x, y = center(pause.get("bounds"))
        individual_disconnects_before = self.b5_disconnects["selected-paused"]
        self.adb("shell", "input", "tap", str(x), str(y))

        desired = {"selected-active": "downloading", "selected-paused": "paused",
                   "selected-queued": "queued", "other-active": "downloading",
                   "other-finished": "finished"}
        nodes = wait_tree(lambda ns: all(
            has_state(ns, self.names[key], token) for key, token in desired.items()),
                          "the requested active/paused/queued/active/finished state mix")
        if self.args.capture_c8_light:
            self.capture("c8-populated", "c8-light.png", "Search downloads", prompt=False)
            captured = list(ET.parse(self.evidence / "c8-populated-ui.xml").getroot().iter("node"))
            require(all(has_state(captured, self.names[key], token) for key, token in desired.items()),
                    "C8 capture no longer shows the five real mixed-state jobs")
            require(not any(n.get("resource-id", "").endswith(":id/sel_title") for n in captured),
                    "C8 capture unexpectedly entered selection mode")
            self.log("PASS representative C8: real active, paused, queued and finished fixture rows; Light verified in Settings")
            if self.args.c8_typed_a1_failed:
                require(all(len(matching(captured, self.names[k])) == 1 for k in B5_KEYS),
                        "Typed C8 capture must show all five exact nonce names")
                require(has_state(captured, self.names["selected-queued"], "queued") and
                        not any(r["file"] == "selected-queued" and r["method"] == "GET" for r in self.requests),
                        "Locally admitted Later job is not still queued without a request")
                # Free both active slots using each exact row action; no global pause.
                for key in ("selected-active", "other-active"):
                    ns = wait_tree(lambda ns: has_state(ns, self.names[key], "downloading"),
                                   "owned active row before pause")
                    self.smoke.tap(action_for(ns, self.names[key], "Pause"))
                    wait_tree(lambda ns: has_state(ns, self.names[key], "paused"), "owned row paused")
                failed_path = "/sdcard/Download/" + self.names["owned-failed"]
                add_hook_job("owned-failed", "owned-failed-" + self.nonce if self.args.verify_a1_retry else None)
                # Search isolates the sixth row without relying on list ordering or viewport height.
                self.smoke.enter("Search downloads", self.names["owned-failed"])
                self.shell("input", "keyevent", "KEYCODE_BACK")  # Hide only the search keyboard.
                def failed_visible(ns):
                    return (len(matching(ns, self.names["owned-failed"])) == 1 and
                            has_state(ns, self.names["owned-failed"], "failed") and
                            len([n for n in ns if n.get("resource-id", "").endswith(":id/action") and
                                 n.get("content-desc") == "Retry " + self.names["owned-failed"] and
                                 n.get("enabled") == "true"]) == 1 and
                            all(any(r["file"] == "owned-failed" and r["method"] == method for r in self.requests)
                                for method in ("HEAD", "GET")))
                tree = wait_tree(failed_visible, "real 404 fixture failure and Retry", return_tree=True)
                require(self.shell(f"if [ -e '{failed_path}' ] || [ -L '{failed_path}' ]; then echo present; fi").strip() != "present",
                        "Failed fixture unexpectedly produced a completed output")
                (self.evidence / "a1-real-failed-capture.png").write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
                ET.ElementTree(tree).write(self.evidence / "a1-real-failed-ui.xml", encoding="utf-8", xml_declaration=True)
                self.log("PASS typed C8 and A1: exact mixed file names, Later row, and real fixture 404 failure with Retry.")
                if self.args.verify_a1_retry:
                    def get_count():
                        return sum(r["file"] == "owned-failed" and r["method"] == "GET" for r in self.requests)
                    count = get_count()
                    started = time.monotonic()
                    tree = wait_tree(lambda ns: time.monotonic() - started >= 4 and failed_visible(ns),
                                     "failed job stable before explicit Retry", return_tree=True)
                    require(get_count() == count and count > 0, "Failure retried autonomously before user action")
                    actions = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/action")
                               and n.get("content-desc") == "Retry " + self.names["owned-failed"]
                               and n.get("enabled") == "true" and n.get("clickable") == "true"]
                    require(len(actions) == 1, "Retry is missing or ambiguous")
                    require(self.shell(f"if [ -e '{failed_path}' ] || [ -L '{failed_path}' ]; then echo present; fi").strip() != "present",
                            "Retry output appeared before the tap")
                    self.retry_output_owned = True
                    armed_at = time.monotonic()
                    self.retry_armed.set()
                    require(get_count() == count, "Unexpected retry before arming completed")
                    tap_started = time.monotonic()
                    self.smoke.tap(actions[0])
                    require(not any(r["file"] == "owned-failed" and r["method"] == "GET" and
                                    armed_at <= r.get("monotonic", 0) < tap_started for r in self.requests),
                            "Unexpected retry before the explicit tap")
                    self.retry_allowed.set()
                    expected = hashlib.sha256(fixture_bytes("owned-failed")).hexdigest()
                    def retried_done(ns):
                        digest = self.shell(f"if [ -f '{failed_path}' ] && [ ! -L '{failed_path}' ]; then sha256sum '{failed_path}'; fi").split()
                        return (len(matching(ns, self.names["owned-failed"])) == 1 and
                                has_state(ns, self.names["owned-failed"], "finished") and
                                get_count() > count and digest[:1] == [expected])
                    tree = wait_tree(retried_done, "same retry row finished with matching owned payload", return_tree=True)
                    (self.evidence / "a1-real-retry-capture.png").write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
                    ET.ElementTree(tree).write(self.evidence / "a1-real-retry-ui.xml", encoding="utf-8", xml_declaration=True)
                    (self.evidence / "a1-real-retry-proof.json").write_text(json.dumps({"sha256": expected, "gets_before": count, "gets_after": get_count()}, indent=2) + "\n", encoding="utf-8")
                    self.log("PASS A1: explicit Retry completed the same named fixture row and matched its payload hash.")
                return
        if self.args.capture_dark_mixed:
            active_keys = ("selected-active", "other-active")
            def valid_mixed(nodes):
                rate = [n.get("text", "") for n in nodes if n.get("resource-id", "").endswith(":id/summary_rate")]
                if len(rate) != 1: return False
                match = re.fullmatch(r"([0-9]+(?:\.[0-9]+)?) (?:B|KB|MB|GB|TB)/s", rate[0])
                return bool(match and float(match.group(1)) > 0 and
                            all(has_state(nodes, self.names[key], state) for key, state in desired.items()) and
                            not any(n.get("resource-id", "").endswith(":id/sel_title") for n in nodes))
            for key, reference in (("a1-dark-mixed", "a1-downloads.png"),
                                   ("a2-dark-mixed", "a2-downloads-later.png")):
                before = {name: self.sent[name] for name in active_keys}
                wait_tree(lambda ns: all(0 < before[name] < self.sent[name] < FILE_SIZE for name in active_keys)
                          and valid_mixed(ns), "both real transfers advancing and nonzero Host summary rate")
                self.capture(key, reference, "Search downloads", prompt=False)
                captured = list(ET.parse(self.evidence / f"{key}-ui.xml").getroot().iter("node"))
                require(valid_mixed(captured), f"{key} no longer shows the mixed jobs and a nonzero summary rate")
                after = {name: self.sent[name] for name in active_keys}
                require(all(0 < before[name] < after[name] < FILE_SIZE for name in active_keys),
                        f"{key} did not retain both advancing fixture transfers")
                self.log(f"PASS {key}: representative dark mixed list; fixture byte samples {before} -> {after}")
            nodes = wait_tree(lambda ns: all(has_state(ns, self.names[key], state)
                                             for key, state in desired.items()), "mixed jobs before B5 selection")
        # Select precisely the active, paused and queued rows by their visible,
        # uniquely identified cards. The new selection anchor is state-checked
        # below before the overflow menu is opened.
        first = matching(nodes, self.names["selected-active"])[0]
        x, y = center(first.get("bounds"), quarter=True)
        self.adb("shell", "input", "swipe", str(x), str(y), str(x), str(y), "900")
        def selected(nodes, count):
            return any(n.get("resource-id", "").endswith(":id/sel_title") and n.get("text") == f"{count} selected"
                       for n in nodes)
        for count, key in enumerate(("selected-paused", "selected-queued"), 1):
            name = self.names[key]
            nodes = wait_tree(lambda nodes: selected(nodes, count) and len(matching(nodes, name)) == 1,
                              f"{count} selected and the next fixture row")
            x, y = center(matching(nodes, name)[0].get("bounds"), quarter=True)
            self.adb("shell", "input", "tap", str(x), str(y))
        nodes = wait_tree(lambda nodes: selected(nodes, 3), "three selected rows")
        for key in ("selected-active", "selected-paused", "selected-queued"):
            require(any(n.get("content-desc", "").startswith(self.names[key] + ". ")
                        and n.get("selected") == "true" for n in nodes),
                    f"Expected {key} to be selected")
        for key in ("other-active", "other-finished"):
            require(any(n.get("content-desc", "").startswith(self.names[key] + ". ")
                        and n.get("selected") == "false" for n in nodes),
                    f"Expected {key} to remain unselected")
        self.log("PASS B5 state mix: selected active/paused/queued; unselected active/finished. Exact selection count: 3.")
        # Only click a visible, enabled overflow control identified from the current UI tree.
        menu = [n for n in nodes if n.get("resource-id", "").endswith(":id/sel_more")
                and n.get("clickable") == "true" and n.get("enabled") == "true"]
        require(len(menu) == 1, "Could not uniquely identify the enabled bulk overflow control")
        x, y = center(menu[0].get("bounds"))
        self.adb("shell", "input", "tap", str(x), str(y))
        def menu_entry(nodes, label):
            return [n for n in nodes if n.get("content-desc") == label]
        menu_nodes = wait_tree(lambda ns: len(menu_entry(ns, "Move to queue")) == 1, "bulk overflow menu")
        again = menu_entry(menu_nodes, "Download again")
        require(len(again) == 1 and again[0].get("enabled") == "false",
                "Download again must be disabled while all selected rows are unfinished")
        self.capture("b5-unattended", "b5-select.png", "Move to queue", prompt=False)
        if self.args.verify_b5_bulk_pause:
            self.shell("input", "keyevent", "4")  # Close the actual overflow popup.
            active_keys = ("selected-active", "other-active")
            running_before = {key: self.sent[key] for key in active_keys}

            def ready_to_pause(nodes):
                # Individual row actions are hidden in selection mode. Use the
                # identified rows' live rate and their independent fixture bytes.
                return (selected(nodes, 3) and not menu_entry(nodes, "Move to queue")
                        and all(re.search(r"\b[0-9]+(?:\.[0-9]+)? [kmgt]?b/s", row_state(nodes, self.names[key]))
                                and 0 < running_before[key] < self.sent[key] < FILE_SIZE for key in active_keys))

            nodes = wait_tree(ready_to_pause, "both real transfers advancing with three selected rows")
            pause_actions = [n for n in nodes if n.get("resource-id", "").endswith(":id/sel_pause")
                             and n.get("content-desc") == "Pause selected" and n.get("enabled") == "true"
                             and n.get("clickable") == "true"]
            require(len(pause_actions) == 1, "Expected exactly one enabled bulk Pause control")
            disconnects_before = self.b5_disconnects["selected-active"]
            self.smoke.tap(pause_actions[0])
            paused = dict(desired, **{"selected-active": "paused"})

            def pause_states(nodes):
                return all(has_state(nodes, self.names[key], state) for key, state in paused.items())

            wait_tree(lambda ns: pause_states(ns) and self.b5_disconnects["selected-active"] > disconnects_before,
                      "the targeted job paused and its active fixture socket closed")
            before = {key: self.sent[key] for key in active_keys}
            started = time.monotonic()
            tree = wait_tree(lambda ns: time.monotonic() - started >= 4
                             and self.sent["other-active"] > before["other-active"] and pause_states(ns),
                             "the unrelated transfer advanced throughout bulk Pause", return_tree=True)
            after = {key: self.sent[key] for key in before}
            require(0 < before["selected-active"] == after["selected-active"],
                    "The selected transfer did not hold its partial bytes after bulk Pause")
            (self.evidence / "b5-bulk-pause-proof.json").write_text(
                json.dumps({"before": before, "after": after, "seconds": time.monotonic() - started}, indent=2) + "\n",
                encoding="utf-8")
            (self.evidence / "b5-bulk-pause-capture.png").write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / "b5-bulk-pause-ui.xml", encoding="utf-8", xml_declaration=True)
            self.log("PASS B5 bulk Pause: the active selection paused at fixed bytes; the unselected transfer advanced.")
            if self.args.verify_b5_bulk_resume:
                # The separately paused row must remain untouched throughout the
                # resume proof. Wait for the old socket to close and quiesce.
                untouched = "selected-paused"

                def fixture_gets(key):
                    return sum(r["file"] == key and r["method"] == "GET" for r in self.requests)

                wait_tree(lambda ns: pause_states(ns)
                          and self.b5_disconnects[untouched] > individual_disconnects_before,
                          "independent paused fixture socket closed")
                untouched_before = self.b5_total_sent[untouched]
                untouched_gets = fixture_gets(untouched)
                quiet_start = time.monotonic()
                nodes = wait_tree(lambda ns: time.monotonic() - quiet_start >= 4 and pause_states(ns),
                                  "independent paused row quiescent before Resume")
                require(0 < untouched_before == self.b5_total_sent[untouched]
                        and untouched_gets == fixture_gets(untouched),
                        "Independent paused fixture did not remain quiescent")

                # Pause clears selection. Select precisely the newly paused row;
                # never select the independently paused row or queued row.
                target = matching(nodes, self.names["selected-active"])
                require(len(target) == 1 and has_state(nodes, self.names["selected-active"], "paused"),
                        "Resume target is not uniquely paused")
                x, y = center(target[0].get("bounds"), quarter=True)
                self.adb("shell", "input", "swipe", str(x), str(y), str(x), str(y), "900")
                def selected_target(ns):
                    return (selected(ns, 1) and
                            all(len(matching(ns, self.names[k])) == 1 and
                                matching(ns, self.names[k])[0].get("selected") ==
                                ("true" if k == "selected-active" else "false")
                                for k in keys))
                selection_tree = wait_tree(selected_target, "only the paused target selected", return_tree=True)
                resume = [n for n in selection_tree.iter("node") if n.get("resource-id", "").endswith(":id/sel_resume")
                          and n.get("content-desc") == "Resume selected" and n.get("enabled") == "true"
                          and n.get("clickable") == "true"]
                require(len(resume) == 1, "Expected exactly one enabled bulk Resume control")
                (self.evidence / "b5-bulk-resume-selection.png").write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
                ET.ElementTree(selection_tree).write(self.evidence / "b5-bulk-resume-selection.xml",
                                                      encoding="utf-8", xml_declaration=True)
                get_count = fixture_gets("selected-active")
                total_before = self.b5_total_sent["selected-active"]
                self.smoke.tap(resume[0])
                # The fixture does not implement Range: a resumed GET starts at
                # zero, so sample only after the new request has been observed.
                resumed = dict(paused, **{"selected-active": "downloading"})
                def resumed_states(ns):
                    return all(has_state(ns, self.names[k], state) for k, state in resumed.items())
                wait_tree(lambda ns: resumed_states(ns) and
                          fixture_gets("selected-active") > get_count and
                          self.b5_total_sent["selected-active"] > total_before and
                          0 < self.sent["selected-active"] < FILE_SIZE,
                          "resumed job and its new fixture request")
                sample = self.b5_total_sent["selected-active"]
                started_resume = time.monotonic()
                tree = wait_tree(lambda ns: time.monotonic() - started_resume >= 4 and resumed_states(ns)
                                 and sample < self.b5_total_sent["selected-active"] and self.sent["selected-active"] < FILE_SIZE,
                                 "resumed fixture advances while other job remains paused", return_tree=True)
                untouched_after = self.b5_total_sent[untouched]
                untouched_gets_after = fixture_gets(untouched)
                require(untouched_before == untouched_after and untouched_gets == untouched_gets_after,
                        "Unselected paused fixture changed during Resume")
                (self.evidence / "b5-bulk-resume-proof.json").write_text(
                    json.dumps({"selected_fixture_bytes": [sample, self.b5_total_sent["selected-active"]],
                                "unselected_paused_fixture_bytes": [untouched_before, untouched_after],
                                "unselected_paused_fixture_gets": [untouched_gets, untouched_gets_after],
                                "seconds": time.monotonic() - started_resume}, indent=2) + "\n", encoding="utf-8")
                (self.evidence / "b5-bulk-resume-capture.png").write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
                ET.ElementTree(tree).write(self.evidence / "b5-bulk-resume-ui.xml", encoding="utf-8", xml_declaration=True)
                self.log("PASS B5 bulk Resume: only the selected paused fixture resumed; the independent paused fixture stayed unchanged.")
        if self.args.capture_mixed_a3:
            self.shell("input", "keyevent", "4")  # Close the actual overflow popup.
            wait_tree(lambda ns: selected(ns, 3) and not menu_entry(ns, "Move to queue"), "popup dismissed")
            self.shell("input", "keyevent", "4")  # Close the actual selection mode.
            wait_tree(lambda ns: not selected(ns, 3) and all(
                has_state(ns, self.names[key], state) for key, state in desired.items()),
                "the five unselected owned rows")
            target = "/sdcard/Download/" + self.names["other-active"]
            require(self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                    "A3 exact fixture destination appeared before release")
            self.a3_output_owned = True
            self.b5_release["other-active"].set()
            completed = dict(desired, **{"other-active": "finished"})
            filename = self.names["other-active"]
            def completion_visible(ns):
                return (all(has_state(ns, self.names[key], state) for key, state in completed.items()) and
                        any(n.get("text") == filename + " finished" for n in ns) and
                        any(n.get("resource-id", "").endswith(":id/snackbar_action") and
                            n.get("text") == "Open" and n.get("enabled") == "true" for n in ns))
            tree = wait_tree(completion_visible, "the exact completed fixture snackbar and mixed rows", return_tree=True)
            image = self.evidence / "a3-dark-mixed-capture.png"
            image.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / "a3-dark-mixed-ui.xml", encoding="utf-8", xml_declaration=True)
            pairer = load_module("a3_mixed_pair", PAIR_HELPER_PATH)
            pairer.make_pair(MOCKUPS / "a3-downloads-finished.png", image,
                             self.evidence / "a3-dark-mixed-pair.png", "A3 mixed")
            expected_hash = hashlib.sha256(fixture_bytes("other-active")).hexdigest()
            digest = self.shell(f"if [ -f '{target}' ]; then sha256sum '{target}'; fi").split()
            require(digest[:1] == [expected_hash], "A3 exact completed fixture payload hash mismatch")
            (self.evidence / "a3-mixed-sha256.txt").write_text(expected_hash + "\n", encoding="ascii")
            self.log("PASS representative A3: exact completed fixture snackbar and mixed rows, matching payload hash")

    def add_b5_later_job(self, key):
        """Admit the selected queued row through the real New download form."""
        # The start action is the app's ordinary Downloads Add control. Resource
        # selectors and visible form labels are checked rather than coordinates
        # guessed from a stale screen.
        tree = self.smoke.tree()
        add = [node for node in tree.iter("node")
               if node.get("resource-id", "").endswith(":id/fab")
               and node.get("clickable") == "true" and node.get("enabled") == "true"]
        require(len(add) == 1, "Could not identify the enabled Downloads Add control")
        self.smoke.tap(add[0])
        link = self.smoke.wait_node("Link or several links")[0]
        self.smoke.tap(link)
        address = None
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            tree = self.smoke.tree()
            address = next((n for n in tree.iter("node")
                            if n.get("resource-id", "").endswith(":id/address")), None)
            if address is not None:
                break
            time.sleep(.3)
        require(address is not None, "NewDownloadScreen address field was not exposed")
        x, y = self.smoke_module.center(address)
        self.adb("shell", "input", "tap", str(x), str(y))
        self.adb("shell", "input", "text", f"http://127.0.0.1:{self.port}/{key}")
        later = self.smoke.wait_node("Later", resource="when_later")[0]
        self.smoke.tap(later)
        submit = self.smoke.wait_node("Download", resource="new_download_btn")[0]
        self.smoke.tap(submit)
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            nodes = list(self.smoke.tree().iter("node"))
            if any(n.get("text") == self.names[key]
                   or n.get("content-desc", "").startswith(self.names[key] + ". ")
                   for n in nodes):
                return
            time.sleep(.3)
        raise RuntimeError("NewDownloadScreen Later submission did not expose the named queued row")

    def verify_b5_finished_payload(self, *, allow_missing=False):
        name = self.names["other-finished"]
        expected = hashlib.sha256(fixture_bytes("other-finished")).hexdigest()
        command = (f"if [ -f '/sdcard/Download/{name}' ]; then "
                   f"sha256sum '/sdcard/Download/{name}'; else echo MISSING:{name}; fi")
        result = ""
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            result = self.shell(command).strip()
            if result.split()[:1] == [expected]:
                break
            if result.startswith(f"MISSING:{name}") and allow_missing:
                self.log("B5 cleanup: finished fixture file is already absent; nothing to remove.")
                return
            if not result.startswith(f"MISSING:{name}"):
                break
            time.sleep(.3)
        require(result.split()[:1] == [expected],
                f"Refusing B5 cleanup: finished fixture payload hash mismatch or missing file: {result!r}")
        self.log(f"PASS B5 finished payload: {name} SHA-256 {expected}.")

    def cleanup_b5_finished_payload(self):
        name = self.names["other-finished"]
        self.verify_b5_finished_payload(allow_missing=True)
        self.shell("rm", "-f", f"/sdcard/Download/{name}")
        require(self.shell("test", "!", "-e", f"/sdcard/Download/{name}").strip() == "",
                "Exact B5 finished fixture payload remained after cleanup")

    def clean(self):
        errors = []
        writer_stopped = True
        if (self.args.capture_mixed_a3 or self.retry_output_owned) and self.installed:
            # Stop the writer before fixture release or exact-file cleanup.
            try:
                self.shell("am", "force-stop", PACKAGE)
                require(self.shell(f"if pidof {PACKAGE} >/dev/null; then echo running; else echo stopped; fi").strip() == "stopped",
                        "Probe writer did not stop; preserving all outputs")
            except Exception as exc:
                writer_stopped = False
                errors.append(str(exc))
        if self.started and getattr(self.args, "details_flow", False):
            self.shell("cmd", "statusbar", "collapse")
        if writer_stopped:
            for event in self.b5_release.values():
                event.set()
        if self.fixture is not None:
            try:
                if self.fixture_thread is not None and self.fixture_thread.is_alive():
                    self.fixture.shutdown()
                    self.fixture_thread.join(timeout=5)
                self.fixture.server_close()
                self.fixture = None
            except Exception as exc:
                errors.append(f"fixture cleanup: {exc}")
        if self.reversed:
            try:
                self.adb("reverse", "--remove", f"tcp:{self.port}")
                self.reversed = False
                self.log("Cleanup PASS: removed only this runner's ADB reverse mapping and stopped its fixture.")
            except Exception as exc:
                errors.append(f"reverse cleanup: {exc}")
        if self.installed:
            self.adb("uninstall", PACKAGE)
            require(f"package:{PACKAGE}" not in self.shell("pm", "list", "packages", PACKAGE),
                    "Probe package remains installed after scoped cleanup")
            self.log("Cleanup PASS: uninstalled only the probe package; shared Download files were not deleted.")
            if not writer_stopped:
                for event in self.b5_release.values():
                    event.set()
        if self.installed and writer_stopped and getattr(self.args, "unattended_b5", False):
            try:
                self.cleanup_b5_finished_payload()
            except Exception as exc:
                # Never replace the original capture failure or delete an
                # unexpected/mismatched file. Keep the cleanup diagnostic visible.
                self.log(f"Cleanup WARNING: {exc}")
        if self.retry_output_owned and self.installed and writer_stopped:
            try:
                target = "/sdcard/Download/" + self.names["owned-failed"]
                if self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() == "present":
                    expected = hashlib.sha256(fixture_bytes("owned-failed")).hexdigest()
                    digest = self.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
                    require(digest[:1] == [expected], "Retry output is not exact; preserving it")
                    self.shell("rm", "--", target)
                    require(self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                            "Exact Retry fixture output remains after cleanup")
            except Exception as exc:
                errors.append(f"Retry output cleanup: {exc}")
        if self.args.capture_mixed_a3 and self.a3_output_owned and self.installed and writer_stopped:
            target = "/sdcard/Download/" + self.names["other-active"]
            if self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() == "present":
                expected = hashlib.sha256(fixture_bytes("other-active")).hexdigest()
                digest = self.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
                require(digest[:1] == [expected], "A3 output changed or is incomplete; preserving it")
                self.shell("rm", "--", target)
                require(self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                        "Exact A3 fixture output remains after cleanup")
                self.log("PASS A3 cleanup: exact hash-matched output removed after probe uninstall")
        if self.started:
            try:
                self.shell("rm", "-f", self.xml_path)
            except Exception:
                pass

        if errors:
            raise RuntimeError("; ".join(errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt", help="Path to Android build-tools aapt(.exe)")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit isolated APK with rcloneprobe application ID")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="New, previously absent evidence directory")
    parser.add_argument("--unattended-a7-navigation", action="store_true",
                        help="Opt in to HOME + notification-shade navigation for A7 only")
    parser.add_argument("--active-only", action="store_true", help="Capture real A2 progress automatically; no other states")
    parser.add_argument("--details-flow", action="store_true", help="Extend --active-only through Details, notification and completion")
    parser.add_argument("--mixed-a2", action="store_true",
                        help="Opt in to four real uniquely named A2 fixture downloads and guided mixed-state/graph captures")
    parser.add_argument("--capture-b5", action="store_true",
                        help="With --mixed-a2, capture B5 using three real fixture rows selected in Downloads")
    parser.add_argument("--verify-b5-bulk-pause", action="store_true", help="With --unattended-b5 apply bulk Pause and verify transfer isolation")
    parser.add_argument("--verify-b5-bulk-resume", action="store_true", help="After bulk Pause resume only its paused target and verify another paused job remains unchanged")
    parser.add_argument("--capture-mixed-a3", action="store_true", help="After B5 capture one real completion snackbar among the dark mixed jobs")
    parser.add_argument("--capture-dark-mixed", action="store_true", help="With --unattended-b5 capture representative dark A1/A2 real mixed states")
    parser.add_argument("--capture-c8-light", action="store_true", help="With --unattended-b5 capture the real mixed list in Light mode before selection")
    parser.add_argument("--c8-typed-a1-failed", action="store_true", help="Capture typed C8 rows and a separate real 404 failure; ends before B5 selection")
    parser.add_argument("--verify-a1-retry", action="store_true", help="After actual fixture 404, tap Retry and verify the exact completed hash")
    parser.add_argument("--unattended-b5", action="store_true",
                        help="Standalone opt-in B5 mixed-state lane: select active, paused and queued rows; leave active and finished rows unselected")
    args = parser.parse_args()
    if args.verify_b5_bulk_resume and not args.verify_b5_bulk_pause:
        parser.error("--verify-b5-bulk-resume requires --verify-b5-bulk-pause")
    if args.verify_b5_bulk_pause and (not args.unattended_b5 or args.capture_mixed_a3):
        parser.error("--verify-b5-bulk-pause requires --unattended-b5 without the mixed A3 lane")
    if args.capture_mixed_a3 and not (args.unattended_b5 and args.capture_dark_mixed):
        parser.error("--capture-mixed-a3 requires --unattended-b5 --capture-dark-mixed")
    if args.capture_dark_mixed and (not args.unattended_b5 or args.capture_c8_light):
        parser.error("--capture-dark-mixed requires --unattended-b5 and cannot be combined with --capture-c8-light")
    if args.verify_a1_retry and not args.c8_typed_a1_failed:
        parser.error("--verify-a1-retry requires --c8-typed-a1-failed")
    if args.c8_typed_a1_failed and (not args.capture_c8_light or not args.unattended_b5 or
                                    args.verify_b5_bulk_pause or args.verify_b5_bulk_resume):
        parser.error("--c8-typed-a1-failed requires --unattended-b5 --capture-c8-light and excludes bulk actions")
    if args.capture_c8_light and not args.unattended_b5:
        parser.error("--capture-c8-light requires --unattended-b5")
    if args.details_flow and (not args.active_only or args.mixed_a2 or args.unattended_a7_navigation):
        parser.error("--details-flow requires --active-only and owns navigation")
    if args.capture_b5 and not args.mixed_a2:
        parser.error("--capture-b5 requires --mixed-a2")
    if args.unattended_b5 and (args.mixed_a2 or args.capture_b5 or args.active_only or args.unattended_a7_navigation):
        parser.error("--unattended-b5 is a standalone lane and cannot be combined with other lane flags")
    args.instructions = {
        "a1-dark-mixed": "Representative dark Downloads with five real mixed jobs and a live rate.",
        "a2-dark-mixed": "Representative dark Downloads with five real mixed jobs and a live rate.",
        "c8-populated": "Light Downloads with five real fixture jobs in mixed states before selection.",
        "b5-unattended": "Three uniquely identified B5 fixture rows are selected and the bulk overflow menu is open.",
        "a2-progress": "Keep the active fixture download showing nonzero, changing progress.",
        "a2-queued": "Leave the first transfer active and show the second fixture job Queued.",
        "a6-active-details": "Open Details for the active fixture job; show the active-state fields/actions.",
        "a7-notification": "Background Inbound, expand notifications, and show ongoing progress/actions.",
        "a3-finished-snackbar": "Immediately capture the completion snackbar and finished row after the active file completes.",
        "a6-finished-details": "Open Details for the completed fixture job; show finished-state fields/actions.",
    }
    capture = Capture(args)
    print(f"Evidence directory: {capture.evidence}")
    try:
        print(f"Evidence: {capture.run()}")
    except Exception:
        raise


if __name__ == "__main__":
    main()
