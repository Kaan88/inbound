"""Opt-in A3 snackbar Open check on a fresh, isolated API 33 probe."""
import argparse
import hashlib
import importlib.machinery
import io
import re
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
MODULE = importlib.machinery.SourceFileLoader("a3_existing_runner", str(HERE / "capture-download-states-parity.py")).load_module()
PACKAGE = MODULE.PACKAGE
PHOTOS = "com.google.android.apps.photos"
COLOR = (231, 32, 55)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def png():
    output = io.BytesIO()
    Image.new("RGB", (640, 480), COLOR).save(output, "PNG")
    return output.getvalue()


def focus(smoke):
    return next((line.strip() for line in smoke.shell("dumpsys", "window").splitlines()
                 if "mCurrentFocus=" in line), "")


def snackbar_action(tree, name):
    texts = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/snackbar_text")
             and n.get("text") == name + " finished"]
    if len(texts) != 1:
        return None
    parents = {child: parent for parent in tree.iter() for child in parent}
    node = texts[0]
    for _ in range(6):
        if node not in parents:
            break
        node = parents[node]
        actions = [n for n in node.iter("node") if n.get("resource-id", "").endswith(":id/snackbar_action")
                   and n.get("text") == "Open" and n.get("enabled") == "true" and n.get("clickable") == "true"]
        if len(actions) == 1:
            return actions[0]
    return None


def run(args):
    # This class supplies the existing exact-probe install/timeout ownership routine.
    args.active_only = True
    args.details_flow = False
    args.unattended_b5 = False
    runner = MODULE.Capture(args)
    smoke = runner.smoke
    name = "inbound-a3-open-" + runner.nonce + ".png"
    path = "/sdcard/Download/" + name
    body = png()
    digest = hashlib.sha256(body).hexdigest()
    server = None
    thread = None
    server_started = False
    reverse = None
    validated = False
    destination_owned = False
    gate = threading.Event()
    arrived = threading.Event()
    request_done = threading.Event()
    cleanup_errors = []

    class Handler(BaseHTTPRequestHandler):
        def respond(self, send_body):
            if self.path != "/" + name:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", "image/png")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Content-Disposition", f'attachment; filename="{name}"')
            self.end_headers()
            if not send_body:
                return
            arrived.set()
            try:
                if gate.wait(60):
                    self.wfile.write(body)
                    self.wfile.flush()
                    request_done.set()
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass
        def do_HEAD(self): self.respond(False)
        def do_GET(self): self.respond(True)
        def log_message(self, *_): pass

    try:
        require(re.fullmatch(r"emulator-\d+", args.serial), "Refusing a physical device")
        require(smoke.adb("emu", "avd", "name").splitlines()[0] == MODULE.AVD, "Wrong AVD")
        require(smoke.shell("getprop", "ro.build.version.sdk").strip() == MODULE.API, "Wrong Android API")
        validated = True
        apk = Path(args.apk).resolve(strict=True)
        aapt = args.aapt or runner.smoke_module.find_aapt()
        require(aapt, "Android aapt is required")
        badging = subprocess.run([aapt, "dump", "badging", str(apk)], capture_output=True,
                                 text=True, timeout=25, check=True).stdout
        require(re.search(r"^package: name=['\"]" + re.escape(PACKAGE) + r"['\"](?:\s|$)", badging, re.MULTILINE),
                "APK is not the isolated probe")
        require(f"package:{PACKAGE}" not in smoke.shell("pm", "list", "packages", PACKAGE), "Probe already installed")
        require(smoke.shell(f"test -e {path} && echo PRESENT || echo ABSENT").strip() == "ABSENT",
                "Fixture destination already exists")
        viewer = smoke.shell("cmd", "package", "query-activities", "--brief", "-a", "android.intent.action.VIEW",
                             "-d", f"content://{PACKAGE}.fileprovider/{name}", "-t", "image/png")
        require("1 activities found:" in viewer and PHOTOS in viewer, "Expected exactly one PNG viewer: Photos")
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        endpoint = "tcp:" + str(server.server_port)
        existing = smoke.adb("reverse", "--list").splitlines()
        require(not any(endpoint in line.split() for line in existing), "Fixture reverse already exists")
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        server_started = True
        smoke.adb("reverse", endpoint, endpoint)
        reverse = endpoint
        runner.install_and_launch()
        destination_owned = True
        smoke.adb("shell", "am", "start", "-W", "-n", MODULE.ACTIVITY, "--ez", "skip_first_run", "true",
                  "--es", "add_url", f"http://127.0.0.1:{server.server_port}/{name}")
        runner.started = True
        require(arrived.wait(40), "PNG fixture was not requested")
        smoke.wait_content_description(lambda d: d.startswith(name + ". "), "owned PNG job", timeout=30)
        require(PACKAGE in focus(smoke), "Probe is not foreground before completion")
        gate.set()
        require(request_done.wait(20), "PNG fixture was not sent")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if PACKAGE not in focus(smoke):
                raise RuntimeError("Probe lost foreground before snackbar Open")
            tree = smoke.tree()
            action = snackbar_action(tree, name)
            if action is not None:
                break
            time.sleep(.2)
        else:
            raise RuntimeError("Expected fixture completion snackbar with its own Open action")
        smoke.tap(action)
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if PHOTOS + ".pager.HostPhotoPagerActivity" in focus(smoke):
                screenshot = smoke.adb("exec-out", "screencap", "-p", raw=True)
                with Image.open(io.BytesIO(screenshot)) as image:
                    pixel = image.convert("RGB").getpixel((image.width // 2, image.height // 2))
                if pixel == COLOR:
                    break
            time.sleep(.3)
        else:
            raise RuntimeError("Expected Photos to display the fixture PNG")
        result = smoke.shell("sha256sum", path).strip().split()
        require(result == [digest, path], "Owned completed file did not match fixture hash")
        smoke.log(f"PASS fixture PNG snackbar Open: Photos displayed expected centre pixel; SHA-256 {digest}.")
    finally:
        import sys
        pending = sys.exc_info()[0] is not None
        gate.set()
        # Only remove the exact nonce path after matching content; never remove a mismatch.
        def remove_owned():
            exists = smoke.shell(f"test -e {path} && echo PRESENT || echo ABSENT").strip()
            if exists == "ABSENT":
                return
            result = smoke.shell("sha256sum", path).strip().split()
            require(result == [digest, path], "Fixture path exists with unexpected contents; retained")
            smoke.shell("rm", "-f", path)
            require(smoke.shell(f"test -e {path} && echo PRESENT || echo ABSENT").strip() == "ABSENT",
                    "Fixture path still exists")
        actions = []
        if server_started:
            actions.append(("fixture shutdown", server.shutdown))
        if server is not None:
            actions.append(("fixture close", server.server_close))
        if server_started and thread is not None:
            actions.append(("fixture thread", lambda: thread.join(timeout=5)))
        if validated:
            actions.append(("temporary UI dump", lambda: smoke.shell("rm", "-f", smoke.xml_path)))
        if runner.installed:
            def uninstall():
                smoke.adb("uninstall", PACKAGE)
                require(f"package:{PACKAGE}" not in smoke.shell("pm", "list", "packages", PACKAGE), "Probe remains installed")
                runner.installed = False
            actions.append(("probe uninstall", uninstall))
        if destination_owned:
            def clean_output():
                require(not runner.installed, "Probe may still write the owned fixture; retained")
                remove_owned()
                if not request_done.is_set():
                    smoke.log("An unfinished shared part, if present, was left for exact-path verification.")
            actions.append(("owned file", clean_output))
        if reverse:
            actions.append(("owned reverse", lambda: smoke.adb("reverse", "--remove", reverse)))
        for label, action in actions:
            try: action()
            except Exception:
                cleanup_errors.append(label)
                smoke.log("Cleanup failed: " + label)
        if cleanup_errors and not pending:
            raise RuntimeError("A3 cleanup incomplete: " + ", ".join(cleanup_errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    run(parser.parse_args())


if __name__ == "__main__":
    main()
