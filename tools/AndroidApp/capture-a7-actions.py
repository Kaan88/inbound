"""Opt-in A7 notification Open/Pause all check on the dedicated API 33 probe."""
import argparse
import importlib.machinery
import re
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
states = importlib.machinery.SourceFileLoader("a7_existing_states", str(HERE / "capture-download-states-parity.py")).load_module()
queues = importlib.machinery.SourceFileLoader("a7_existing_queues", str(HERE / "capture-queue-host-parity.py")).load_module()


def require(ok, message):
    if not ok: raise RuntimeError(message)


def run(args):
    args.active_only = True
    args.details_flow = False
    args.mixed_a2 = args.unattended_b5 = False
    args.queue_name = "A7 Fixture"
    args.host_name = "Parity Host"
    cap = states.Capture(args)
    smoke = cap.smoke
    validated = False
    errors = []
    shade_open = False

    def focus():
        return next((line for line in cap.shell("dumpsys", "window").splitlines()
                     if "mCurrentFocus=" in line), "")

    def single_job():
        tree = smoke.tree()
        all_counts = [n for n in tree.iter("node") if n.get("text") == "All 1" and n.get("checked") == "true"]
        rows = [n for n in tree.iter("node") if n.get("content-desc", "").startswith(cap.names["active"] + ". ")]
        require(len(all_counts) == 1 and len(rows) == 1, "Probe must contain exactly the owned fixture job")
        return rows[0]

    def notification_action(label):
        nonlocal shade_open
        cap.shell("cmd", "statusbar", "expand-notifications")
        shade_open = True
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            keys = cap.shell("cmd", "notification", "list").splitlines()
            owned_pattern = r"^0\|" + re.escape(states.PACKAGE) + r"\|1001\|[A-Za-z0-9_.|:-]+$"
            owned = [line for line in keys if re.fullmatch(owned_pattern, line.strip())]
            require(len(owned) == 1, "Expected exactly one owned ongoing notification")
            # The collapsed shade can omit the app's name and notification body.
            # Read the exact owned record and use its title only if unique in the shade.
            record = cap.shell("cmd notification get '" + owned[0].strip() + "'")
            match = re.search(r"android\.title=String \(([^\r\n]*)\)", record)
            own_title = match[1] if match and match[1].startswith("1 download") else None
            tree = smoke.tree()
            rows = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/expandableNotificationRow")
                    and any(x.get("resource-id") == "android:id/title" and x.get("text", "").startswith("1 download") for x in n.iter("node"))
                    and any("Inbound Probe" in (x.get("text", ""), x.get("content-desc", ""))
                            or (own_title and x.get("resource-id") == "android:id/title" and x.get("text") == own_title)
                            for x in n.iter("node"))]
            require(len(rows) <= 1, "Fixture notification is ambiguous")
            if len(rows) == 1:
                row = rows[0]
                actions = [n for n in row.iter("node") if n.get("text", "").casefold() == label.casefold()
                           and n.get("clickable") == "true" and n.get("enabled") == "true"]
                fixture_visible = any(cap.names["active"] in (n.get("text", "") + " " + n.get("content-desc", ""))
                                      for n in row.iter("node"))
                if len(actions) == 1 and fixture_visible:
                    return actions[0]
                expand = [n for n in row.iter("node") if n.get("resource-id", "").endswith(":id/expand_button")
                          and n.get("content-desc", "").casefold() == "expand" and n.get("clickable") == "true"]
                if len(expand) == 1: smoke.tap(expand[0])
            time.sleep(.3)
        raise RuntimeError("Fixture notification action was not uniquely available: " + label)

    try:
        # Reuse exact emulator, API, absent-package and APK-badging checks.
        queues.Capture.check(cap)
        validated = True
        cap.start_fixture()
        cap.install_and_launch()  # exact installed-APK ownership check on timeout
        cap.shell("pm", "grant", states.PACKAGE, "android.permission.POST_NOTIFICATIONS")
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY, "--ez", "skip_first_run", "true",
                "--es", "add_url", f"http://127.0.0.1:{cap.port}/active")
        cap.started = True
        smoke.wait_node("Search downloads")
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline and cap.sent["active"] < 256 * 1024: time.sleep(.2)
        require(0 < cap.sent["active"] < states.FILE_SIZE, "Fixture did not start")
        single_job()
        cap.shell("input", "keyevent", "KEYCODE_HOME")
        smoke.tap(notification_action("Open"))
        shade_open = False
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if states.PACKAGE in focus():
                tree = smoke.tree()
                if any(n.get("resource-id", "").endswith(":id/nav_downloads") and n.get("selected") == "true" for n in tree.iter("node")):
                    break
            time.sleep(.3)
        else: raise RuntimeError("Notification Open did not return to probe Downloads")
        single_job()
        cap.log("PASS A7 Open: the owned ongoing notification returned to probe Downloads.")
        # Release the fixture's 512 KiB gate before testing Pause, so the server
        # cannot artificially make a still-running job appear stopped.
        cap.capture_gate.set()
        before = cap.sent["active"]
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline and cap.sent["active"] <= before: time.sleep(.2)
        require(cap.sent["active"] > before, "Fixture did not progress before Pause all")
        smoke.tap(notification_action("Pause all"))
        cap.shell("cmd", "statusbar", "collapse")
        shade_open = False
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY)
        smoke.wait_node("Search downloads")
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if ". paused" in single_job().get("content-desc", "").casefold(): break
            time.sleep(.3)
        else: raise RuntimeError("Fixture job did not become paused")
        # Allow an already-buffered chunk to settle; then independently observe
        # the fixture counter remain unchanged while the app still shows Paused.
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            first = cap.sent["active"]; time.sleep(2)
            if cap.sent["active"] == first: break
        else: raise RuntimeError("Fixture stream did not settle after Pause all")
        first = cap.sent["active"]; time.sleep(4); second = cap.sent["active"]
        require(0 < first == second < states.FILE_SIZE, "Fixture bytes advanced or job completed after Pause")
        require(". paused" in single_job().get("content-desc", "").casefold(), "Fixture job is no longer paused")
        cap.log(f"PASS A7: owned notification Open returned to Downloads; Pause all paused the sole fixture job; {first} bytes unchanged for four seconds.")
    finally:
        import sys
        pending = sys.exc_info()[0] is not None
        cap.capture_gate.set()
        if shade_open and validated:
            try: cap.shell("cmd", "statusbar", "collapse")
            except Exception: errors.append("notification shade")
        if cap.fixture is not None:
            for label, operation in (("fixture shutdown", cap.fixture.shutdown),
                                     ("fixture close", cap.fixture.server_close),
                                     ("fixture thread", lambda: cap.fixture_thread.join(timeout=5))):
                try: operation()
                except Exception: errors.append(label)
        if cap.reversed:
            try: cap.adb("reverse", "--remove", f"tcp:{cap.port}")
            except Exception: errors.append("owned reverse")
        if cap.installed:
            try:
                cap.adb("uninstall", states.PACKAGE)
                require(f"package:{states.PACKAGE}" not in cap.shell("pm", "list", "packages", states.PACKAGE), "Probe remains installed")
            except Exception: errors.append("probe uninstall")
        if validated:
            try: cap.shell("rm", "-f", cap.xml_path)
            except Exception: errors.append("temporary UI dump")
        cap.log("Retention: any owned incomplete shared Download part is left for exact ownership inspection.")
        if validated and not errors:
            cap.log("Cleanup PASS: uninstalled only the probe package; owned reverse and fixture stopped.")
        if errors:
            cap.log("Cleanup incomplete: " + ", ".join(errors))
            if not pending: raise RuntimeError("A7 cleanup incomplete")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    run(parser.parse_args())


if __name__ == "__main__": main()
