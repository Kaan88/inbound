"""Disposable loopback paired-host fixture for API 33 C5/C6 captures.

Run with --server-dll <built DownloadManager.Server.dll>. The process prints
one JSON descriptor containing the base URL, synthetic password, and payload
URL to stdout for the capture runner's in-memory use only. Never persist it.
"""

import argparse
from contextlib import contextmanager
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time
from urllib.error import URLError
from urllib.request import Request, urlopen


PAYLOAD = bytes((i * 37 + 11) % 256 for i in range(1024 * 1024))
PAYLOAD_SHA256 = hashlib.sha256(PAYLOAD).hexdigest()
PAYLOAD_PATH = "/fixture/c5-c6.bin"


class PayloadHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != PAYLOAD_PATH:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(PAYLOAD)))
        self.end_headers()
        self.wfile.write(PAYLOAD)

    def do_HEAD(self):
        if self.path != PAYLOAD_PATH:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(PAYLOAD)))
        self.end_headers()

    def log_message(self, *_args):
        pass


def ephemeral_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def fixture_session(fixture):
    """Open an in-memory session on this run's disposable loopback Host."""
    def post(path, data, token=None):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        request = Request(fixture["base_url"] + path, json.dumps(data).encode(), headers)
        with urlopen(request, timeout=10) as response:
            return json.load(response)

    pairing = post("/api/pair", {"role": "remoteClient", "password": fixture["password"],
                                 "name": "Disposable fixture seeder"})
    if pairing.get("role") != "remoteClient" or not pairing.get("token"):
        raise RuntimeError("The disposable Host refused the fixture seeder")
    def request(kind, payload):
        message_id = secrets.token_hex(16)
        reply = post("/api/ipc", {"v": 1, "id": message_id, "type": kind, "payload": payload}, pairing["token"])
        if reply.get("replyTo") != message_id:
            raise RuntimeError("The disposable Host did not return the expected reply")
        return reply

    return request


def seed_download(fixture, *, filename="c5-c6.bin", slow=False):
    """Create a real transfer on the owned Host via its documented client path."""
    request = fixture_session(fixture)
    reply = request("job.add", {"url": fixture["slow_url"] if slow else fixture["payload_url"],
                               "fileName": filename, "startMode": "startNow", "connections": 1})
    result = reply.get("payload") or {}
    if reply.get("type") != "job.addResult" or not result.get("accepted") or result.get("duplicate"):
        raise RuntimeError("The disposable Host refused a new fixture transfer")
    return (result.get("job") or {}).get("id")


def fixture_job_state(fixture, job_id):
    """Read only the matching synthetic job state and byte counts."""
    if not job_id:
        raise RuntimeError("The Host did not identify the fixture transfer")
    reply = fixture_session(fixture)("jobs.snapshot", {})
    if reply.get("type") != "jobs.snapshot":
        raise RuntimeError("The disposable Host did not return a snapshot")
    matches = [job for job in (reply.get("payload") or {}).get("jobs", []) if job.get("id") == job_id]
    if len(matches) != 1:
        raise RuntimeError("Expected one exact synthetic job in the Host snapshot")
    row = matches[0]
    return {key:row.get(key) for key in ("fileName", "state", "receivedBytes", "totalBytes")}


@contextmanager
def running_fixture(server_dll, dotnet="dotnet", temp_root=None, name="Parity Host"):
    dll = Path(server_dll).resolve(strict=True)
    if dll.suffix.lower() != ".dll" or dll.name.lower() != "downloadmanager.server.dll":
        raise ValueError("--server-dll must be the explicitly built DownloadManager.Server.dll")

    owned = tempfile.TemporaryDirectory(prefix="inbound-paired-host-parity-", dir=temp_root)
    root = Path(owned.name) / "downloads"
    data = Path(owned.name) / "data"
    root.mkdir()
    data.mkdir()
    stop = threading.Event()
    counters = {"bytes": 0, "lock": threading.Lock()}
    slow_payload = PAYLOAD * 8
    class FixtureHandler(PayloadHandler):
        def do_HEAD(self):
            if self.path != "/fixture/slow.bin":
                return super().do_HEAD()
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(slow_payload)))
            self.end_headers()

        def do_GET(self):
            if self.path != "/fixture/slow.bin":
                return super().do_GET()
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(slow_payload)))
            self.end_headers()
            try:
                chunk_size = 128 * 1024
                for offset in range(0, len(slow_payload), chunk_size):
                    if stop.is_set():
                        break
                    chunk = slow_payload[offset:offset + chunk_size]
                    self.wfile.write(chunk)
                    self.wfile.flush()
                    with counters["lock"]:
                        counters["bytes"] += len(chunk)
                    # Each actual transfer pulse leaves Android time for a fresh UI hierarchy.
                    if stop.wait(1.7):
                        break
            except (BrokenPipeError, ConnectionResetError, OSError):
                pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
    listener = threading.Thread(target=server.serve_forever, daemon=True)
    password = "Parity-" + secrets.token_urlsafe(18).replace("-", "A").replace("_", "B")
    port = ephemeral_port()
    base = f"http://127.0.0.1:{port}"
    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(("DOWNLOADMANAGER_", "ASPNETCORE_"))}
    env.update({
        "DOWNLOADMANAGER_DATA": str(data),
        "DOWNLOADMANAGER_ROOTS": str(root),
        "DOWNLOADMANAGER_URLS": base,
        "DOWNLOADMANAGER_AUTH": "on",
        "DOWNLOADMANAGER_NAME": name,
        "DOWNLOADMANAGER_PASSWORD": password,
        "DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED": "0",
        "DOWNLOADMANAGER_TORRENT_UPNP": "off",
    })
    child = None
    listening = False
    try:
        listener.start()
        listening = True
        child = subprocess.Popen(
            [dotnet, str(dll)], cwd=str(dll.parent), env=env,
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            if child.poll() is not None:
                raise RuntimeError("disposable server exited during startup")
            try:
                with urlopen(base + "/health", timeout=1) as response:
                    if response.status == 200:
                        break
            except (OSError, URLError):
                time.sleep(0.2)
        else:
            raise RuntimeError("disposable server did not become healthy")
        yield {
            "base_url": base,
            "password": password,
            "payload_url": f"http://127.0.0.1:{server.server_port}{PAYLOAD_PATH}",
            "slow_url": f"http://127.0.0.1:{server.server_port}/fixture/slow.bin",
            "slow_counters": counters,
            "payload_sha256": PAYLOAD_SHA256,
            "payload_bytes": len(PAYLOAD),
            "data_directory": str(data),
            "download_root": str(root),
        }
    finally:
        stop.set()
        try:
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
        finally:
            try:
                if listening:
                    server.shutdown()
                    listener.join(timeout=3)
            finally:
                server.server_close()
                owned.cleanup()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-dll", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    try:
        with running_fixture(args.server_dll, args.dotnet) as descriptor:
            print(json.dumps({k: v for k, v in descriptor.items() if k != "slow_counters"}), flush=True)
            # End on stdin EOF, making graceful cleanup available to scripts too.
            sys.stdin.read()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
