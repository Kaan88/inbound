"""Isolated API 33 A7 finished-notification proof; no production app changes."""
import argparse
import hashlib
import importlib.machinery
import io
import re
import sys
import threading
import time
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
states = importlib.machinery.SourceFileLoader("a7_finished_states", str(HERE / "capture-download-states-parity.py")).load_module()
queues = importlib.machinery.SourceFileLoader("a7_finished_queues", str(HERE / "capture-queue-host-parity.py")).load_module()
PHOTOS = "com.google.android.apps.photos.pager.HostPhotoPagerActivity"
COLOR = (37, 120, 205)


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def run(args):
    args.active_only = True
    args.details_flow = args.mixed_a2 = args.unattended_b5 = args.capture_mixed_a3 = False
    args.queue_name = "A7 Fixture"
    args.host_name = "Parity Host"
    cap = states.Capture(args)
    cap.names["active"] = "inbound-a7-finished-" + cap.nonce + ".png"
    name = cap.names["active"]
    target = "/sdcard/Download/" + name
    image = io.BytesIO()
    Image.new("RGB", (64, 64), COLOR).save(image, format="PNG")
    payload = image.getvalue()
    expected = hashlib.sha256(payload).hexdigest()
    arrived, release = threading.Event(), threading.Event()
    validated = False
    shade_open = False
    owned_output = False
    errors = []

    def focus():
        return next((line for line in cap.shell("dumpsys", "window").splitlines()
                     if "mCurrentFocus=" in line), "")

    class Handler(BaseHTTPRequestHandler):
        def serve(self, body):
            if self.path != "/active":
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", "image/png")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Content-Disposition", f'attachment; filename="{name}"')
            self.end_headers()
            if body:
                arrived.set()
                if release.wait(90):
                    try:
                        self.wfile.write(payload)
                        self.wfile.flush()
                    except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                        pass

        def do_HEAD(self): self.serve(False)
        def do_GET(self): self.serve(True)
        def log_message(self, *_): pass

    def exact_finished_key():
        keys = cap.shell("cmd", "notification", "list").splitlines()
        pattern = r"^0\|" + re.escape(states.PACKAGE) + r"\|[0-9]+\|[A-Za-z0-9_.|:-]+$"
        matches = []
        for key in (line.strip() for line in keys):
            if not re.fullmatch(pattern, key):
                continue
            record = cap.shell("cmd notification get '" + key + "'")
            title = re.search(r"android\.title=String \(([^\r\n]*)\)", record)
            if title and title.group(1) == name and re.search(r"(?:channel|channelId)=finished\b", record):
                matches.append(key)
        require(len(matches) <= 1, "More than one owned finished notification has the fixture title")
        return matches[0] if matches else None

    def finished_row():
        tree = cap.smoke.tree()
        rows = [n for n in tree.iter("node")
                if n.get("resource-id", "").endswith(":id/expandableNotificationRow")
                and any(x.get("resource-id") == "android:id/title" and x.get("text") == name
                        for x in n.iter("node"))]
        require(len(rows) <= 1, "Fixture notification row is ambiguous")
        return rows[0] if rows else None

    try:
        queues.Capture.check(cap)
        validated = True
        require(cap.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                "Exact nonce-named output already exists")
        cap.fixture = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        cap.port = cap.fixture.server_port
        require(not re.search(rf"\btcp:{cap.port}\b", cap.adb("reverse", "--list")), "Fixture reverse already exists")
        cap.fixture_thread = threading.Thread(target=cap.fixture.serve_forever, daemon=True)
        cap.fixture_thread.start()
        cap.adb("reverse", "--no-rebind", f"tcp:{cap.port}", f"tcp:{cap.port}")
        cap.reversed = True
        cap.install_and_launch()
        cap.shell("pm", "grant", states.PACKAGE, "android.permission.POST_NOTIFICATIONS")
        permission = cap.shell("dumpsys", "package", states.PACKAGE)
        require(re.search(r"android\.permission\.POST_NOTIFICATIONS:\s*granted=true", permission),
                "Probe notification permission was not granted")
        if args.open_viewer:
            viewer = cap.shell("cmd", "package", "query-activities", "--brief", "-a", "android.intent.action.VIEW",
                               "-d", f"content://{states.PACKAGE}.fileprovider/{name}", "-t", "image/png")
            require("1 activities found:" in viewer and "com.google.android.apps.photos" in viewer,
                    "Photos is not the sole PNG viewer; Open check refused")
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY, "--ez", "skip_first_run", "true",
                "--es", "add_url", f"http://127.0.0.1:{cap.port}/active")
        cap.started = True
        cap.smoke.wait_node("Search downloads")
        require(arrived.wait(30), "The owned PNG GET did not start")
        tree = cap.smoke.tree()
        require(sum(n.get("text") == "All 1" and n.get("checked") == "true" for n in tree.iter("node")) == 1,
                "Probe does not contain exactly one owned download")
        require(sum(n.get("content-desc", "").startswith(name + ". ") for n in tree.iter("node")) == 1,
                "The unique owned PNG row is missing")
        require(cap.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                "The exact output appeared before releasing the fixture")
        require(states.PACKAGE + "/" in focus(), "Probe was not the foreground app before Home")
        cap.shell("input", "keyevent", "KEYCODE_HOME")
        current_focus = focus()
        require("Window{" in current_focus and states.PACKAGE + "/" not in current_focus, "Probe is not backgrounded")
        owned_output = True
        release.set()
        deadline = time.monotonic() + 60
        key = None
        while time.monotonic() < deadline:
            require(states.PACKAGE not in focus(), "Probe returned to foreground before completion")
            digest = cap.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
            key = exact_finished_key()
            if digest[:1] == [expected] and key:
                break
            time.sleep(.3)
        else:
            raise RuntimeError("Background completion did not produce matching PNG and owned finished notification")
        (cap.evidence / "finished-hash.txt").write_text(expected + "\n", encoding="ascii")
        cap.shell("cmd", "statusbar", "expand-notifications")
        shade_open = True
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            row = finished_row()
            if row is not None:
                actions = [n for n in row.iter("node") if n.get("text") == "Open" and n.get("clickable") == "true"
                           and n.get("enabled") == "true"]
                if len(actions) == 1:
                    break
                expand = [n for n in row.iter("node") if n.get("resource-id", "").endswith(":id/expand_button")
                          and n.get("content-desc", "").casefold() == "expand" and n.get("clickable") == "true"]
                if len(expand) == 1:
                    cap.smoke.tap(expand[0])
            time.sleep(.3)
        else:
            raise RuntimeError("The owned finished notification Open action was not uniquely available")
        ET.ElementTree(row).write(cap.evidence / "finished-notification.xml", encoding="utf-8", xml_declaration=True)
        bounds = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", row.get("bounds", ""))
        require(bounds is not None, "Owned notification has invalid bounds")
        with Image.open(io.BytesIO(cap.adb("exec-out", "screencap", "-p", raw=True))) as screen:
            box = tuple(map(int, bounds.groups()))
            require(box[0] < box[2] <= screen.width and box[1] < box[3] <= screen.height, "Owned notification bounds outside screenshot")
            screen.crop(box).save(cap.evidence / "finished-notification.png")
        cap.log("PASS A7: exact owned PNG completed while backgrounded; hash and available finished notification Open verified.")
        if args.open_viewer:
            cap.smoke.tap(actions[0])
            shade_open = False
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                if PHOTOS in focus():
                    data = cap.adb("exec-out", "screencap", "-p", raw=True)
                    with Image.open(io.BytesIO(data)) as screen:
                        rgb = screen.convert("RGB").getpixel((screen.width // 2, screen.height // 2))
                    if rgb == COLOR:
                        (cap.evidence / "finished-viewer.png").write_bytes(data)
                        cap.log("PASS A7 finished Open: Photos rendered the owned PNG color.")
                        break
                time.sleep(.3)
            else:
                raise RuntimeError("Finished Open did not render the expected PNG in Photos")
    finally:
        pending = sys.exc_info()[0] is not None
        if cap.installed:
            try:
                cap.shell("am", "force-stop", states.PACKAGE)
            except Exception:
                errors.append("probe force-stop")
        release.set()
        if shade_open and validated:
            try: cap.shell("cmd", "statusbar", "collapse")
            except Exception: errors.append("notification shade")
        if cap.fixture is not None:
            try:
                if cap.fixture_thread is not None and cap.fixture_thread.is_alive(): cap.fixture.shutdown()
                cap.fixture.server_close()
                if cap.fixture_thread is not None: cap.fixture_thread.join(timeout=5)
            except Exception: errors.append("fixture shutdown")
        if cap.reversed:
            try: cap.adb("reverse", "--remove", f"tcp:{cap.port}")
            except Exception: errors.append("owned reverse")
        uninstalled = False
        if cap.installed:
            try:
                cap.adb("uninstall", states.PACKAGE)
                require(f"package:{states.PACKAGE}" not in cap.shell("pm", "list", "packages", states.PACKAGE), "Probe remains installed")
                uninstalled = True
            except Exception: errors.append("probe uninstall")
        if owned_output and uninstalled:
            try:
                present = cap.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() == "present"
                if present:
                    digest = cap.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
                    require(digest[:1] == [expected], "Fixture output hash differs; preserving it")
                    cap.shell("rm", "--", target)
                    require(cap.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present", "Fixture output remains")
            except Exception: errors.append("exact output cleanup (preserved)")
        if validated:
            try: cap.shell("rm", "-f", cap.xml_path)
            except Exception: errors.append("temporary UI dump")
        if errors:
            cap.log("Cleanup incomplete: " + ", ".join(errors))
            if not pending: raise RuntimeError("A7 finished cleanup incomplete")
        elif validated:
            cap.log("Cleanup PASS: owned probe, fixture, reverse and verified output only.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--open-viewer", action="store_true", help="Open only if Photos is the uniquely resolved PNG viewer")
    run(parser.parse_args())


if __name__ == "__main__": main()
