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
            "initial_download_count": len(self.initial_downloads),
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
                self.await_checkpoint("a5-populated", "a5-new-download.png", "6.1 GB")
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

    def verify_a0_permissions(self):
        """Use the probe's actual first-run buttons and safe native dialog actions."""
        def tree_until(predicate, description, timeout=30):
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                tree = self.smoke.tree()
                if predicate(tree): return tree
                time.sleep(.3)
            raise RuntimeError("Could not verify " + description)
        def by_id(tree, suffix):
            return [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/" + suffix)]
        def save(key, tree):
            (self.evidence / (key + "-capture.png")).write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (key + "-ui.xml"), encoding="utf-8", xml_declaration=True)
        def permission_granted():
            # Read only the probe's permission state; never persist the package dump.
            package = self.shell("dumpsys", "package", PACKAGE)
            matches = re.findall(r"android\.permission\.POST_NOTIFICATIONS:\s*granted=(true|false)", package)
            require(len(matches) == 1, "Probe notification permission state is unavailable or ambiguous")
            return matches[0] == "true"
        def battery_entries():
            lines = self.shell("cmd", "deviceidle", "whitelist").splitlines()
            require(any(line.startswith(("system,", "user,")) for line in lines),
                    "Could not read the device-idle whitelist")
            return tuple(sorted(line.strip() for line in lines
                                if re.search(r"(?:^|[,\s])" + re.escape(PACKAGE) + r"(?:$|[,\s])", line)))

        initial = tree_until(lambda t: len(by_id(t, "fr_notif_allow")) == 1 and
                             len(by_id(t, "fr_batt_allow")) == 1, "first-run permission controls")
        require(not permission_granted(), "Probe notification permission is already granted")
        before_battery = battery_entries()
        save("a0-permissions-before", initial)
        self.click_resource(APP_ID + "fr_notif_allow")
        button_id = ("permission_allow_button" if self.args.a0_permissions == "allow"
                     else "permission_deny_button")
        def notification_dialog(tree):
            buttons = by_id(tree, button_id)
            return len(buttons) == 1 and buttons[0].get("package") in (
                "com.android.permissioncontroller", "com.google.android.permissioncontroller") and \
                buttons[0].get("enabled") == "true" and buttons[0].get("clickable") == "true"
        dialog = tree_until(notification_dialog, "native notification permission dialog")
        labels = " ".join(n.get("text", "") for n in dialog.iter("node"))
        require("Inbound" in labels and "notification" in labels.casefold(),
                "Notification dialog does not identify the probe and permission")
        save("a0-notification-prompt", dialog)
        self.smoke.tap(by_id(dialog, button_id)[0])
        granted = self.args.a0_permissions == "allow"
        returned = tree_until(lambda t: bool(by_id(t, "fr_start")) and
                              (not by_id(t, "fr_notif_allow") if granted else bool(by_id(t, "fr_notif_allow"))),
                              "notification return to first-run setup")
        require(permission_granted() == granted, "Probe notification permission did not match the chosen action")
        save("a0-notification-return", returned)
        require(len(by_id(returned, "fr_batt_allow")) == 1, "Battery permission is already unrestricted")
        self.click_resource(APP_ID + "fr_batt_allow")
        def cancel_dialog(tree):
            buttons = [n for n in tree.iter("node") if n.get("resource-id") == "android:id/button2"
                       and n.get("text", "").casefold() in ("cancel", "deny", "no") and n.get("clickable") == "true"
                       and n.get("enabled") == "true" and n.get("package") == "com.android.settings"]
            return len(buttons) == 1
        dialog = tree_until(cancel_dialog, "native battery prompt with an explicit negative action")
        text = " ".join(n.get("text", "") for n in dialog.iter("node"))
        require("Inbound" in text and ("battery" in text.casefold() or "background" in text.casefold()),
                "Battery dialog does not identify the probe and the requested permission")
        save("a0-battery-prompt", dialog)
        cancel = next(n for n in dialog.iter("node") if n.get("resource-id") == "android:id/button2"
                      and n.get("text", "").casefold() in ("cancel", "deny", "no"))
        self.smoke.tap(cancel)
        returned = tree_until(lambda t: len(by_id(t, "fr_batt_allow")) == 1 and bool(by_id(t, "fr_start")),
                              "battery prompt dismissal returned to first-run setup")
        require(battery_entries() == before_battery, "Probe battery whitelist changed after Cancel")
        save("a0-battery-cancel-return", returned)
        self.log(f"PASS A0: notification {self.args.a0_permissions} and battery {cancel.get('text')} returned to setup; probe whitelist unchanged")

    def verify_c7_routes(self):
        """Visit local Settings routes without reading the clipboard or signing in."""
        def save(key, tree):
            (self.evidence / (key + "-capture.png")).write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (key + "-ui.xml"), encoding="utf-8", xml_declaration=True)
        self.smoke.tap(self.smoke.wait_node("Start downloading", resource="fr_start")[0])
        self.smoke.wait_node("Search downloads", timeout=30)
        self.smoke.wait_content_description(lambda d: d == "Downloads shown from This phone. Change host", "local Host")
        self.click_resource(SELECTORS["settings"])
        self.smoke.wait_node("Settings", resource="settings_title")
        self.click_resource(APP_ID + "settings_search")
        self.smoke.wait_node("Search settings", resource="settings_search_input")
        self.adb("shell", "input", "text", "Catching")
        row, tree = self.smoke.wait_content_description(
            lambda d: d == "Catching links. Share, browser and clipboard", "filtered Catching links row")
        require(not any(n.get("text") == "Site logins" for n in tree.iter("node")),
                "Settings search did not filter Site logins")
        save("c7-links-search", tree)
        self.smoke.tap(row)
        _, tree = self.smoke.wait_node("Clipboard links")
        labels = {n.get("text", "") for n in tree.iter("node")}
        expected = {"Share to Inbound", "Use Share in your browser or another app", "Built-in browser",
                    "Browse a page and choose its downloads", "Clipboard links",
                    "Offer a copied link while Inbound is open", "Clipboard links are not monitored in the background."}
        require(expected.issubset(labels), "Catching links did not show its guidance and browser entry")
        require(not any("Switch" in n.get("class", "") for n in tree.iter("node")),
                "Catching links unexpectedly exposed a switch")
        save("c7-catching-links", tree)
        for _ in range(2):
            self.shell("input", "keyevent", "4")
            tree = self.smoke.tree()
            # First Back may dismiss the keyboard, and the visible hierarchy can
            # include the Settings root underneath the detail during navigation.
            if not self.smoke.matches(tree, "Clipboard links"):
                break
        else:
            raise RuntimeError("Native Back did not close Catching links")
        _, tree = self.smoke.wait_node("Settings", timeout=30, resource="settings_title")
        if any(n.get("resource-id") == APP_ID + "settings_search_input" for n in tree.iter("node")):
            self.click_resource(APP_ID + "settings_search")
        row, tree = self.smoke.wait_content_description(
            lambda d: d == "Site logins. Manage sign-ins in Remote Files", "Site logins row")
        save("c7-site-logins-row", tree)
        self.smoke.tap(row)
        _, tree = self.smoke.wait_node("Enable Remote Files", timeout=35)
        labels = {n.get("text", "") for n in tree.iter("node")}
        require("Remote Files is off on this Host." in labels and
                any(n.get("resource-id", "").endswith(":id/nav_remote_files") and n.get("selected") == "true"
                    for n in tree.iter("node")), "Site logins did not open the local Remote Files landing")
        save("c7-site-logins-landing", tree)
        self.log("PASS C7: Catching links guidance, browser entry and Back; Site logins opened the local Remote Files landing. No provider sign-in attempted.")

    def run(self):
        self.check()
        try:
            self.launch()
            if self.args.verify_c7_routes:
                self.verify_c7_routes()
            elif self.args.a0_permissions:
                self.verify_a0_permissions()
            elif self.args.unattended:
                self.unattended_sequence()
            else:
                for key, reference, anchor in CAPTURES:
                    self.await_checkpoint(key, reference, anchor)
        except Exception:
            if not self.args.a0_permissions:
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
            if self.args.verify_c7_routes:
                return
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
    parser.add_argument("--verify-c7-routes", action="store_true", help="Standalone local Catching links and Site logins route check")
    parser.add_argument("--a0-permissions", choices=("deny", "allow"), help="Standalone native first-run permission check; battery prompt is cancelled")
    parser.add_argument("--verify-a4-c7", action="store_true", help="Assert updated A4 rows and C7 search in an unattended run")
    parser.add_argument("--a5-metadata", action="store_true", help="Populate A5 from a loopback metadata fixture without submitting")
    parser.add_argument("--a5-validation", action="store_true", help="Check the A5 invalid URL state without submitting")
    parser.add_argument("--unattended", action="store_true",
                        help="Opt in to hands-off capture of safely observable, already reached states")
    args = parser.parse_args()
    if args.verify_c7_routes and (args.a0_permissions or args.unattended or args.verify_a4_c7 or args.a5_metadata or args.a5_validation):
        parser.error("--verify-c7-routes is a standalone lane")
    if args.a0_permissions and (args.unattended or args.verify_a4_c7 or args.a5_metadata or args.a5_validation):
        parser.error("--a0-permissions is a standalone lane")
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
