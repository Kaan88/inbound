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


def seed_download(fixture):
    """Create a real transfer on the owned Host via its documented client path."""
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
    message_id = secrets.token_hex(16)
    reply = post("/api/ipc", {
        "v": 1, "id": message_id, "type": "job.add",
        "payload": {"url": fixture["payload_url"], "fileName": "c5-c6.bin",
                    "startMode": "startNow", "connections": 1},
    }, pairing["token"])
    if reply.get("type") != "job.addResult" or reply.get("replyTo") != message_id or not (reply.get("payload") or {}).get("accepted"):
        raise RuntimeError("The disposable Host refused the fixture transfer")


@contextmanager
def running_fixture(server_dll, dotnet="dotnet", temp_root=None):
    dll = Path(server_dll).resolve(strict=True)
    if dll.suffix.lower() != ".dll" or dll.name.lower() != "downloadmanager.server.dll":
        raise ValueError("--server-dll must be the explicitly built DownloadManager.Server.dll")

    owned = tempfile.TemporaryDirectory(prefix="inbound-paired-host-parity-", dir=temp_root)
    root = Path(owned.name) / "downloads"
    data = Path(owned.name) / "data"
    root.mkdir()
    data.mkdir()
    server = ThreadingHTTPServer(("127.0.0.1", 0), PayloadHandler)
    listener = threading.Thread(target=server.serve_forever, daemon=True)
    listener.start()
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
        "DOWNLOADMANAGER_NAME": "Parity Host",
        "DOWNLOADMANAGER_PASSWORD": password,
        "DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED": "0",
        "DOWNLOADMANAGER_TORRENT_UPNP": "off",
    })
    child = None
    try:
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
            "payload_sha256": PAYLOAD_SHA256,
            "payload_bytes": len(PAYLOAD),
            "data_directory": str(data),
            "download_root": str(root),
        }
    finally:
        if child is not None and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=8)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait()
        server.shutdown()
        server.server_close()
        listener.join(timeout=3)
        owned.cleanup()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-dll", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    try:
        with running_fixture(args.server_dll, args.dotnet) as descriptor:
            print(json.dumps(descriptor), flush=True)
            # End on stdin EOF, making graceful cleanup available to scripts too.
            sys.stdin.read()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
