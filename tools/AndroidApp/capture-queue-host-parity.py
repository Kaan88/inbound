"""Guided C1/C2/C5/C6 captures on the isolated inbound33 probe."""

import argparse
import hashlib
import importlib.machinery
import json
import re
import sys
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import time
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MOCKUPS = ROOT / "docs" / "plans" / "android-mockups"
PACKAGE = "app.inbound.android.rcloneprobe"
ACTIVITY = PACKAGE + "/app.inbound.android.MainActivity"
AVD = "inbound33"
API = "33"
SMOKE_PATH = Path(__file__).with_name("smoke-remote-files-ui.py")
PAIR_PATH = Path(__file__).with_name("capture-remote-files-parity.py")

# Each state is prepared manually in the probe. Repeated reference filenames
# intentionally produce separate, state-specific comparison images.
CAPTURES = (
    ("c1-empty", "c1-queues.png", "Double tap to edit"),
    ("c1-populated-stopped", "c1-queues.png", "{queue}, Stopped"),
    ("c1-populated-running", "c1-queues.png", "{queue}"),
    ("c1-queue-actions", "c1-queues.png", "Edit schedule"),
    ("c2-no-schedule", "c2-schedule.png", "Schedule"),
    ("c2-configured", "c2-schedule.png", "Night"),
    ("c2-editor-footer", "c2-schedule.png", "Times use this Host"),
    ("c6-unpaired", None, "Hosts and web UI"),
    ("c6-pairing-form", None, "Connect to another Inbound"),
    ("c6-paired", "c6-remote-host.png", "Files are saved on"),
    ("c5-phone-selected", "c5-switch-host.png", "Where downloads run"),
    ("c5-paired-selected", "c5-switch-host.png", "Where downloads run"),
)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def load_module(name, path):
    return importlib.machinery.SourceFileLoader(name, str(path)).load_module()


def checkpoint_anchor(anchor, args):
    return anchor.replace("{queue}", args.queue_name).replace("{host}", args.host_name)


class Capture:
    def __init__(self, args):
        self.args = args
        self.smoke_module = load_module("queue_host_parity_smoke", SMOKE_PATH)
        self.smoke = self.smoke_module.Smoke(args)
        self.evidence = self.smoke.evidence
        self.installed = False
        self.started = False

    def adb(self, *args, raw=False):
        return self.smoke.adb(*args, raw=raw)

    def shell(self, *args):
        return self.smoke.shell(*args)

    def log(self, message):
        self.smoke.log(message)

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
        import subprocess
        badging = subprocess.run([aapt, "dump", "badging", str(apk)],
                                 capture_output=True, text=True, timeout=25, check=True).stdout
        import re
        require(re.search(r"^package: name=['\"]" + re.escape(PACKAGE) + r"['\"](?:\s|$)",
                          badging, re.MULTILINE),
                f"APK package must be exactly {PACKAGE}")
        require(f"package:{PACKAGE}" not in self.shell("pm", "list", "packages", PACKAGE),
                "Probe package is already installed; refusing to replace or clear it")
        version = re.search(r"^package: .*?versionCode='([^']*)'.*?versionName='([^']*)'",
                            badging, re.MULTILINE)
        apk_hash = hashlib.sha256(apk.read_bytes()).hexdigest()
        self.pairer = load_module("queue_host_parity_pairer", PAIR_PATH)
        manifest = {
            "avd": AVD,
            "api": API,
            "serial": self.args.serial,
            "package": PACKAGE,
            "apk_sha256": apk_hash,
            "apk_version_code": version.group(1) if version else None,
            "apk_version_name": version.group(2) if version else None,
            "display_size": self.shell("wm", "size").strip(),
            "display_density": self.shell("wm", "density").strip(),
            "theme_and_dynamic_colour": "operator records actual values in flow.txt",
            "fixture_inputs": {
                "queue_name": self.args.queue_name,
                "host_display_name": self.args.host_name,
                "paired_host_address": "operator-supplied disposable loopback fixture; not stored",
                "pairing_password": "entered only on emulator; never stored",
            },
        }
        (self.evidence / "preflight.json").write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        (self.evidence / "apk.sha256").write_text(apk_hash + "\n", encoding="ascii")
        self.log("Preflight PASS: inbound33/API 33; exact isolated probe APK; package absent.")
        self.log(f"Use disposable queue name {self.args.queue_name!r} and host label {self.args.host_name!r}.")

    def launch(self):
        self.adb("install", str(Path(self.args.apk).resolve()))
        self.installed = True
        require(f"package:{PACKAGE}" in self.shell("pm", "list", "packages", PACKAGE),
                "Probe package did not install")
        unattended = (self.args.unattended_c1_c2 or self.args.unattended_configured_c2
                      or self.args.unattended_local_hosts or self.args.paired_server_dll
                      or self.args.unattended_c1_workload)
        extra = ("--ez", "skip_first_run", "true") if unattended else ()
        self.adb("shell", "am", "start", "-W", "-n", ACTIVITY, *extra)
        self.started = True
        self.log("Launched fresh probe normally. Complete first-run setup with disposable/local values only.")

    def capture_checkpoint(self, key, reference, anchor):
        self.current_checkpoint = key
        expected = checkpoint_anchor(anchor, self.args)
        if self.args.unattended_c1_workload:
            pass  # The workload lane has already checked the specific state.
        elif self.args.unattended_configured_c2:
            if key in ("c1-empty", "c1-populated-stopped"):
                self.navigate_c1_c2(key)
            elif key == "c2-configured":
                self.configure_c2()
            elif key == "c2-editor-footer":
                tree = self.smoke.tree()
                if not any("Times use this Host" in n.get("text", "") for n in tree.iter("node")):
                    self.shell("input", "swipe", "530", "1500", "530", "550", "350")
                else:
                    self.log("The whole configured editor fits on screen, including its Host and timing footer.")
        elif self.args.unattended_c1_c2 and key in (
                "c1-empty", "c1-populated-stopped", "c2-no-schedule"):
            self.navigate_c1_c2(key)
        elif self.args.unattended_local_hosts:
            self.navigate_local_hosts(key)
        elif self.args.paired_server_dll and key in ("c6-paired", "c5-paired-selected", "c5-phone-selected"):
            pass  # run_paired already navigated and verified the expected state.
        else:
            self.log(f"Prepare {key}: {self.args.instructions[key]}")
            input(f"\nNavigate the isolated probe to {key} and match the reference state. "
                  "Press Enter to capture (Ctrl+C aborts): ")
        deadline = time.monotonic() + 30
        tree = None
        while time.monotonic() < deadline:
            tree = self.smoke.tree()
            texts = [node.get("text", "") + " " + node.get("content-desc", "")
                     for node in tree.iter("node")]
            if any(expected.casefold() in text.casefold() for text in texts):
                if key == "c1-workload":
                    descriptions = [n.get("content-desc", "") for n in tree.iter("node")]
                    expected_cards = (("Main, Running", ("1 of 1 running", "left")),
                                      ("Night, Scheduled for ", (self.c1_start, self.c1_stop, "1 file")),
                                      ("Mobile, Stopped", ("1 file",)))
                    if not all(sum(d.startswith(prefix) and "Double tap to edit" in d
                                       and all(token in d for token in tokens)
                                       and (not prefix.startswith("Night,") or self.c1_start in d.split(". ", 1)[0])
                                       for d in descriptions) == 1
                               for prefix, tokens in expected_cards):
                        time.sleep(0.5)
                        continue
                break
            time.sleep(0.5)
        else:
            raise RuntimeError(
                f"{key} does not expose expected UI anchor {expected!r}; "
                "inspect the latest UI dump before continuing")
        screenshot = self.evidence / f"{key}-capture.png"
        screenshot.write_bytes(self.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(tree).write(
            self.evidence / f"{key}-ui.xml", encoding="utf-8", xml_declaration=True)
        if not key.startswith(("c5-", "c6-")):
            logs = self.adb("logcat", "-d", "-s", "Inbound", "InboundRemoteFilesProbe")
            (self.evidence / f"{key}-logcat.txt").write_text(logs, encoding="utf-8")
        if reference is None:
            self.log(f"Captured {key}: anchor={expected!r}; supplemental screenshot, no approved reference.")
        else:
            pair = self.evidence / f"{key}-pair.png"
            size = self.pairer.make_pair(MOCKUPS / reference, screenshot, pair, key.upper())
            self.log(f"Captured {key}: anchor={expected!r}; pair={pair.name} ({size[0]}x{size[1]}).")

    @staticmethod
    def _nodes(tree, resource):
        return [node for node in tree.iter("node")
                if node.get("resource-id", "").endswith(":id/" + resource)]

    def _unique_resource(self, tree, resource):
        nodes = self._nodes(tree, resource)
        require(len(nodes) == 1, f"Expected exactly one android:id/{resource}; got {len(nodes)}")
        return nodes[0]

    def _tap_resource(self, resource, *, checked=None):
        tree = self.smoke.tree()
        node = self._unique_resource(tree, resource)
        if checked is not None:
            require(node.get("checked") == str(checked).lower(),
                    f"android:id/{resource} checked={node.get('checked')!r}, expected {checked}")
        require(node.get("enabled") != "false",
                f"android:id/{resource} is disabled")
        self.smoke.tap(node)
        return node

    def _time_picker(self, hour, period="AM"):
        """Set a native API 33 clock picker by its observed resource IDs and radial bounds."""
        tree = self.smoke.tree()
        picker = self._unique_resource(tree, "timePicker")
        require(picker.get("class", "").endswith("TimePicker"),
                "Native android:id/timePicker is not a TimePicker")
        hours = self._unique_resource(tree, "hours")
        minutes = self._unique_resource(tree, "minutes")
        ok = self._unique_resource(tree, "button1")
        require(hours.get("clickable") == "true" and minutes.get("clickable") == "true",
                "Native hours/minutes selectors are not clickable")
        require(ok.get("text") == "OK", "Native time-picker confirmation is not OK")
        self.smoke.tap(hours)
        tree = self.smoke.tree()
        radial = self._unique_resource(tree, "radial_picker")
        hour_nodes = [n for n in tree.iter("node")
                      if n.get("content-desc") == str(hour)
                      and n.get("clickable") == "false"
                      and n.get("bounds") and radial.get("bounds")]
        require(len(hour_nodes) == 1,
                f"Expected one non-clickable radial hour node {hour}; got {len(hour_nodes)}")
        radial_bounds = [int(x) for x in re.findall(r"\d+", radial.get("bounds", ""))]
        hour_bounds = [int(x) for x in re.findall(r"\d+", hour_nodes[0].get("bounds", ""))]
        require(len(radial_bounds) == 4 and len(hour_bounds) == 4
                and radial_bounds[0] <= hour_bounds[0] < hour_bounds[2] <= radial_bounds[2]
                and radial_bounds[1] <= hour_bounds[1] < hour_bounds[3] <= radial_bounds[3],
                "Radial hour bounds are missing or outside android:id/radial_picker")
        self.smoke.tap(hour_nodes[0])
        tree = self.smoke.tree()
        self.smoke.tap(self._unique_resource(tree, "minutes"))
        tree = self.smoke.tree()
        radial = self._unique_resource(tree, "radial_picker")
        zero_nodes = [n for n in tree.iter("node")
                      if n.get("content-desc") in ("00", "0")
                      and n.get("clickable") == "false"
                      and n.get("bounds") and radial.get("bounds")]
        require(len(zero_nodes) == 1,
                f"Expected one non-clickable radial minute 00 node; got {len(zero_nodes)}")
        radial_bounds = [int(x) for x in re.findall(r"\d+", radial.get("bounds", ""))]
        zero_bounds = [int(x) for x in re.findall(r"\d+", zero_nodes[0].get("bounds", ""))]
        require(len(radial_bounds) == 4 and len(zero_bounds) == 4
                and radial_bounds[0] <= zero_bounds[0] < zero_bounds[2] <= radial_bounds[2]
                and radial_bounds[1] <= zero_bounds[1] < zero_bounds[3] <= radial_bounds[3],
                "Radial minute 00 bounds are missing or outside android:id/radial_picker")
        self.smoke.tap(zero_nodes[0])
        tree = self.smoke.tree()
        am = self._unique_resource(tree, "am_label")
        pm = self._unique_resource(tree, "pm_label")
        require(am.get("text") == "AM" and pm.get("text") == "PM",
                "Native AM/PM labels do not match the observed picker tree")
        require(period in ("AM", "PM"), "Invalid picker period")
        wanted, other = (am, pm) if period == "AM" else (pm, am)
        if wanted.get("checked") != "true":
            self.smoke.tap(wanted)
        tree = self.smoke.tree()
        require(self._unique_resource(tree, period.lower() + "_label").get("checked") == "true"
                and self._unique_resource(tree, ("pm" if period == "AM" else "am") + "_label").get("checked") == "false",
                f"Native picker did not retain exclusive {period} selection")
        self.smoke.tap(self._unique_resource(tree, "button1"))

    def configure_c2(self):
        """Create, save, reopen and verify only the disposable Night queue's schedule."""
        queue_card, _ = self.smoke.wait_content_description(
            lambda desc: desc.startswith(self.args.queue_name + ", ")
            and "Double tap to edit" in desc, "disposable queue editor card")
        self.smoke.tap(queue_card)
        self.smoke.wait_node("Choose or add a schedule")
        self._tap_exact_text("Choose or add a schedule")
        add, _ = self.smoke.wait_node("+  Add schedule")
        self.smoke.tap(add)
        field, _ = self.smoke.wait_node("Schedule name")
        self.smoke.tap(field)
        self.shell("input", "text", "Weekdays")
        create, _ = self.smoke.wait_node("Continue")
        self.smoke.tap(create)
        self.smoke.wait_content_description(
            lambda desc: desc.startswith("Start time"), "start-time control")
        self._tap_description_prefix("Start time")
        self._time_picker(1)
        self._tap_description_prefix("Stop time")
        choose, _ = self.smoke.wait_node("Choose time")
        self.smoke.tap(choose)
        self._time_picker(7)

        # QueueEditorScreen creates the seven day controls with stable source IDs.
        for day in ("sat", "sun"):
            tree = self.smoke.tree()
            resource = "queue_editor_day_" + day
            node = self._unique_resource(tree, resource)
            require(node.get("checked") == "true",
                    f"Expected default {day} chip checked before deselecting it")
            self.smoke.tap(node)
            require(self._unique_resource(self.smoke.tree(), resource).get("checked") == "false",
                    f"{day} chip remained selected")
        for resource in ("queue_editor_charging", "queue_editor_unmetered"):
            if self._unique_resource(self.smoke.tree(), resource).get("checked") != "true":
                self._tap_resource(resource)

        # The selected schedule label hides when it is the only schedule. Use
        # QueueEditorScreen's existing overflow → Schedules path instead.
        self._open_schedule_list()
        toggle, _ = self.smoke.wait_content_description(
            lambda desc: desc == "Enable Weekdays", "Weekdays schedule enabled switch")
        require(toggle.get("checked") == "true",
                "New Weekdays schedule is not enabled before fixture-safety disable")
        self.smoke.tap(toggle)
        self._tap_exact_text("Done")
        save, _ = self.smoke.wait_node("Save", resource="queue_editor_save")
        self.smoke.tap(save)
        self.smoke.wait_content_description(
            lambda desc: desc.startswith("Night, ") and "Off:" in desc
            and "01:00" in desc and "07:00" in desc
            and "unmetered" in desc and "charging" in desc,
            "saved disabled Night queue summary")
        card, _ = self.smoke.wait_content_description(
            lambda desc: desc.startswith(self.args.queue_name + ", ")
            and "01:00" in desc and "07:00" in desc
            and "Off:" in desc and "unmetered" in desc and "charging" in desc,
            "persisted Night queue card summary")
        require("0 files" in card.get("content-desc", ""),
                "Disposable Night queue unexpectedly contains workload")
        # Reopen and validate persisted values in the editor, not just the card.
        self.smoke.tap(card)
        tree = self.smoke.tree()
        for resource, expected in (
                ("queue_editor_day_mon", "true"),
                ("queue_editor_day_tue", "true"),
                ("queue_editor_day_wed", "true"),
                ("queue_editor_day_thu", "true"),
                ("queue_editor_day_fri", "true"),
                ("queue_editor_day_sat", "false"),
                ("queue_editor_day_sun", "false"),
                ("queue_editor_charging", "true"),
                ("queue_editor_unmetered", "true")):
            node = self._unique_resource(tree, resource)
            actual = node.get("checked")
            require(actual == expected,
                    f"Persisted {resource}={actual!r}; expected {expected!r}")
        for description, expected in (("Start time", "01:00"), ("Stop time", "07:00")):
            node = self._unique_description_prefix(tree, description)
            require(expected in node.get("content-desc", "") or expected in node.get("text", ""),
                    f"Persisted {description} does not show {expected}")
        # QueueEditorScreen hides the selected schedule label for a sole schedule;
        # use its title area/options affordance to reopen the schedule list.
        self._open_schedule_list()
        toggle, _ = self.smoke.wait_content_description(
            lambda desc: desc == "Enable Weekdays", "persisted Weekdays schedule switch")
        require(toggle.get("checked") == "false",
                "Weekdays schedule is enabled after save/reopen")
        self._tap_exact_text("Done")
        # Leave the reopened, persisted editor visible for the dedicated captures.
        self.smoke.wait_node("Save", resource="queue_editor_save")

    def _open_schedule_list(self):
        self._tap_resource("queue_editor_more")
        option, _ = self.smoke.wait_node("Schedules")
        self.smoke.tap(option)

    def _tap_exact_text(self, text):
        tree = self.smoke.tree()
        found = [n for n in tree.iter("node") if n.get("text") == text and n.get("enabled") != "false"]
        require(len(found) == 1, f"Expected exactly one enabled {text!r} control; got {len(found)}")
        self.smoke.tap(found[0])

    def _unique_description_prefix(self, tree, prefix):
        found = [n for n in tree.iter("node")
                 if n.get("content-desc", "").startswith(prefix)]
        require(len(found) == 1, f"Expected exactly one content description starting {prefix!r}; got {len(found)}")
        return found[0]

    def _tap_description_prefix(self, prefix):
        node = self._unique_description_prefix(self.smoke.tree(), prefix)
        require(node.get("enabled") != "false", f"{prefix!r} control is disabled")
        self.smoke.tap(node)

    def tap_unique(self, tree, label, resource=None):
        found = [node for node in self.smoke_module.Smoke.matches(tree, label)
                 if not resource or node.get("resource-id", "").endswith(":id/" + resource)]
        require(len(found) == 1, f"Expected exactly one source-backed selector {label!r}/{resource!r}; got {len(found)}")
        self.smoke.tap(found[0])

    def navigate_c1_c2(self, key):
        """Create only a named disposable queue; schedule values remain operator-entered."""
        self.log(f"Unattended navigation for {key}; resource selectors are verified in Android source.")
        tree = self.smoke.tree()
        if key == "c1-empty":
            self.smoke.wait_node("Search downloads", timeout=25)
            tree = self.smoke.tree()
            self.tap_unique(tree, "Queues", "nav_queues")
            return
        if key == "c1-populated-stopped":
            self.smoke.wait_content_description(
                lambda desc: desc.startswith("Main, ") and "Double tap to edit" in desc,
                "Main queue card")
            tree = self.smoke.tree()
            self.tap_unique(tree, "New queue", "queue_new")
            field, _ = self.smoke.wait_content_description(
                lambda desc: desc == "Queue name", "queue name field")
            self.smoke.tap(field)
            self.shell("input", "text", self.args.queue_name.replace(" ", "%s"))
            button, _ = self.smoke.wait_node("Create")
            self.smoke.tap(button)
            # The create request persists the stopped queue and opens its editor.
            # Back leaves the untouched editor without making a second write.
            self.smoke.wait_node("Save", resource="queue_editor_save")
            self.shell("input", "keyevent", "4")
            self.smoke.wait_content_description(
                lambda desc: desc.startswith(self.args.queue_name + ", "),
                "created disposable queue card")
            return
        # Open only the disposable queue created above; never mutate Main.
        row, _ = self.smoke.wait_content_description(
            lambda desc: desc.startswith(self.args.queue_name + ", ") and "Double tap to edit" in desc,
            "disposable queue edit card")
        self.smoke.tap(row)

    def navigate_local_hosts(self, key):
        if key == "c6-unpaired":
            node, _ = self.smoke.wait_node("Settings", resource="nav_settings")
            self.smoke.tap(node)
            node, _ = self.smoke.wait_content_description(
                lambda desc: desc == "Hosts and web UI. Select or pair a Host", "Host settings category")
            self.smoke.tap(node)
            self.smoke.wait_node("Pair another Host")
        elif key == "c6-pairing-form":
            node, _ = self.smoke.wait_node("Pair another Host")
            self.smoke.tap(node)
            node, _ = self.smoke.wait_node("Connect", resource="pair_btn")
            require(node.get("enabled") == "false", "Blank pairing form must not connect")
        else:
            raise RuntimeError(f"Unsupported unattended host checkpoint {key}")

    def collect_failure(self):
        if not self.started:
            return
        try:
            tree = self.smoke.tree()
            ET.ElementTree(tree).write(
                self.evidence / "failure-ui.xml", encoding="utf-8", xml_declaration=True)
            if not getattr(self, "current_checkpoint", "").startswith("c6-"):
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
        self.log("Cleanup PASS: uninstalled only the probe package; no shared files, device settings, "
                 "or other packages were changed.")

    def run(self):
        self.check()
        if self.args.paired_server_dll:
            return self.run_paired()
        if self.args.unattended_c1_workload:
            return self.run_c1_workload()
        try:
            self.launch()
            checkpoints = CAPTURES
            if self.args.unattended_c1_c2:
                checkpoints = [item for item in CAPTURES if item[0] in
                               ("c1-empty", "c1-populated-stopped", "c2-no-schedule")]
                self.log("UNVERIFIED in unattended mode: running/action C1, configured C2, C5 and C6.")
            elif self.args.unattended_configured_c2:
                checkpoints = [item for item in CAPTURES if item[0] in
                               ("c1-empty", "c1-populated-stopped", "c2-configured", "c2-editor-footer")]
                self.log("UNVERIFIED: running/action C1, C5 and C6. No workload was created; Weekdays remains disabled.")
            elif self.args.unattended_local_hosts:
                checkpoints = [item for item in CAPTURES if item[0] in ("c6-unpaired", "c6-pairing-form")]
                self.log("UNVERIFIED in unattended local-host mode: paired C6 and both C5 switcher states.")
            for key, reference, anchor in checkpoints:
                self.capture_checkpoint(key, reference, anchor)
        except Exception:
            self.collect_failure()
            raise
        finally:
            self.clean()
        return self.evidence

    def run_c1_workload(self):
        """Use a fresh probe, ordinary UI and a bounded local HTTP workload."""
        states = load_module("c1_state_fixture", Path(__file__).with_name("capture-download-states-parity.py"))
        body = states.fixture_bytes()
        nonce = uuid.uuid4().hex[:10]
        names = {key: f"inbound-c1-{key}-{nonce}.bin" for key in ("main", "night", "mobile")}
        sent = {key: 0 for key in names}
        stop = threading.Event()
        class Handler(BaseHTTPRequestHandler):
            def respond(self, include_body):
                key = self.path.strip("/")
                if key not in names:
                    self.send_error(404); return
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Content-Disposition", f'attachment; filename="{names[key]}"')
                self.end_headers()
                if not include_body: return
                try:
                    for offset in range(0, len(body), states.CHUNK_SIZE):
                        if stop.is_set(): return
                        self.wfile.write(body[offset:offset + states.CHUNK_SIZE]); self.wfile.flush()
                        sent[key] = offset + states.CHUNK_SIZE
                        if stop.wait(0.25): return
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass
            def do_HEAD(self): self.respond(False)
            def do_GET(self): self.respond(True)
            def log_message(self, *_): pass
        server = None
        reverse = False
        server_started = False
        try:
            # Window begins at least one hour ahead and ends an hour later.
            # Local device time is read only; never change it.
            clock = self.shell("date", "+%H%M").strip()
            require(re.fullmatch(r"(?:[01][0-9]|2[0-3])[0-5][0-9]", clock), "Cannot establish emulator local time")
            start_hour = (int(clock[:2]) + 2) % 24
            stop_hour = (start_hour + 1) % 24
            self.c1_start, self.c1_stop = f"{start_hour:02}:00", f"{stop_hour:02}:00"
            def set_hour(hour):
                self._time_picker(hour % 12 or 12, "PM" if hour >= 12 else "AM")
            server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
            port = server.server_address[1]
            require(not re.search(rf"\btcp:{port}\b", self.adb("reverse", "--list")), "Reverse port already in use")
            threading.Thread(target=server.serve_forever, daemon=True).start()
            server_started = True
            self.adb("reverse", f"tcp:{port}", f"tcp:{port}"); reverse = True
            self.launch()
            self.smoke.wait_node("Search downloads")
            self.smoke.tap(self.smoke.wait_node("Queues", resource="nav_queues")[0])

            def card(name):
                return self.smoke.wait_content_description(
                    lambda d: d.startswith(name + ", ") and "Double tap to edit" in d,
                    name + " queue card")[0]
            def create(name):
                self.smoke.tap(self.smoke.wait_node("New queue", resource="queue_new")[0])
                self.smoke.tap(self.smoke.wait_content_description(lambda d: d == "Queue name", "queue name")[0])
                self.shell("input", "text", name)
                self.smoke.tap(self.smoke.wait_node("Create")[0])
                self.smoke.wait_node("Save", resource="queue_editor_save")
                self.shell("input", "keyevent", "4")
                card(name)
            create("Night")
            self.smoke.tap(card("Night"))
            self.smoke.wait_node("Choose or add a schedule")
            self._tap_exact_text("Choose or add a schedule")
            self.smoke.tap(self.smoke.wait_node("+  Add schedule")[0])
            self.smoke.tap(self.smoke.wait_node("Schedule name")[0]); self.shell("input", "text", "Daily")
            self.smoke.tap(self.smoke.wait_node("Continue")[0])
            self._tap_description_prefix("Start time"); set_hour(start_hour)
            self._tap_description_prefix("Stop time")
            self.smoke.tap(self.smoke.wait_node("Choose time")[0]); set_hour(stop_hour)
            self._open_schedule_list()
            toggle, _ = self.smoke.wait_content_description(lambda d: d == "Enable Daily", "enabled Daily schedule")
            require(toggle.get("checked") == "true", "Night schedule is not enabled")
            self._tap_exact_text("Done")
            self.smoke.tap(self.smoke.wait_node("Save", resource="queue_editor_save")[0])
            self.smoke.wait_content_description(lambda d: d.startswith("Night, Scheduled for ")
                                                and self.c1_start in d.split(". ", 1)[0]
                                                and self.c1_start in d and self.c1_stop in d,
                                                "enabled future Night schedule")
            create("Mobile")
            require(", Stopped." in card("Mobile").get("content-desc", ""), "Mobile is not stopped")
            self.smoke.tap(self.smoke.wait_node("Downloads", resource="nav_downloads")[0])

            def add(key, queue):
                self.smoke.tap(self.smoke.wait_node("Add", resource="fab")[0])
                self.smoke.tap(self.smoke.wait_node("Link or several links")[0])
                self.smoke.wait_node("New download")
                address = self._unique_resource(self.smoke.tree(), "address")
                self.smoke.tap(address); self.shell("input", "text", f"http://127.0.0.1:{port}/{key}")
                self.shell("input", "keyevent", "KEYCODE_BACK")  # dismiss the keyboard
                # A successful metadata probe sets the server-provided filename.
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    tree = self.smoke.tree()
                    name_field = self._unique_resource(tree, "file_name")
                    if name_field.get("text") == names[key]: break
                    time.sleep(.4)
                else: raise RuntimeError(f"Metadata probe did not populate fixture filename for {key}")
                timing, _ = self.smoke.wait_content_description(lambda d: d == "Wait for queue", "queue timing")
                self.smoke.tap(timing)
                timing, _ = self.smoke.wait_content_description(lambda d: d == "Wait for queue", "queue timing")
                require(timing.get("checked") == "true", "Queue timing was not selected")
                tree = self.smoke.tree()
                choices = [n for n in tree.iter("node")
                           if n.get("resource-id", "").endswith(":id/text_input_end_icon")
                           and n.get("content-desc") == "Choose queue"
                           and n.get("clickable") == "true" and n.get("enabled") == "true"]
                require(len(choices) == 1, "Expected one enabled Choose queue dropdown icon")
                self.smoke.tap(choices[0])
                option, _ = self.smoke.wait_node(queue)
                self.smoke.tap(option)
                selected = [n for n in self.smoke.tree().iter("node")
                            if n.get("class", "").endswith("EditText")
                            and n.get("content-desc") == "Choose queue" and n.get("text") == queue]
                require(len(selected) == 1, "The New download form did not retain the selected queue")
                submit, _ = self.smoke.wait_node("Download", resource="new_download_btn")
                require(submit.get("enabled") == "true", "Download submission is disabled")
                self.smoke.tap(submit)
                self.smoke.wait_content_description(lambda d: d.startswith(names[key] + ". "), "fixture row")
            # Add stopped workloads first so the live Main has maximum capture time.
            add("night", "Night"); add("mobile", "Mobile")
            self.smoke.tap(self.smoke.wait_node("Queues", resource="nav_queues")[0])
            main = card("Main").get("content-desc", "")
            if ", Running" not in main and ", Ready for downloads" not in main:
                self.smoke.tap(self.smoke.wait_content_description(lambda d: d == "Start Main", "Start Main")[0])
                self.smoke.wait_content_description(lambda d: d == "Stop Main", "Stop Main")
            self.smoke.tap(self.smoke.wait_node("Downloads", resource="nav_downloads")[0])
            add("main", "Main")
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline and sent["main"] == 0: time.sleep(.5)
            before = sent["main"]; time.sleep(2); after = sent["main"]
            require(0 < before < after < len(body), "Main fixture did not show changing nonterminal bytes")
            self.smoke.tap(self.smoke.wait_node("Queues", resource="nav_queues")[0])
            main = card("Main").get("content-desc", "")
            night = card("Night").get("content-desc", "")
            mobile = card("Mobile").get("content-desc", "")
            require(", Running" in main and "1 of 1 running" in main and "left" in main, "Main card lacks running known-byte workload")
            require(night.startswith("Night, Scheduled for ") and self.c1_start in night.split(". ", 1)[0]
                    and self.c1_stop in night and "1 file" in night, "Night card lacks scheduled workload")
            require(", Stopped." in mobile and "1 file" in mobile, "Mobile card lacks stopped workload")
            (self.evidence / "c1-workload.json").write_text(json.dumps({"names": names, "bytes_before": before,
                "bytes_after": after, "fixture_bytes": len(body), "cards": [main, night, mobile], "device_clock": clock,
                "schedule_start": self.c1_start, "schedule_stop": self.c1_stop}, indent=2) + "\n", encoding="utf-8")
            self.capture_checkpoint("c1-workload", "c1-queues.png", "Main")
            node = card("Mobile")
            bounds = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
            require(bounds, "Mobile card has invalid bounds")
            left, top, right, bottom = map(int, bounds.groups())
            require(left < right and top < bottom, "Mobile card is not visible")
            x, y = left + (right - left) // 4, (top + bottom) // 2
            self.shell("input", "swipe", str(x), str(y), str(x), str(y), "900")
            self.smoke.wait_node("Edit schedule")
            self.capture_checkpoint("c1-queue-actions", "c1-queues.png", "Edit schedule")
            self.log("C1 workload: fixture bytes and queue cards recorded; visual review and actual schedule execution remain open.")
        except Exception:
            self.collect_failure(); raise
        finally:
            pending = sys.exc_info()[0] is not None
            errors = []
            stop.set()
            # Try each owned cleanup independently, preserving the capture error.
            for label, action in (
                ("fixture shutdown", lambda: server.shutdown() if server_started else None),
                ("fixture close", lambda: server.server_close() if server is not None else None),
                ("reverse removal", lambda: self.adb("reverse", "--remove", f"tcp:{port}") if reverse else None),
                ("probe uninstall", self.clean),
            ):
                try: action()
                except Exception as exc:
                    errors.append(f"{label}: {exc}")
                    self.log(f"Cleanup WARNING: {label}: {exc}")
            self.log("Retention: task-named partial/shared Download files are not deleted; inspect exact paths before any removal.")
            if errors and not pending:
                raise RuntimeError("C1 cleanup incomplete: " + "; ".join(errors))
        return self.evidence

    def run_paired(self):
        import hashlib
        from urllib.parse import urlsplit

        fixture = load_module("paired_host_parity_fixture", Path(__file__).with_name("paired-host-parity-fixture.py"))
        refs = {key: (reference, anchor) for key, reference, anchor in CAPTURES}

        def record(key):
            self.capture_checkpoint(key, *refs[key])

        def tap(label, resource=None):
            node, _ = self.smoke.wait_node(label, resource=resource)
            self.smoke.tap(node)

        def enter(label, resource, value, *, secret=False):
            tree = self.smoke.tree()
            fields = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/" + resource)]
            require(len(fields) == 1, f"Pairing form does not show the {label} field")
            self.smoke.tap(fields[0])
            try:
                self.shell("input", "text", value.replace(" ", "%s"))
            except Exception:
                if secret:
                    raise RuntimeError("Could not enter the disposable pairing password") from None
                raise

        def host_chip(name):
            node, _ = self.smoke.wait_content_description(
                lambda desc: desc == f"Downloads shown from {name}. Change host", f"{name} host chip")
            self.smoke.tap(node)

        with fixture.running_fixture(self.args.paired_server_dll) as owned:
            port = urlsplit(owned["base_url"]).port
            endpoint = f"tcp:{port}"
            require(not any(endpoint in line.split() for line in self.adb("reverse", "--list").splitlines()),
                    "The disposable fixture port already has a reverse mapping")
            self.adb("reverse", endpoint, endpoint)
            try:
                self.launch()
                self.current_checkpoint = "c6-pairing-form"  # Never retain pairing-field UI or logs on failure.
                host_chip("This phone")
                tap("Connect to another Inbound", "host_connect")
                self.smoke.wait_node("Connect", resource="pair_btn")
                enter("Address", "pair_address", owned["base_url"])
                self.shell("input", "keyevent", "4")  # Dismiss address keyboard before locating the password field.
                enter("Password of that Inbound", "pair_password", owned["password"], secret=True)
                self.shell("input", "keyevent", "4")
                connect, tree = self.smoke.wait_node("Connect", resource="pair_btn")
                require(not any(n.get("resource-id", "").endswith(":id/pair_plain_http") for n in tree.iter("node")),
                        "Expected the app's loopback-only exception, not external plain HTTP")
                require(connect.get("enabled") == "true", "Fixture-only pairing did not enable Connect")
                self.smoke.tap(connect)
                self.smoke.wait_node("Files are saved on Parity Host", timeout=30)
                self.current_checkpoint = "c6-paired"
                fixture.seed_download(owned)
                path = Path(owned["download_root"]) / "c5-c6.bin"
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    if path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest() == owned["payload_sha256"]:
                        break
                    time.sleep(0.25)
                else:
                    raise RuntimeError("Disposable Host did not complete the hash-matched payload")
                self.smoke.wait_node("c5-c6.bin")
                record("c6-paired")
                host_chip("Parity Host")
                self.smoke.wait_content_description(
                    lambda desc: desc.startswith("Parity Host, ") and ", shown now" in desc, "selected fixture Host")
                record("c5-paired-selected")
                node, _ = self.smoke.wait_content_description(lambda desc: desc.startswith("This phone, "), "This phone row")
                self.smoke.tap(node)
                host_chip("This phone")
                self.smoke.wait_content_description(
                    lambda desc: desc.startswith("This phone, ") and ", shown now" in desc, "selected phone Host")
                record("c5-phone-selected")
                self.log("PASS remote Host: hash-matched fixture in Host storage, paired and phone switcher captures.")
            finally:
                try:
                    self.shell("rm", "-f", self.smoke.xml_path)
                finally:
                    try:
                        self.clean()
                    finally:
                        self.adb("reverse", "--remove", endpoint)
                        self.log("Cleanup PASS: removed the owned fixture reverse.")
        return self.evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt", help="Path to Android build-tools aapt(.exe)")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True, help="Explicit app.inbound.android.rcloneprobe APK")
    parser.add_argument("--evidence", type=Path, required=True, help="New, previously absent evidence directory")
    parser.add_argument("--queue-name", default="Parity Queue",
                        help="Exact disposable queue name to enter manually")
    parser.add_argument("--host-name", default="Parity Host",
                        help="Exact disposable paired-host label to enter manually")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--unattended-c1-workload", action="store_true",
                      help="Capture real local Main, scheduled Night and stopped Mobile workloads")
    mode.add_argument("--unattended-c1-c2", action="store_true",
                      help="Capture empty and stopped C1 plus the disposable queue's no-schedule C2")
    mode.add_argument("--unattended-configured-c2", action="store_true",
                      help="Opt-in API 33 lane: create only the named Night queue, configure/save/reopen its disabled schedule, and capture editor/footer; no workload")
    mode.add_argument("--unattended-local-hosts", action="store_true",
                      help="Capture unpaired Host settings and a blank pairing form without connecting")
    mode.add_argument("--paired-server-dll", type=Path,
                      help="Capture C5/C6 with the explicit built Server DLL and an owned loopback fixture")
    args = parser.parse_args()
    args.instructions = {
        "c1-empty": "Select This phone → Queues; capture the empty/ready Main queue.",
        "c1-populated-stopped": "Create the named disposable queue, leave it stopped, and show its card beside Main.",
        "c1-populated-running": "Start that same disposable queue with no downloads; capture its ready/running state.",
        "c1-queue-actions": "Open that queue's long-press actions sheet; do not invoke a destructive action.",
        "c2-no-schedule": "Open the named queue's schedule editor with no schedule configured.",
        "c2-configured": "Configure the fixture schedule from the runbook and show its saved summary.",
        "c2-editor-footer": "Open the schedule editor at the configured value and scroll to its footer.",
        "c5-phone-selected": "Open Where downloads run; show This phone selected and the paired fixture host listed.",
        "c5-paired-selected": "Select the disposable paired host and reopen Where downloads run to show the remote selection.",
        "c6-unpaired": "Open Settings → Hosts and web UI before pairing; use the phone-local host.",
        "c6-pairing-form": "Open Connect to another Inbound and capture the blank address/password form; enter fixture values only after this capture.",
        "c6-paired": "Pair to the disposable fixture Host, select it, and show its populated Downloads list and remote destination banner.",
    }
    if args.unattended_c1_c2:
        args.instructions["c5-phone-selected"] = "C5 host switching remains manual and unverified."
        args.instructions["c5-paired-selected"] = "C5 host switching remains manual and unverified."
        args.instructions["c6-unpaired"] = "C6 pairing remains manual and unverified."
        args.instructions["c6-pairing-form"] = "C6 pairing remains manual and unverified."
        args.instructions["c6-paired"] = "C6 pairing remains manual and unverified."
    if args.unattended_configured_c2:
        args.queue_name = "Night"
        args.instructions["c1-empty"] = "Capture empty Main/Queues before creating Night."
        args.instructions["c1-populated-stopped"] = "Create only the disposable Night queue; add no downloads."
        args.instructions["c2-configured"] = "Configure Night as disabled Mon–Fri 01:00–07:00 with charging and unmetered required; capture persisted editor."
        args.instructions["c2-editor-footer"] = "Capture the reopened persisted Night editor footer separately."
    capture = Capture(args)
    print(f"Evidence: {capture.run()}")


if __name__ == "__main__":
    main()
