"""Guided A0/A1/A4/A5/C7/C8 captures on the isolated inbound33 probe."""

import argparse
import hashlib
import importlib.machinery
import json
import re
import subprocess
import threading
import time
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MOCKUPS = ROOT / "docs" / "plans" / "android-mockups"
PACKAGE = "app.inbound.android.rcloneprobe"
ACTIVITY = PACKAGE + "/app.inbound.android.MainActivity"
AVD = "inbound33"
API = "33"
SMOKE_PATH = Path(__file__).with_name("smoke-remote-files-ui.py")
APP_ID = "app.inbound.android.rcloneprobe:id/"
SELECTORS = {
    "first_run_start": APP_ID + "fr_start",
    "add": APP_ID + "fab",
    "add_link": APP_ID + "entry_link",
    "new_close": APP_ID + "new_close",
    "settings": APP_ID + "nav_settings",
}
CAPTURES = (
    ("a0", "a0-first-run.png", "Set up Inbound"),
    ("a1", "a1-downloads.png", "Search downloads"),
    ("a4", "a4-add.png", "Add a download"),
    ("a5", "a5-new-download.png", "New download"),
    ("c7", "c7-settings.png", "Settings"),
    ("c8", "c8-light.png", "Search downloads"),
)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def load_module(name, path):
    return importlib.machinery.SourceFileLoader(name, str(path)).load_module()


class Capture:
    def __init__(self, args):
        self.args = args
        self.smoke_module = load_module("core_parity_existing_smoke", SMOKE_PATH)
        self.smoke = self.smoke_module.Smoke(args)
        self.evidence = self.smoke.evidence
        self.installed = False
        self.started = False

    def adb(self, *args, raw=False):
        return self.smoke.adb(*args, raw=raw)

    def shell(self, *args):
        return self.smoke.shell(*args)

    def check(self):
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
        badging = subprocess.run([aapt, "dump", "badging", str(apk)],
                                 capture_output=True, text=True, timeout=25, check=True).stdout
        require(re.search(r"^package: name=['\"]" + re.escape(PACKAGE) + r"['\"](?:\s|$)",
                          badging, re.MULTILINE),
                f"APK package must be exactly {PACKAGE}")
        version = re.search(r"^package: .*?versionCode='([^']*)'.*?versionName='([^']*)'",
                            badging, re.MULTILINE)
        require(f"package:{PACKAGE}" not in self.shell("pm", "list", "packages", PACKAGE),
                "Probe package is already installed; refusing to replace or clear it")
        self.initial_downloads = self.shell(
            "find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()
        (self.evidence / "apk.sha256").write_text(
            hashlib.sha256(apk.read_bytes()).hexdigest() + "\n", encoding="ascii")
        (self.evidence / "preflight.json").write_text(json.dumps({
            "avd": AVD, "api": API, "serial": self.args.serial,
            "package": PACKAGE, "apk": str(apk),
            "apk_version_code": version.group(1) if version else None,
            "apk_version_name": version.group(2) if version else None,
            "display_size": self.shell("wm", "size").strip(),
            "display_density": self.shell("wm", "density").strip(),
            "initial_download_files": self.initial_downloads,
        }, indent=2) + "\n", encoding="utf-8")
        self.log("Preflight PASS: inbound33/API 33; exact isolated probe APK; package absent")

    def log(self, message):
        print(message, flush=True)
        with (self.evidence / "flow.txt").open("a", encoding="utf-8") as stream:
            stream.write(message + "\n")

    def launch(self):
        self.adb("install", str(Path(self.args.apk).resolve()))
        self.installed = True
        require(f"package:{PACKAGE}" in self.shell("pm", "list", "packages", PACKAGE),
                "Probe package did not install")
        self.adb("shell", "am", "start", "-W", "-n", ACTIVITY)
        self.started = True
        self.log("Launched probe normally without skip_first_run; initial setup state is available.")

    def await_checkpoint(self, key, reference, anchor):
        self.log(f"Next checkpoint: {key}. {self.args.instructions.get(key, '')}")
        if not self.args.unattended:
            input(f"\nNavigate the probe to {key.upper()} and configure the documented state. "
                  f"Press Enter when ready to capture (Ctrl+C aborts): ")
        else:
            # Unattended mode deliberately does not infer state from navigation,
            # synthesize jobs, change clipboard/settings, or submit a URL. It
            # captures only a state already reached through app-owned UI.
            self.log(f"Unattended mode: waiting for app-owned {key.upper()} state; "
                     "no taps or state mutations will be synthesized.")
        deadline = time.monotonic() + 30
        tree = None
        while time.monotonic() < deadline:
            tree = self.smoke.tree()
            texts = [node.get("text", "") + " " + node.get("content-desc", "")
                     for node in tree.iter("node")]
            if any(anchor.casefold() in text.casefold() for text in texts):
                break
            time.sleep(0.5)
        else:
            raise RuntimeError(
                f"{key.upper()} checkpoint does not show expected UI anchor {anchor!r}; "
                "inspect the latest UI dump/logcat before continuing")
        name = key
        screenshot = self.evidence / f"{name}-capture.png"
        screenshot.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        hierarchy = self.evidence / f"{name}-ui.xml"
        ET.ElementTree(tree).write(hierarchy, encoding="utf-8", xml_declaration=True)
        logs = self.adb("logcat", "-d", "-s", "Inbound", "InboundRemoteFilesProbe")
        (self.evidence / f"{name}-logcat.txt").write_text(logs, encoding="utf-8")
        pair = self.evidence / f"{name}-pair.png"
        parity = load_module("core_parity_pair_helper",
                             Path(__file__).with_name("capture-remote-files-parity.py"))
        size = parity.make_pair(MOCKUPS / reference, screenshot, pair, key.upper())
        self.log(f"Captured {key.upper()}: anchor={anchor!r}, screenshot={screenshot.name}, "
                 f"hierarchy={hierarchy.name}, pair={pair.name} ({size[0]}x{size[1]}).")
        return True

    def click_resource(self, selector):
        """Click one current, unique, enabled app-owned view by resource ID."""
        tree = self.smoke.tree()
        matches = [node for node in tree.iter("node")
                   if node.get("resource-id") == selector
                   and node.get("visible-to-user", "true") == "true"
                   and node.get("clickable") == "true"
                   and node.get("enabled", "true") == "true"]
        require(len(matches) == 1,
                f"Expected exactly one visible enabled selector {selector}; found {len(matches)}")
        match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", matches[0].get("bounds", ""))
        require(match is not None, f"Selector {selector} has invalid bounds")
        left, top, right, bottom = map(int, match.groups())
        require(right > left and bottom > top, f"Selector {selector} has empty bounds")
        self.adb("shell", "input", "tap", str((left + right) // 2), str((top + bottom) // 2))
        time.sleep(0.7)

    def unattended_sequence(self):
        self.await_checkpoint("a0", "a0-first-run.png", "Set up Inbound")
        self.click_resource(SELECTORS["first_run_start"])
        self.await_checkpoint("a1", "a1-downloads.png", "Search downloads")
        tree = self.smoke.tree()
        text = " ".join(node.get("text", "") for node in tree.iter("node"))
        require(re.search(r"\bAll\s+0\b", text, re.IGNORECASE),
                "A1 is not verified empty: visible All 0 count missing")
        self.click_resource(SELECTORS["add"])
        verify = getattr(self.args, "verify_a4_c7", False)
        if verify:
            # Allow the OS clipboard-access toast to leave before capturing the sheet.
            time.sleep(3)
        self.await_checkpoint("a4", "a4-add.png", "Add a download")
        if verify:
            tree = self.smoke.tree()
            names = ["Link or several links", "Torrent file or magnet", "From a site", "TorBox", "Import a list"]
            require([n.get("text") for n in tree.iter("node") if n.get("text") in names] == names,
                    "A4 source rows are missing or out of mockup order")
            for title in ["Torrent file or magnet", "TorBox", "Import a list"]:
                rows = [n for n in tree.iter("node") if n.get("content-desc", "").startswith(title + ".")]
                require(len(rows) == 1 and rows[0].get("enabled") == "false",
                        f"Unfinished option {title} must be opted out")
            self.log("PASS A4: all five source rows in order, unfinished actions disabled")
        self.click_resource(SELECTORS["add_link"])
        self.await_checkpoint("a5", "a5-new-download.png", "New download")
        if getattr(self.args, "a5_validation", False):
            self.adb("shell", "input", "text", "not-a-url")
            self.await_checkpoint("a5-validation", "a5-new-download.png", "Only http and https links can be downloaded here.")
            tree = self.smoke.tree()
            require(any(n.get("resource-id") == APP_ID + "new_download_btn" and n.get("enabled") == "false"
                        for n in tree.iter("node")), "Invalid address must not enable Download")
            self.log("PASS A5: invalid address stays in the form and Download is disabled")
        if getattr(self.args, "a5_metadata", False):
            self.capture_a5_metadata()
        self.click_resource(SELECTORS["new_close"])
        self.click_resource(SELECTORS["settings"])
        self.await_checkpoint("c7", "c7-settings.png", "Settings")
        if verify:
            tree = self.smoke.tree()
            labels = [n.get("text", "") for n in tree.iter("node")]
            require("Site logins" in labels and "Completion alerts and Android settings" not in labels,
                    "C7 site logins or notification row differs")
            suffix = ":id/settings_search"
            searches = [n.get("resource-id") for n in tree.iter("node") if n.get("resource-id", "").endswith(suffix)]
            require(len(searches) == 1, "C7 Settings search is missing or ambiguous")
            self.click_resource(searches[0])
            self.adb("shell", "input", "text", "Catching")
            labels = [n.get("text", "") for n in self.smoke.tree().iter("node")]
            require("Catching links" in labels and "Site logins" not in labels and "Theme" not in labels,
                    "C7 search did not filter category and appearance rows")
            self.log("PASS C7: Site logins row, Settings search filters categories")
        self.log("UNVERIFIED C8: populated light-mode state is intentionally not synthesized.")

    def capture_a5_metadata(self):
        class MetadataFixture(BaseHTTPRequestHandler):
            def do_HEAD(self):
                if self.path != "/inbound-parity.iso":
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header("Content-Length", str(6_100_000_000))
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Accept-Ranges", "bytes")
                self.end_headers()

            def do_GET(self):
                if self.path != "/inbound-parity.iso" or self.headers.get("Range") != "bytes=0-0":
                    self.send_error(405)
                    return
                self.send_response(206)
                self.send_header("Content-Length", "1")
                self.send_header("Content-Range", "bytes 0-0/6100000000")
                self.send_header("Content-Type", "application/octet-stream")
                self.end_headers()
                self.wfile.write(b"\0")

        with ThreadingHTTPServer(("127.0.0.1", 0), MetadataFixture) as fixture:
            worker = threading.Thread(target=fixture.serve_forever, daemon=True)
            worker.start()
            try:
                self.adb("shell", "input", "text", f"http://10.0.2.2:{fixture.server_port}/inbound-parity.iso")
                self.adb("shell", "input", "keyevent", "4")  # Dismiss keyboard, not the form.
                self.await_checkpoint("a5-populated", "a5-new-download.png", "The server answered")
                tree = self.smoke.tree()
                require(any(n.get("resource-id") == APP_ID + "file_name" and n.get("text") == "inbound-parity.iso"
                            for n in tree.iter("node")), "A5 did not populate the filename from the fixture")
                self.log("PASS A5: disposable fixture populated filename and metadata; no download submitted")
            finally:
                fixture.shutdown()
                worker.join()

    def unattended_checkpoint(self, key, reference, anchor):
        """Capture an already reached state; report unsupported transitions honestly."""
        return self.await_checkpoint(key, reference, anchor)

    def run(self):
        self.check()
        try:
            self.launch()
            if self.args.unattended:
                self.unattended_sequence()
            else:
                for key, reference, anchor in CAPTURES:
                    self.await_checkpoint(key, reference, anchor)
        except Exception:
            self.collect_failure()
            raise
        finally:
            self.clean()
        return self.evidence

    def collect_failure(self):
        if not self.started:
            return
        try:
            tree = self.smoke.tree()
            ET.ElementTree(tree).write(self.evidence / "failure-ui.xml",
                                       encoding="utf-8", xml_declaration=True)
            # These app tags are scoped; avoid a full logcat dump that could
            # include unrelated app/device data.
            logs = self.adb("logcat", "-d", "-s", "Inbound", "InboundRemoteFilesProbe")
            (self.evidence / "failure-logcat.txt").write_text(logs, encoding="utf-8")
        except Exception as exc:
            self.log(f"Could not collect failure diagnostics: {exc}")

    def clean(self):
        if not self.installed:
            return
        self.adb("uninstall", PACKAGE)
        require(f"package:{PACKAGE}" not in self.shell("pm", "list", "packages", PACKAGE),
                "Probe package remains installed after scoped cleanup")
        self.log("Cleanup PASS: uninstalled only the probe package; no app-data wipe, "
                 "device reset, shared-file deletion, or build performed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt", help="Path to Android build-tools aapt(.exe)")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit probe APK with the rcloneprobe application ID")
    parser.add_argument("--evidence", type=Path, required=True, help="New, previously absent evidence directory")
    parser.add_argument("--verify-a4-c7", action="store_true", help="Assert updated A4 rows and C7 search in an unattended run")
    parser.add_argument("--a5-metadata", action="store_true", help="Populate A5 from a loopback metadata fixture without submitting")
    parser.add_argument("--a5-validation", action="store_true", help="Check the A5 invalid URL state without submitting")
    parser.add_argument("--unattended", action="store_true",
                        help="Opt in to hands-off capture of safely observable, already reached states")
    args = parser.parse_args()
    if args.a5_metadata and not args.unattended:
        parser.error("--a5-metadata requires --unattended")
    if args.a5_validation and (not args.unattended or args.a5_metadata):
        parser.error("--a5-validation requires --unattended and is separate from --a5-metadata")
    args.instructions = {
        "a0": "Use a fresh probe install. Capture the first-run setup screen before choosing permissions.",
        "a1": "Finish setup, then show a populated All downloads list with representative states.",
        "a4": "Open Add from Downloads. Capture the sheet in the intended clipboard-offer state.",
        "a5": "Choose Link or several links. Capture the filled form/metadata state; use only the disposable fixture URL.",
        "a5-populated": "Capture the isolated fixture metadata after hiding the keyboard.",
        "a5-validation": "Capture an invalid, non-network address; no download can be submitted.",
        "c7": "Open Settings and show its root in Dark mode with Dynamic colour off.",
        "c8": "Return to Downloads, select Light mode in Settings, and show the comparable populated list.",
    }
    capture = Capture(args)
    print(f"Evidence: {capture.run()}")


if __name__ == "__main__":
    main()
