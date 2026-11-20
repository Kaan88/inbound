"""Loopback-only synthetic S3 endpoint for conditional-upload acceptance tests."""

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading
from xml.sax.saxutils import escape
from urllib.parse import parse_qs, urlsplit


class S3Fixture:
    """State and explicit race controls shared by tests and emulator runners."""

    def __init__(self):
        self.lock = threading.Lock()
        self.objects = {"pub/readme.txt": b"synthetic S3 fixture\n"}
        self.puts = []
        self.race_arrived = threading.Event()
        self.race_release = threading.Event()
        self.race_enabled = False

    def competitor_write(self, key, data):
        with self.lock:
            self.objects[key] = bytes(data)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    @property
    def fixture(self):
        return self.server.fixture

    def _reply(self, status, body=b"", content_type="application/xml"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def _key(self):
        path = urlsplit(self.path).path.strip("/")
        parts = path.split("/", 1)
        return parts[1] if len(parts) == 2 and parts[0] == "bucket" else None

    def do_PUT(self):
        key = self._key()
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        auth = self.headers.get("Authorization", "")
        signed_shape = (auth.startswith("AWS4-HMAC-SHA256 ")
                        and "Credential=" in auth and "SignedHeaders=" in auth
                        and "Signature=" in auth)
        with self.fixture.lock:
            self.fixture.puts.append({
                "path": self.path,
                "key": key,
                "if_none_match": self.headers.get("If-None-Match"),
                "authorization_shape": signed_shape,
                "bytes": body,
            })
        if not key or urlsplit(self.path).query != "x-id=PutObject" or not signed_shape:
            self._reply(400)
            return

        if self.fixture.race_enabled and key == "race.bin":
            self.fixture.race_arrived.set()
            if not self.fixture.race_release.wait(15):
                self._reply(503)
                return

        # The precondition check and publication are one indivisible operation.
        with self.fixture.lock:
            if self.headers.get("If-None-Match") != "*":
                self._reply(400)
                return
            if key in self.fixture.objects:
                self._reply(412, b"<Error><Code>PreconditionFailed</Code><Message>Object exists</Message></Error>")
                return
            self.fixture.objects[key] = body
        self._reply(200, b"", "application/xml")

    def do_HEAD(self):
        key = self._key()
        with self.fixture.lock:
            body = self.fixture.objects.get(key)
        if body is None:
            self._reply(404)
        else:
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Last-Modified", "Sat, 09 May 2026 12:00:00 GMT")
            self.end_headers()

    def do_GET(self):
        key = self._key()
        if urlsplit(self.path).path == "/":
            self._reply(200, b"<ListAllMyBucketsResult><Buckets><Bucket><Name>bucket</Name>"
                        b"<CreationDate>2026-05-09T12:00:00Z</CreationDate></Bucket></Buckets></ListAllMyBucketsResult>")
            return
        if key is None and urlsplit(self.path).path.rstrip("/") == "/bucket":
            query = parse_qs(urlsplit(self.path).query)
            prefix = query.get("prefix", [""])[0]
            delimiter = query.get("delimiter", [""])[0]
            with self.fixture.lock:
                items = sorted(self.fixture.objects.items())
            groups = set()
            visible = []
            for name, body in items:
                if not name.startswith(prefix):
                    continue
                tail = name[len(prefix):]
                if delimiter and delimiter in tail:
                    groups.add(prefix + tail.split(delimiter, 1)[0] + delimiter)
                else:
                    visible.append((name, body))
            entries = "".join(
                f"<Contents><Key>{escape(k)}</Key><Size>{len(v)}</Size>"
                "<LastModified>2026-05-09T12:00:00Z</LastModified></Contents>"
                for k, v in visible
            ) + "".join(
                f"<CommonPrefixes><Prefix>{escape(group)}</Prefix></CommonPrefixes>"
                for group in sorted(groups)
            )
            self._reply(200, ("<ListBucketResult>" + entries +
                              "</ListBucketResult>").encode())
            return
        if key is None:
            self._reply(404)
            return
        with self.fixture.lock:
            body = self.fixture.objects.get(key)
        if body is None:
            self._reply(404)
        else:
            self._reply(200, body, "application/octet-stream")

def serve(host="127.0.0.1", port=0, fixture=None):
    if host not in ("127.0.0.1", "::1", "localhost"):
        raise ValueError("S3 fixture must bind to loopback")
    fixture = fixture or S3Fixture()
    server = ThreadingHTTPServer((host, port), Handler)
    server.fixture = fixture
    return server


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=47659)
    args = parser.parse_args()
    server = serve(port=args.port)
    print(json.dumps({"url": f"http://127.0.0.1:{server.server_port}", "bucket": "bucket"}), flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()
