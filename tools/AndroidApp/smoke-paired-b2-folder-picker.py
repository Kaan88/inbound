"""Opt-in B2 picker smoke on inbound33. No real credentials or production host."""

import argparse
from collections import Counter
import hashlib
import importlib.machinery
import json
import re
import socket
import threading
import time
import uuid
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
        a5 = getattr(self.args, "verify_a5_folder", False) or getattr(self.args, "verify_a5_collision", False) or getattr(self.args, "verify_a5_advanced", False) or getattr(self.args, "verify_a5_multi_sha", False)
        self.smoke.preflight(include_remote_files_payload=not a5)
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
        self.fixture_requests = Counter()
        self.fixture_request_lock = threading.Lock()
        self.advanced_referers = Counter()
        requests = self.fixture_requests
        referers = self.advanced_referers
        lock = self.fixture_request_lock

        class CountedHandler(link.Handler):
            def do_GET(self):
                with lock:
                    requests[self.path] += 1
                    if self.path.startswith("/files/a.zip?advanced="):
                        expected = f"http://127.0.0.1:{port}/synthetic-source"
                        referers[self.path, self.headers.get("Referer") == expected] += 1
                super().do_GET()

        self.server = ThreadingHTTPServer(("127.0.0.1", port), CountedHandler)
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

    def verify_a5_collision(self, paired, fixture):
        """Real A5 policies, using only explicitly owned disposable Host files."""
        owned_root = Path(fixture["download_root"]).resolve(strict=True)
        require(owned_root.is_dir() and set(owned_root.iterdir()) == set(self.child_paths),
                "Unexpected disposable root contents")
        decoy, root = self.child_paths
        require(root.resolve(strict=True).parent == owned_root and not any(root.iterdir()),
                "Expected a fresh direct child of the owned Host root")
        request = paired.fixture_session(fixture)
        payload = load("a5_collision_payload", HERE / "link-fixture" / "server.py").PAYLOAD
        payload_hash = hashlib.sha256(payload).hexdigest()
        nonce = uuid.uuid4().hex[:12]
        sentinel = ("Owned A5 sentinel " + nonce).encode("ascii")
        sentinel_hash = hashlib.sha256(sentinel).hexdigest()
        names = {case: f"a5-{case}-{nonce}.zip" for case in ("rename", "overwrite", "fail")}
        paths = {case: root / name for case, name in names.items()}
        for path in paths.values():
            require(not path.exists() and not path.is_symlink(), "Collision fixture path already exists")
            with path.open("xb") as stream:
                stream.write(sentinel)
        require(all(p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest() == sentinel_hash for p in paths.values()),
                "Sentinel setup failed")
        baseline = request("jobs.snapshot", {})
        require(baseline.get("type") == "jobs.snapshot" and (baseline.get("payload") or {}).get("jobs") == [],
                "Disposable Host must start with no jobs")

        def one(resource):
            nodes = [n for n in self.tree().iter("node") if n.get("package") == PACKAGE and
                     n.get("resource-id", "").endswith(":id/" + resource)]
            require(len(nodes) == 1, "Expected unique control " + resource)
            return nodes[0]
        def show(resource):
            for _ in range(7):
                tree = self.tree()
                nodes = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/" + resource)]
                if len(nodes) == 1: return nodes[0], tree
                scroll = one("new_scroll")
                match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", scroll.get("bounds", ""))
                require(match, "New download scroll bounds unavailable")
                l,t,r,b = map(int, match.groups()); x = (l+r)//2
                self.shell("input", "swipe", str(x), str(t+(b-t)*4//5), str(x), str(t+(b-t)//3), "350")
            raise RuntimeError("New download control not visible: " + resource)
        def capture(key, tree):
            require(not self.pairing_fields_may_be_visible and not any(n.get("password") == "true" for n in tree.iter("node")),
                    "Refusing to retain a credentials form")
            (self.evidence / (key + ".png")).write_bytes(self.smoke.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)
        def jobs():
            reply = request("jobs.snapshot", {})
            rows = (reply.get("payload") or {}).get("jobs")
            require(reply.get("type") == "jobs.snapshot" and isinstance(rows, list), "Host jobs unavailable")
            return rows

        def get_count(url):
            from urllib.parse import urlsplit
            parts = urlsplit(url)
            with self.fixture_request_lock:
                return self.fixture_requests[parts.path + "?" + parts.query]
        observed = {}
        for case in ("fail", "rename", "overwrite"):
            name = names[case]
            url = f"http://127.0.0.1:{self.port}/files/a.zip?case={case}-{nonce}"
            self.tap("Add", "fab"); self.tap("Link or several links"); self.node("New download")
            self.smoke.tap(one("address")); self.shell("input", "text", url)
            self.shell("input", "keyevent", "KEYCODE_BACK")
            self.node(FILENAME, "file_name", timeout=35)
            field = one("file_name"); old = field.get("text", "")
            self.smoke.tap(field)
            self.shell("input", "keyevent", "KEYCODE_MOVE_END")
            if old: self.shell("input", "keyevent", *(["KEYCODE_DEL"] * len(old)))
            self.shell("input", "text", name); self.shell("input", "keyevent", "KEYCODE_BACK")
            self.node(name, "file_name")
            path_control, _ = self.smoke.wait_content_description(
                lambda d: d == "Choose a folder on " + HOST_NAME, "New Download folder action")
            self.smoke.tap(path_control)
            selector, _ = self.smoke.wait_content_description(
                lambda d: d in {"Folder " + str(owned_root), "Folder " + str(root)}, "owned picker folder")
            if selector.get("content-desc") != "Folder " + str(root):
                self.tap_clickable_label(root.name)
                self.smoke.wait_content_description(
                    lambda d: d == "Folder " + str(root), "selected owned Host destination")
            self.tap_clickable_label("Save here")
            _, selected = self.smoke.wait_content_description(
                lambda d: d == "Save to " + str(root) + " on " + HOST_NAME, "exact draft save path")
            capture("a5-collision-" + case + "-populated", selected)
            header, _ = show("more_header")
            self.smoke.tap(header)
            chosen, _ = show("exists_" + case)
            tree = self.tree()
            default = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/exists_rename")]
            require(len(default) == 1 and default[0].get("checked") == "true", "Rename is not selected by default")
            if case != "rename": self.smoke.tap(chosen)
            tree = self.tree()
            for option in ("rename", "overwrite", "fail"):
                controls = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/exists_" + option)]
                require(len(controls) == 1 and controls[0].get("checked") == ("true" if option == case else "false"),
                        "Collision choice did not retain exclusive selection")
            capture("a5-collision-" + case + "-form", tree)
            before_gets = get_count(url)
            self.tap("Download", "new_download_btn")
            if case == "fail":
                expected = "A file with this name already exists. Choose Rename or Overwrite, or change the file name."
                self.node(expected, "new_submit_error")
                tree = self.tree()
                controls = {n.get("resource-id", "").rsplit(":id/", 1)[-1]: n for n in tree.iter("node")}
                error = controls.get("new_submit_error")
                require(error is not None and error.get("text") == expected, "Missing inline collision explanation")
                error_top, error_bottom = (int(y) for y in re.findall(r",(\d+)\]", error.get("bounds", "")))
                require(error_top < error_bottom, "Collision explanation has no visible area")
                for action in ("new_cancel", "new_download_btn"):
                    button = controls.get(action)
                    require(button is not None and button.get("enabled") == button.get("clickable") == "true",
                            "Fail disabled or hid a form action")
                    top, bottom = (int(y) for y in re.findall(r",(\d+)\]", button.get("bounds", "")))
                    require(error_bottom <= top < bottom, "Collision explanation overlaps a form action")
                require(not any(j.get("url") == url for j in jobs())
                        and get_count(url) == before_gets
                        and hashlib.sha256(paths[case].read_bytes()).hexdigest() == sentinel_hash,
                        "Fail started a payload request, admitted a job or changed the sentinel")
                capture("a5-collision-fail-refusal", tree)
                rename, _ = show("exists_rename")
                self.smoke.tap(rename)
                corrected = self.tree()
                require(not any(n.get("resource-id", "").endswith(":id/new_submit_error") for n in corrected.iter("node")),
                        "Changing the collision policy left a stale refusal visible")
                capture("a5-collision-fail-policy-corrected", corrected)
                self.tap("Cancel", "new_cancel")
                self.node("Files are saved on " + HOST_NAME)
                observed[case] = {"state": "refused", "payloadGets": 0, "sentinelSha256": sentinel_hash,
                                  "inlineError": expected, "footerVisible": True, "errorClearedOnPolicyChange": True}
                continue

            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                rows = [j for j in jobs() if j.get("url") == url]
                require(len(rows) <= 1, "Fixture admitted duplicate collision jobs")
                if rows and str(rows[0].get("state", "")).lower() in ("completed", "failed"):
                    break
                time.sleep(.35)
            else:
                raise RuntimeError("Collision job did not settle: " + case)
            state = str(rows[0].get("state", "")).lower()
            actual_policy = rows[0].get("conflictPolicy")
            payload_gets = get_count(url) - before_gets
            failure = (rows[0].get("failure") or {}).get("kind")
            require(state == "completed" and actual_policy == case and payload_gets > 0,
                    f"Collision case {case}: state={state}, policy={actual_policy}, "
                    f"payload GETs={payload_gets}, failure kind={failure}")
            target = paths[case]
            actual = hashlib.sha256(target.read_bytes()).hexdigest() if target.is_file() and not target.is_symlink() else None
            require(actual == (payload_hash if case == "overwrite" else sentinel_hash), "Collision changed wrong sentinel: " + case)
            if case == "rename":
                renamed = root / (target.stem + " (1)" + target.suffix)
                require(renamed.is_file() and not renamed.is_symlink() and hashlib.sha256(renamed.read_bytes()).hexdigest() == payload_hash,
                        f"Default Rename did not create its expected numbered output. "
                        f"Host reported {rows[0].get('finalFilePath')!r}; owned child contains {sorted(p.name for p in root.iterdir())!r}")
            expected_path = renamed if case == "rename" else target
            require(rows[0].get("finalFilePath") == str(expected_path), "Host wrote an unexpected destination")
            observed[case] = {"state": state, "sentinelSha256": actual, "jobId": rows[0].get("id"),
                              "payloadGets": get_count(url) - before_gets}
            require(observed[case]["jobId"], "Host did not identify collision job")
        require(len(jobs()) == 2 and len({observed[c]["jobId"] for c in ("rename", "overwrite")}) == 2,
                "Unexpected Host job count or identity")
        require(set(owned_root.iterdir()) == set(self.child_paths)
                and set(root.iterdir()) == (set(paths.values()) | {renamed})
                and not any(decoy.iterdir()), "Unexpected fixture output")
        (self.evidence / "a5-collision-proof.json").write_text(json.dumps({"cases": observed, "payloadSha256": payload_hash,
                    "initialSentinelSha256": sentinel_hash}, indent=2) + "\n", encoding="utf-8")
        self.smoke.log("PASS A5 paired: default Rename, explicit Overwrite and Fail match owned Host files and job outcomes.")
        # The owned Server and its entire private temporary root are disposed by
        # running_fixture only after the Host stops; no shared output is removed.

    def verify_a5_multi_sha(self, paired, fixture):
        """Exercise the checksum guard in the actual A5 form without admitting jobs."""
        request = paired.fixture_session(fixture)
        root = Path(fixture["download_root"]).resolve(strict=True)

        def empty():
            reply = request("jobs.snapshot", {})
            require(reply.get("type") == "jobs.snapshot" and (reply.get("payload") or {}).get("jobs") == [],
                    "A5 validation must leave the Host empty")
            require(set(root.iterdir()) == set(self.child_paths)
                    and all(p.is_dir() and not p.is_symlink() and not any(p.iterdir()) for p in self.child_paths),
                    "A5 validation changed the owned destination inventory")
        def visible(resource):
            for _ in range(7):
                tree = self.tree()
                nodes = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/" + resource)]
                if len(nodes) == 1:
                    return nodes[0]
                scroll = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/new_scroll")]
                require(len(scroll) == 1, "A5 scroll region unavailable")
                m = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", scroll[0].get("bounds", ""))
                require(m is not None, "A5 scroll bounds unavailable")
                l,t,r,b = map(int, m.groups()); x=(l+r)//2
                start, end = ((t+(b-t)//3, t+(b-t)*4//5) if resource == "address" else (t+(b-t)*4//5, t+(b-t)//3))
                self.shell("input", "swipe", str(x), str(start), str(x), str(end), "350")
            raise RuntimeError("A5 field unavailable: " + resource)

        def field(resource, value):
            node = visible(resource)
            old = node.get("text", "")
            self.smoke.tap(node)
            if old:
                self.shell("input", "keyevent", "KEYCODE_MOVE_END")
                self.shell("input", "keyevent", *(["KEYCODE_DEL"] * len(old)))
            if value: self.shell("input", "text", value)
            self.shell("input", "keyevent", "KEYCODE_BACK")
        def save(name):
            tree = self.tree()
            (self.evidence / (name + ".png")).write_bytes(self.smoke.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (name + ".xml"), encoding="utf-8", xml_declaration=True)
        empty()
        self.tap("Add", "fab"); self.tap("Link or several links"); self.node("New download")
        field("address", f"http://127.0.0.1:{self.port}/files/a.zip")
        self.node(FILENAME, "file_name", timeout=35)
        self.smoke.tap(visible("more_header"))
        checksum = "a" * 64
        field("sha", checksum)
        field("address", f"http://127.0.0.1:{self.port}/files/f[01-02].zip")
        self.node("Use one link or clear the SHA-256 checksum.", "many_note")
        require(self.node("Download", "new_download_btn").get("enabled") == "false", "Multi-link SHA did not disable Download")
        save("a5-multi-sha-blocked")
        empty()
        # Collapse More options; the many-link note must still explain the blocker.
        self.smoke.tap(visible("more_header"))
        self.node("Use one link or clear the SHA-256 checksum.", "many_note")
        collapsed = self.tree()
        require(not any(n.get("resource-id", "").endswith((":id/more_body", ":id/sha"))
                        for n in collapsed.iter("node")), "More options did not collapse")
        require(self.node("Download", "new_download_btn").get("enabled") == "false",
                "Collapsing More options enabled a multi-link SHA submission")
        save("a5-multi-sha-collapsed")
        field("address", f"http://127.0.0.1:{self.port}/files/a.zip")
        self.node(FILENAME, "file_name", timeout=35)
        require(not any(n.get("text") == "Use one link or clear the SHA-256 checksum." for n in self.tree().iter("node")),
                "Single-link transition did not clear the blocker")
        self.smoke.tap(visible("more_header"))
        sha = visible("sha")
        require(sha.get("text") == checksum, "Returning to one link did not preserve the checksum")
        require(self.node("Download", "new_download_btn").get("enabled") == "true",
                "Returning to one link did not re-enable Download")
        save("a5-multi-sha-recovered")
        self.tap("Cancel", "new_cancel")
        empty()
        self.smoke.log("PASS A5: multi-link checksum rejected with no job or output; one-link return cleared the blocker.")

    def verify_a5_advanced(self, paired, fixture):
        """Two owned paired Host jobs with real referrer and SHA verification."""
        from urllib.parse import urlsplit
        payload = load("a5_advanced_payload", HERE / "link-fixture" / "server.py").PAYLOAD
        digest = hashlib.sha256(payload).hexdigest()
        incorrect = ("0" if digest[0] != "0" else "1") + digest[1:]
        nonce = uuid.uuid4().hex[:12]
        root = Path(fixture["download_root"]).resolve(strict=True)
        decoy, destination = self.child_paths
        require(set(root.iterdir()) == set(self.child_paths) and not any(decoy.iterdir())
                and not any(destination.iterdir()), "Expected two empty owned Host folders")
        request = paired.fixture_session(fixture)
        def jobs():
            reply = request("jobs.snapshot", {})
            rows = (reply.get("payload") or {}).get("jobs")
            require(reply.get("type") == "jobs.snapshot" and isinstance(rows, list), "Host snapshot unavailable")
            return rows
        require(jobs() == [], "Disposable Host must have no jobs")

        def one(resource):
            found = [n for n in self.tree().iter("node") if n.get("package") == PACKAGE
                     and n.get("resource-id", "").endswith(":id/" + resource)]
            require(len(found) == 1, "Expected unique field " + resource)
            return found[0]
        def show(resource):
            for _ in range(7):
                nodes = [n for n in self.tree().iter("node") if n.get("resource-id", "").endswith(":id/" + resource)]
                if len(nodes) == 1: return nodes[0]
                scroll = one("new_scroll")
                match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", scroll.get("bounds", ""))
                require(match is not None, "New download scroll bounds unavailable")
                l, t, r, b = map(int, match.groups()); x = (l + r) // 2
                self.shell("input", "swipe", str(x), str(t + (b-t)*4//5), str(x), str(t + (b-t)//3), "350")
            raise RuntimeError("New download control not visible: " + resource)
        def set_field(resource, value):
            node = show(resource); old = node.get("text", "")
            self.smoke.tap(node)
            if old:
                self.shell("input", "keyevent", "KEYCODE_MOVE_END")
                self.shell("input", "keyevent", *(["KEYCODE_DEL"] * len(old)))
            self.shell("input", "text", value.replace(" ", "%s"))
            self.shell("input", "keyevent", "KEYCODE_BACK")
            require(show(resource).get("text") == value, "Advanced fixture field did not round-trip: " + resource)
        def capture(name):
            tree = self.tree()
            require(not self.pairing_fields_may_be_visible and not any(n.get("password") == "true" for n in tree.iter("node")),
                    "Refusing capture of credentials")
            (self.evidence / (name + ".png")).write_bytes(self.smoke.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / (name + ".xml"), encoding="utf-8", xml_declaration=True)
        def counts(path):
            with self.fixture_request_lock:
                return (self.advanced_referers[path, True], self.advanced_referers[path, False])

        results = {}
        expected_outputs = set()
        for case, expected in (("valid", digest), ("mismatch", incorrect)):
            name = f"a5-advanced-{case}-{nonce}.zip"
            target = destination / name
            require(not target.exists() and not target.is_symlink(), "Owned target already exists")
            path = f"/files/a.zip?advanced={case}-{nonce}"
            url = f"http://127.0.0.1:{self.port}{path}"
            description = "Synthetic A5 " + case
            self.tap("Add", "fab"); self.tap("Link or several links"); self.node("New download")
            set_field("address", url)
            self.node(FILENAME, "file_name", timeout=35)
            set_field("file_name", name)
            picker, _ = self.smoke.wait_content_description(lambda d: d == "Choose a folder on " + HOST_NAME, "Host folder picker")
            self.smoke.tap(picker)
            selector, _ = self.smoke.wait_content_description(lambda d: d in {"Folder " + str(root), "Folder " + str(destination)}, "owned folder")
            if selector.get("content-desc") != "Folder " + str(destination):
                self.tap_clickable_label(destination.name)
                self.smoke.wait_content_description(lambda d: d == "Folder " + str(destination), "owned destination")
            self.tap_clickable_label("Save here")
            self.smoke.wait_content_description(lambda d: d == "Save to " + str(destination) + " on " + HOST_NAME, "draft destination")
            self.smoke.tap(show("more_header"))
            set_field("referrer", f"http://127.0.0.1:{self.port}/synthetic-source")
            set_field("description", description)
            if case == "valid":
                set_field("sha", "abc")
                self.node("A SHA-256 checksum is 64 hexadecimal characters.")
                require(self.node("Download", "new_download_btn").get("enabled") == "false", "Invalid SHA did not disable Download")
                capture("a5-advanced-malformed")
            set_field("sha", expected)
            require(self.node("Download", "new_download_btn").get("enabled") == "true", "Valid SHA did not enable Download")
            capture("a5-advanced-" + case + "-form")
            before = counts(path)
            self.tap("Download", "new_download_btn")
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                rows = [j for j in jobs() if j.get("url") == url and j.get("fileName") == name]
                require(len(rows) <= 1, "Duplicate owned job")
                if rows and str(rows[0].get("state", "")).lower() in ("completed", "failed"): break
                time.sleep(.35)
            else: raise RuntimeError("Advanced fixture job did not settle: " + case)
            job = rows[0]; state = str(job.get("state", "")).lower()
            after = counts(path)
            require(job.get("id") and job.get("description") == description and job.get("expectedSha256", "").lower() == expected
                    and job.get("receivedBytes") == len(payload), "Host job did not retain advanced values and bytes")
            require(after[0] > before[0] and after[1] == before[1], "Transfer did not use the exact synthetic referrer")
            outcome = {"state": state, "bytes": job.get("receivedBytes"), "jobId": job.get("id"),
                       "matchingReferrerGets": after[0] - before[0], "expectedSha256": expected}
            require(job.get("finalFilePath") == str(target), "Unexpected destination for the advanced fixture job")
            if case == "valid":
                require(state == "completed" and target.is_file() and not target.is_symlink()
                        and hashlib.sha256(target.read_bytes()).hexdigest() == digest, "Valid checksum did not finish with matching bytes")
                expected_outputs.add(target)
            else:
                require(state == "failed" and (job.get("failure") or {}).get("kind", "").lower() == "verificationfailed"
                        and not target.exists() and not target.is_symlink(), "Mismatch did not fail without final output")
                partial = destination / f"{name}.{uuid.UUID(job['id']).hex}.part"
                require(partial.is_file() and not partial.is_symlink() and partial.stat().st_size == len(payload)
                        and hashlib.sha256(partial.read_bytes()).hexdigest() == digest, "Mismatch did not preserve the owned partial")
                expected_outputs.add(partial)
                outcome["retainedPartialSha256"] = digest
                self.smoke.wait_content_description(lambda d: d.startswith(name + ". ")
                    and "checksum did not match" in d.lower(), "checksum failure row", timeout=35)
                capture("a5-advanced-mismatch-row")
            results[case] = outcome
        require(len(jobs()) == 2 and results["valid"]["jobId"] != results["mismatch"]["jobId"]
                and not any(decoy.iterdir()) and set(destination.iterdir()) == expected_outputs,
                "Unexpected Host output or job identities")
        (self.evidence / "a5-advanced-proof.json").write_text(json.dumps({"cases": results, "payloadSha256": digest}, indent=2) + "\n", encoding="utf-8")
        self.smoke.log("PASS A5 advanced: synthetic referrer, descriptions, valid checksum and mismatch verified on the owned Host.")
        # Host and its owned temporary data/partial directory are removed only after its writer stops.

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
                if getattr(self.args, "verify_a5_multi_sha", False):
                    self.verify_a5_multi_sha(paired, fixture)
                    return self.evidence
                if getattr(self.args, "verify_a5_advanced", False):
                    self.verify_a5_advanced(paired, fixture)
                    return self.evidence
                if getattr(self.args, "verify_a5_collision", False):
                    self.verify_a5_collision(paired, fixture)
                    return self.evidence
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
        self.smoke.log("Cleanup PASS: removed the isolated probe, owned reverse mappings and browser fixture.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit isolated probe APK")
    parser.add_argument("--server-dll", required=True, help="Explicit built DownloadManager.Server.dll")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--verify-a5-multi-sha", action="store_true", help="Check the multi-link SHA guard without jobs")
    parser.add_argument("--verify-a5-advanced", action="store_true", help="Verify synthetic referrer, description and SHA outcomes")
    parser.add_argument("--verify-a5-collision", action="store_true", help="Exercise Rename, Overwrite and Fail on owned paired Host files")
    parser.add_argument("--verify-a5-folder", action="store_true",
                        help="Verify the paired New Download picker selection and Cancel without submitting")
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    if args.verify_a5_multi_sha and (args.verify_a5_advanced or args.verify_a5_collision or args.verify_a5_folder):
        parser.error("--verify-a5-multi-sha is a standalone lane")
    if args.verify_a5_advanced and (args.verify_a5_collision or args.verify_a5_folder):
        parser.error("--verify-a5-advanced is a standalone lane")
    if args.verify_a5_collision and args.verify_a5_folder:
        parser.error("--verify-a5-collision is separate from --verify-a5-folder")
    run = PairedB2(args)
    print(f"Evidence: {run.run()}", flush=True)


if __name__ == "__main__":
    main()
