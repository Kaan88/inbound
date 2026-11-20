"""Opt-in isolated API 33 A6/A7 global unmetered hold and recovery proof.

Uses test-build hooks to simulate metering; this is not real network-condition evidence.
"""
import argparse
import copy
import hashlib
import importlib.machinery
import json
import re
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
states = importlib.machinery.SourceFileLoader("wait_states", str(HERE / "capture-download-states-parity.py")).load_module()
queues = importlib.machinery.SourceFileLoader("wait_queues", str(HERE / "capture-queue-host-parity.py")).load_module()


def require(value, message):
    if not value:
        raise RuntimeError(message)


def run(args):
    args.queue_name = "Waiting fixture"
    args.host_name = "This phone"
    args.active_only = True
    args.details_flow = True  # Release the existing fixture gate for a fast, verified recovery.
    args.mixed_a2 = args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    validated = False
    own_outputs = False
    shade = False
    errors = []
    name = cap.names["active"]
    path = "/sdcard/Download/" + name
    digest = hashlib.sha256(states.fixture_bytes()).hexdigest()
    concurrent = args.verify_a7_concurrent
    active_before_hold = "queued"
    if concurrent:
        cap.names["prior-finished"] = f"inbound-a7-finished-{smoke.nonce}.bin"
        cap.sent["prior-finished"] = cap.b5_total_sent["prior-finished"] = 0
    keys = ("active", active_before_hold, "prior-finished") if concurrent else ("active",)
    paths = {key: "/sdcard/Download/" + cap.names[key] for key in keys}

    def absent(target=path):
        return cap.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() != "present"

    def exact(target):
        result = cap.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
        return result == [digest, target]

    def add(key):
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY, "--es", "add_url", f"http://127.0.0.1:{cap.port}/{key}")

    def hook(key, value):
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY, "--ez", key, value)

    def wait(predicate, label, timeout=45):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                value = predicate()
                if value is not None and value is not False:
                    return value
            except (ET.ParseError,):
                pass
            time.sleep(.3)
        raise RuntimeError("Timed out waiting for " + label)

    def tree_node(resource, text=None):
        nodes = [n for n in smoke.tree().iter("node") if n.get("resource-id", "").endswith(":id/" + resource)
                 and (text is None or n.get("text") == text)]
        return nodes[0] if len(nodes) == 1 else None

    def owned_row():
        nodes = [n for n in smoke.tree().iter("node") if n.get("clickable") == "true" and
                 n.get("content-desc", "").startswith(name + ". ")]
        return nodes[0] if len(nodes) == 1 else None

    def notification_key(number=1003):
        pattern = rf"^0\|{re.escape(states.PACKAGE)}\|{number}\|[A-Za-z0-9_.|:-]+$"
        matches = [line.strip() for line in cap.shell("cmd", "notification", "list").splitlines()
                   if re.fullmatch(pattern, line.strip())]
        require(len(matches) <= 1, "Ambiguous owned waiting notification")
        return matches[0] if matches else None

    try:
        queues.Capture.check(cap)
        validated = True
        require(all(absent(target) for target in paths.values()), "Unique fixture output already exists")
        own_outputs = True
        cap.start_fixture()
        cap.install_and_launch()
        cap.shell("pm", "grant", states.PACKAGE, "android.permission.POST_NOTIFICATIONS")
        hook("skip_first_run", "true")
        cap.started = True
        smoke.wait_node("Search downloads")
        if concurrent:
            hook("metered_override", "false")
            cap.capture_gate.set()
            add("prior-finished")
            wait(lambda: exact(paths["prior-finished"]), "prior finished fixture hash")
            cap.capture_gate.clear()
            add(active_before_hold)
            wait(lambda: cap.sent[active_before_hold] >= 512 * 1024, "nonzero reported running payload")
            ongoing = wait(lambda: notification_key(1001), "owned active notification")
            active_record = cap.shell("cmd notification get '" + ongoing + "'")
            require(cap.names[active_before_hold] in active_record, "Ongoing notice did not name the owned active file")
        hook("metered_override", "true")
        hook("wifi_only", "true")
        # Wait for the test hook's persisted setting through the real Settings UI.
        smoke.tap(smoke.wait_node("Settings", resource="nav_settings")[0])
        smoke.tap(smoke.wait_content_description(lambda d: d == "Network. Network rule and speed limit", "Network settings")[0])
        wait(lambda: next((n for n in smoke.tree().iter("node") if n.get("content-desc") == "Unmetered network only"
                           and n.get("checked") == "true"), None), "unmetered setting on")
        cap.shell("input", "keyevent", "4")
        smoke.tap(smoke.wait_node("Downloads", resource="nav_downloads")[0])
        add("active")
        row = wait(lambda: owned_row(), "owned waiting row")
        key = wait(notification_key, "owned waiting notice")
        before = len([r for r in cap.requests if r["file"] == "active" and r["method"] == "GET"])
        active_before = cap.sent[active_before_hold]
        time.sleep(5)
        after = len([r for r in cap.requests if r["file"] == "active" and r["method"] == "GET"])
        require(before == after == 0 and cap.sent["active"] == 0 and absent(), "Payload request or output while held")
        if concurrent:
            require(0 < active_before < cap.sent[active_before_hold] < states.FILE_SIZE,
                    "Nonresumable job did not advance alongside the held job")
            require(notification_key(1001) is not None, "Owned ongoing notification missing alongside waiting")
        smoke.tap(row)
        wait(lambda: tree_node("details_title", name), "owned Details")
        wait(lambda: tree_node("hero_rate", "Queued"), "queued Details state")
        require(tree_node("hero_main", "Resume") is not None, "Waiting Details does not offer Resume")
        require(tree_node("hero_stop") is None and tree_node("hero_again") is None, "Unexpected Stop or Download again while queued")
        wait(lambda: any(n.get("content-desc") == "Network, Unmetered network only" for n in smoke.tree().iter("node")),
             "unmetered network Details line")
        screenshot = cap.evidence / "a6-waiting-capture.png"
        screenshot.write_bytes(cap.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(smoke.tree()).write(cap.evidence / "a6-waiting-ui.xml", encoding="utf-8", xml_declaration=True)
        cap.pairer.make_pair(states.MOCKUPS / "a6-details.png", screenshot, cap.evidence / "a6-waiting-pair.png", "A6 waiting")
        record = cap.shell("cmd notification get '" + key + "'")
        require("Waiting for Wi-Fi" in record and "1 download will resume" in record and "Use data" in record,
                "Waiting notification record lacks expected title, body or action")
        cap.shell("cmd", "statusbar", "expand-notifications")
        shade = True
        def expandable(node):
            return node.get("resource-id", "").endswith(":id/expandableNotificationRow")

        def titles(row):
            return [n.get("text", "") for n in row.iter("node") if
                    n.get("resource-id", "").endswith((":id/title", ":id/notification_title"))]

        def owned(row):
            return any(t == "Waiting for Wi-Fi" or t == cap.names.get("prior-finished") or
                       t == "1 download" or t.startswith("1 download · ") for t in titles(row))

        def top_rows(tree):
            rows = []
            def collect(node):
                if expandable(node): rows.append(node)
                else:
                    for child in node: collect(child)
            collect(tree)
            return rows

        def write_owned(tree, name):
            sanitized = ET.Element("hierarchy")
            for row in top_rows(tree):
                if owned(row): sanitized.append(copy.deepcopy(row))
            ET.ElementTree(sanitized).write(cap.evidence / name, encoding="utf-8", xml_declaration=True)

        def notice():
            tree = smoke.tree()
            write_owned(tree, "a7-latest-shade-ui.xml")
            surface = [n for n in tree.iter("node") if expandable(n)]
            def expand(row):
                controls = [x for x in row.iter("node") if x.get("resource-id", "").endswith(":id/expand_button") and
                            "expand" in x.get("content-desc", "").casefold() and x.get("clickable") == "true"]
                if controls: smoke.tap(controls[0])

            for row in top_rows(tree):
                if "Waiting for Wi-Fi" in titles(row) and any(child is not row and expandable(child) for child in row.iter("node")):
                    if any(n.get("resource-id", "").endswith(":id/notification_title") and n.get("text") == "Waiting for Wi-Fi"
                           for n in row.iter("node")) and not any(n.get("resource-id") == "android:id/title" and n.get("text") == "Waiting for Wi-Fi"
                                                                   for n in row.iter("node")):
                        expand(row)  # Android nests multiple silent notices under one expandable row.
                        return None
            leaves = [n for n in surface if not any(child is not n and expandable(child) for child in n.iter("node"))]
            rows = [row for row in leaves if "Waiting for Wi-Fi" in titles(row)]
            require(len(rows) <= 1, "Waiting shade row is ambiguous")
            if not rows: return None
            actions = [x for x in rows[0].iter("node") if x.get("text") == "Use data" and
                       x.get("clickable") == "true" and x.get("enabled") == "true"]
            if len(actions) == 1:
                if concurrent:
                    others = [r for r in leaves if r is not rows[0]]
                    ongoing = [r for r in others if any(t == "1 download" or t.startswith("1 download · ") for t in titles(r))]
                    finished = [r for r in others if cap.names["prior-finished"] in titles(r)]
                    require(len(ongoing) == len(finished) == 1 and ongoing[0] is not finished[0]
                            and notification_key(1001) is not None and notification_key(1003) == key,
                            "Expected three separate owned notification rows in the captured shade")
                return tree
            expand(rows[0])
            return None
        tree = wait(notice, "waiting notification Use data control")
        screenshot = cap.evidence / "a7-waiting-capture.png"
        from io import BytesIO
        with Image.open(BytesIO(cap.adb("exec-out", "screencap", "-p", raw=True))) as native:
            draw = ImageDraw.Draw(native)
            for row in top_rows(tree):
                if owned(row): continue
                box = tuple(map(int, re.findall(r"\d+", row.get("bounds", ""))))
                if len(box) == 4: draw.rectangle(box, fill=native.getpixel((0, box[1])))
            native.save(screenshot)
        write_owned(tree, "a7-waiting-ui.xml")
        cap.pairer.make_pair(states.MOCKUPS / "a7-background.png", screenshot, cap.evidence / "a7-waiting-pair.png", "A7 waiting")
        if concurrent:
            use_data = [node for node in tree.iter("node") if node.get("text") == "Use data"
                        and node.get("enabled") == "true" and node.get("clickable") == "true"]
            require(len(use_data) == 1, "Expected one Use data action in the captured shade")
            smoke.tap(use_data[0])
        cap.shell("cmd", "statusbar", "collapse")
        shade = False
        if not concurrent:
            hook("metered_override", "false")
        cap.capture_gate.set()
        wait(lambda: all(exact(target) for target in paths.values()), "exact recovered output hashes", timeout=180)
        require(any(r["file"] == "active" and r["method"] == "GET" for r in cap.requests), "No owned recovery GET")
        (cap.evidence / "waiting-proof.json").write_text(json.dumps({"network": "test-hook simulated metered",
            "held_gets": [before, after], "held_bytes": 0, "notification_id": 1003,
            "recovery_gets": sum(r["file"] == "active" and r["method"] == "GET" for r in cap.requests),
            "sha256": digest, "concurrent_notices": concurrent, "use_data_tapped": concurrent}, indent=2) + "\n", encoding="utf-8")
        release = "Use data in the concurrent shade" if concurrent else "clearing simulated metering in the foreground"
        cap.log(f"PASS simulated metered hold: zero payload, waiting Details and notification; {release} recovered hash-matched payloads.")
    finally:
        pending = sys.exc_info()[0] is not None
        if shade and validated:
            try: cap.shell("cmd", "statusbar", "collapse")
            except Exception: errors.append("notification shade")
        stopped = not cap.installed
        if cap.installed:
            try:
                cap.shell("am", "force-stop", states.PACKAGE)
                require(cap.shell(f"if pidof {states.PACKAGE} >/dev/null; then echo running; else echo stopped; fi").strip() == "stopped", "Probe still running")
                stopped = True
            except Exception: errors.append("probe force-stop")
        cap.capture_gate.set()
        if cap.fixture is not None:
            try:
                cap.fixture.shutdown(); cap.fixture.server_close(); cap.fixture_thread.join(timeout=5)
            except Exception: errors.append("fixture shutdown")
        if cap.reversed:
            try: cap.adb("reverse", "--remove", f"tcp:{cap.port}")
            except Exception: errors.append("owned reverse")
        if cap.installed and stopped:
            try:
                cap.adb("uninstall", states.PACKAGE)
                require(f"package:{states.PACKAGE}" not in cap.shell("pm", "list", "packages", states.PACKAGE), "Probe remains installed")
            except Exception: errors.append("probe uninstall")
        if own_outputs and stopped:
            try:
                for target in paths.values():
                    if not absent(target):
                        require(exact(target), "Output differs; retained")
                        cap.shell("rm", "--", target)
                        require(absent(target), "Owned output remains")
                cap.shell("rm", "-f", cap.xml_path)
            except Exception: errors.append("owned output or UI dump; retained")
        if errors:
            cap.log("Cleanup incomplete: " + ", ".join(errors))
            if not pending: raise RuntimeError("Waiting capture cleanup incomplete")
        else:
            cap.log("Cleanup: probe and owned reverse removed; exact hash-matched output removed when present. Any incomplete part is retained.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--verify-a7-concurrent", action="store_true", help="Also verify ongoing and finished notices alongside waiting")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
