"""Capture C3 with real simultaneous tracked uploads and downloads on an owned Host.

Uses the existing pinned-rclone paired fixture and a run-local WebDAV handler. The
normal parity handler accepts only Android's staging/MOVE upload protocol; this
handler additionally accepts one exact disposable final name for rclone copyfile.
No fixture DTOs are injected into Android or the Host.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from urllib.parse import urlsplit

from importlib.machinery import SourceFileLoader

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
UPLOAD_NAME = "c3-owned-upload.bin"
DOWNLOAD_NAME = "session-0412.flac"
UPLOAD = bytes(range(256)) * (8 * 1024 * 1024 // 256)


def load(name, file):
    return SourceFileLoader(name, str(HERE / file)).load_module()


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def host_only(args):
    """Exercise the real transfer engine without launching or modifying Android."""
    c4 = load("c3_host_only_c4", "capture-c4-paired-download.py")
    paired = load("c3_host_only_host", "paired-host-parity-fixture.py")
    dav = load("c3_host_only_dav", "remote-files-parity-fixture.py")
    dav.FILES[DOWNLOAD_NAME] *= 4
    dav.HASHES[DOWNLOAD_NAME] = hashlib.sha256(dav.FILES[DOWNLOAD_NAME]).hexdigest()
    args.evidence.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryDirectory(prefix="inbound-c3-host-only-") as scratch:
        rclone = c4.extract_rclone(args.rclone_zip, Path(scratch))
        with paired.running_fixture(args.server_dll, name=c4.HOST_NAME, rclone=rclone, site_transfer_limit=2) as fixture:
            root = Path(fixture["download_root"]).resolve(strict=True)
            upload_dir, download_dir = root / "C3-upload", root / "C3-download"
            upload_dir.mkdir(); download_dir.mkdir()
            (upload_dir / UPLOAD_NAME).write_bytes(UPLOAD)
            server = dav.FixtureServer(("127.0.0.1", 0), make_handler(dav))
            server.parity_pulse = True
            server.upload_bytes = 0
            server.events = []
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            request = paired.fixture_session(fixture)
            try:
                enabled = request("sites.setEnabled", {"enabled": True})
                require((enabled.get("payload") or {}).get("enabled") is True, "Sites did not enable")
                result = (request("sites.save", {"name": "C3 mixed fixture", "kind": "webdav",
                    "url": f"http://127.0.0.1:{server.server_port}/", "vendor": "other"}).get("payload") or {})
                site_id = (result.get("site") or {}).get("id")
                require(result.get("ok") is True and site_id, "Could not save owned site")
                ids = {}
                for direction, folder, name in (("download", download_dir, DOWNLOAD_NAME),
                                                ("upload", upload_dir, UPLOAD_NAME)):
                    reply = request("sites.transfer.start", {"siteId": site_id, "direction": direction,
                        "remoteFolder": "/" + dav.RECORDINGS, "localFolder": str(folder),
                        "names": [{"name": name, "isFolder": False}], "conflict": "skip"})
                    result = reply.get("payload") or {}
                    ids[direction] = (result.get("transfer") or {}).get("id")
                    require(reply.get("type") == "sites.transfer.result" and result.get("ok") and ids[direction],
                            "Transfer start refused: " + direction)
                deadline = time.monotonic() + 190
                overlap = False
                previous = (0, 0)
                while time.monotonic() < deadline:
                    reply = request("sites.transfer.list", {})
                    by_id = {x.get("id"): x for x in (reply.get("payload") or {}).get("transfers", [])}
                    with server.parity_lock:
                        current = (server.upload_bytes, server.parity_pulsed_bytes)
                        events = list(server.events[-40:])
                    state = {d: {key: by_id.get(ids[d], {}).get(key) for key in
                                  ("direction", "state", "bytes", "bytesDone", "speed", "filesRunning", "filesDone", "filesFailed", "problem")}
                             for d in ids}
                    state.update({"upload_received_bytes": current[0], "download_sent_bytes": current[1], "webdav_events": events})
                    with (args.evidence / "host-only-diagnostics.jsonl").open("a", encoding="utf-8") as out:
                        out.write(json.dumps(state) + "\n")
                    if all(by_id.get(ids[d], {}).get("state") == "running" for d in ids) and all(b > a > 0 for a, b in zip(previous, current)):
                        overlap = True
                    if all(by_id.get(ids[d], {}).get("state") == "done" for d in ids):
                        break
                    if any(by_id.get(ids[d], {}).get("state") in ("failed", "canceled") for d in ids):
                        raise RuntimeError("Tracked transfer failed; see sanitized diagnostics")
                    previous = current
                    time.sleep(1)
                else:
                    raise RuntimeError("Transfer deadline; see sanitized diagnostics")
                require(overlap, "No simultaneous advancing tracked transfers")
                with server.parity_lock:
                    uploaded = server.parity_uploads.get(dav.RECORDINGS + "/" + UPLOAD_NAME)
                require(uploaded is not None and hashlib.sha256(uploaded).digest() == hashlib.sha256(UPLOAD).digest(), "Upload hash mismatch")
                require(hashlib.sha256((download_dir / DOWNLOAD_NAME).read_bytes()).hexdigest() == dav.HASHES[DOWNLOAD_NAME], "Download hash mismatch")
                (args.evidence / "host-only-proof.json").write_text(json.dumps({"overlap": True,
                    "upload_sha256": hashlib.sha256(UPLOAD).hexdigest(), "download_sha256": dav.HASHES[DOWNLOAD_NAME],
                    "final_states": {d: by_id[ids[d]]["state"] for d in ids}}, indent=2) + "\n", encoding="utf-8")
                print("PASS real tracked transfers overlapped and both hashes matched", flush=True)
            finally:
                try:
                    request("sites.setEnabled", {"enabled": False})
                finally:
                    server.shutdown(); worker.join(timeout=5); server.server_close()


def make_handler(dav):
    class Handler(dav.Handler):
        def _kind(self):
            path = self._path()
            if path == dav.RECORDINGS + "/" + UPLOAD_NAME:
                return "upload_target"
            if path == dav.RECORDINGS + "/" + DOWNLOAD_NAME:
                return "download_source"
            if path.startswith(dav.RECORDINGS + "/"):
                return "other_recordings_child"
            return "other"

        def _note(self, kind, **fields):
            with self.server.parity_lock:
                self.server.events.append({"method": self.command, "target": kind, **fields})

        def send_response(self, status, message=None):
            self._note(self._kind(), status=status)
            super().send_response(status, message)

        def do_PROPFIND(self):
            self._note(self._kind(), depth=self.headers.get("Depth", "unspecified"))
            super().do_PROPFIND()

        def do_GET(self):
            self._note(self._kind())
            super().do_GET()

        def do_HEAD(self):
            self._note(self._kind())
            super().do_HEAD()

        def do_PUT(self):
            self._note(self._kind(), content_length_present=len(self.headers.get_all("Content-Length", [])) == 1,
                       transfer_encoding_present="Transfer-Encoding" in self.headers)
            if self._path() != dav.RECORDINGS + "/" + UPLOAD_NAME:
                return super().do_PUT()
            lengths = self.headers.get_all("Content-Length", [])
            if lengths != [str(len(UPLOAD))] or "Transfer-Encoding" in self.headers:
                self.send_error(400)
                return
            chunks = []
            remaining = len(UPLOAD)
            while remaining:
                block = self.rfile.read(min(16384, remaining))
                if not block:
                    self.send_error(400)
                    return
                chunks.append(block)
                remaining -= len(block)
                with self.server.parity_lock:
                    self.server.upload_bytes += len(block)
                time.sleep(.08)
            payload = b"".join(chunks)
            with self.server.parity_lock:
                exists = self._path() in self.server.parity_uploads
                if not exists:
                    self.server.parity_uploads[self._path()] = payload
            self._status(412 if exists else 201)

    return Handler


def run(args):
    c4 = load("c3_mixed_c4", "capture-c4-paired-download.py")
    states = load("c3_mixed_states", "capture-download-states-parity.py")
    paired = load("c3_mixed_host", "paired-host-parity-fixture.py")
    dav = load("c3_mixed_dav", "remote-files-parity-fixture.py")
    # Extend the run-local module's payload so the slower of UI polling and
    # transfer startup still has time to observe a genuine concurrent state.
    dav.FILES[DOWNLOAD_NAME] *= 4
    dav.HASHES[DOWNLOAD_NAME] = hashlib.sha256(dav.FILES[DOWNLOAD_NAME]).hexdigest()
    parity = load("c3_mixed_pair", "capture-remote-files-parity.py")
    args.active_only = True
    args.details_flow = args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)

    Handler = make_handler(dav)

    with tempfile.TemporaryDirectory(prefix="inbound-c3-mixed-relay-") as scratch:
        temp = Path(scratch).resolve()
        rclone = c4.extract_rclone(args.rclone_zip, temp)
        with paired.running_fixture(args.server_dll, name=c4.HOST_NAME, rclone=rclone, site_transfer_limit=2) as fixture:
            root = Path(fixture["download_root"]).resolve(strict=True)
            upload_dir, download_dir = root / "C3-upload", root / "C3-download"
            upload_dir.mkdir(); download_dir.mkdir()
            (upload_dir / UPLOAD_NAME).write_bytes(UPLOAD)
            server = dav.FixtureServer(("127.0.0.1", 0), Handler)
            server.parity_pulse = True
            server.upload_bytes = 0
            server.events = []
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            try:
                listener = paired.ephemeral_port()
                with c4.tls_relay(args.node, urlsplit(fixture["base_url"]).port, listener, temp) as (origin, fingerprint, tls, opener):
                    endpoint = f"tcp:{listener}"
                    cap.adb("reverse", "--no-rebind", endpoint, endpoint)
                    smoke.port = listener
                    smoke.reversed = True
                    request = None
                    try:
                        capabilities = c4.host_capabilities(origin, fixture["password"], tls, opener)
                        require("sites" in capabilities, "Fixture Host does not advertise Sites")
                        cap.install_and_launch()
                        smoke.installed = cap.installed
                        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY)
                        smoke.started = True
                        smoke.tap(smoke.wait_node("Start downloading", resource="fr_start")[0])
                        smoke.wait_node("Search downloads")
                        c4.pair_probe(smoke, origin, fixture["password"], fingerprint)
                        request = paired.fixture_session(fixture)
                        enabled = request("sites.setEnabled", {"enabled": True})
                        require((enabled.get("payload") or {}).get("enabled") is True, "Sites did not enable")
                        saved = request("sites.save", {"name": "C3 mixed fixture", "kind": "webdav",
                            "url": f"http://127.0.0.1:{server.server_port}/", "vendor": "other"})
                        result = saved.get("payload") or {}
                        site_id = (result.get("site") or {}).get("id")
                        require(saved.get("type") == "sites.result" and result.get("ok") is True and site_id,
                                "Could not save the owned WebDAV site")
                        smoke.tap(smoke.wait_node("Remote Files", resource="nav_remote_files")[0])
                        smoke.wait_content_description_prefix("C3 mixed fixture. ", timeout=40)

                        ids = {}
                        for direction, folder, name in (("download", download_dir, DOWNLOAD_NAME),
                                                        ("upload", upload_dir, UPLOAD_NAME)):
                            reply = request("sites.transfer.start", {"siteId": site_id, "direction": direction,
                                "remoteFolder": "/" + dav.RECORDINGS, "localFolder": str(folder),
                                "names": [{"name": name, "isFolder": False}], "conflict": "skip"})
                            payload = reply.get("payload") or {}
                            transfer = payload.get("transfer") or {}
                            require(reply.get("type") == "sites.transfer.result" and payload.get("ok") is True
                                    and transfer.get("id"), "Host refused the owned " + direction)
                            ids[direction] = transfer["id"]

                        deadline = time.monotonic() + 65
                        captured = False
                        pattern = r"2 transfers · 1 up, 1 down · (?:[1-9][0-9]*(?:\.[0-9]+)?|0\.[0-9]*[1-9][0-9]*) (?:B|KB|MB|GB)/s\. Open transfers\."
                        diagnostics = smoke.evidence / "c3-mixed-diagnostics.jsonl"
                        while time.monotonic() < deadline:
                            reply = request("sites.transfer.list", {})
                            transfers = (reply.get("payload") or {}).get("transfers", [])
                            by_id = {x.get("id"): x for x in transfers}
                            tree = smoke.tree()
                            summary = [n.get("content-desc", "") for n in tree.iter("node")
                                if re.fullmatch(pattern, n.get("content-desc", ""))]
                            with server.parity_lock:
                                before = (server.upload_bytes, server.parity_pulsed_bytes)
                                events = list(server.events[-40:])
                            visible = [n.get("content-desc", "") for n in tree.iter("node")
                                       if n.get("content-desc", "").endswith(". Open transfers.")]
                            snapshot = {d: {key: by_id.get(ids[d], {}).get(key) for key in
                                               ("direction", "state", "bytes", "bytesDone", "speed", "filesRunning", "filesDone", "filesFailed", "problem")}
                                        for d in ids}
                            snapshot.update({"elapsed_seconds": round(65 - max(0, deadline - time.monotonic()), 1),
                                             "upload_received_bytes": before[0], "download_sent_bytes": before[1],
                                             "visible_summaries": visible, "webdav_events": events})
                            with diagnostics.open("a", encoding="utf-8") as output:
                                output.write(json.dumps(snapshot, ensure_ascii=False) + "\n")
                            if summary and all(by_id.get(ids[d], {}).get("state") == "running" for d in ids) and min(before) > 0:
                                time.sleep(1)
                                with server.parity_lock:
                                    after = (server.upload_bytes, server.parity_pulsed_bytes)
                                if all(b > a for a, b in zip(before, after)):
                                    tree = smoke.tree()
                                    summary = [n.get("content-desc", "") for n in tree.iter("node")
                                               if re.fullmatch(pattern, n.get("content-desc", ""))]
                                    if not summary:
                                        continue
                                    image = smoke.evidence / "c3-paired-mixed-capture.png"
                                    image.write_bytes(smoke.adb("exec-out", "screencap", "-p", raw=True))
                                    ET.ElementTree(tree).write(smoke.evidence / "c3-paired-mixed-ui.xml", encoding="utf-8", xml_declaration=True)
                                    parity.make_pair(ROOT / "docs/plans/android-mockups/c3-sites.png", image,
                                                     smoke.evidence / "c3-paired-mixed-pair.png", "C3 MIXED")
                                    captured = True
                                    break
                            time.sleep(.3)
                        require(captured, "Did not observe both running tracked transfers and advancing bytes in C3")
                        deadline = time.monotonic() + 180
                        while time.monotonic() < deadline:
                            reply = request("sites.transfer.list", {})
                            by_id = {x.get("id"): x for x in (reply.get("payload") or {}).get("transfers", [])}
                            if all(by_id.get(ids[d], {}).get("state") == "done" for d in ids):
                                break
                            require(not any(by_id.get(ids[d], {}).get("state") in ("failed", "canceled") for d in ids),
                                    "A tracked transfer failed")
                            time.sleep(.5)
                        else:
                            raise RuntimeError("Tracked transfers did not complete")
                        with server.parity_lock:
                            uploaded = server.parity_uploads.get(dav.RECORDINGS + "/" + UPLOAD_NAME)
                        download = download_dir / DOWNLOAD_NAME
                        require(uploaded is not None and hashlib.sha256(uploaded).digest() == hashlib.sha256(UPLOAD).digest(), "Upload hash mismatch")
                        require(download.is_file() and hashlib.sha256(download.read_bytes()).hexdigest() == dav.HASHES[DOWNLOAD_NAME], "Download hash mismatch")
                        (smoke.evidence / "c3-paired-mixed-proof.json").write_text(json.dumps({
                            "upload_sha256": hashlib.sha256(UPLOAD).hexdigest(), "download_sha256": dav.HASHES[DOWNLOAD_NAME],
                            "summary": summary[0], "fixture_bytes_before": before, "fixture_bytes_after": after,
                            "transfer_ids": ids, "final_states": {d: by_id[ids[d]]["state"] for d in ids}}, indent=2) + "\n", encoding="utf-8")
                        smoke.log("PASS C3 paired tracked upload and download, simultaneous progress and exact hashes")
                    finally:
                        try:
                            if request is not None:
                                stopped = request("sites.setEnabled", {"enabled": False})
                                require((stopped.get("payload") or {}).get("rclone") == "off", "Fixture rclone did not stop")
                        finally:
                            if cap.installed:
                                smoke.installed = True
                            smoke.clean(include_remote_files_payload=False)
            finally:
                server.shutdown(); worker.join(timeout=5); server.server_close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial")
    parser.add_argument("--apk")
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--server-dll", type=Path, required=True)
    parser.add_argument("--rclone-zip", type=Path, required=True)
    parser.add_argument("--node", default="node")
    parser.add_argument("--host-only", action="store_true", help="Run real Host transfers without Android")
    args = parser.parse_args()
    if args.host_only:
        host_only(args)
    else:
        if not args.serial or not args.apk:
            parser.error("--serial and --apk are required for Android capture")
        run(args)


if __name__ == "__main__":
    main()
