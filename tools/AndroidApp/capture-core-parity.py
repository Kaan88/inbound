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

    def verify_a4_site_route(self):
        """Follow the site entry without inspecting or changing clipboard contents."""
        self.smoke.wait_node("Set up Inbound", timeout=30)
        self.click_resource(SELECTORS["first_run_start"])
        self.smoke.wait_node("Search downloads", timeout=30)
        self.click_resource(SELECTORS["add"])
        row, tree = self.smoke.wait_content_description(
            lambda value: value.startswith("From a site. "), "A4 From a site row")
        require(row.get("enabled") == "true" and row.get("clickable") == "true",
                "From a site is unavailable on this probe")
        for title in ("Torrent file or magnet", "TorBox", "Import a list"):
            rows = [n for n in tree.iter("node") if n.get("content-desc", "").startswith(title + ".")]
            require(len(rows) == 1 and rows[0].get("enabled") == "false",
                    f"{title} is not marked unavailable")
        # Do not save the Add hierarchy or image: a pre-existing clipboard
        # offer may contain private data. Never tap its Use action here.
        self.smoke.tap(row)
        _, landing = self.smoke.wait_node("Enable Remote Files", timeout=35)
        require(any(n.get("resource-id", "").endswith(":id/nav_remote_files") and n.get("selected") == "true"
                    for n in landing.iter("node")), "From a site did not select Remote Files")
        (self.evidence / "a4-site-landing-capture.png").write_bytes(
            self.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(landing).write(self.evidence / "a4-site-landing-ui.xml",
                                      encoding="utf-8", xml_declaration=True)
        self.log("PASS A4: From a site opened local Remote Files landing; unfinished rows were disabled. Clipboard was not changed or captured.")

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
        require(not before_battery, "Probe is already on the battery whitelist")
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
        grant_battery = self.args.a0_battery_grant
        battery_button = "android:id/button1" if grant_battery else "android:id/button2"
        battery_labels = ("allow", "yes") if grant_battery else ("cancel", "deny", "no")
        def battery_dialog(tree):
            buttons = [n for n in tree.iter("node") if n.get("resource-id") == battery_button
                       and n.get("text", "").casefold() in battery_labels and n.get("clickable") == "true"
                       and n.get("enabled") == "true" and n.get("package") == "com.android.settings"]
            return len(buttons) == 1
        dialog = tree_until(battery_dialog, "native battery prompt with the requested explicit action")
        text = " ".join(n.get("text", "") for n in dialog.iter("node"))
        require("Inbound" in text and ("battery" in text.casefold() or "background" in text.casefold()),
                "Battery dialog does not identify the probe and the requested permission")
        save("a0-battery-prompt", dialog)
        action = next(n for n in dialog.iter("node") if n.get("resource-id") == battery_button
                      and n.get("text", "").casefold() in battery_labels)
        self.smoke.tap(action)
        returned = tree_until(lambda t: bool(by_id(t, "fr_start")) and
                              (not by_id(t, "fr_batt_allow") if grant_battery else len(by_id(t, "fr_batt_allow")) == 1),
                              "battery prompt returned to first-run setup")
        after_battery = battery_entries()
        require(bool(after_battery) if grant_battery else after_battery == before_battery,
                "Probe battery whitelist did not match the chosen action")
        save("a0-battery-allow-return" if grant_battery else "a0-battery-cancel-return", returned)
        if grant_battery:
            self.shell("am", "force-stop", PACKAGE)
            self.adb("shell", "am", "start", "-W", "-n", ACTIVITY)
            reopened = tree_until(lambda t: bool(by_id(t, "fr_start")) and not by_id(t, "fr_batt_allow"),
                                  "first-run setup with battery exemption after relaunch")
            require(battery_entries() == after_battery, "Battery exemption did not persist across relaunch")
            save("a0-battery-allow-reopened", reopened)
        self.log(f"PASS A0: notification {self.args.a0_permissions}; battery {action.get('text')} "
                 + ("persisted across probe relaunch" if grant_battery else "left probe whitelist unchanged"))

    def verify_c7_appearance(self):
        """Exercise the app-owned Theme setting on the fresh disposable probe."""
        def save(key, tree, reference=None):
            image = self.evidence / (key + "-capture.png")
            image.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (key + "-ui.xml"), encoding="utf-8", xml_declaration=True)
            if reference:
                parity = load_module("appearance_pair_helper", Path(__file__).with_name("capture-remote-files-parity.py"))
                parity.make_pair(MOCKUPS / reference, image, self.evidence / (key + "-pair.png"), key.upper())

        def settings():
            self.smoke.wait_node("Settings", resource="settings_title", timeout=30)
            _, tree = self.smoke.wait_node("Theme", timeout=30)
            tabs = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/nav_settings")]
            require(len(tabs) == 1 and tabs[0].get("selected") == "true", "Settings tab is not selected")
            return tree

        indicator_capture = 0
        def require_indicator(selected_tab):
            nonlocal indicator_capture
            from PIL import Image
            import io
            deadline = time.monotonic() + 12
            while time.monotonic() < deadline:
                tree = self.smoke.tree()
                items = [n for n in tree.iter("node")
                         if n.get("resource-id", "").endswith((":id/nav_downloads", ":id/nav_settings",
                                                                ":id/nav_queues", ":id/nav_remote_files"))]
                selected = [n.get("resource-id", "").split(":id/")[-1] for n in items if n.get("selected") == "true"]
                if selected != [selected_tab]:
                    time.sleep(.25); continue
                data = self.adb("exec-out", "screencap", "-p", raw=True)
                samples = {}
                with Image.open(io.BytesIO(data)) as image:
                    image = image.convert("RGB")
                    for item in items:
                        icon = [n for n in item.iter("node") if n.get("resource-id", "").endswith(":id/navigation_bar_item_icon_view")]
                        require(len(icon) == 1, "Native nav icon missing")
                        match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", icon[0].get("bounds", ""))
                        require(match, "Native nav icon bounds missing")
                        l,t,r,b = map(int, match.groups())
                        require(0 <= l < r <= image.width and 0 <= t < b <= image.height, "Nav icon bounds invalid")
                        # One icon width to either side of its center lies inside
                        # the 64x32 pill and outside the 24dp icon glyph.
                        center = (l+r)//2; width = r-l; y = (t+b)//2
                        require(center-width >= 0 and center+width < image.width, "Nav sample outside screenshot")
                        samples[item.get("resource-id", "").split(":id/")[-1]] = [
                            image.getpixel((center-width,y)), image.getpixel((center+width,y))]
                active = samples[selected_tab]
                others = [pair for key, pair in samples.items() if key != selected_tab]
                inactive = others[0][0] if others else None
                if (len(others) >= 2 and active[0] == active[1]
                        and all(pair[0] == pair[1] == inactive for pair in others)
                        and sum(abs(a-b) for a,b in zip(active[0], inactive)) >= 15):
                    indicator_capture += 1
                    key = f"c7-nav-pixel-{indicator_capture}-{selected_tab}"
                    (self.evidence / (key + ".png")).write_bytes(data)
                    ET.ElementTree(tree).write(self.evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)
                    (self.evidence / (key + ".json")).write_text(json.dumps({"selected": selected, "samples": samples}, indent=2) + "\n", encoding="utf-8")
                    return tree
                time.sleep(.25)
            raise RuntimeError("Selected navigation pill pixels did not follow " + selected_tab)

        def theme_dialog(expected=None):
            self.click_resource(APP_ID + "setting_theme")
            _, tree = self.smoke.wait_node("Light")
            for label in ("Follows the system", "Light", "Dark"):
                nodes = [n for n in tree.iter("node") if n.get("text") == label and n.get("checkable") == "true"]
                require(len(nodes) == 1, "Theme choice is missing or ambiguous: " + label)
                if expected is not None:
                    require(nodes[0].get("checked") == str(label == expected).lower(),
                            "Theme chooser does not reflect " + expected)
            return tree

        def choose(theme):
            node, _ = self.smoke.wait_node(theme)
            require(node.get("checkable") == "true" and node.get("enabled") == "true", "Theme choice unavailable")
            self.smoke.tap(node)
            selected, _ = self.smoke.wait_node(theme)
            require(selected.get("checked") == "true", "Theme draft was not selected")
            self.smoke.tap(self.smoke.wait_node("Apply")[0])
            self.smoke.wait_node(theme, resource="theme_text", timeout=30)
            settings()  # AppCompat may recreate the activity.
            return require_indicator("nav_settings")

        self.smoke.tap(self.smoke.wait_node("Start downloading", resource="fr_start")[0])
        self.smoke.wait_node("Search downloads", timeout=30)
        self.smoke.wait_content_description(lambda d: d == "Downloads shown from This phone. Change host", "local Host")
        require_indicator("nav_downloads")
        for tab in ("nav_queues", "nav_remote_files"):
            self.click_resource(APP_ID + tab)
            require_indicator(tab)
        self.click_resource(APP_ID + "nav_downloads")
        self.smoke.wait_node("Search downloads", timeout=30)
        require_indicator("nav_downloads")
        self.click_resource(SELECTORS["settings"])
        settings()
        require_indicator("nav_settings")
        switch, _ = self.smoke.wait_node("Dynamic colour", resource="dynamic_switch")
        if switch.get("checked") == "true":
            require(switch.get("enabled") == "true", "Dynamic colour cannot be disabled in the probe")
            self.smoke.tap(switch)
            settings()
        require(self.smoke.wait_node("Dynamic colour", resource="dynamic_switch")[0].get("checked") == "false",
                "Dynamic colour remained enabled")
        originals = [n for n in settings().iter("node")
                     if n.get("resource-id", "").endswith(":id/theme_text")]
        require(len(originals) == 1, "Theme summary is missing or ambiguous")
        original_theme = originals[0].get("text")
        require(original_theme in ("Follows the system", "Light", "Dark"), "Unknown initial theme")
        save("c7-appearance-chooser", theme_dialog(original_theme))
        cancelled_theme = "Dark" if original_theme == "Light" else "Light"
        self.smoke.tap(self.smoke.wait_node(cancelled_theme)[0])
        draft, tree = self.smoke.wait_node(cancelled_theme)
        require(draft.get("checked") == "true", "Cancelled theme draft was not selected")
        save("c7-appearance-cancel-draft", tree)
        self.smoke.tap(self.smoke.wait_node("Cancel")[0])
        self.smoke.wait_node(original_theme, resource="theme_text", timeout=30)
        save("c7-appearance-cancel-reopened", theme_dialog(original_theme))
        tree = choose("Light")
        save("c7-appearance-light", tree)
        self.shell("input", "keyevent", "4")
        self.smoke.wait_node("Search downloads", timeout=30)
        require_indicator("nav_downloads")
        self.click_resource(SELECTORS["settings"])
        require_indicator("nav_settings")
        self.smoke.wait_node("Light", resource="theme_text", timeout=30)
        settings()
        tree = theme_dialog("Light")
        save("c7-appearance-light-reopened", tree)
        tree = choose("Dark")
        save("c7-appearance-dark", tree, "c7-settings.png")
        self.shell("input", "keyevent", "4")
        self.smoke.wait_node("Search downloads", timeout=30)
        require_indicator("nav_downloads")
        self.click_resource(SELECTORS["settings"])
        require_indicator("nav_settings")
        self.smoke.wait_node("Dark", resource="theme_text", timeout=30)
        settings()
        tree = theme_dialog("Dark")
        save("c7-appearance-dark-reopened", tree)
        self.smoke.tap(self.smoke.wait_node("Cancel")[0])
        settings()
        switch, _ = self.smoke.wait_node("Dynamic colour", resource="dynamic_switch")
        if switch.get("enabled") == "true":
            for desired in ("true", "false"):
                switch, _ = self.smoke.wait_node("Dynamic colour", resource="dynamic_switch")
                require(switch.get("checked") != desired, "Dynamic colour did not begin in the opposite state")
                self.smoke.tap(switch)
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    switch, _ = self.smoke.wait_node("Dynamic colour", resource="dynamic_switch")
                    if switch.get("checked") == desired: break
                    time.sleep(.25)
                else: raise RuntimeError("Dynamic colour selection did not persist")
                settings(); tree = require_indicator("nav_settings")
                save("c7-dynamic-" + ("on" if desired == "true" else "off"), tree)
                self.shell("input", "keyevent", "4")
                self.smoke.wait_node("Search downloads", timeout=30)
                require_indicator("nav_downloads")
                self.click_resource(SELECTORS["settings"])
                settings(); require_indicator("nav_settings")
                switch, tree = self.smoke.wait_node("Dynamic colour", resource="dynamic_switch")
                require(switch.get("checked") == desired, "Dynamic colour did not survive Back/reopen")
                save("c7-dynamic-" + ("on" if desired == "true" else "off") + "-reopened", tree)
        else:
            self.log("Dynamic colour unavailable on this device; On/Off remains unverified.")
        self.log("PASS C7 appearance: Cancel preserved the original theme; Apply committed Light and Dark across recreation and Back/reopen; navigation highlight followed Settings and Downloads.")

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

    def verify_c7_settings_details(self):
        """Inspect local C7 details and system handoffs without applying settings."""
        def save(key, tree):
            (self.evidence / (key + "-capture.png")).write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (key + "-ui.xml"), encoding="utf-8", xml_declaration=True)
        def wait_tree(predicate, description):
            end = time.monotonic() + 30
            while time.monotonic() < end:
                tree = self.smoke.tree()
                if predicate(tree): return tree
                time.sleep(.3)
            raise RuntimeError("Could not verify " + description)
        def row(tree, label):
            candidates = [n for n in tree.iter("node") if n.get("clickable") == "true"
                          and n.get("enabled") == "true" and any(child.get("text") == label for child in n.iter("node"))]
            matches = [n for n in candidates if not any(child in candidates for child in n.iter("node") if child is not n)]
            require(len(matches) == 1, f"Expected one enabled row for {label}, found {len(matches)}")
            return matches[0]
        def row_value(tree, label):
            texts = [n.get("text", "") for n in row(tree, label).iter("node") if n.get("text")]
            require(len(texts) == 2 and texts[0] == label, f"Could not identify value for {label}")
            return texts[1]
        def category(query, description, detail_anchor):
            self.smoke.wait_node("Settings", resource="settings_title")
            self.click_resource(APP_ID + "settings_search")
            self.smoke.wait_node("Search settings", resource="settings_search_input")
            self.adb("shell", "input", "text", query)
            selected, _ = self.smoke.wait_content_description(lambda d: d == description, description)
            self.smoke.tap(selected)
            return self.smoke.wait_node(detail_anchor)[1]
        def back_to_root():
            self.shell("input", "keyevent", "4")
            tree = self.smoke.wait_node("Settings", resource="settings_title")[1]
            if any(n.get("resource-id") == APP_ID + "settings_search_input" for n in tree.iter("node")):
                self.click_resource(APP_ID + "settings_search")
            self.smoke.wait_node("Settings", resource="settings_title")
        def numeric(label, input_description, draft, key, speed=False):
            tree = self.smoke.tree(); before = row_value(tree, label)
            self.smoke.tap(row(tree, label))
            field, tree = self.smoke.wait_content_description(lambda d: d == input_description, input_description)
            if speed:
                choice, _ = self.smoke.wait_content_description(lambda d: d == "Custom overall speed limit", "custom overall speed")
                self.smoke.tap(choice)
                field, tree = self.smoke.wait_content_description(lambda d: d == input_description, input_description)
            require(field.get("enabled") == "true", "Numeric field is not editable")
            self.smoke.tap(field); self.shell("input", "keyevent", "KEYCODE_MOVE_END")
            current = field.get("text", "")
            if speed and current == "KB/s":
                current = ""
            require(len(current) <= 24 and (not current or current.isdigit()), "Unexpected numeric field content")
            for _ in current: self.shell("input", "keyevent", "KEYCODE_DEL")
            self.shell("input", "text", draft); self.shell("input", "keyevent", "4")
            field, tree = self.smoke.wait_content_description(lambda d: d == input_description, input_description)
            require(field.get("text") == draft, "Dialog did not retain the disposable draft")
            save(key + "-dialog", tree)
            self.smoke.tap(self.smoke.wait_node("Cancel")[0])
            tree = wait_tree(lambda t: any(n.get("text") == label for n in t.iter("node")) and
                             not any(n.get("text") == "Apply" for n in t.iter("node")), label + " after Cancel")
            require(row_value(tree, label) == before, label + " changed after Cancel")
            save(key + "-cancel", tree)
        def system_handoff(label, key, predicate):
            tree = self.smoke.tree(); self.smoke.tap(row(tree, label))
            tree = wait_tree(predicate, key + " Android system surface")
            save(key + "-system", tree)
            self.shell("input", "keyevent", "4")
            _, returned = self.smoke.wait_node(label, timeout=30)
            save(key + "-return", returned)
        def system_package(tree):
            return any(n.get("package") == "com.android.settings" for n in tree.iter("node"))
        def notification_surface(tree, activity):
            focus = self.shell("dumpsys", "activity", "activities").splitlines()
            return system_package(tree) and any(("topResumedActivity" in line or "mResumedActivity" in line) and
                                                "com.android.settings" in line and activity in line for line in focus)
        def system_permissions():
            package = self.shell("dumpsys", "package", PACKAGE)
            grants = re.findall(r"android\.permission\.POST_NOTIFICATIONS:\s*granted=(true|false)", package)
            require(len(grants) == 1, "Probe notification permission is unavailable or ambiguous")
            whitelist = self.shell("cmd", "deviceidle", "whitelist").splitlines()
            require(any(line.startswith(("system,", "user,")) for line in whitelist), "Battery whitelist is unavailable")
            entries = tuple(sorted(line.strip() for line in whitelist
                                   if re.search(r"(?:^|[,\s])" + re.escape(PACKAGE) + r"(?:$|[,\s])", line)))
            return grants[0], entries
        self.smoke.tap(self.smoke.wait_node("Start downloading", resource="fr_start")[0])
        self.smoke.wait_node("Search downloads", timeout=30)
        self.smoke.wait_content_description(lambda d: d == "Downloads shown from This phone. Change host", "local Host")
        self.shell("pm", "grant", PACKAGE, "android.permission.POST_NOTIFICATIONS")
        before_system = system_permissions()
        self.click_resource(SELECTORS["settings"])
        if not self.args.only_c7_system_handoffs:
            tree = category("Downloads", "Downloads. Folder and connections", "Download folder")
            require(any(n.get("text") == "This phone" for n in tree.iter("node")), "Downloads is not phone scoped")
            save("c7-downloads", tree)
            for label, draft, key in (("Downloads at once", "7", "downloads-at-once"),
                                      ("Connections per file", "9", "connections-per-file"),
                                      ("Connections per host", "10", "connections-per-host")):
                numeric(label, label, draft, "c7-" + key)
            back_to_root()
            tree = category("Network", "Network. Network rule and speed limit", "Overall speed limit")
            save("c7-network", tree)
            require(any(n.get("content-desc") == "Unmetered network only" for n in tree.iter("node")), "Unmetered rule missing")
            numeric("Overall speed limit", "Speed limit in KB per second", "123", "c7-overall-speed", speed=True)
            back_to_root()
        tree = category("Background", "Background and battery. Resume and sleep prevention", "Android battery settings")
        require({"Resume on launch", "Keep awake while transferring"}.issubset(
            {n.get("content-desc", "") for n in tree.iter("node")}), "Background switches missing")
        save("c7-background", tree)
        system_handoff("Android battery settings", "c7-battery", lambda t: notification_surface(t, "HighPowerApplicationsActivity"))
        back_to_root()
        tree = category("Notifications", "Notifications and sounds", "Notification settings")
        require(any(n.get("content-desc") == "Download finished" for n in tree.iter("node")), "Completion switch missing")
        save("c7-notifications", tree)
        system_handoff("Sound and vibration", "c7-sound", lambda t: system_package(t)
                       and any(n.get("text", "").casefold() == "finished downloads" for n in t.iter("node")))
        system_handoff("Notification settings", "c7-notification-app", lambda t: system_package(t) and
                       (notification_surface(t, "AppNotificationSettingsActivity") or
                        {"Downloads in progress", "Finished downloads"}.issubset({n.get("text") for n in t.iter("node")})))
        require(system_permissions() == before_system, "Probe notification permission or battery exemption changed")
        if not self.args.only_c7_system_handoffs:
            self.log("PASS C7 local limits: draft changes were cancelled and settings stayed unchanged.")
        self.log("PASS C7 system handoffs: battery management, finished channel and app notification settings returned with probe permission and battery exemption unchanged after the setup grant.")

    def run(self):
        self.check()
        try:
            self.launch()
            if self.args.verify_c7_appearance:
                self.verify_c7_appearance()
            elif self.args.verify_c7_routes:
                self.verify_c7_routes()
            elif self.args.verify_c7_settings_details:
                self.verify_c7_settings_details()
            elif self.args.a0_permissions:
                self.verify_a0_permissions()
            elif self.args.verify_a4_site:
                self.verify_a4_site_route()
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
        if self.args.verify_a4_site:
            # The Add sheet may show private clipboard text; do not capture it
            # on a failed or interrupted route.
            return
        try:
            tree = self.smoke.tree()
            ET.ElementTree(tree).write(self.evidence / "failure-ui.xml",
                                       encoding="utf-8", xml_declaration=True)
            if self.args.verify_c7_routes or self.args.verify_c7_appearance or self.args.verify_c7_settings_details:
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
    parser.add_argument("--verify-c7-appearance", action="store_true", help="Standalone local app Theme chooser and persistence check")
    parser.add_argument("--verify-c7-routes", action="store_true", help="Standalone local Catching links and Site logins route check")
    parser.add_argument("--verify-c7-settings-details", action="store_true", help="Standalone C7 local limits, battery and notification handoffs")
    parser.add_argument("--only-c7-system-handoffs", action="store_true", help="Repeat just the three Android system handoffs")
    parser.add_argument("--a0-permissions", choices=("deny", "allow"), help="Standalone native first-run permission check; battery prompt is cancelled")
    parser.add_argument("--a0-battery-grant", action="store_true", help="In the A0 lane grant battery exemption and verify it across a probe relaunch")
    parser.add_argument("--verify-a4-site", action="store_true", help="Standalone A4 From a site route on a fresh local probe")
    parser.add_argument("--verify-a4-c7", action="store_true", help="Assert updated A4 rows and C7 search in an unattended run")
    parser.add_argument("--a5-metadata", action="store_true", help="Populate A5 from a loopback metadata fixture without submitting")
    parser.add_argument("--a5-validation", action="store_true", help="Check the A5 invalid URL state without submitting")
    parser.add_argument("--unattended", action="store_true",
                        help="Opt in to hands-off capture of safely observable, already reached states")
    args = parser.parse_args()
    if args.only_c7_system_handoffs and not args.verify_c7_settings_details:
        parser.error("--only-c7-system-handoffs requires --verify-c7-settings-details")
    if args.verify_c7_settings_details and (args.verify_c7_appearance or args.verify_c7_routes or args.verify_a4_site or args.a0_permissions or args.unattended or args.verify_a4_c7 or args.a5_metadata or args.a5_validation):
        parser.error("--verify-c7-settings-details is a standalone lane")
    if args.a0_battery_grant and not args.a0_permissions:
        parser.error("--a0-battery-grant requires --a0-permissions")
    if args.verify_a4_site and (args.verify_c7_appearance or args.verify_c7_routes or args.a0_permissions or args.unattended or args.verify_a4_c7 or args.a5_metadata or args.a5_validation):
        parser.error("--verify-a4-site is a standalone lane")
    if args.verify_c7_appearance and (args.verify_c7_routes or args.a0_permissions or args.unattended or args.verify_a4_c7 or args.a5_metadata or args.a5_validation):
        parser.error("--verify-c7-appearance is a standalone lane")
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
