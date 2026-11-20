"""Guided B1-B6 Android link-intake captures using the disposable link fixture."""

import argparse
import hashlib
import subprocess
import sys
import importlib.machinery
import uuid
from http.server import ThreadingHTTPServer
import re
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).parent
MOCKUPS = ROOT / "docs" / "plans" / "android-mockups"
FIXTURE = HERE / "link-fixture" / "server.py"
SMOKE = HERE / "smoke-remote-files-ui.py"
PACKAGE = "app.inbound.android.rcloneprobe"
ACTIVITY = PACKAGE + "/app.inbound.android.MainActivity"
AVD = "inbound33"
API = "33"

# The actual Android chooser is B1. Its post-share decision is additional
# functional evidence; the distinct caught-download confirmation is B2.
CAPTURES = (
    ("b1-chooser", "b1-share.png", "http://127.0.0.1:PORT/"),
    ("b1-caught", None, "Shared link"),
    ("b2", "b2-caught.png", "Download with Inbound"),
    ("b3", "b3-signed-in.png", "Cookies stay on this phone"),
    ("b4", "b4-all-links.png", "Links on page"),
    ("b6", "b6-expired-link.png", "This link has expired"),
)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def load_module(name, path):
    return importlib.machinery.SourceFileLoader(name, str(path)).load_module()


class Capture:
    def __init__(self, args):
        self.args = args
        self.smoke_module = load_module("link_intake_existing_smoke", SMOKE)
        self.smoke = self.smoke_module.Smoke(args)
        self.evidence = self.smoke.evidence
        self.fixture = None
        self.fixture_thread = None
        self.reversed = False

    def adb(self, *args, raw=False):
        return self.smoke.adb(*args, raw=raw)

    def shell(self, *args):
        return self.smoke.shell(*args)

    def log(self, text):
        self.smoke.log(text)

    def preflight(self):
        self.smoke.preflight(include_remote_files_payload=False)
        require(self.adb("emu", "avd", "name").splitlines()[0] == AVD,
                f"Refusing anything other than {AVD}")
        require(self.shell("getprop", "ro.build.version.sdk").strip() == API,
                f"Refusing anything other than API {API}")
        baseline = self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f")
        (self.evidence / "device-baseline-count.txt").write_text(str(len(baseline.splitlines())) + "\n", encoding="ascii")
        module = load_module("link_intake_fixture", FIXTURE)
        self.fixture_module = module
        with module.PARTIAL_STATE["lock"]:
            module.PARTIAL_STATE["expired"] = False
            module.PARTIAL_STATE["ranges"].clear()
        if self.args.verify_b3_completion:
            self.b3_target = "/sdcard/Download/report-q3-2026.xlsx"
            require(self.shell(f"if [ -e '{self.b3_target}' ] || [ -L '{self.b3_target}' ]; then echo present; fi").strip() != "present",
                    "B3 exact output already exists; refusing to replace it")
            with module.REPORT_STATE["lock"]:
                module.REPORT_STATE["authenticated_gets"] = 0
            self.b3_hash = hashlib.sha256((FIXTURE.parent / "report-q3-2026.xlsx").read_bytes()).hexdigest()
        self.fixture_hash = hashlib.sha256(module.PAYLOAD).hexdigest()
        (self.evidence / "fixture.txt").write_text(
            f"Loopback fixture only; payload bytes={len(module.PAYLOAD)}; "
            f"SHA-256={self.fixture_hash}\n"
            "Routes: /page, /signin, /files/a.zip, /files/b.pdf, /files/c.jpg, "
            "/auth.zip, /expired.zip?token=gone or token=new\n",
            encoding="ascii")

    def start_fixture(self):
        module = self.fixture_module

        class QuietHandler(module.Handler):
            pass

        self.fixture = ThreadingHTTPServer(("127.0.0.1", 0), QuietHandler)
        self.port = self.fixture.server_port
        self.fixture_thread = threading.Thread(target=self.fixture.serve_forever, daemon=True)
        self.fixture_thread.start()
        self.adb("reverse", "--no-rebind", f"tcp:{self.port}", f"tcp:{self.port}")
        self.reversed = True
        self.log(f"Disposable link fixture listening only on loopback; emulator URL uses port {self.port}.")

    def launch(self):
        self.adb("install", str(Path(self.args.apk).resolve()))
        self.smoke.installed = True
        self.adb("shell", "am", "start", "-W", "-n", ACTIVITY)
        self.smoke.started = True
        self.log("Probe launched normally. Open Chrome manually for B1; do not synthesize a share intent.")

    def capture(self, key, reference, anchor, *, prompt=True):
        anchor = anchor.replace("PORT", str(self.port))
        instruction = self.args.instructions[key].replace("PORT", str(self.port))
        self.log(f"Checkpoint {key}: {instruction}")
        if prompt:
            input(f"\n{instruction}\nPress Enter to capture on inbound33 (Ctrl+C aborts): ")
        deadline = time.monotonic() + 30
        tree = None
        while time.monotonic() < deadline:
            tree = self.smoke.tree()
            text = " ".join(node.get("text", "") + " " + node.get("content-desc", "")
                            for node in tree.iter("node")).casefold()
            if anchor.casefold() in text:
                break
            time.sleep(0.5)
        else:
            raise RuntimeError(f"{key}: expected visible UI anchor {anchor!r}")
        stem = key
        image = self.evidence / f"{stem}-capture.png"
        image.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(tree).write(self.evidence / f"{stem}-ui.xml",
                                   encoding="utf-8", xml_declaration=True)
        helper = load_module("link_intake_pair_helper",
                             HERE / "capture-remote-files-parity.py")
        if reference is None:
            self.log(f"Captured {key}: {image.name}, {stem}-ui.xml; functional evidence only (no reference).")
        else:
            size = helper.make_pair(MOCKUPS / reference, image,
                                    self.evidence / f"{stem}-pair.png", key.upper())
            self.log(f"Captured {key}: {image.name}, {stem}-ui.xml, pair {size[0]}x{size[1]}.")

    def guided_b1_only(self):
        """Operator drives Chrome and the real Android chooser; no share intent is injected."""
        setup, _ = self.smoke.wait_node("Start downloading", resource="fr_start")
        self.smoke.tap(setup)
        self.smoke.wait_node("Search downloads", timeout=30)

        def download_count():
            tree = self.smoke.tree()
            counts = []
            for node in tree.iter("node"):
                match = re.fullmatch(r"All (\d+)", node.get("text", ""))
                if match:
                    require(node.get("checked") == "true", "Downloads All filter is not selected")
                    counts.append(int(match.group(1)))
            require(len(counts) == 1, "Expected exactly one selected All downloads count")
            return counts[0]

        def fixture_outputs():
            # Compare only fixture-specific names; do not serialize unrelated filenames.
            paths = self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()
            return {path.rsplit("/", 1)[-1] for path in paths
                    if re.fullmatch(r"(?:a(?: \(\d+\))?\.zip|page(?: \(\d+\))?(?:\.html)?)", path.rsplit("/", 1)[-1])}

        baseline = download_count()
        outputs = fixture_outputs()
        inventory = None
        if self.args.b1_action != "cancel":
            require(baseline == 0, "Fresh probe must have no jobs before the B1 action")
            self.smoke.wait_content_description(lambda d: d == "Downloads shown from This phone. Change host", "local Host")
            # Keep the shared Download inventory only in memory; do not save unrelated names.
            inventory = set(self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines())
        self.log(f"B1 probe baseline: selected All count {baseline}; fixture-name baseline recorded in memory only.")
        refs = {key: (reference, anchor) for key, reference, anchor in CAPTURES}
        self.capture("b1-chooser", *refs["b1-chooser"])
        chooser = ET.parse(self.evidence / "b1-chooser-ui.xml").getroot()
        system_packages = {"android", "com.android.intentresolver", "com.google.android.intentresolver"}
        origin = f"http://127.0.0.1:{self.port}"
        system_nodes = [node for node in chooser.iter("node") if node.get("package") in system_packages]
        expected_link = origin + ("/page" if self.args.b1_action == "open-page" else "/files/a.zip#section")
        require(any(node.get("resource-id", "").endswith(":id/content_preview_text")
                    and node.get("text") == expected_link for node in system_nodes),
                "Android chooser did not preview an exact URL from the owned fixture")
        require(any("Inbound Probe" in (node.get("text"), node.get("content-desc"))
                    for node in system_nodes), "Android chooser did not expose the distinct Inbound Probe target")
        self.log("B1 chooser verified")
        self.capture("b1-caught", *refs["b1-caught"])
        dialog = ET.parse(self.evidence / "b1-caught-ui.xml").getroot()
        probe_nodes = [node for node in dialog.iter("node") if node.get("package") == PACKAGE]
        labels = {node.get("text") for node in probe_nodes}
        require({"Shared link", "Open page", "Add file", "Cancel"}.issubset(labels)
                and any(node.get("resource-id") == "android:id/message"
                        and node.get("text", "").partition("\n")[0] == "127.0.0.1" for node in probe_nodes),
                "Expected the probe Shared link dialog with the exact fixture host and all three actions")
        self.log("B1 shared dialog verified")
        if self.args.b1_action == "open-page":
            input("\nIn the probe tap Open page; show the owned fixture page, then press Enter: ")
            self.capture("b1-open-page", None, "Inbound link fixture", prompt=False)
            tree = ET.parse(self.evidence / "b1-open-page-ui.xml").getroot()
            nodes = [n for n in tree.iter("node") if n.get("package") == PACKAGE]
            require(sum(n.get("content-desc") == "Close browser" for n in nodes) == 1
                    and sum(n.get("class", "").endswith("EditText") and n.get("text") == origin + "/page" for n in nodes) == 1
                    and any(n.get("text") == "Inbound link fixture" for n in nodes),
                    "Open page did not show the exact owned HTML page in the probe browser")
            self.log("B1 action state verified")
            input("\nClose the probe browser, then press Enter: ")
        elif self.args.b1_action == "add-file":
            input("\nIn the probe tap Add file; show the populated form, then press Enter: ")
            self.capture("b1-add-file", None, "New download", prompt=False)
            tree = ET.parse(self.evidence / "b1-add-file-ui.xml").getroot()
            nodes = [n for n in tree.iter("node") if n.get("package") == PACKAGE]
            addresses = [n for n in nodes if n.get("resource-id", "").endswith(":id/address")]
            require(len(addresses) == 1 and addresses[0].get("text") == origin + "/files/a.zip"
                    and sum(n.get("resource-id", "").endswith(":id/new_cancel") for n in nodes) == 1,
                    "Add file did not show the exact normalized Archive address and Cancel")
            self.log("B1 action state verified")
            input("\nCancel the probe form without downloading, then press Enter: ")
        else:
            input("\nIn the probe tap Cancel, return to its Downloads list, then press Enter: ")
        # Downloads can remain in the hierarchy under a page or dialog. Require
        # one fresh tree to prove the exact foreground and the absence of overlays.
        deadline = time.monotonic() + 30
        final_tree = None
        while time.monotonic() < deadline:
            focus = next((line for line in self.shell("dumpsys", "window").splitlines()
                          if "mCurrentFocus=" in line), "")
            if not re.search(r"\b" + re.escape(PACKAGE) + r"/", focus):
                time.sleep(.3); continue
            tree = self.smoke.tree()
            nodes = [n for n in tree.iter("node") if n.get("package") == PACKAGE]
            counts = [n for n in nodes if n.get("text") == f"All {baseline}" and n.get("checked") == "true"]
            search = [n for n in nodes if n.get("resource-id", "").endswith(":id/search_input")]
            local_host = [n for n in nodes if n.get("content-desc") == "Downloads shown from This phone. Change host"]
            overlays = [n for n in nodes if n.get("text") == "Shared link"
                        or n.get("content-desc") == "Close browser"
                        or n.get("resource-id", "").endswith((":id/new_root", ":id/new_cancel", ":id/address"))
                        or (n.get("class", "").endswith("EditText") and not n.get("resource-id", "").endswith(":id/search_input"))]
            if len(counts) == len(search) == len(local_host) == 1 and not overlays:
                final_tree = tree; break
            time.sleep(.3)
        require(final_tree is not None, "Probe Downloads is not uniquely foreground with all action overlays closed")
        focus = next((line for line in self.shell("dumpsys", "window").splitlines()
                      if "mCurrentFocus=" in line), "")
        require(re.search(r"\b" + re.escape(PACKAGE) + r"/", focus) is not None,
                "Probe lost foreground before final capture")
        (self.evidence / "b1-final-downloads-capture.png").write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(final_tree).write(self.evidence / "b1-final-downloads-ui.xml", encoding="utf-8", xml_declaration=True)
        require(fixture_outputs() == outputs, "The fixture-named shared Download inventory changed")
        if inventory is not None:
            require(set(self.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()) == inventory,
                    "Shared Download file inventory changed during the action")
        label = "cancellation" if self.args.b1_action == "cancel" else self.args.b1_action
        self.log(f"PASS B1 {label}: probe All count remained {baseline}; no new fixture-named output observed.")
        self.log("B2–B6 were not captured in this B1-only run.")

    def unattended_sequence(self):
        def tap(label, resource=None):
            node, _ = self.smoke.wait_node(label, resource=resource)
            self.smoke.tap(node)

        tap("Start downloading", "fr_start")
        if (self.args.capture_b6 or self.args.capture_b6_partial) and not (self.args.verify_search or self.args.capture_caught):
            (self.capture_b6_partial if self.args.capture_b6_partial else self.capture_b6)()
            self.log("UNVERIFIED: B1–B5 were not taken in this B6-only run.")
            return
        tap("Add", "fab")
        tap("More download sources", "entry_browser")
        tap("Browse and catch a download")
        self.smoke.enter("https://example.com", f"http://127.0.0.1:{self.port}/page")
        tap("Browser options")
        tap("Go")
        for _ in range(12):
            if self.smoke.matches(self.smoke.tree(), "Inbound link fixture"):
                break  # WebView exposes the title and heading; this is a presence check.
            time.sleep(0.4)
        else:
            raise RuntimeError("The loopback page did not render")
        if self.args.capture_caught and not self.args.verify_search:
            self.capture_fixture_catches()
            if self.args.capture_archives:
                self.capture_archives()
            if self.args.capture_b6_partial:
                self.capture_b6_partial()
            elif self.args.capture_b6:
                self.capture_b6()
            return
        tap("Browser options")
        tap("Links on page")
        self.capture("b4", "b4-all-links.png", "Select shown (8)", prompt=False)
        if self.args.verify_search:
            tap("Search links", "page_links_search_toggle")
            tap("Search links", "page_links_search")
            self.smoke.shell("input", "text", "Document")
            self.smoke.wait_node("Select shown (1)")
            tap("Close link search", "page_links_search_toggle")
            self.smoke.wait_node("Select shown (8)")
            self.log("PASS B4: header search narrowed to one link and clearing restored all eight.")
        for title in ("Document", "Image"):
            for attempt in range(4):
                tree = self.smoke.tree()
                nodes = [n for n in tree.iter("node") if n.get("checkable") == "true"
                         and n.get("text", "").split("\n", 1)[0] == title]
                require(len(nodes) == 1, f"Expected exactly one {title} checkbox")
                if nodes[0].get("checked") == "true":
                    break
                if attempt == 3:
                    raise RuntimeError(f"{title} was not selected")
                try:
                    self.smoke.tap(nodes[0])
                except subprocess.CalledProcessError as exc:
                    self.log(f"ADB tap returned {exc.returncode}; recheck {title} before retrying.")
        self.capture("b4-selected", None, "Download 2", prompt=False)
        self.log("PASS B4: real fixture listing and two selected rows; no job submitted.")
        if self.args.capture_archives or self.args.capture_caught:
            tap("Close", "page_links_close")
        if self.args.capture_archives:
            self.capture_archives()
            if self.args.capture_caught:
                self.navigate_browser("/page")
        if self.args.capture_caught:
            self.capture_fixture_catches()
        else:
            self.log("UNVERIFIED: B2 and B3 were not taken.")
        if self.args.capture_b6:
            self.capture_b6()
        elif self.args.capture_b6_partial:
            self.capture_b6_partial()
        else:
            self.log("UNVERIFIED: B6 was not taken.")
        self.log("UNVERIFIED: B1 chooser/share and B5 Downloads multiselect were not taken.")

    def capture_b6(self):
        """Exercise only the app's real expired-link and refresh UI on the probe."""
        name = f"inbound-b6-{uuid.uuid4().hex[:10]}.zip"
        target = f"/sdcard/Download/{name}"
        require(self.shell("test", "!", "-e", target).strip() == "",
                f"Refusing B6: exact output already exists: {target}")
        url = f"http://127.0.0.1:{self.port}/expired.zip?token=gone"

        def tap(label, resource=None):
            node, _ = self.smoke.wait_node(label, resource=resource)
            self.smoke.tap(node)

        def field(resource, label):
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                nodes = [n for n in self.smoke.tree().iter("node")
                         if n.get("resource-id", "").endswith(":id/" + resource)]
                require(len(nodes) <= 1, f"Ambiguous B6 {label} field")
                if nodes:
                    self.smoke.tap(nodes[0])
                    return nodes[0]
                time.sleep(0.25)
            raise RuntimeError(f"B6 could not locate the {label} field")

        # B4/caught walkthroughs leave the probe in the built-in browser. Return
        # to Downloads using Back only; never launch or inject an Android intent.
        tree = self.smoke.tree()
        if any(n.get("resource-id", "").endswith(":id/page_links_close")
               for n in tree.iter("node")):
            tap("Close", "page_links_close")
        for _ in range(4):
            if self.smoke.matches(self.smoke.tree(), "Search downloads"):
                break
            self.smoke.shell("input", "keyevent", "4")
        self.smoke.wait_node("Search downloads")

        def capture_state(key, anchor, reference=None):
            tree = self.smoke.tree()
            text = " ".join(n.get("text", "") + " " + n.get("content-desc", "")
                            for n in tree.iter("node"))
            require(anchor.casefold() in text.casefold(),
                    f"B6 expected visible state {anchor!r}")
            image = self.evidence / f"{key}-capture.png"
            image.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(tree).write(self.evidence / f"{key}-ui.xml",
                                       encoding="utf-8", xml_declaration=True)
            if reference:
                helper = load_module("link_intake_b6_pair_helper",
                                     HERE / "capture-remote-files-parity.py")
                helper.make_pair(MOCKUPS / reference, image, self.evidence / f"{key}-pair.png", "B6")

        tap("Add", "fab")
        self.smoke.wait_node("Add a download")
        field("entry_link", "Link or several links")
        field("address", "Address")
        self.shell("input", "text", url)
        # Set the exact disposable filename through the visible filename field.
        filename = field("file_name", "filename")
        self.shell("input", "keyevent", "KEYCODE_MOVE_END")
        old_name = filename.get("text", "")
        if old_name and old_name != filename.get("hint", "File name"):
            self.shell("input", "keyevent", *["KEYCODE_DEL"] * len(old_name))
        self.shell("input", "text", name)
        self.smoke.shell("input", "keyevent", "4")
        self.smoke.wait_node(name, resource="file_name")
        tap("Download", "new_download_btn")
        row_node, _ = self.smoke.wait_content_description(
            lambda desc: desc.startswith(name + ". Failed") and ("expired" in desc or "410" in desc),
            "unique failed fixture row", timeout=60)
        row = self.evidence / "b6-failed-row-capture.png"
        row.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        tree = self.smoke.tree()
        ET.ElementTree(tree).write(self.evidence / "b6-failed-row-ui.xml",
                                   encoding="utf-8", xml_declaration=True)
        self.log("Captured B6 failed Downloads row with expired-link message.")
        self.smoke.tap(row_node)
        self.smoke.wait_node("Refresh link")
        capture_state("b6-failed-details", "Refresh link")
        tap("Refresh link")
        # The refresh sheet is captured before changing or submitting its URL.
        self.smoke.wait_node("This link has expired", resource="refresh_title")
        capture_state("b6-refresh-sheet", "This link has expired", "b6-expired-link.png")
        tap("Paste a new address")
        field("refresh_address", "New address")
        self.shell("input", "text", f"http://127.0.0.1:{self.port}/expired.zip?token=new")
        self.smoke.shell("input", "keyevent", "4")
        tap("Refresh", "refresh_submit")
        deadline = time.monotonic() + 90
        digest = None
        while time.monotonic() < deadline:
            if self.shell(f"if [ -f '{target}' ]; then echo present; fi").strip() == "present":
                digest = self.shell("sha256sum", target).split()[0].lower()
                if digest == self.fixture_hash:
                    break
            time.sleep(0.5)
        require(digest == self.fixture_hash,
                f"B6 output hash mismatch; preserving file for inspection: {target}")
        self.log(f"PASS B6: same task refreshed and completed; {name} SHA-256 matches fixture.")
        # Delete only the unique task path, and only after rechecking its hash.
        require(self.shell("sha256sum", target).split()[0].lower() == self.fixture_hash,
                "B6 cleanup refused: output hash changed after verification")
        self.shell("rm", "--", target)
        require(self.shell("test", "!", "-e", target).strip() == "",
                "B6 exact verified output cleanup did not complete")
        self.log(f"Removed only verified B6 output {name}; baseline paths are untouched.")

    @staticmethod
    def details_percent(tree):
        return [node.get('text', '') for node in tree.iter('node')
                if node.get('resource-id', '').endswith(':id/hero_percent')]

    def leave_refresh_browser(self):
        for _ in range(5):
            tree = self.smoke.tree()
            if not self.smoke.matches(tree, "Browser options") and self.smoke.matches(tree, "Search downloads"):
                return
            self.shell("input", "keyevent", "4")
        raise RuntimeError("The refresh browser did not return to Downloads")

    def capture_browser_replacement(self, name, before_refresh):
        def tap(label, resource=None):
            node, _ = self.smoke.wait_node(label, resource=resource)
            self.smoke.tap(node)

        def replacement_link():
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                nodes = [n for n in self.smoke.matches(self.smoke.tree(), "Catch replacement")
                         if n.get("clickable") == "true" and n.get("enabled") == "true"]
                if nodes:
                    require(len(nodes) == 1, "Ambiguous clickable replacement link")
                    return nodes[0]
            raise RuntimeError("The real browser did not render the replacement link")

        def catch_confirmation(key):
            tap("Open the page", "refresh_open_page")
            self.smoke.wait_node("Browser options")
            self.navigate_browser("/partial/page")
            self.smoke.tap(replacement_link())
            _, confirm = self.smoke.wait_node("Refresh " + name + "?")
            (self.evidence / (key + ".png")).write_bytes(
                self.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(confirm).write(self.evidence / (key + ".xml"),
                                          encoding="utf-8", xml_declaration=True)

        with self.fixture_module.PARTIAL_STATE["lock"]:
            before_cancel = list(self.fixture_module.PARTIAL_STATE["ranges"])
        catch_confirmation("b6-browser-confirm-before-cancel")
        tap("Cancel")
        replacement_link()
        self.leave_refresh_browser()
        row, _ = self.smoke.wait_content_description(
            lambda desc: desc.startswith(name + ". ") and ("expired" in desc.lower() or "410" in desc),
            "same expired partial task after cancellation", timeout=30)
        self.smoke.tap(row)
        refresh, details = self.smoke.wait_node("Refresh link", resource="hero_main", timeout=30)
        require(len(before_refresh) == 1 and re.fullmatch(r"[1-9][0-9]*%", before_refresh[0])
                and self.details_percent(details) == before_refresh,
                "Cancelled confirmation changed the visible partial progress")
        with self.fixture_module.PARTIAL_STATE["lock"]:
            require(self.fixture_module.PARTIAL_STATE["ranges"] == before_cancel,
                    "Cancelled confirmation produced a new range request")
        (self.evidence / "b6-browser-cancel.png").write_bytes(
            self.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(details).write(self.evidence / "b6-browser-cancel.xml",
                                      encoding="utf-8", xml_declaration=True)
        self.log("Cancelled browser confirmation preserved the expired partial task and added no range request.")
        self.smoke.tap(refresh)
        self.smoke.wait_node("This link has expired", resource="refresh_title")
        catch_confirmation("b6-browser-confirm-retry")
        tap("Refresh link")

    def capture_b6_partial(self):
        """Guided real-app pause/410/refresh flow with nonzero saved bytes."""
        name = f"inbound-b6-partial-{uuid.uuid4().hex[:10]}.zip"
        target = f"/sdcard/Download/{name}"
        require(self.shell("test", "!", "-e", target).strip() == "",
                f"Refusing partial B6: exact output already exists: {target}")
        old_url = f"http://127.0.0.1:{self.port}/partial/old.zip"
        new_url = f"http://127.0.0.1:{self.port}/partial/new.zip"

        def tap(label, resource=None):
            node, _ = self.smoke.wait_node(label, resource=resource)
            self.smoke.tap(node)

        def field(resource, value, label):
            nodes = [n for n in self.smoke.tree().iter("node")
                     if n.get("resource-id", "").endswith(":id/" + resource)]
            require(len(nodes) == 1, f"Partial B6 could not locate unique {label} field")
            self.smoke.tap(nodes[0])
            self.shell("input", "text", value)

        for _ in range(4):
            if self.smoke.matches(self.smoke.tree(), "Search downloads"):
                break
            self.shell("input", "keyevent", "4")
        self.smoke.wait_node("Search downloads")
        tap("Add", "fab")
        self.smoke.wait_node("Add a download")
        entries = [n for n in self.smoke.tree().iter("node")
                   if n.get("resource-id", "").endswith(":id/entry_link") and n.get("clickable") == "true"]
        require(len(entries) == 1, "Expected one link-entry action")
        self.smoke.tap(entries[0])
        field("address", old_url, "URL")
        filename = [n for n in self.smoke.tree().iter("node")
                    if n.get("resource-id", "").endswith(":id/file_name")]
        require(len(filename) == 1, "Partial B6 could not locate unique filename field")
        self.smoke.tap(filename[0])
        previous = filename[0].get("text", "")
        if previous and previous != filename[0].get("hint", "File name"):
            self.shell("input", "keyevent", "KEYCODE_MOVE_END")
            self.shell("input", "keyevent", *["KEYCODE_DEL"] * len(previous))
        self.shell("input", "text", name)
        self.shell("input", "keyevent", "4")
        self.smoke.wait_node(name, resource="file_name")
        tree = self.smoke.tree()
        def bounds_for(resource):
            nodes = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/" + resource)]
            require(len(nodes) == 1, f"Expected one {resource} control")
            return tuple(map(int, re.findall(r"\d+", nodes[0].get("bounds", ""))))
        left, top, right, bottom = bounds_for("connections_block")
        label_bottom = bounds_for("connections_value")[3]
        require(top < label_bottom < bottom, "Connections slider track is outside the visible form")
        self.shell("input", "tap", left + (right - left) // 20, (label_bottom + bottom) // 2)
        self.smoke.wait_node("1", resource="connections_value")
        tap("Download", "new_download_btn")
        row, _ = self.smoke.wait_content_description(
            lambda desc: desc.startswith(name + ". "),
            "active partial B6 row", timeout=45)
        self.smoke.tap(row)
        def saved_amount(tree, resource):
            for node in tree.iter("node"):
                if node.get("resource-id", "").endswith(":id/" + resource):
                    match = re.match(r"(\d+(?:\.\d+)?) (?:B|KB|MB|GB|TB)\b", node.get("text", ""))
                    if match:
                        return float(match.group(1))
            return 0

        self.smoke.wait_node("Pause")
        deadline = time.monotonic() + 30
        while saved_amount(self.smoke.tree(), "hero_progress") <= 0:
            require(time.monotonic() < deadline, "Details never reported received bytes")
        if self.args.verify_details_stop:
            stop, active = self.smoke.wait_node("Stop", resource="hero_stop")
            require(stop.get("enabled") == "true", "Active Details Stop is not enabled")
            require(not any(n.get("resource-id", "").endswith(":id/hero_again")
                            for n in active.iter("node")),
                    "Active Details unexpectedly exposes restart from zero")
            self.smoke.tap(stop)
            retry, stopped = self.smoke.wait_node("Retry", timeout=30, resource="hero_main")
            require(any(n.get("resource-id", "").endswith(":id/hero_rate")
                        and n.get("text") == "Cancelled" for n in stopped.iter("node")),
                    "Stop did not leave the original Details in its Cancelled state")
            require(any(n.get("resource-id", "").endswith(":id/hero_percent")
                        and re.fullmatch(r"[1-9][0-9]*%", n.get("text", ""))
                        for n in stopped.iter("node")),
                    "Stopped Details lost visible partial progress")
            image = self.evidence / "a6-stopped-details-capture.png"
            image.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
            action = "stopped"
        else:
            tap("Pause")
            retry, paused = self.smoke.wait_node("Resume", timeout=30, resource="hero_main")
            require(saved_amount(paused, "hero_progress") > 0, "Paused Details showed no saved bytes")
            action = "paused"
        with self.fixture_module.PARTIAL_STATE["lock"]:
            self.fixture_module.PARTIAL_STATE["expired"] = True
        self.log(f"Observed same task {action} with nonzero visible saved progress.")
        self.smoke.tap(retry)
        button, failed = self.smoke.wait_node("Refresh link", resource="hero_main", timeout=60)
        before_refresh = self.details_percent(failed)
        self.smoke.tap(button)
        self.smoke.wait_node("This link has expired", resource="refresh_title", timeout=60)
        sheet = self.smoke.tree()
        require(saved_amount(sheet, "refresh_description") > 0,
                "B6 sheet did not report nonzero saved bytes")
        image = self.evidence / "b6-partial-refresh-sheet-capture.png"
        image.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(sheet).write(self.evidence / "b6-partial-refresh-sheet-ui.xml",
                                    encoding="utf-8", xml_declaration=True)
        helper = load_module("link_intake_partial_pair_helper",
                             HERE / "capture-remote-files-parity.py")
        helper.make_pair(MOCKUPS / "b6-expired-link.png", image,
                         self.evidence / "b6-partial-refresh-sheet-pair.png", "B6")
        if self.args.capture_b6_browser:
            self.capture_browser_replacement(name, before_refresh)
        else:
            tap("Paste a new address")
            field("refresh_address", new_url, "replacement URL")
            self.shell("input", "keyevent", "4")
            tap("Refresh", "refresh_submit")

        expected = hashlib.sha256(self.fixture_module.PARTIAL_PAYLOAD).hexdigest()
        deadline = time.monotonic() + 120
        digest = None
        while time.monotonic() < deadline:
            if self.shell(f"if [ -f '{target}' ]; then echo present; fi").strip() == "present":
                digest = self.shell("sha256sum", target).split()[0].lower()
                if digest == expected:
                    break
            time.sleep(0.5)
        require(digest == expected,
                f"Partial B6 output hash mismatch; preserving file: {target}")
        with self.fixture_module.PARTIAL_STATE["lock"]:
            ranges = list(self.fixture_module.PARTIAL_STATE["ranges"])
        old_expired_ranges = [value for path, value in ranges
                              if path == "/partial/old.zip" and value.startswith("bytes=")]
        require(any(int(value[6:].split("-", 1)[0]) > 0 for value in old_expired_ranges),
                "Expired old URL was not retried with a nonzero Range")
        replacement_ranges = [value for path, value in ranges
                              if path == "/partial/new.zip" and value.startswith("bytes=")]
        require(any(int(value[6:].split("-", 1)[0]) > 0 for value in replacement_ranges),
                "Replacement did not request a nonzero Range; preserving output")
        (self.evidence / "partial-b6-requests.txt").write_text(
            "\n".join(f"{path} Range {value}" for path, value in ranges) + "\n",
            encoding="ascii")
        self.log(f"PASS partial B6: replacement used nonzero Range; SHA-256 {expected}.")
        if self.args.capture_b6_browser:
            self.leave_refresh_browser()
        else:
            self.shell("input", "keyevent", "4")
        row, _ = self.smoke.wait_content_description(
            lambda desc: desc.startswith(name + ". Finished"),
            "completed partial B6 row", timeout=30)
        self.smoke.tap(row)
        tap("Delete", "hero_delete")
        self.smoke.wait_node("Remove from the list only", timeout=20)
        tap("Delete")
        self.smoke.wait_node("Search downloads", timeout=30)
        require(not any(name in n.get("content-desc", "") or name == n.get("text", "")
                        for n in self.smoke.tree().iter("node")),
                "Partial B6 disposable job remained after normal UI deletion")
        require(self.shell(f"if [ -f '{target}' ]; then echo present; fi").strip() == "present",
                "UI cleanup unexpectedly removed the payload before exact hash cleanup")
        require(self.shell("sha256sum", target).split()[0].lower() == expected,
                "Partial B6 cleanup refused: output hash changed")
        self.shell("rm", "--", target)
        require(self.shell("test", "!", "-e", target).strip() == "",
                "Partial B6 exact output cleanup did not complete")
        self.log(f"Removed only hash-verified output {name}; baseline paths untouched.")

    def navigate_browser(self, path):
        address = next(n for n in self.smoke.tree().iter("node") if n.get("class") == "android.widget.EditText")
        self.smoke.tap(address)
        self.shell("input", "keycombination", "KEYCODE_CTRL_LEFT", "KEYCODE_A")
        self.shell("input", "text", f"http://127.0.0.1:{self.port}{path}")
        self.smoke.tap(self.smoke.wait_node("Browser options")[0])
        self.smoke.tap(self.smoke.wait_node("Go")[0])

    def capture_archives(self):
        self.navigate_browser("/field-recordings")
        for _ in range(12):
            if any(n.get("clickable") == "true" for n in self.smoke.matches(self.smoke.tree(), "field-recordings-vol1.zip")):
                break
        else:
            raise RuntimeError("Archives fixture page did not render")
        self.smoke.tap(self.smoke.wait_node("Browser options")[0])
        self.smoke.tap(self.smoke.wait_node("Links on page")[0])
        self.smoke.tap(self.smoke.wait_node("Archives 6")[0])
        self.smoke.tap(self.smoke.wait_node("All 6 archives", resource="page_links_select_shown")[0])
        _, tree = self.smoke.wait_node("Download 6", resource="page_links_download")
        # RecyclerView only exposes attached rows. Collect them while scrolling,
        # then return to the top for the mockup comparison.
        def fmt_bytes(size):
            units = ("B", "KB", "MB", "GB", "TB")
            value = float(size)
            unit = 0
            while value >= 1000 and unit < len(units) - 1:
                value /= 1000
                unit += 1
            return f"{value:.0f} {units[unit]}" if unit == 0 or value >= 100 else f"{value:.1f} {units[unit]}"

        list_node = next(n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/page_links_list"))
        bounds = list(map(int, re.findall(r"\d+", list_node.get("bounds", ""))))
        require(len(bounds) == 4, "Missing page-links list bounds")
        left, top, right, bottom = bounds
        x, high, low = (left + right) // 2, top + 20, bottom - 20
        require(low > high, "Page-links list too small to inspect")
        expected = {f"{name}\n{fmt_bytes(len(data))}": "true"
                    for name, data in self.fixture_module.ARCHIVES.items()}
        expected.update({"Cover image": "false", "Readme": "false"})
        observed = {}
        for swipes in range(8):
            for node in self.smoke.tree().iter("node"):
                if node.get("resource-id", "").endswith(":id/page_link_check"):
                    label = node.get("text", "")
                    observed[label] = node.get("checked")
            if observed == expected:
                break
            if swipes < 7:
                self.shell("input", "swipe", str(x), str(low), str(x), str(high), "300")
        require(observed == expected, f"Unexpected archive list rows or check states: {observed!r}")
        for _ in range(swipes):
            self.shell("input", "swipe", str(x), str(high), str(x), str(low), "300")
        total = sum(map(len, self.fixture_module.ARCHIVES.values()))
        total_text = fmt_bytes(total)
        summary = f"6 files \u00b7 {total_text}"
        self.smoke.wait_node(total_text, resource="page_links_group_size")
        self.smoke.wait_node(summary, resource="page_links_summary")
        self.capture("b4-archives", "b4-all-links.png", summary, prompt=False)
        self.smoke.tap(self.smoke.wait_node("Close", resource="page_links_close")[0])

    def verify_phone_folder(self, tap_label):
        child = "000-inbound-b2-" + uuid.uuid4().hex[:12]
        directory = "/sdcard/Download/" + child
        target = directory + "/a.zip"
        self.shell("mkdir", directory)
        try:
            self.smoke.tap(self.smoke.wait_node("Change", resource="caught_change_destination")[0])
            tap_label(child)
            self.smoke.wait_content_description(
                lambda text: text.startswith("Folder ") and text.endswith("/" + child),
                "rendered phone child folder")
            tap_label("Save here")
            tree = self.smoke.tree()
            chosen = [n.get("text", "") for n in tree.iter("node")
                      if n.get("resource-id", "").endswith(":id/caught_destination")]
            require(len(chosen) == 1 and chosen[0].endswith("/" + child), "Sheet did not show the chosen folder")
            self.smoke.tap(self.smoke.wait_node("Download", resource="caught_submit")[0])
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                if self.shell(f"if [ -f '{target}' ]; then sha256sum '{target}'; fi").split()[:1] == [self.fixture_hash]:
                    break
                time.sleep(.4)
            else:
                raise RuntimeError("Phone output missing or hash mismatch in the selected folder")
            (self.evidence / "phone-folder-sha256.txt").write_text(
                f"{self.fixture_hash}  {chosen[0]}/a.zip\n", encoding="utf-8")
            self.log("PASS B2 phone: hash-matched archive in the exact chosen child.")
            require(self.shell("sha256sum", target).split()[0] == self.fixture_hash, "Phone output changed")
            self.shell("rm", "--", target)
        finally:
            if not self.shell("ls", "-A", directory).strip():
                self.shell("rmdir", directory)

    def verify_b3_completion(self):
        """Submit the real local signed-in sheet; remove only the owned exact hash after stopping its writer."""
        target = self.b3_target
        require(self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                "B3 output appeared before submission; preserving it")
        with self.fixture_module.REPORT_STATE["lock"]:
            before = self.fixture_module.REPORT_STATE["authenticated_gets"]
        submitted = False
        try:
            submit, _ = self.smoke.wait_node("Download", resource="caught_submit")
            submitted = True  # A lost tap reply cannot prove the job was not admitted.
            self.smoke.tap(submit)
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                with self.fixture_module.REPORT_STATE["lock"]:
                    count = self.fixture_module.REPORT_STATE["authenticated_gets"]
                digest = self.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
                if count > before and digest[:1] == [self.b3_hash]:
                    break
                time.sleep(.4)
            else:
                raise RuntimeError("B3 did not produce both an authenticated report GET and matching output hash")
            (self.evidence / "b3-auth-completion.txt").write_text(
                f"Protected report GETs after submission: {count - before}\nSHA-256: {self.b3_hash}\n",
                encoding="ascii")
            self.log("PASS B3 completion: the protected fixture report was fetched after Download and matched SHA-256")
        finally:
            pending = sys.exc_info()[0] is not None
            try:
                if submitted:
                    self.shell("am", "force-stop", PACKAGE)
                    require(self.shell(f"if pidof {PACKAGE} >/dev/null; then echo running; else echo stopped; fi").strip() == "stopped",
                            "Probe writer did not stop; preserving B3 output")
                    if self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() == "present":
                        result = self.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
                        require(result[:1] == [self.b3_hash], "B3 output changed or is incomplete; preserving it")
                        self.shell("rm", "--", target)
                        require(self.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present",
                                "B3 owned file remains after cleanup")
            except Exception as exc:
                if not pending: raise
                self.log(f"B3 cleanup warning; preserving original failure and unexpected file: {exc}")

    def capture_fixture_catches(self):
        def tap_web_link(label):
            for _ in range(12):
                tree = self.smoke.tree()
                found = [node for node in self.smoke.matches(tree, label) if node.get("clickable") == "true"]
                require(len(found) <= 1, f"Ambiguous clickable link {label!r}")
                if found:
                    self.smoke.tap(found[0])
                    return
                time.sleep(0.4)
            raise RuntimeError(f"Fixture link {label!r} did not appear")

        tap_web_link("Archive")
        self.smoke.wait_node("a.zip", resource="caught_name")
        self.capture("b2", "b2-caught.png", "Download with Inbound", prompt=False)
        more, _ = self.smoke.wait_node("More options", resource="caught_more")
        self.smoke.tap(more)
        self.smoke.wait_node("Connections", resource="caught_connections")
        fewer, _ = self.smoke.wait_node("Fewer options", resource="caught_more")
        self.smoke.tap(fewer)
        _, tree = self.smoke.wait_node("More options", resource="caught_more")
        require(not any(node.get("resource-id", "").endswith(":id/caught_connections") for node in tree.iter("node")),
                "B2 connection control remained visible after closing More options")
        self.log("PASS B2: More options opened and closed without submitting.")
        if self.args.verify_phone_folder:
            self.verify_phone_folder(tap_web_link)
        else:
            self.smoke.shell("input", "keyevent", "4")
        tap_web_link("Sign in")
        # This is only the fixture's synthetic loopback cookie.
        tap_web_link("Authenticated archive")
        self.smoke.wait_node("auth.zip", resource="caught_name")
        self.shell("input", "keyevent", "4")
        self.navigate_browser("/reports")
        tap_web_link("report-q3-2026.xlsx")
        self.smoke.wait_node("report-q3-2026.xlsx", resource="caught_name")
        self.capture("b3", "b3-signed-in.png", "Cookies stay on this phone", prompt=False)
        if self.args.verify_b3_completion:
            self.verify_b3_completion()
            return
        self.smoke.shell("input", "keyevent", "4")
        self.log("PASS B2/B3: fixture filenames and local cookie notice before signed-in submission.")

    def run(self):
        self.preflight()
        try:
            self.start_fixture()
            self.launch()
            if self.args.b1_only:
                self.guided_b1_only()
            elif self.args.unattended:
                self.unattended_sequence()
            else:
                for key, reference, anchor in CAPTURES:
                    self.capture(key, reference, anchor)
            self.log("Capture sequence complete. Verify job counts, hashes and fixture request log manually.")
        except Exception as exc:
            self.log(f"Capture failed: {exc}")
            if self.smoke.started:
                try:
                    ET.ElementTree(self.smoke.tree()).write(self.evidence / "failure-ui.xml",
                                                             encoding="utf-8", xml_declaration=True)
                except Exception:
                    pass
            raise
        finally:
            pending = sys.exc_info()[0] is not None
            errors = []
            try:
                if self.fixture_thread is not None and self.fixture_thread.is_alive():
                    self.fixture.shutdown()
                    self.fixture_thread.join(timeout=5)
                if self.fixture is not None:
                    self.fixture.server_close()
            except Exception as exc:
                errors.append(f"fixture shutdown: {exc}")
            if self.reversed:
                try:
                    self.adb("reverse", "--remove", f"tcp:{self.port}")
                    self.log("Removed only this run's ADB reverse.")
                except Exception as exc:
                    errors.append(f"reverse removal: {exc}")
            try:
                self.smoke.clean(include_remote_files_payload=False)
            except Exception as exc:
                errors.append(f"probe cleanup: {exc}")
            if errors:
                message = "Link fixture cleanup failed: " + "; ".join(errors)
                if not pending:
                    raise RuntimeError(message)
                self.log(message)
        return self.evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt", help="Android build-tools aapt(.exe)")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit isolated rcloneprobe APK")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="New, previously absent evidence directory")
    parser.add_argument("--b1-only", action="store_true", help="Guided real Chrome share chooser check only")
    parser.add_argument("--b1-action", choices=("cancel", "open-page", "add-file"), default="cancel",
                        help="In the B1-only lane, verify Cancel, Open page or Add file")
    parser.add_argument("--unattended", action="store_true",
                         help="Capture B4 and its selected state in the built-in browser without prompting")
    parser.add_argument("--verify-search", action="store_true", help="Verify the B4 header search toggle")
    parser.add_argument("--verify-phone-folder", action="store_true", help="Choose a unique phone Downloads child and verify the archive SHA-256")
    parser.add_argument("--capture-archives", action="store_true", help="Verify six real archives and their selected byte total")
    parser.add_argument("--verify-b3-completion", action="store_true", help="Complete the isolated authenticated B3 fixture report and verify its hash")
    parser.add_argument("--capture-caught", action="store_true", help="Also capture B2 and B3 against the loopback fixture")
    parser.add_argument("--capture-b6", action="store_true",
                        help="Opt in to the loopback expired-link → refresh → hash/cleanup B6 flow")
    parser.add_argument("--capture-b6-partial", action="store_true",
                        help="Opt in to guided B6 nonzero-byte pause/410/refresh/Range/hash/cleanup")
    parser.add_argument("--capture-b6-browser", action="store_true",
                        help="Use the actual browser confirmation and Cancel in the partial B6 lane")
    parser.add_argument("--verify-details-stop", action="store_true",
                        help="In the partial B6 flow verify active Details Stop retains progress and hides restart from zero")
    args = parser.parse_args()
    if args.b1_action != "cancel" and not args.b1_only:
        parser.error("--b1-action open-page/add-file requires --b1-only")
    if args.verify_b3_completion and (not args.unattended or not args.capture_caught or args.b1_only
                                       or args.verify_search or args.verify_phone_folder or args.capture_archives
                                       or args.capture_b6 or args.capture_b6_partial or args.capture_b6_browser or args.verify_details_stop):
        parser.error("--verify-b3-completion requires only --unattended --capture-caught")
    if args.b1_only and (args.unattended or args.verify_search or args.verify_phone_folder or args.capture_archives
                         or args.capture_caught or args.capture_b6 or args.capture_b6_partial
                         or args.capture_b6_browser or args.verify_details_stop):
        parser.error("--b1-only is a standalone guided lane")
    if args.capture_b6_browser and not (args.unattended and args.capture_b6_partial):
        parser.error("--capture-b6-browser requires --unattended --capture-b6-partial")
    if args.verify_details_stop and not (args.unattended and args.capture_b6_partial):
        parser.error("--verify-details-stop requires --unattended --capture-b6-partial")
    if args.verify_phone_folder and not (args.capture_caught and args.unattended):
        parser.error("--verify-phone-folder requires --unattended --capture-caught")
    if args.verify_search and not args.unattended:
        parser.error("--verify-search requires --unattended")
    if args.capture_caught and not args.unattended:
        parser.error("--capture-caught requires --unattended")
    if (args.capture_b6 or args.capture_b6_partial) and not args.unattended:
        parser.error("--capture-b6 and --capture-b6-partial require --unattended")
    if args.capture_b6 and args.capture_b6_partial:
        parser.error("choose only one B6 lane")
    args.instructions = {
        "b4-archives": "Capture six checked archives and the combined size.",
        "b1-chooser": "In Chrome open http://127.0.0.1:PORT/page; Share link → Android chooser. Capture chooser itself.",
        "b1-caught": "Choose Inbound Probe in the Android chooser; capture Shared link before choosing an action.",
        "b1-open-page": "Capture the exact owned page in the probe browser.",
        "b1-add-file": "Capture the prefilled Archive form before cancelling.",
        "b2": "Open the fixture in Inbound's browser and tap Archive. Capture Download with Inbound before submitting; verify one job/hash separately.",
        "b3": "Select This phone. In Inbound's browser, visit the fixture /signin then tap Authenticated archive. Capture the cookie-use notice before submitting.",
        "b4": "On the fixture page open Links on page; capture the eight-link list/filter state.",
        "b4-selected": "Select only Document and Image; capture selected count/action without submitting.",
        "b6": "Add /expired.zip?token=gone, capture HTTP 410 refresh UI, replace with token=new, verify same job completes.",
    }
    capture = Capture(args)
    try:
        print(f"Evidence: {capture.run()}")
    except Exception:
        raise


if __name__ == "__main__":
    main()
