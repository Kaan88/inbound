"""Verify a normal local Resume reuses the synthetic sign-in on a later HTTP byte range."""

import argparse
from contextlib import ExitStack
import hashlib
import json
from pathlib import Path
import re
import time
import uuid
import xml.etree.ElementTree as ET

from importlib.util import module_from_spec, spec_from_file_location


HERE = Path(__file__).resolve().parent


def load(name):
    spec = spec_from_file_location(name, HERE / (name + ".py"))
    module = module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def until(predicate, description, timeout=35):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value is not None and value is not False:
            return value
        time.sleep(.25)
    raise RuntimeError(description)


def run(args):
    base = load("capture-link-intake-parity")
    owned_fixture = load("b3-auth-resume-fixture")
    cap = base.Capture(args)
    smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)
    read_tree = smoke.tree

    def fresh_tree():
        for attempt in range(4):
            try:
                return read_tree()
            except RuntimeError as exc:
                if str(exc) != "Android did not write a fresh UI hierarchy" or attempt == 3:
                    raise
                time.sleep(.3)

    smoke.tree = fresh_tree
    name = "inbound-b3-auth-" + uuid.uuid4().hex[:12] + ".zip"
    target = "/sdcard/Download/" + name
    payload = hashlib.shake_256(b"Inbound Android B3 authenticated resume fixture").digest(16 * 1024 * 1024)
    digest = hashlib.sha256(payload).hexdigest()
    (cap.evidence / "fixture.txt").write_text(
        f"Owned authenticated range fixture: {name}\nBytes: {len(payload)}\nSHA-256: {digest}\n",
        encoding="ascii")

    def target_present():
        return cap.shell(f"if [ -e '{target}' ] || [ -L '{target}' ]; then echo present; fi").strip() == "present"

    def target_digest():
        result = cap.shell(f"if [ -f '{target}' ] && [ ! -L '{target}' ]; then sha256sum '{target}'; fi").split()
        return result[0] if len(result) == 2 and result[1] == target else None

    def tap(label, resource=None):
        smoke.tap(smoke.wait_node(label, resource=resource)[0])

    def description(wanted):
        return smoke.wait_content_description(lambda value: value == wanted, wanted)[0]

    def web_link(label):
        def find():
            nodes = [node for node in smoke.matches(smoke.tree(), label)
                     if node.get("clickable") == "true" and node.get("enabled") == "true"]
            require(len(nodes) <= 1, "Ambiguous link in owned sign-in fixture")
            return nodes[0] if nodes else None
        smoke.tap(until(find, "Missing owned link " + label))

    def evidence(stem, tree):
        for node in tree.iter("node"):
            value = node.get("text", "") + " " + node.get("content-desc", "")
            require(node.get("password") != "true" and "inbound_test" not in value.lower(),
                    "Refusing evidence containing sign-in material")
        (cap.evidence / (stem + ".png")).write_bytes(cap.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(tree).write(cap.evidence / (stem + ".xml"), encoding="utf-8", xml_declaration=True)

    def set_one_connection():
        tap("Settings", "nav_settings")
        smoke.tap(description("Downloads. Folder and connections"))
        def connection_row():
            candidates = [node for node in smoke.tree().iter("node") if node.get("clickable") == "true"
                          and node.get("enabled") == "true"
                          and any(item.get("text") == "Connections per file" for item in node.iter("node"))]
            leaves = [node for node in candidates if not any(
                item is not node and item in candidates for item in node.iter("node"))]
            require(len(leaves) <= 1, "Ambiguous probe Connections per file row")
            return leaves[0] if leaves else None

        smoke.tap(until(connection_row, "Missing probe Connections per file setting"))
        tree = smoke.tree()
        fields = [node for node in tree.iter("node") if node.get("class") == "android.widget.EditText"
                  and node.get("content-desc") == "Connections per file"]
        require(len(fields) == 1, "Expected a unique probe connection setting field")
        smoke.tap(fields[0])
        cap.shell("input", "keycombination", "KEYCODE_CTRL_LEFT", "KEYCODE_A")
        cap.shell("input", "text", "1")
        cap.shell("input", "keyevent", "KEYCODE_BACK")  # Dismiss the number keyboard.
        tap("Apply")
        def setting_is_one():
            row = connection_row()
            return row is not None and any(node.get("text") == "1" for node in row.iter("node"))

        until(setting_is_one, "Probe did not save one connection per file")
        cap.shell("input", "keyevent", "KEYCODE_BACK")
        tap("Downloads", "nav_downloads")
        description("Downloads shown from This phone. Change host")

    require(not target_present(), "Owned fixture file already exists; preserving it")
    submitted = False
    with ExitStack() as stack:
        fixture = stack.enter_context(owned_fixture.serve(name, payload))
        port = fixture.server_address[1]
        endpoint = f"tcp:{port}"
        cap.adb("reverse", "--no-rebind", endpoint, endpoint)
        stack.callback(cap.adb, "reverse", "--remove", endpoint)

        def clean_probe():
            pending = None
            try:
                if submitted and smoke.installed:
                    cap.shell("am", "force-stop", base.PACKAGE)
                    require(cap.shell(f"if pidof {base.PACKAGE} >/dev/null; then echo running; else echo stopped; fi").strip() == "stopped",
                            "Probe writer did not stop; preserving output")
                    if target_present():
                        require(target_digest() == digest, "The exact fixture path has an unexpected file; preserving it")
                        cap.shell("rm", "--", target)
                        require(not target_present(), "Owned completed file remained after cleanup")
            except Exception as exc:
                pending = exc
            finally:
                smoke.clean(include_remote_files_payload=False)
            if pending:
                raise pending

        stack.callback(clean_probe)

        def record_failure(kind, _error, _traceback):
            if kind and smoke.started:
                try:
                    (cap.evidence / "b3-auth-resume-incomplete.json").write_text(
                        json.dumps(fixture.snapshot(), indent=2) + "\n", encoding="utf-8")
                    tree = read_tree()
                    if any(node.get("package") == base.PACKAGE for node in tree.iter("node")):
                        evidence("b3-auth-resume-failure", tree)
                except Exception:
                    pass  # Preserve the original error, including evidence privacy checks.

        stack.push(record_failure)
        cap.adb("install", str(Path(args.apk).resolve()))
        smoke.installed = True
        cap.adb("shell", "am", "start", "-W", "-n", base.ACTIVITY)
        smoke.started = True
        tap("Start downloading", "fr_start")
        description("Downloads shown from This phone. Change host")
        set_one_connection()
        baseline, _ = smoke.wait_node("All 0")
        require(baseline.get("checked") == "true", "Expected fresh isolated All 0")
        tap("Add", "fab")
        tap("More download sources", "entry_browser")
        tap("Browse and catch a download")
        smoke.wait_node("Browser options")
        origin = f"http://127.0.0.1:{port}"
        smoke.enter("https://example.com", origin + "/page")
        tap("Browser options")
        tap("Go")
        web_link("Sign in")
        web_link("Authenticated archive")
        _, sheet = smoke.wait_node(name, resource="caught_name")
        require(any("Cookies stay on this phone" in node.get("text", "") for node in sheet.iter("node")),
                "Expected local signed-in disclosure on the caught sheet")
        evidence("b3-auth-resume-caught", sheet)
        require(not target_present(), "Output appeared before submitting the owned download")
        submit = smoke.wait_node("Download", resource="caught_submit")[0]
        submitted = True
        smoke.tap(submit)
        close = description("Close browser")
        smoke.tap(close)
        row, _ = smoke.wait_content_description(lambda value: value.startswith(name + ". "), "exact owned active job")
        smoke.tap(row)
        smoke.wait_node(name, resource="details_title")

        def active_partial():
            view = smoke.tree()
            percent = base.Capture.details_percent(view)
            return view if len(percent) == 1 and re.fullmatch(r"(?:[1-9]|[1-9][0-9])%", percent[0]) else None

        until(active_partial, "No visible nonzero incomplete progress before Pause")
        tap("Pause", "hero_main")
        fixture.continue_initial.set()
        _, paused = smoke.wait_node("Resume", resource="hero_main")
        require(base.Capture.details_percent(paused) == base.Capture.details_percent(until(active_partial, "Paused view lost partial progress")),
                "Paused Details did not retain a stable nonzero percent")
        evidence("b3-auth-resume-paused", paused)

        def settled():
            snap = fixture.snapshot()
            return snap if snap["active_responses"] == 0 else None

        before = until(settled, "Paced fixture response did not close after Pause")
        require(0 < before["initial_bytes"] < len(payload) and before["resumed_bytes"] == 0,
                "Unexpected byte counters before Resume")
        require(any(request["method"] == "GET" and request["authenticated"] for request in before["requests"]),
                "Initial transfer lacked its captured synthetic sign-in")
        time.sleep(3)
        require(fixture.snapshot() == before, "The paused download made progress or requests")
        resume, _ = smoke.wait_node("Resume", resource="hero_main")
        smoke.tap(resume)

        def resumed_range():
            snap = fixture.snapshot()
            new_requests = snap["requests"][len(before["requests"]):]
            matching = [request for request in new_requests if request["method"] == "GET"
                        and request["authenticated"] and request["range_valid"] and request["range"]
                        and 0 < request["range"][0] <= before["initial_bytes"]
                        and request["range"][1] == len(payload) - 1 and request["body_bytes"] > 0]
            return matching[0]["range"] if matching else None

        requested = until(resumed_range, "Resume did not issue an authenticated later byte range")
        smoke.log(f"Observed authenticated Resume from byte {requested[0]} toward byte {requested[1]}.")
        fixture.release.set()
        until(lambda: target_digest() == digest, "Owned output did not match SHA-256", timeout=60)
        after = until(settled, "Fixture responses did not finish after Resume")
        resumed_requests = after["requests"][len(before["requests"]):]
        suffix_bytes = len(payload) - requested[0]
        require(any(request["method"] == "GET" and request["authenticated"]
                    and request["range"] == requested and request["body_bytes"] == suffix_bytes
                    for request in resumed_requests),
                "The authenticated later response did not supply its exact payload through EOF")
        require(all(request["authenticated"] and (
                    request["range"] == [0, 0] or request["range"] and request["range"][0] > 0)
                    for request in resumed_requests if request["method"] == "GET" and request["body_bytes"] > 0),
                "A resumed request transferred a full replacement body or did not authenticate")
        smoke.wait_node("Open", resource="hero_main")
        smoke.wait_node(name, resource="details_title")
        tap("Back", "details_back")
        count, _ = smoke.wait_node("All 1")
        require(count.get("checked") == "true", "Expected one local job after same-row Resume")
        finished, tree = smoke.wait_content_description(
            lambda value: value.startswith(name + ". Finished"), "one finished owned job after Resume")
        require(finished.get("clickable") == "true", "Finished row is not actionable")
        evidence("b3-auth-resume-finished", tree)
        (cap.evidence / "b3-auth-resume-proof.json").write_text(json.dumps({
            "initial_fixture_bytes": before["initial_bytes"],
            "paused_seconds": 3,
            "resumed_range": requested,
            "resumed_suffix_bytes": suffix_bytes,
            "resumed_get_authenticated": True,
            "new_full_replacement_body": False,
            "completed_bytes": len(payload),
            "sha256": digest,
            "jobs_after": 1,
        }, indent=2) + "\n", encoding="utf-8")
        smoke.log("PASS local B3: normal Resume reused synthetic sign-in on a later range and matched SHA-256.")
    smoke.log("Cleanup PASS: owned probe, fixture output, listener and reverse removed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", required=True, type=Path)
    run(parser.parse_args())


if __name__ == "__main__":
    main()
