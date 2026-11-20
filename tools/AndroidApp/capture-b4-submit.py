"""Opt-in isolated B4 submission: two selected fixture links and exact cleanup."""
import argparse
import hashlib
import importlib.machinery
import json
import re
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
states = importlib.machinery.SourceFileLoader("b4_submit_states", str(HERE / "capture-download-states-parity.py")).load_module()


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def fixture_items(nonce):
    require(re.fullmatch(r"[0-9a-f]{12}", nonce) is not None, "Invalid fixture nonce")
    labels = ("Selected Alpha", "Selected Beta", "Unselected Gamma", "Unselected Delta")
    result = []
    for index, label in enumerate(labels):
        filename = f"inbound-b4-{nonce}-{index}.bin"
        body = hashlib.sha256((nonce + label).encode("ascii")).digest() * 4096
        result.append({"label": label, "name": filename, "body": body,
                       "sha256": hashlib.sha256(body).hexdigest(), "selected": index < 2})
    return result


def run(args):
    args.active_only = True
    args.details_flow = False
    args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    items = fixture_items(uuid.uuid4().hex[:12])
    paths = {i["name"]: "/sdcard/Download/" + i["name"] for i in items}
    counts = {i["name"]: 0 for i in items}
    lock = threading.Lock()
    server = None
    thread = None
    server_started = False
    reverse = None
    validated = False
    writer_stopped = False
    submission_attempted = False
    errors = []

    def absent(path):
        return cap.shell(f"if [ -e '{path}' ] || [ -L '{path}' ]; then echo PRESENT; else echo ABSENT; fi").strip() == "ABSENT"

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_HEAD(self): self.respond(False)
        def do_GET(self): self.respond(True)
        def respond(self, send_body):
            if self.path == "/page":
                html = '<!doctype html><meta name="viewport" content="width=device-width"><title>B4 owned fixture</title><h1>B4 owned fixture</h1>'
                html += ''.join(f'<p><a href="/{i["name"]}">{i["label"]}</a></p>' for i in items)
                data = html.encode("ascii")
                self.send_response(200); self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(data))); self.end_headers()
                if send_body: self.wfile.write(data)
                return
            item = next((i for i in items if self.path == "/" + i["name"]), None)
            if item is None:
                self.send_error(404); return
            if send_body:
                with lock: counts[item["name"]] += 1
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Disposition", f'attachment; filename="{item["name"]}"')
            self.send_header("Content-Length", str(len(item["body"])))
            self.end_headers()
            if send_body:
                try: self.wfile.write(item["body"])
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass

    def tap(label, resource=None):
        control, _ = smoke.wait_node(label, resource=resource)
        smoke.tap(control)

    def row_checks(expected):
        tree = smoke.tree()
        found = {}
        for item in items:
            rows = [n for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/page_link_check")
                    and n.get("text", "").split("\n", 1)[0] == item["label"]]
            require(len(rows) == 1, "Fixture link row missing or ambiguous: " + item["label"])
            require(rows[0].get("checked") == str(item["label"] in expected).lower(), "Unexpected selection: " + item["label"])
            found[item["label"]] = rows[0]
        return found

    def capture(key, tree):
        (cap.evidence / f"{key}.png").write_bytes(cap.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(tree).write(cap.evidence / f"{key}.xml", encoding="utf-8", xml_declaration=True)

    def cleanup_file(item):
        path = paths[item["name"]]
        if absent(path): return
        require(item["selected"], "Unexpected unselected output retained")
        result = cap.shell(f"if [ -f '{path}' ] && [ ! -L '{path}' ]; then sha256sum '{path}'; fi").strip().split()
        require(result == [item["sha256"], path], "Unexpected or incomplete fixture output retained")
        cap.shell("rm", "--", path)
        require(absent(path), "Owned output remained after cleanup")

    try:
        smoke.preflight(include_remote_files_payload=False)
        validated = True
        for path in paths.values(): require(absent(path), "Fixture destination already exists")
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        endpoint = "tcp:" + str(server.server_port)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start(); server_started = True
        cap.adb("reverse", "--no-rebind", endpoint, endpoint); reverse = endpoint
        cap.install_and_launch()  # existing timeout ownership check; active_only means no launch yet
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY, "--ez", "skip_first_run", "true")
        cap.started = True
        smoke.wait_node("Search downloads")
        tree = smoke.tree()
        require(any(n.get("text") == "All 0" and n.get("checked") == "true" for n in tree.iter("node")),
                "Fresh probe must show zero jobs")
        smoke.wait_content_description(lambda d: d == "Downloads shown from This phone. Change host", "phone Host")
        tap("Add", "fab"); tap("More download sources", "entry_browser"); tap("Browse and catch a download")
        smoke.enter("https://example.com", f"http://127.0.0.1:{server.server_port}/page")
        tap("Browser options"); tap("Go")
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            if smoke.matches(smoke.tree(), "B4 owned fixture"):
                break  # The WebView exposes both page title and heading.
            time.sleep(.4)
        else:
            raise RuntimeError("The owned browser page did not render")
        tap("Browser options"); tap("Links on page")
        smoke.wait_node("Select shown (4)")
        rows = row_checks(set())
        selected_labels = set()
        for item in items[:2]:
            label = item["label"]
            smoke.tap(rows[label])
            selected_labels.add(label)
            rows = row_checks(selected_labels)
        tap_button, selected_tree = smoke.wait_node("Download 2", resource="page_links_download")
        require(tap_button.get("enabled") == "true", "Download 2 is disabled")
        capture("b4-submit-selected", selected_tree)
        for path in paths.values():
            require(absent(path), "Fixture destination appeared before submission")
        submission_attempted = True  # never retry an uncertain tap
        smoke.tap(tap_button)
        close, _ = smoke.wait_content_description(lambda d: d == "Close browser", "close owned browser", timeout=40)
        smoke.tap(close)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            tree = smoke.tree()
            texts = [n.get("text", "") for n in tree.iter("node")]
            if "All 2" in texts and "Finished 2" in texts:
                break
            time.sleep(.4)
        else: raise RuntimeError("Exactly two finished probe jobs were not shown")
        descriptions = [n.get("content-desc", "") for n in tree.iter("node")]
        for item in items:
            matches = [d for d in descriptions if d.startswith(item["name"] + ". ")]
            require(len(matches) == (1 if item["selected"] else 0), "Unexpected Downloads row: " + item["name"])
            if matches: require(". Finished" in matches[0], "Selected job is not finished")
        with lock: get_counts = dict(counts)
        for item in items:
            require((get_counts[item["name"]] > 0) == item["selected"], "Unexpected fixture payload GET")
            if item["selected"]:
                path = paths[item["name"]]
                result = cap.shell(f"if [ -f '{path}' ] && [ ! -L '{path}' ]; then sha256sum '{path}'; fi").strip().split()
                require(result == [item["sha256"], path], "Selected output hash/path mismatch")
            else:
                require(absent(paths[item["name"]]), "Unselected output exists")
        capture("b4-submit-finished", tree)
        (cap.evidence / "b4-submit.json").write_text(json.dumps({"jobs": 2, "items": [
            {"label": i["label"], "name": i["name"], "selected": i["selected"],
             "get_count": get_counts[i["name"]], "sha256": i["sha256"] if i["selected"] else None} for i in items]}, indent=2) + "\n", encoding="utf-8")
        cap.log("PASS B4: exactly two selected jobs completed with distinct matching hashes; no sentinel GETs.")
    finally:
        pending = sys.exc_info()[0] is not None
        if server_started:
            try: server.shutdown()
            except Exception: errors.append("fixture shutdown")
        if server is not None:
            try: server.server_close()
            except Exception: errors.append("fixture close")
        if thread is not None and server_started:
            try: thread.join(timeout=5)
            except Exception: errors.append("fixture thread")
        if cap.installed:
            try:
                cap.adb("uninstall", states.PACKAGE)
                require(f"package:{states.PACKAGE}" not in cap.shell("pm", "list", "packages", states.PACKAGE), "Probe remains installed")
                writer_stopped = True
            except Exception: errors.append("probe uninstall; output retained")
        else:
            writer_stopped = not submission_attempted
        if reverse:
            try: cap.adb("reverse", "--remove", reverse)
            except Exception: errors.append("owned reverse")
        if submission_attempted and writer_stopped:
            for item in items:
                try: cleanup_file(item)
                except Exception: errors.append(item["name"] + " retained or cleanup failed")
        if validated:
            try: cap.shell("rm", "-f", smoke.xml_path)
            except Exception: errors.append("temporary UI dump")
        if errors:
            cap.log("Cleanup incomplete: " + ", ".join(errors))
            if not pending: raise RuntimeError("B4 cleanup incomplete")
        elif validated:
            cap.log("Cleanup PASS: owned probe and reverse removed; only the hash-matched selected outputs were removed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    run(parser.parse_args())


if __name__ == "__main__": main()
