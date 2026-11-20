"""Disposable local B3 sign-in and authenticated ranged-download fixture.

Pass deterministic synthetic payload bytes to serve(). No app/device actions.
"""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Event, Lock, Thread
from urllib.parse import quote, urlsplit
import re

ETAG = '"inbound-partial-v1"'
CHUNK = 16 * 1024


class Fixture(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, filename, payload, port=0, delay=0.12):
        if not re.fullmatch(r"inbound-b3-auth-[0-9a-f]{10,32}\.zip", filename):
            raise ValueError("Expected a nonce-owned B3 archive name")
        if not isinstance(payload, bytes) or not payload:
            raise ValueError("Expected nonempty synthetic payload bytes")
        if not 0 <= delay <= 1:
            raise ValueError("Pacing delay must be bounded")
        self.filename, self.payload, self.delay = filename, payload, delay
        self.stop_event, self.release = Event(), Event()
        self.continue_initial = Event()
        self.lock = Lock()
        self.requests = []
        self.initial_bytes = self.resumed_bytes = self.active_responses = 0
        super().__init__(("127.0.0.1", port), Handler)

    def snapshot(self):
        with self.lock:
            return {"requests": [dict(r) for r in self.requests],
                    "initial_bytes": self.initial_bytes, "resumed_bytes": self.resumed_bytes,
                    "active_responses": self.active_responses}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def respond(self, status, body=b"", headers=()):
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        for key, value in headers:
            self.send_header(key, value)
        self.end_headers()
        if self.command != "HEAD" and body:
            self.wfile.write(body)

    def do_HEAD(self):
        self.handle_request(False)

    def do_GET(self):
        self.handle_request(True)

    def do_POST(self):
        self.handle_request(False)

    def handle_request(self, body):
        fixture = self.server
        path = urlsplit(self.path).path
        protected = "/auth-resume/" + fixture.filename
        if self.command == "POST" and path != protected:
            self.respond(405)
            self.close_connection = True
            return
        if path == "/page":
            self.respond(200, b'<!doctype html><title>Inbound sign-in fixture</title><a href="/signin">Sign in</a>',
                         (("Content-Type", "text/html; charset=utf-8"),))
            return
        if path == "/signin":
            self.respond(303, headers=(("Set-Cookie", "inbound_test=yes; Path=/; HttpOnly; SameSite=Lax"),
                                       ("Location", "/auth-resume")))
            return
        if path == "/auth-resume":
            page = (b'<!doctype html><meta name="viewport" content="width=device-width">'
                    b'<title>Authenticated fixture</title><a href="/auth-resume/' +
                    quote(fixture.filename).encode("ascii") + b'">Authenticated archive</a>')
            self.respond(200, page, (("Content-Type", "text/html; charset=utf-8"),))
            return
        if path != protected:
            self.respond(404)
            return
        auth = any(x.strip() == "inbound_test=yes" for x in self.headers.get("Cookie", "").split(";"))
        raw = self.headers.get("Range", "")
        match = re.fullmatch(r"bytes=(\d{1,12})-(\d{0,12})", raw) if raw else None
        start, end = 0, len(fixture.payload) - 1
        valid = not raw or match is not None
        if match:
            start = int(match.group(1))
            end = min(int(match.group(2)) if match.group(2) else end, end)
            valid = start <= end and start < len(fixture.payload)
        request = {"method": self.command, "authenticated": auth,
                   "range": [start, end] if raw and valid else None,
                   "range_valid": valid, "body_bytes": 0}
        with fixture.lock:
            fixture.requests.append(request)
        if self.command == "POST":
            self.respond(405)
            self.close_connection = True
            return
        if not auth:
            self.respond(403)
            return
        if not valid:
            self.respond(416, headers=(("Content-Range", f"bytes */{len(fixture.payload)}"),))
            return
        self.send_response(206 if raw else 200)
        self.send_header("Content-Type", "application/zip")
        self.send_header("Content-Disposition", f'attachment; filename="{fixture.filename}"')
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("ETag", ETAG)
        if raw:
            self.send_header("Content-Range", f"bytes {start}-{end}/{len(fixture.payload)}")
        self.end_headers()
        if not body:
            return
        with fixture.lock:
            fixture.active_responses += 1
        try:
            for offset in range(start, end + 1, CHUNK):
                if fixture.stop_event.is_set():
                    break
                chunk = fixture.payload[offset:min(offset + CHUNK, end + 1)]
                self.wfile.write(chunk)
                self.wfile.flush()
                with fixture.lock:
                    request["body_bytes"] += len(chunk)
                    if start > 0:
                        fixture.resumed_bytes += len(chunk)
                    else:
                        fixture.initial_bytes += len(chunk)
                if start == 0 and offset + len(chunk) == 2 * 1024 * 1024:
                    # Allow the UI hierarchy to settle while the real response stays open.
                    # The driver releases this hold after tapping Pause, so closure is observable.
                    fixture.continue_initial.wait(24)
                if not fixture.release.is_set() and fixture.stop_event.wait(fixture.delay):
                    break
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, TimeoutError):
            pass
        finally:
            with fixture.lock:
                fixture.active_responses -= 1
            self.close_connection = True


@contextmanager
def serve(filename, payload, port=0, delay=0.12):
    """Yield fixture; use server_address, snapshot(), release.set(), stop_event.set()."""
    fixture = Fixture(filename, payload, port=port, delay=delay)
    thread = Thread(target=fixture.serve_forever, daemon=True)
    thread.start()
    try:
        yield fixture
    finally:
        fixture.stop_event.set()
        fixture.continue_initial.set()
        fixture.shutdown()
        fixture.server_close()
        thread.join(timeout=5)
        if thread.is_alive():
            raise RuntimeError("Fixture server thread did not stop")
