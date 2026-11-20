"""Opt-in A6 queue, nested destination and two-range probe on inbound33."""
import argparse
import importlib.machinery
import itertools
import json
import re
import time
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
host = importlib.machinery.SourceFileLoader("a6_queue_runner", str(HERE / "capture-queue-host-parity.py")).load_module()
fixture_module = importlib.machinery.SourceFileLoader("a6_range_fixture", str(HERE / "a6_range_fixture.py")).load_module()
downloads_runner = importlib.machinery.SourceFileLoader("a6_download_runner", str(HERE / "capture-download-states-parity.py")).load_module()


def require(ok, message):
    if not ok: raise RuntimeError(message)


def unmetered(dump):
    match = re.search(r"Active default network:\s*(?:Network\{)?(\d+)", dump, re.I)
    if not match: return False
    network = match.group(1)
    records = re.split(r"(?=NetworkAgentInfo\{)", dump)
    candidates = [r for r in records if re.match(r"NetworkAgentInfo\{", r)
                  and re.search(r"network\{" + re.escape(network) + r"\}", r, re.I)]
    # Only trust the matching agent's own line; never infer from another network
    # or from unrelated later diagnostics. Multiline/unknown formats fail closed.
    return len(candidates) == 1 and bool(re.search(r"\bNOT_METERED\b", candidates[0].splitlines()[0]))


def run(args):
    args.queue_name = "A6" + uuid.uuid4().hex[:8]
    args.host_name = "Parity Host"
    args.unattended_c1_c2 = True
    args.unattended_configured_c2 = args.unattended_local_hosts = False
    args.unattended_c1_workload = args.paired_server_dll = False
    args.mixed_hosts = False
    args.active_only = True
    args.details_flow = False
    cap = host.Capture(args)
    smoke = cap.smoke
    nonce = uuid.uuid4().hex[:10]
    folder1, folder2 = "inbound-a6-" + nonce, "nested-" + nonce
    root = "/sdcard/Download/" + folder1
    target = root + "/" + folder2
    filename = "inbound-a6-" + nonce + ".bin"
    path = target + "/" + filename
    created = []
    reverse = None
    cleanup_errors = []
    preflight_ok = False
    install_stopped = False
    details_open = False

    def tap(label, resource=None):
        node, _ = smoke.wait_node(label, resource=resource)
        smoke.tap(node)

    def desc(exact):
        return smoke.wait_content_description(lambda s: s == exact, exact)[0]

    def node(resource):
        return cap._unique_resource(smoke.tree(), resource)

    def bounds(v):
        parts = [int(x) for x in re.findall(r"\d+", v.get("bounds", ""))]
        require(len(parts) == 4 and parts[0] < parts[2] and parts[1] < parts[3], "Missing control bounds")
        return parts

    def absent(p):
        return smoke.shell(f"test -e {p} && echo PRESENT || echo ABSENT").strip() == "ABSENT"

    def card():
        return smoke.wait_content_description(lambda d: d.startswith(args.queue_name + ", ") and "Double tap to edit" in d,
                                              "owned queue")[0]

    def cleanup_paths():
        if not absent(path):
            result = smoke.shell("sha256sum", path).strip().split()
            require(result == [fixture_module.SHA256, path], "Owned final file has unexpected contents; retained")
            smoke.shell("rm", "-f", path)
            require(absent(path), "Owned final file remains")
        for directory in reversed(created):
            smoke.shell("rmdir", directory)  # refuses non-empty dirs, including retained partials
            require(absent(directory), "Owned fixture folder remains")

    try:
        cap.check()
        preflight_ok = True
        require(unmetered(smoke.shell("dumpsys", "connectivity")), "Cannot verify active default network is unmetered")
        require(absent(root), "Unique fixture folder already exists")
        smoke.shell("mkdir", root)
        created.append(root)
        smoke.shell("mkdir", target)
        created.append(target)
        cap.log(f"Owned A6 fixture directory: {target}")
        with fixture_module.running_fixture(filename) as owned:
            endpoint = "tcp:" + str(owned["port"])
            require(not any(endpoint in line.split() for line in smoke.adb("reverse", "--list").splitlines()),
                    "Fixture reverse already exists")
            smoke.adb("reverse", "--no-rebind", endpoint, endpoint)
            reverse = endpoint
            # Reuse the download runner's exact-APK ownership check on an install timeout.
            downloads_runner.Capture.install_and_launch(cap)
            smoke.adb("shell", "am", "start", "-W", "-n", host.ACTIVITY, "--ez", "skip_first_run", "true")
            cap.started = True
            smoke.wait_node("Search downloads")
            tap("Settings", "nav_settings")
            smoke.tap(desc("Network. Network rule and speed limit"))
            global_rule = desc("Unmetered network only")
            require(global_rule.get("checked") == "false" and global_rule.get("enabled") == "true",
                    "Global unmetered rule is not verifiably off")
            smoke.shell("input", "keyevent", "4")
            tap("Queues", "nav_queues")
            tap("New queue", "queue_new")
            smoke.tap(desc("Queue name")); smoke.shell("input", "text", args.queue_name)
            tap("Create")
            switch = node("queue_editor_unmetered")
            require(switch.get("checked") in ("true", "false"), "Queue rule is unavailable")
            if switch.get("checked") == "false": smoke.tap(switch)
            require(node("queue_editor_unmetered").get("checked") == "true", "Queue rule did not enable")
            tap("Save", "queue_editor_save")
            smoke.tap(card())
            require(node("queue_editor_unmetered").get("checked") == "true", "Queue rule was not saved")
            smoke.shell("input", "keyevent", "4")
            card()
            smoke.tap(desc("Start " + args.queue_name))
            desc("Stop " + args.queue_name)
            tap("Downloads", "nav_downloads")
            tap("Add", "fab")
            tap("Link or several links")
            smoke.wait_node("New download")
            smoke.tap(node("address")); smoke.shell("input", "text", f"http://127.0.0.1:{owned['port']}/a6.bin")
            smoke.shell("input", "keyevent", "4")
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if node("file_name").get("text") == filename: break
                time.sleep(.4)
            else: raise RuntimeError("Fixture metadata name did not arrive")
            save = node("save_to")
            require(save.get("clickable") == "true" and save.get("enabled") == "true", "Local folder picker unavailable")
            smoke.tap(save)
            smoke.wait_content_description(lambda d: d.startswith("Folder ") and d.endswith("/Download"), "Download folder")
            smoke.tap(desc(folder1)); smoke.tap(desc(folder2))
            smoke.wait_content_description(lambda d: d.startswith("Folder ") and d.endswith("/" + folder1 + "/" + folder2), "nested folder")
            tap("Save here")
            save = node("save_to")
            require(folder1 in save.get("content-desc", "") and folder2 in save.get("content-desc", ""), "Destination not retained")
            left, top, right, bottom = bounds(node("connections_block"))
            _, _, _, header_bottom = bounds(node("connections_value"))
            require(top < header_bottom < bottom, "Connections slider area is missing")
            require(node("connections_value").get("text") == "8", "Expected the default eight-connection peer limit")
            # The slider occupies the last 48dp. Its 16dp track inset scales with the measured height.
            inset = (bottom - header_bottom) // 3
            second_step_x = left + inset + (right - left - 2 * inset) // 7
            slider_y = str((header_bottom + bottom) // 2)
            smoke.shell("input", "swipe", str(right - inset), slider_y, str(second_step_x), slider_y, "450")
            value = node("connections_value").get("text")
            require(value == "2", f"Connections slider displayed {value!r} instead of two")
            smoke.tap(desc("Wait for queue"))
            tree = smoke.tree()
            selectors = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/text_input_end_icon")
                         and n.get("content-desc") == "Choose queue" and n.get("enabled") == "true"]
            require(len(selectors) == 1, "Queue chooser unavailable")
            smoke.tap(selectors[0]); tap(args.queue_name)
            require(any(n.get("content-desc") == "Choose queue" and n.get("text") == args.queue_name
                        for n in smoke.tree().iter("node")), "Queue choice not retained")
            require(unmetered(smoke.shell("dumpsys", "connectivity")), "Active network no longer verified unmetered")
            tap("Download", "new_download_btn")
            row, _ = smoke.wait_content_description(lambda d: d.startswith(filename + ". "), "owned job")
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                snapshot = owned["snapshot"]()
                live = [r for r in snapshot["requests"] if r["active"] and r["overlap"] and r["bytes_sent"] > 0]
                pairs = [(a, b) for a, b in itertools.combinations(live, 2)
                         if a["id"] != b["id"] and (a["end"] < b["start"] or b["end"] < a["start"])]
                if pairs: break
                time.sleep(.2)
            else: raise RuntimeError("Two concurrent independent range workers not observed")
            (cap.evidence / "range-overlap.json").write_text(
                json.dumps(pairs[0], indent=2) + "\n", encoding="utf-8")
            smoke.tap(row)
            smoke.wait_node(filename, resource="details_title")
            details_open = True
            smoke.wait_node("2 connections", resource="connections_title")
            smoke.wait_content_description(lambda d: d.startswith("2 connections, "), "two segment bars")
            for label in ("Queue", args.queue_name, "Network", "Unmetered network only", "Saved to",
                          f"Downloads / {folder1} / {folder2}", "Part file in " + folder2):
                smoke.wait_node(label)
            initial_sent = {record["id"]: record["bytes_sent"] for record in pairs[0]}
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                current = owned["snapshot"]()
                current_pair = [r for r in current["requests"] if r["id"] in initial_sent]
                if len(current_pair) == 2 and all(r["active"] and r["bytes_sent"] > initial_sent[r["id"]]
                                                  for r in current_pair):
                    break
                time.sleep(.1)
            else: raise RuntimeError("Both concurrent range workers must advance before the capture")
            smoke.wait_node("2 connections", resource="connections_title")
            focus = next((line for line in smoke.shell("dumpsys", "window").splitlines() if "mCurrentFocus=" in line), "")
            require(host.PACKAGE in focus, "Probe foreground cannot be established")
            screenshot = cap.evidence / "a6-queue-nested-capture.png"
            screenshot.write_bytes(smoke.adb("exec-out", "screencap", "-p", raw=True))
            ET.ElementTree(smoke.tree()).write(cap.evidence / "a6-queue-nested-ui.xml", encoding="utf-8", xml_declaration=True)
            cap.pairer.make_pair(host.MOCKUPS / "a6-details.png", screenshot,
                                 cap.evidence / "a6-queue-nested-pair.png", "A6")
            (cap.evidence / "range-evidence.json").write_text(json.dumps({"workers_initial": pairs[0], "workers_later": current_pair,
                "global_unmetered": False, "queue_unmetered": True, "active_unmetered": True}, indent=2) + "\n", encoding="utf-8")
            owned["release"].set()
            deadline = time.monotonic() + 150
            while time.monotonic() < deadline:
                result = smoke.shell(f"if [ -f {path} ]; then sha256sum {path}; else echo MISSING; fi").strip().split()
                if result == [fixture_module.SHA256, path]: break
                time.sleep(.5)
            else: raise RuntimeError("Completed fixture hash not verified")
            (cap.evidence / "fixture.sha256").write_text(fixture_module.SHA256 + "\n", encoding="ascii")
            cap.log("PASS A6 queue, nested destination, concurrent ranges and final fixture hash; visual review remains open.")
    except Exception:
        if details_open:
            try:
                tree = smoke.tree()
                if any(n.get("text") == filename and n.get("resource-id", "").endswith(":id/details_title")
                       for n in tree.iter("node")):
                    ET.ElementTree(tree).write(cap.evidence / "details-diagnostic.xml", encoding="utf-8")
            except Exception:
                pass
        raise
    finally:
        import sys
        pending = sys.exc_info()[0] is not None
        if cap.installed:
            try:
                cap.clean()
                install_stopped = True
            except Exception:
                cleanup_errors.append("probe uninstall")
        else:
            install_stopped = True
        if reverse:
            try: smoke.adb("reverse", "--remove", reverse)
            except Exception: cleanup_errors.append("reverse")
        if created and install_stopped:
            try: cleanup_paths()
            except Exception: cleanup_errors.append("owned files/folders")
        elif created:
            cleanup_errors.append("owned files/folders retained while writer ownership is unresolved")
        if preflight_ok:
            try: smoke.shell("rm", "-f", smoke.xml_path)
            except Exception: cleanup_errors.append("temporary UI dump")
        if cleanup_errors:
            cap.log("Cleanup incomplete: " + ", ".join(cleanup_errors))
            if not pending: raise RuntimeError("A6 cleanup incomplete")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    run(parser.parse_args())


if __name__ == "__main__": main()
