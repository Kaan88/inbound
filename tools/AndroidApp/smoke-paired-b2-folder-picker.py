"""Opt-in B2 picker smoke on inbound33. No real credentials or production host."""

import argparse
import hashlib
import importlib.machinery
import json
import socket
import threading
import time
import xml.etree.ElementTree as ET
from http.server import ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).parent
PACKAGE = "app.inbound.android.rcloneprobe"
ACTIVITY = PACKAGE + "/app.inbound.android.MainActivity"
AVD = "inbound33"
API = "33"
HOST_NAME = "Parity Host"
CHILD = "B2-selected-child"
FILENAME = "a.zip"


def require(ok, reason):
    if not ok:
        raise RuntimeError(reason)


def load(name, path):
    return importlib.machinery.SourceFileLoader(name, str(path)).load_module()


class PairedB2:
    def __init__(self, args):
        self.args = args
        self.smoke = load("b2_smoke_base", HERE / "smoke-remote-files-ui.py").Smoke(args)
        self.evidence = self.smoke.evidence
        self.pairing_fields_may_be_visible = False
        self.cleaned = False
        self.fixture = self.server = None
        self.reverse_ports = []

    def adb(self, *args):
        return self.smoke.adb(*args)

    def shell(self, *args):
        return self.smoke.shell(*args)

    def tree(self):
        return self.smoke.tree()

    def node(self, label, resource=None, timeout=30):
        return self.smoke.wait_node(label, timeout=timeout, resource=resource)[0]

    def tap(self, label, resource=None):
        self.smoke.tap(self.node(label, resource))

    def tap_clickable_label(self, label, attempts=12, interval=0.4):
        for _ in range(attempts):
            tree = self.tree()
            found = [node for node in self.smoke.matches(tree, label)
                     if node.get("clickable") == "true" and node.get("enabled") != "false"]
            require(len(found) <= 1, f"Ambiguous clickable control {label!r}")
            if found:
                self.smoke.tap(found[0])
                return
            time.sleep(interval)
        raise RuntimeError(f"Clickable control {label!r} did not appear")

    def enter(self, resource, value, secret=False):
        nodes = [n for n in self.tree().iter("node")
                 if n.get("resource-id", "").endswith(":id/" + resource)]
        require(len(nodes) == 1, "Pairing form field unavailable")
        self.smoke.tap(nodes[0])
        try:
            self.shell("input", "text", value.replace(" ", "%s"))
        except Exception:
            if secret:
                raise RuntimeError("Could not enter disposable pairing value") from None
            raise

    def preflight(self):
        self.smoke.preflight(include_remote_files_payload=not getattr(self.args, "verify_a5_folder", False))
        require(self.adb("emu", "avd", "name").splitlines()[0] == AVD,
                "Refusing anything other than inbound33")
        require(self.shell("getprop", "ro.build.version.sdk").strip() == API,
                "Refusing anything other than API 33")
        require(Path(self.args.server_dll).name.lower() == "downloadmanager.server.dll"
                and Path(self.args.server_dll).is_file(),
                "Supply the explicit built DownloadManager.Server.dll")

    def start_browser_fixture(self):
        link = load("b2_link_fixture", HERE / "link-fixture" / "server.py")
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            port = s.getsockname()[1]
        self.port = port
        self.server = ThreadingHTTPServer(("127.0.0.1", port), link.Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.add_reverse(port)
        self.browser_url = f"http://127.0.0.1:{port}/page"

    def add_reverse(self, port):
        endpoint = f"tcp:{port}"
        require(not any(endpoint in line.split() for line in self.adb("reverse", "--list").splitlines()),
                "Disposable fixture port already has a reverse mapping")
        self.adb("reverse", "--no-rebind", endpoint, endpoint)
        self.reverse_ports.append(port)

    def pair(self, fixture):
        from urllib.parse import urlsplit
        port = urlsplit(fixture["base_url"]).port
        self.add_reverse(port)
        self.pairing_fields_may_be_visible = True
        chip, _ = self.smoke.wait_content_description(
            lambda value: value == "Downloads shown from This phone. Change host",
            "This phone host chip")
        self.smoke.tap(chip)
        self.tap("Connect to another Inbound", "host_connect")
        self.node("Connect", "pair_btn")
        self.enter("pair_address", fixture["base_url"])
        self.shell("input", "keyevent", "4")
        self.enter("pair_password", fixture["password"], secret=True)
        self.shell("input", "keyevent", "4")
        button = self.node("Connect", "pair_btn")
        require(button.get("enabled") == "true", "Disposable fixture pairing did not enable Connect")
        self.smoke.tap(button)
        self.node("Files are saved on " + HOST_NAME, timeout=40)
        self.pairing_fields_may_be_visible = False
        # The Server's own disposable root contains precisely two picker children.
        self.child_paths = []
        for name in ("B2-decoy-child", CHILD):
            path = Path(fixture["download_root"]) / name
            path.mkdir()
            self.child_paths.append(path)

    def verify_a5_folder(self, paired, fixture):
        """Exercise the real New Download destination picker without admitting a job."""
        root = Path(fixture["download_root"]).resolve(strict=True)
        decoy, chosen = self.child_paths
        request = paired.fixture_session(fixture)
        url = f"http://127.0.0.1:{self.port}/files/{FILENAME}"

        def no_mutation():
            state = request("jobs.snapshot", {})
            require(state.get("type") == "jobs.snapshot" and (state.get("payload") or {}).get("jobs") == [],
                    "The paired Host admitted a job while browsing destinations")
            require(set(root.iterdir()) == set(self.child_paths)
                    and all(not any(path.iterdir()) for path in self.child_paths),
                    "Browsing destinations changed the disposable Host folder tree")

        def one(tree, resource):
            matches = [node for node in tree.iter("node") if node.get("package") == PACKAGE
                       and node.get("resource-id", "").endswith(":id/" + resource)]
            require(len(matches) == 1, "Expected a unique New Download field: " + resource)
            return matches[0]

        def picker_path(folder):
            _, tree = self.smoke.wait_content_description(lambda d: d == "Folder " + str(folder), "exact displayed Host folder")
            return tree

        def draft(folder):
            expected = "Save to " + str(folder) + " on " + HOST_NAME
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                tree = self.tree()
                if not any(self.smoke.matches(tree, label) for label in ("Save to, on " + HOST_NAME, "Save here")):
                    if (self.smoke.matches(tree, "New download")
                            and one(tree, "address").get("text") == url
                            and one(tree, "file_name").get("text") == FILENAME
                            and one(tree, "save_to").get("content-desc") == expected):
                        return tree
                time.sleep(.35)
            raise RuntimeError("New Download did not return with the expected unchanged fields and destination")

        def evidence(key, tree):
            require(not self.pairing_fields_may_be_visible
                    and all(node.get("password") != "true" for node in tree.iter("node")),
                    "Refusing to save a credentials form")
            (self.evidence / (key + ".png")).write_bytes(self.smoke.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)

        no_mutation()
        self.tap("Add", "fab")
        self.tap("Link or several links")
        self.node("New download")
        self.smoke.tap(one(self.tree(), "address"))
        self.shell("input", "text", url)
        self.shell("input", "keyevent", "KEYCODE_BACK")
        self.node(FILENAME, "file_name")
        draft(root)

        choose = "Choose a folder on " + HOST_NAME
        opener, _ = self.smoke.wait_content_description(lambda d: d == choose, "native remote folder icon")
        require(opener.get("clickable") == "true" and opener.get("enabled") == "true",
                "Folder icon was not actionable")
        self.smoke.tap(opener)
        picker_path(root)
        self.tap_clickable_label(decoy.name)
        evidence("a5-paired-picker-cancel-choice", picker_path(decoy))
        self.tap_clickable_label("Cancel")
        evidence("a5-paired-picker-cancel-return", draft(root))
        no_mutation()

        field = one(draft(root), "save_to")
        require(field.get("clickable") == "true" and field.get("enabled") == "true",
                "The A5 destination field was not actionable")
        self.smoke.tap(field)
        picker_path(root)
        self.tap_clickable_label(decoy.name)
        picker_path(decoy)
        up, _ = self.smoke.wait_content_description(lambda d: d == "Up one folder", "native parent-folder action")
        require(up.get("clickable") == "true" and up.get("enabled") == "true",
                "The parent-folder action was not enabled")
        self.smoke.tap(up)
        evidence("a5-paired-picker-parent", picker_path(root))
        self.tap_clickable_label(chosen.name)
        evidence("a5-paired-picker-chosen", picker_path(chosen))
        self.tap_clickable_label("Save here")
        evidence("a5-paired-picker-draft-selected", draft(chosen))
        no_mutation()
        self.tap("Cancel", "new_cancel")
        self.node("Files are saved on " + HOST_NAME)
        tree = self.tree()
        require(not any(node.get("resource-id", "").endswith((":id/new_download_btn", ":id/save_to"))
                        for node in tree.iter("node")) and not self.smoke.matches(tree, "New download"),
                "Cancel left the New Download draft open")
        no_mutation()
        evidence("a5-paired-picker-form-dismissed", tree)
        (self.evidence / "a5-paired-picker-proof.json").write_text(json.dumps({
            "initial_destination": "host root", "cancelled_child": decoy.name, "saved_draft_child": chosen.name,
            "address_and_name_preserved": True, "form_dismissed": True,
            "host_jobs": 0, "host_created_files": 0,
        }, indent=2) + "\n", encoding="utf-8")
        self.smoke.log("PASS A5: paired folder Cancel retained the original destination; Save here updated the draft. Form Cancel left zero jobs.")

    def run(self):
        self.preflight()
        paired = load("b2_pair_fixture", HERE / "paired-host-parity-fixture.py")
        with paired.running_fixture(self.args.server_dll, self.args.dotnet) as fixture:
            try:
                self.start_browser_fixture()
                self.adb("install", str(Path(self.args.apk).resolve()))
                self.smoke.installed = True
                self.adb("shell", "am", "start", "-W", "-n", ACTIVITY)
                self.smoke.started = True
                self.tap("Start downloading", "fr_start")
                self.pair(fixture)
                if getattr(self.args, "verify_a5_folder", False):
                    self.verify_a5_folder(paired, fixture)
                    return self.evidence
                self.tap("Add", "fab")
                self.tap("More download sources", "entry_browser")
                self.tap("Browse and catch a download")
                self.smoke.enter("https://example.com", self.browser_url)
                self.tap("Browser options")
                self.tap("Go")
                self.tap_clickable_label("Archive")
                self.node(FILENAME, "caught_name", timeout=20)
                change = self.node("Change", "caught_change_destination", timeout=5)
                self.smoke.tap(change)
                self.tap_clickable_label(CHILD)
                self.smoke.wait_content_description(
                    lambda value: value.startswith("Folder ") and value.endswith(CHILD),
                    "rendered selected child")
                self.tap_clickable_label("Save here")
                # The selected destination is visibly rendered in the caught sheet.
                destination = next((n for n in self.tree().iter("node")
                                    if n.get("resource-id", "").endswith(":id/caught_destination")), None)
                require(destination is not None and CHILD in destination.get("text", ""),
                        "Picker selection was not reflected as the displayed destination")
                (self.evidence / "displayed-destination.txt").write_text(
                    destination.get("text", "") + "\n", encoding="utf-8")
                self.tap("Download", "caught_submit")
                target = self.child_paths[1] / FILENAME
                deadline = time.monotonic() + 45
                expected = hashlib.sha256(load("b2_payload_fixture", HERE / "link-fixture" / "server.py").PAYLOAD).hexdigest()
                while time.monotonic() < deadline:
                    if target.is_file() and hashlib.sha256(target.read_bytes()).hexdigest() == expected:
                        break
                    time.sleep(.25)
                require(target.is_file() and hashlib.sha256(target.read_bytes()).hexdigest() == expected,
                        "No hash-matched file appeared in the selected Host child folder")
                require(not any(self.child_paths[0].iterdir()),
                        "The unselected decoy child was not empty")
                (self.evidence / "used-destination.txt").write_text(str(target.parent) + "\n", encoding="utf-8")
                (self.evidence / "payload.sha256").write_text(expected + "\n", encoding="ascii")
                self.smoke.log("PASS B2 paired folder picker: selected visible child, displayed destination and Host file hash match.")
                # Delete only the exact payload after rechecking its fixture hash.
                require(hashlib.sha256(target.read_bytes()).hexdigest() == expected,
                        "Cleanup refused: selected-child file hash changed")
                target.unlink()
            finally:
                self.cleanup_device()
        return self.evidence

    def cleanup_device(self):
        if self.cleaned:
            return
        self.cleaned = True
        errors = []
        try:
            if self.smoke.installed:
                self.adb("uninstall", PACKAGE)
                require(f"package:{PACKAGE}" not in self.shell("pm", "list", "packages", PACKAGE),
                        "The isolated probe remains installed")
        except Exception:
            errors.append("probe uninstall failed")
        for port in reversed(self.reverse_ports):
            try:
                self.adb("reverse", "--remove", f"tcp:{port}")
            except Exception:
                errors.append("owned reverse cleanup failed")
        if getattr(self.smoke, "validated_emulator", False):
            try:
                self.shell("rm", "-f", self.smoke.xml_path)
            except Exception:
                errors.append("owned UI dump cleanup failed")
        if self.server:
            self.server.shutdown()
            self.server.server_close()
            self.thread.join(timeout=3)
        # Never persist hierarchy/logcat while pairing fields may contain data.
        if errors:
            raise RuntimeError("; ".join(errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit isolated probe APK")
    parser.add_argument("--server-dll", required=True, help="Explicit built DownloadManager.Server.dll")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--verify-a5-folder", action="store_true",
                        help="Verify the paired New Download picker selection and Cancel without submitting")
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    run = PairedB2(args)
    print(f"Evidence: {run.run()}", flush=True)


if __name__ == "__main__":
    main()
