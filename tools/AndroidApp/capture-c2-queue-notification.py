"""Verify phone queue completion notifications on the dedicated API 33 probe."""
import argparse
import hashlib
import io
import json
import re
import subprocess
import threading
import time
from contextlib import ExitStack
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from importlib.machinery import SourceFileLoader
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
queues = SourceFileLoader("c2_queue_runner", str(HERE / "capture-queue-host-parity.py")).load_module()
downloads = SourceFileLoader("c2_download_runner", str(HERE / "capture-download-states-parity.py")).load_module()
require = queues.require


def run(args):
    args.queue_name = "C2 completion"
    args.host_name = "This phone"
    args.active_only = True
    args.details_flow = False
    cap = queues.Capture(args)
    nonce = cap.smoke.nonce
    folder = "inbound-c2-finish-" + nonce
    root = "/sdcard/Download/" + folder
    choices = {"off": "Silent" + nonce, "on": "Notify" + nonce}
    names = {mode: f"{folder}-{mode}.bin" for mode in choices}
    payloads = {mode: (name + "\n").encode("ascii") * 1024 for mode, name in names.items()}
    arrived = {mode: threading.Event() for mode in choices}
    release = {mode: threading.Event() for mode in choices}
    hashes = {mode: hashlib.sha256(body).hexdigest() for mode, body in payloads.items()}

    def tap(label, resource=None):
        cap.smoke.tap(cap.smoke.wait_node(label, resource=resource)[0])

    def desc(label):
        return cap.smoke.wait_content_description(lambda value: value == label, label)[0]

    def node(resource):
        return cap._unique_resource(cap.smoke.tree(), resource)

    def gone(path):
        command = f"if [ -e '{path}' ] || [ -L '{path}' ]; then echo present; else echo absent; fi"
        return cap.shell(command).strip() == "absent"

    def digest(path):
        return cap.shell(f"if [ -f '{path}' ] && [ ! -L '{path}' ]; then sha256sum '{path}'; fi").split()

    def remove_outputs():
        for mode, name in names.items():
            path = root + "/" + name
            if not gone(path):
                require(digest(path) == [hashes[mode], path], "Unexpected fixture output; preserving it")
                cap.shell("rm", "--", path)
                require(gone(path), "Owned output remains")
        cap.shell("rmdir", root)
        require(gone(root), "Owned destination remains")
        cap.log("Cleanup PASS: removed only the two verified fixture files and their empty directory.")

    def card(queue):
        return cap.smoke.wait_content_description(
            lambda text: text.startswith(queue + ", ") and "Double tap to edit" in text, queue + " card")[0]

    def choice_row():
        cap.shell("input", "swipe", "530", "1500", "530", "550", "350")
        return cap.smoke.wait_content_description(
            lambda label: label.startswith("When the queue finishes, "), "queue completion preference")[0]

    def configure(mode):
        queue = choices[mode]
        tap("Queues", "nav_queues")
        tap("New queue", "queue_new")
        cap.smoke.tap(desc("Queue name"))
        cap.shell("input", "text", queue)
        tap("Create")
        cap.smoke.tap(choice_row())
        label = "Notify and finish" if mode == "on" else "Do nothing"
        tap(label)
        selected, _ = cap.smoke.wait_node(label)
        require(selected.get("checked") == "true", "Completion choice was not checked")
        tap("Apply")
        tap("Save", "queue_editor_save")

        cap.smoke.tap(card(queue))
        expected = "Notify when the queue finishes" if mode == "on" else "Do nothing"
        require(choice_row().get("content-desc") == "When the queue finishes, " + expected,
                "Saved completion preference did not reopen")
        cap.shell("input", "keyevent", "KEYCODE_BACK")
        card(queue)
        cap.smoke.tap(desc("Start " + queue))
        desc("Stop " + queue)
        tap("Downloads", "nav_downloads")

    def add(mode, port):
        name = names[mode]
        tap("Add", "fab")
        tap("Link or several links")
        cap.smoke.wait_node("New download")
        cap.smoke.tap(node("address"))
        cap.shell("input", "text", f"http://127.0.0.1:{port}/{mode}")
        cap.shell("input", "keyevent", "KEYCODE_BACK")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if node("file_name").get("text") == name:
                break
            time.sleep(.25)
        else:
            raise RuntimeError("Fixture metadata did not populate the exact filename")
        cap.smoke.tap(node("save_to"))
        cap.smoke.wait_content_description(lambda label: label.startswith("Folder ") and
                                           label.endswith("/Download"), "Download folder")
        cap.smoke.tap(desc(folder))
        cap.smoke.wait_content_description(lambda label: label.startswith("Folder ") and
                                           label.endswith("/" + folder), "owned destination")
        tap("Save here")
        require(folder in node("save_to").get("content-desc", ""), "Owned destination was not retained")
        selected, _ = cap.smoke.wait_content_description(lambda d: d.startswith("Choose queue, "), "queue timing")
        cap.smoke.tap(selected)
        tap(choices[mode])
        require(any(item.get("content-desc") == "Choose queue, " + choices[mode] and item.get("checked") == "true"
                    for item in cap.smoke.tree().iter("node")), "The selected queue was not retained")
        tap("Download", "new_download_btn")
        cap.smoke.wait_content_description(lambda label: label.startswith(name + ". "), "owned job")

    def records():
        def listed_keys():
            return {line.strip() for line in cap.shell("cmd", "notification", "list").splitlines()}

        for key in sorted(listed_keys()):
            fields = key.split("|")
            if len(fields) < 5 or fields[:2] != ["0", queues.PACKAGE]:
                continue
            require(re.fullmatch(r"[\w.|:-]+", key) is not None, "Unexpected probe notification key")
            try:
                record = cap.shell("cmd notification get '" + key + "'")
            except subprocess.CalledProcessError as error:
                if error.returncode != 1 or key in listed_keys():
                    raise
                continue
            title = re.search(r"android\.title=String \(([^\r\n]*)\)", record)
            body = re.search(r"android\.text=String \(([^\r\n]*)\)", record)
            if re.search(r"(?:channel|channelId)=finished\b", record) and title:
                yield fields[3], title.group(1), body.group(1) if body else None

    def complete(mode, port):
        configure(mode)
        add(mode, port)
        require(arrived[mode].wait(30), "Owned GET did not start")
        require(gone(root + "/" + names[mode]), "Final file appeared before release")
        require(not any(tag == "queue-finished" for tag, _, _ in records()),
                "A queue summary posted before an owned job finished")
        release[mode].set()
        expected_summary = ("queue-finished", choices[mode] + " finished", "1 completed")
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            observed = list(records())
            path = root + "/" + names[mode]
            if digest(path) == [hashes[mode], path] and any(title == names[mode] for _, title, _ in observed):
                if mode == "off" or expected_summary in observed:
                    break
            time.sleep(.3)
        else:
            raise RuntimeError("Expected exact file SHA-256 and finished notification records did not arrive")
        summaries = [item for item in observed if item[0] == "queue-finished"]
        if mode == "off":
            end = time.monotonic() + 4
            while time.monotonic() < end:
                require(not any(tag == "queue-finished" for tag, _, _ in records()),
                        "Do nothing produced a queue summary")
                time.sleep(.3)
        else:
            require(summaries == [expected_summary], "Expected exactly one matching queue summary")
        cap.log(f"PASS C2 {mode}: exact file hash and per-file notification, "
                f"queue summary {'present and correct' if mode == 'on' else 'absent for four seconds'}.")
        return {"queue": choices[mode], "sha256": hashes[mode], "fileNotice": True, "queueSummary": summaries}

    class Handler(BaseHTTPRequestHandler):
        def send_fixture(self, include_body):
            mode = self.path.lstrip("/")
            if mode not in payloads:
                self.send_error(404)
                return
            data = payloads[mode]
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Content-Disposition", f'attachment; filename="{names[mode]}"')
            self.end_headers()
            if include_body:
                arrived[mode].set()
                if release[mode].wait(90):
                    try:
                        self.wfile.write(data)
                    except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                        pass

        def do_HEAD(self):
            self.send_fixture(False)

        def do_GET(self):
            self.send_fixture(True)

        def log_message(self, *_args):
            pass

    def capture_summary(title):
        cap.shell("cmd", "statusbar", "expand-notifications")
        try:
            target, tree = cap.smoke.wait_node(title)
            rows = [row for row in tree.iter("node")
                    if row.get("resource-id", "").endswith(":id/expandableNotificationRow")
                    and target in tuple(row.iter("node"))]
            require(len(rows) == 1, "Queue summary notification row is ambiguous")
            bounds = tuple(map(int, re.findall(r"\d+", rows[0].get("bounds", ""))))
            with Image.open(io.BytesIO(cap.adb("exec-out", "screencap", "-p", raw=True))) as image:
                require(len(bounds) == 4 and 0 <= bounds[0] < bounds[2] <= image.width
                        and 0 <= bounds[1] < bounds[3] <= image.height, "Queue notice bounds are invalid")
                image.crop(bounds).save(cap.evidence / "c2-queue-notification.png")
        finally:
            cap.shell("cmd", "statusbar", "collapse")

    cap.check()
    with ExitStack() as cleanup:
        require(gone(root), "Exact fixture destination already exists")
        cap.shell("mkdir", root)
        cleanup.callback(remove_outputs)
        server = cleanup.enter_context(ThreadingHTTPServer(("127.0.0.1", 0), Handler))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        cleanup.callback(server.shutdown)
        for event in release.values():
            cleanup.callback(event.set)
        endpoint = f"tcp:{server.server_port}"
        require(not any(endpoint in line.split() for line in cap.adb("reverse", "--list").splitlines()),
                "Fixture reverse already exists")
        cap.adb("reverse", "--no-rebind", endpoint, endpoint)
        cleanup.callback(cap.adb, "reverse", "--remove", endpoint)
        cleanup.callback(cap.clean)
        downloads.Capture.install_and_launch(cap)
        cap.adb("shell", "am", "start", "-W", "-n", queues.ACTIVITY, "--ez", "skip_first_run", "true")
        cap.started = True
        cap.smoke.wait_node("Search downloads")
        cap.shell("pm", "grant", queues.PACKAGE, "android.permission.POST_NOTIFICATIONS")
        require(re.search(r"android\.permission\.POST_NOTIFICATIONS:\s*granted=true",
                          cap.shell("dumpsys", "package", queues.PACKAGE)), "Probe notification permission is off")
        proof = {mode: complete(mode, server.server_port) for mode in choices}
        capture_summary(choices["on"] + " finished")
        (cap.evidence / "c2-queue-notification-proof.json").write_text(
            json.dumps(proof, indent=2) + "\n", encoding="utf-8")
    require(not any(endpoint in line.split() for line in cap.adb("reverse", "--list").splitlines()),
            "Fixture reverse was not removed")
    cap.log("Cleanup PASS: removed the owned fixture reverse.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    run(parser.parse_args())
