"""Owned loopback byte-range fixture for a future isolated A6 probe."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import re
import threading

# Identical to capture-download-states-parity.fixture_bytes().
BODY = bytes(range(256)) * (16 * 1024 * 1024 // 256)
SHA256 = hashlib.sha256(BODY).hexdigest()
ETAG = '"a6-range-fixture-v1"'
CHUNK = 16 * 1024


def parse_range(requested, size):
    if requested is None:
        return 0, size - 1
    match = re.fullmatch(r"bytes=(\d{1,12})-(\d{0,12})", requested)
    if not match:
        return None
    start = int(match.group(1))
    end = min(int(match.group(2)) if match.group(2) else size - 1, size - 1)
    return None if start > end or start >= size else (start, end)


@contextmanager
def running_fixture(filename="a6-fixture.bin"):
    if not re.fullmatch(r"[A-Za-z0-9_-]+\.bin", filename):
        raise ValueError("Fixture filename must be a safe .bin basename")
    release = threading.Event()
    stop = threading.Event()
    lock = threading.Lock()
    state = {"active": 0, "max_active": 0, "requests": [], "next_id": 0}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def respond(self, body):
            if self.path != "/a6.bin":
                self.send_error(404)
                return
            requested = self.headers.get("Range")
            bounds = parse_range(requested, len(BODY))
            if bounds is None:
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{len(BODY)}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            start, end = bounds
            self.send_response(206 if requested else 200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Disposition", f'attachment; filename="{filename}"')
            self.send_header("Content-Length", str(end - start + 1))
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("ETag", ETAG)
            if requested:
                self.send_header("Content-Range", f"bytes {start}-{end}/{len(BODY)}")
            self.end_headers()
            if not body:
                return
            # A small range probe must finish so the engine can start workers.
            tracked = bool(requested) and end - start + 1 > CHUNK
            record = None
            try:
                if tracked:
                    with lock:
                        state["next_id"] += 1
                        record = {"id": state["next_id"], "start": start, "end": end,
                                  "bytes_sent": 0, "overlap": False}
                        for other in state["requests"]:
                            if other.get("active"):
                                other["overlap"] = record["overlap"] = True
                        record["active"] = True
                        state["requests"].append(record)
                        state["active"] += 1
                        state["max_active"] = max(state["max_active"], state["active"])
                for offset in range(start, end + 1, CHUNK):
                    if stop.is_set():
                        break
                    chunk = BODY[offset:min(offset + CHUNK, end + 1)]
                    self.wfile.write(chunk)
                    self.wfile.flush()
                    if record is not None:
                        with lock:
                            record["bytes_sent"] += len(chunk)
                        # Send 128 KiB pulses so both workers report actual progress
                        # while leaving stable intervals for the native hierarchy capture.
                        if (offset - start + len(chunk)) % (8 * CHUNK) == 0:
                            if stop.wait(.03 if release.is_set() else 1.4):
                                break
                    elif stop.wait(.01):
                        break
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
                pass
            finally:
                if record is not None:
                    with lock:
                        record["active"] = False
                        state["active"] -= 1

        def do_HEAD(self): self.respond(False)
        def do_GET(self): self.respond(True)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    def snapshot():
        with lock:
            return {"active": state["active"], "max_active": state["max_active"],
                    "requests": [dict(record) for record in state["requests"]]}

    try:
        yield {"port": server.server_port, "size": len(BODY), "sha256": SHA256,
               "release": release, "snapshot": snapshot}
    finally:
        stop.set()
        release.set()
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
