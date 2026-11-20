"""Isolated paired Host B5 removal choices, using only fixture jobs and files."""
import argparse
from collections import Counter
from contextlib import ExitStack
import importlib.util
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import re
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from urllib.parse import urlsplit
import uuid

HERE = Path(__file__).resolve().parent

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

def require(ok, message):
    if not ok:
        raise RuntimeError(message)

def run(args):
    c4 = load('b5_category_c4', 'capture-c4-paired-download.py')
    states = load('b5_category_states', 'capture-download-states-parity.py')
    paired = load('b5_category_host', 'paired-host-parity-fixture.py')
    args.active_only = True
    args.details_flow = args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)
    nonce = uuid.uuid4().hex[:10]
    names = [f'b5-remove-{nonce}-{i}.bin' for i in range(2)]
    counts, lock = Counter(), threading.Lock()
    control_name = f"b5-remove-control-{nonce}.bin"
    control_bytes = f"Unselected B5 control file {nonce}\n".encode("ascii")
    control_hash = hashlib.sha256(control_bytes).hexdigest()

    class Handler(BaseHTTPRequestHandler):
        def respond(self):
            control = args.delete_file and self.path == "/control"
            if not control:
                with lock:
                    counts[self.command] += 1
            self.send_response(200 if control else 503)
            self.send_header("Content-Length", str(len(control_bytes) if control else 0))
            self.end_headers()
            if control and self.command == "GET":
                self.wfile.write(control_bytes)

        do_HEAD = respond
        do_GET = respond
        def log_message(self, *_args): pass

    def screenshot(name, tree):
        require(not any(n.get('password') == 'true' for n in tree.iter('node')),
                'Refusing evidence while a credential field is visible')
        (smoke.evidence / (name + '.png')).write_bytes(smoke.adb('exec-out', 'screencap', '-p', raw=True))
        ET.ElementTree(tree).write(smoke.evidence / (name + '.xml'), encoding='utf-8', xml_declaration=True)

    def wait(predicate, description):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            tree = smoke.tree()
            value = predicate(list(tree.iter('node')))
            if value is not None:
                return value, tree
            time.sleep(.3)
        raise RuntimeError('Expected ' + description)

    def exact_node(nodes, *, text=None, desc=None, rid=None):
        found = [n for n in nodes if (text is None or n.get('text') == text)
                 and (desc is None or n.get('content-desc') == desc)
                 and (rid is None or n.get('resource-id', '').endswith(':id/' + rid))]
        return found[0] if len(found) == 1 else None

    with ExitStack() as stack:
        temp = Path(stack.enter_context(tempfile.TemporaryDirectory(prefix='inbound-b5-remove-')))
        owned = stack.enter_context(paired.running_fixture(args.server_dll, name=c4.HOST_NAME))
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()

        def stop_listener():
            server.shutdown()
            worker.join(timeout=5)
            server.server_close()
            require(not worker.is_alive(), 'Fixture listener failed to stop')
        stack.callback(stop_listener)
        endpoint = f'tcp:{paired.ephemeral_port()}'
        port = int(endpoint.split(':')[1])
        upstream = urlsplit(owned['base_url']).port
        origin, fingerprint, _, _ = stack.enter_context(c4.tls_relay(args.node, upstream, port, temp))
        require(not any(endpoint in x.split() for x in smoke.adb('reverse', '--list').splitlines()), 'Reverse port already in use')
        smoke.adb('reverse', '--no-rebind', endpoint, endpoint)
        smoke.port = port; smoke.reversed = True
        try:
            cap.install_and_launch(); smoke.installed = cap.installed
            smoke.adb('shell', 'am', 'start', '-W', '-n', states.ACTIVITY); smoke.started = True
            smoke.tap(smoke.wait_node('Start downloading', resource='fr_start')[0])
            smoke.wait_node('Search downloads')
            c4.pair_probe(smoke, origin, owned['password'], fingerprint)
            request = paired.fixture_session(owned)
            root = Path(owned["download_root"])
            require(root.is_dir() and not root.is_symlink() and not any(root.iterdir()),
                    "Expected an empty owned Host download directory")
            finished_name = f"b5-remove-finished-{nonce}.bin"
            finished_id = paired.seed_download(owned, filename=finished_name)
            finished_path = root / finished_name
            control_path = root / control_name
            control_id = None

            def add_job(name, url, start_mode):
                reply = request("job.add", {"url": url, "fileName": name, "startMode": start_mode, "connections": 1})
                result = reply.get("payload") or {}
                job = result.get("job") or {}
                require(reply.get("type") == "job.addResult" and result.get("accepted")
                        and not result.get("duplicate") and job.get("id"), "Fixture job was not accepted")
                return job["id"]

            if args.delete_file:
                control_id = add_job(control_name, f"http://127.0.0.1:{server.server_port}/control", "startNow")

            def file_matches(path, digest):
                return path.is_file() and not path.is_symlink() and hashlib.sha256(path.read_bytes()).hexdigest() == digest
            deadline = time.monotonic() + 40
            while time.monotonic() < deadline:
                if (file_matches(finished_path, owned["payload_sha256"])
                        and (not args.delete_file or file_matches(control_path, control_hash))):
                    break
                time.sleep(.3)
            else: raise RuntimeError("Owned finished file did not match fixture hash")
            ids = [add_job(name, f"http://127.0.0.1:{server.server_port}/{i}", "later") for i, name in enumerate(names)]
            residual = [ids[0]]
            if control_id is not None:
                residual.append(control_id)
            initial = [finished_id, ids[1], *residual]
            require(finished_id and len(set(initial)) == len(initial), "Expected distinct fixture jobs")

            def snapshot(expected_ids, *, target_deleted=False):
                reply = request("jobs.snapshot", {}); jobs = (reply.get("payload") or {}).get("jobs", [])
                require(reply.get("type") == "jobs.snapshot", "Host snapshot unavailable")
                rows = {j.get("id"): j for j in jobs}
                require(len(rows) == len(jobs) and set(rows) == set(expected_ids), "Unexpected Host job identities")
                for i, job_id in enumerate(ids):
                    if job_id in rows:
                        j = rows[job_id]
                        require(j.get("fileName") == names[i] and str(j.get("state", "")).lower() == "queued"
                                and str(j.get("startMode", "")).lower() == "later" and j.get("receivedBytes") == 0,
                                "Deferred fixture job changed")
                for job_id, name, path in ((finished_id, finished_name, finished_path), (control_id, control_name, control_path)):
                    if job_id in rows:
                        require(rows[job_id].get("fileName") == name and rows[job_id].get("finalFilePath") == str(path)
                                and str(rows[job_id].get("state", "")).lower() == "completed", "Completed fixture job changed")
                with lock:
                    require(not counts, "Deferred fixture URL was requested")
                expected_paths = set()
                if not target_deleted:
                    require(file_matches(finished_path, owned["payload_sha256"]), "The selected fixture file changed")
                    expected_paths.add(finished_path)
                if control_id is not None:
                    require(file_matches(control_path, control_hash), "The unselected control file changed")
                    expected_paths.add(control_path)
                require(set(root.iterdir()) == expected_paths, "Unexpected Host output")
            snapshot(initial)
            def row(name):
                return lambda ns: next((n for n in ns if n.get("clickable") == "true"
                                        and n.get("content-desc", "").startswith(name + ". ")), None)
            def select(node, long=False):
                m = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
                require(m is not None, "Fixture row has no bounds")
                l,t,r,b=map(int,m.groups()); x=(3*l+r)//4; y=(t+b)//2
                if long: smoke.adb("shell", "input", "swipe", str(x), str(y), str(x), str(y), "900")
                else: smoke.adb("shell", "input", "tap", str(x), str(y))
            target, _ = wait(row(names[-1]), "deferred fixture row")
            select(target, True)
            target, _ = wait(row(finished_name), "finished fixture row")
            select(target)
            def selection(ns):
                a=row(names[-1])(ns); b=row(finished_name)(ns); other=row(names[0])(ns)
                control = row(control_name)(ns) if control_id is not None else None
                if control_id is not None and (control is None or control.get("selected") != "false"):
                    return None
                return a if (a is not None and b is not None and other is not None and a.get("selected") == "true"
                             and b.get("selected") == "true" and other.get("selected") == "false"
                             and exact_node(ns, text="2 selected", rid="sel_title") is not None) else None
            _, tree = wait(selection, "exact two selected rows")
            screenshot("b5-remove-selection", tree)
            def open_delete():
                button, _ = wait(lambda ns: exact_node(ns, desc="Delete selected", rid="sel_delete"), "selected Delete")
                require(button.get("enabled") == "true" and button.get("clickable") == "true", "Delete control unavailable")
                smoke.tap(button)
                _, tree = smoke.wait_node("Delete 2 downloads?")
                return tree
            def choices(expected):
                tree=smoke.tree(); ns=list(tree.iter("node"))
                for label, checked in (("Remove from the list only", expected == "keep"), ("Remove and delete the file", expected == "delete")):
                    items=[n for n in ns if n.get("text") == label and n.get("checkable") == "true"]
                    require(len(items) == 1 and items[0].get("checked") == str(checked).lower(), "Unexpected remove choice: " + label)
                return tree
            open_delete(); screenshot("b5-remove-default", choices("keep"))
            smoke.tap(smoke.wait_node("Remove and delete the file")[0])
            screenshot("b5-remove-alternate", choices("delete"))
            smoke.tap(smoke.wait_node("Cancel")[0])
            _, tree = wait(selection, "same two selected rows after Cancel")
            screenshot("b5-remove-cancel", tree); snapshot(initial)
            open_delete()
            confirm_tree = choices("keep")
            screenshot("b5-remove-default-reopened", confirm_tree)
            if args.delete_file:
                smoke.tap(smoke.wait_node("Remove and delete the file")[0])
                confirm_tree = choices("delete")
                screenshot("b5-remove-delete-confirm", confirm_tree)
                snapshot(initial)
            confirm = exact_node(list(confirm_tree.iter("node")), text="Delete", rid="button1")
            require(confirm is not None and confirm.get("enabled") == "true", "Missing enabled dialog confirmation")
            smoke.tap(confirm)
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                reply = request("jobs.snapshot", {}); rows = (reply.get("payload") or {}).get("jobs", [])
                if (reply.get("type") == "jobs.snapshot" and {j.get("id") for j in rows} == set(residual)
                        and (not args.delete_file or (not finished_path.exists() and not finished_path.is_symlink()))):
                    break
                time.sleep(.3)
            else: raise RuntimeError("Selected jobs were not removed")
            snapshot(residual, target_deleted=args.delete_file)

            def remaining(ns):
                other = row(names[0])(ns)
                if control_id is not None and row(control_name)(ns) is None:
                    return None
                return other if other is not None and row(names[-1])(ns) is None and row(finished_name)(ns) is None else None

            _, tree = wait(remaining, "only the unselected fixture rows")
            screenshot("b5-remove-delete-file" if args.delete_file else "b5-remove-list-only", tree)
            proof = {"initialJobs": len(initial), "remainingJobs": len(residual),
                     "finishedSha256": owned["payload_sha256"], "deferredHeads": 0, "deferredGets": 0,
                     "finishedFilePreserved": not args.delete_file}
            if args.delete_file:
                proof["controlSha256"] = control_hash
            (smoke.evidence / "b5-remove-proof.json").write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
            if args.delete_file:
                smoke.log("PASS B5: alternate Cancel preserved both files; explicit confirmation removed only the selected jobs and file.")
            else:
                smoke.log("PASS B5: alternate Cancel preserved jobs/file; default list-only removed exact two IDs and kept the file.")
        finally:
            if cap.installed: smoke.installed = True
            smoke.clean(include_remote_files_payload=False)

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--adb', default='adb'); p.add_argument('--aapt'); p.add_argument('--serial', required=True)
    p.add_argument('--apk', required=True); p.add_argument('--evidence', type=Path, required=True)
    p.add_argument('--server-dll', type=Path, required=True); p.add_argument('--node', default='node')
    p.add_argument("--delete-file", action="store_true", help="Confirm file deletion for the exact selected fixture jobs")
    run(p.parse_args())

if __name__ == '__main__': main()
