"""Disposable nested WebDAV tree for the C3/C4 Remote Files UI capture."""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import argparse
import hashlib
import re
from threading import Lock
from urllib.parse import quote, unquote, urlsplit


FILES = {
    "session-0412.flac": bytes((index * 17 + 3) % 256 for index in range(1024 * 1024)),
    "session-0419.flac": bytes((index * 29 + 11) % 256 for index in range(2 * 1024 * 1024)),
    "session-0503.flac": bytes((index * 7 + 19) % 256 for index in range(512 * 1024)),
    "notes.pdf": b"%PDF-1.4\nDisposable Remote Files parity fixture.\n%%EOF\n",
    "artwork.zip": b"PK\x05\x06" + b"\0" * 18,
}
HASHES = {name: hashlib.sha256(payload).hexdigest() for name, payload in FILES.items()}
RECORDINGS = "media/recordings/2026"
NOTES = RECORDINGS + "/notes.pdf"
STAGING_PREFIX = ".inbound-upload-"
STAGING_NAME = re.compile(r"\.inbound-upload-[0-9a-fA-F]{32}")
MAX_UPLOAD_BYTES = 16 * 1024 * 1024
DEFAULT_MODIFIED = "Sun, 12 Apr 2026 12:00:00 GMT"

LAST_MODIFIED = {
    "session-0412.flac": "Sun, 12 Apr 2026 12:00:00 GMT",
    "session-0419.flac": "Sun, 19 Apr 2026 12:00:00 GMT",
    "session-0503.flac": "Sun, 03 May 2026 12:00:00 GMT",
    "notes.pdf": "Sun, 03 May 2026 12:00:05 GMT",
    "artwork.zip": "Sat, 09 May 2026 12:00:00 GMT",
}

def numbered_files(prefix, count):
    return tuple((f"{prefix}-{index:02d}.flac", False) for index in range(1, count + 1))

TREE = {
    "": (("media", True),),
    "media": (("recordings", True),),
    "media/recordings": (("2026", True),),
    "media/recordings/2026": (
        ("masters", True),
        ("stems", True),
        ("session-0412.flac", False),
        ("session-0419.flac", False),
        ("session-0503.flac", False),
        ("notes.pdf", False),
        ("artwork.zip", False),
    ),
    "media/recordings/2026/masters": numbered_files("master", 12),
    "media/recordings/2026/stems": numbered_files("stem", 48),
}


def _xml_escape(value):
    return (value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
            .replace('"', "&quot;").replace("'", "&apos;"))


def listing(path, deleted=False, include_children=True, uploads=None):
    uploads = uploads if uploads is not None else {}
    path = path.strip("/")
    children = TREE.get(path)
    if children is None:
        parent, _, name = path.rpartition("/")
        if path in uploads:
            return multistatus([file_response(path, name, len(uploads[path]))])
        if deleted and path == NOTES:
            return None
        if (name, False) not in TREE.get(parent, ()):
            return None
        size = len(FILES[name]) if name in FILES else 1024 * 1024
        return multistatus([file_response(path, name, size)])
    root_href = f"/{_xml_escape(path)}/" if path else "/"
    responses = [
        f"""<D:response><D:href>{root_href}</D:href><D:propstat><D:prop>
        <D:resourcetype><D:collection/></D:resourcetype>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>"""
    ]
    for name, is_folder in children if include_children else ():
        child = f"{path}/{name}".strip("/")
        if deleted and child == NOTES:
            continue
        if is_folder:
            properties = "<D:resourcetype><D:collection/></D:resourcetype>"
        else:
            size = len(FILES[name]) if name in FILES else 1024 * 1024
            responses.append(file_response(child, name, size))
            continue
        responses.append(
            f"""<D:response><D:href>/{_xml_escape(child)}</D:href><D:propstat><D:prop>
            {properties}
            </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>"""
        )
    if include_children:
        for child, payload in uploads.items():
            parent, _, name = child.rpartition("/")
            if parent == path:
                responses.append(file_response(child, name, len(payload)))
    return multistatus(responses)


def file_response(path, name, size=None):
    size = size if size is not None else len(FILES[name])
    modified = LAST_MODIFIED.get(name, DEFAULT_MODIFIED)
    return (f"<D:response><D:href>/{quote(path)}</D:href><D:propstat><D:prop>"
            f"<D:resourcetype/><D:getcontentlength>{size}</D:getcontentlength>"
            f"<D:getlastmodified>{modified}</D:getlastmodified>"
            "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>")


def multistatus(responses):
    return ('<?xml version="1.0" encoding="utf-8"?>'
            '<D:multistatus xmlns:D="DAV:">' + "".join(responses) + '</D:multistatus>').encode()


class Handler(BaseHTTPRequestHandler):
    def _path(self):
        return unquote(urlsplit(self.path).path).strip("/")

    @staticmethod
    def _upload_path(raw):
        if not raw.startswith("/"):
            return None
        parts = [unquote(part) for part in raw[1:].split("/")]
        if any(not part or part in (".", "..") or "/" in part or "\\" in part
               or any(ord(char) < 32 or char == "\x7f" for char in part) for part in parts):
            return None
        parent, name = "/".join(parts[:-1]), parts[-1]
        return (parent, name) if parent in TREE else None

    def _staging(self):
        try:
            target = urlsplit(self.path)
        except ValueError:
            return None
        if target.scheme or target.netloc or target.query or target.fragment:
            return None
        path = self._upload_path(target.path)
        if path and STAGING_NAME.fullmatch(path[1]):
            return "/".join(filter(None, path))
        return None

    def _status(self, status):
        self.send_response(status)
        self.end_headers()

    def do_PROPFIND(self):
        with self.server.parity_lock:
            body = listing(self._path(), self.server.parity_notes_deleted,
                           include_children=self.headers.get("Depth", "1") != "0",
                           uploads=self.server.parity_uploads)
        if body is None:
            self.send_error(404)
            return
        self.send_response(207)
        self.send_header("Content-Type", "application/xml; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self._serve_file(send_body=True)

    def do_HEAD(self):
        self._serve_file(send_body=False)

    def _serve_file(self, send_body):
        path = self._path()
        parent, _, name = path.rpartition("/")
        with self.server.parity_lock:
            payload = self.server.parity_uploads.get(path)
            if (payload is None and parent == RECORDINGS
                    and not (path == NOTES and self.server.parity_notes_deleted)):
                payload = FILES.get(name)
                if payload is not None and send_body:
                    self.server.parity_gets.add(name)
        if payload is None:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Last-Modified", LAST_MODIFIED.get(name, DEFAULT_MODIFIED))
        self.end_headers()
        if send_body:
            self.wfile.write(payload)

    def do_PUT(self):
        stage = self._staging()
        if stage is None:
            self.send_error(404)
            return
        lengths = self.headers.get_all("Content-Length", [])
        if (len(lengths) != 1 or not re.fullmatch(r"[0-9]{1,9}", lengths[0])
                or "Transfer-Encoding" in self.headers):
            self.send_error(400)
            return
        length = int(lengths[0])
        if length > MAX_UPLOAD_BYTES:
            self.send_error(413)
            return
        payload = self.rfile.read(length)
        if len(payload) != length:
            self.send_error(400)
            return
        with self.server.parity_lock:
            if stage in self.server.parity_upload_staging:
                status = 412
            else:
                self.server.parity_upload_staging[stage] = payload
                status = 201
        self._status(status)

    def do_MOVE(self):
        stage = self._staging()
        if stage is None:
            self.send_error(404)
            return
        destinations = self.headers.get_all("Destination", [])
        try:
            target = urlsplit(destinations[0]) if len(destinations) == 1 else None
        except ValueError:
            target = None
        hosts = self.headers.get_all("Host", [])
        port = self.server.server_port
        if (target is None or target.scheme != "http"
                or target.netloc.lower() not in (f"127.0.0.1:{port}", f"localhost:{port}")
                or len(hosts) != 1 or hosts[0].lower() != target.netloc.lower()
                or target.query or target.fragment
                or self.headers.get_all("Overwrite") != ["F"]):
            self.send_error(400)
            return
        destination = self._upload_path(target.path)
        if (destination is None or destination[0] != stage.rpartition("/")[0]
                or destination[1].startswith(STAGING_PREFIX)):
            self.send_error(400)
            return
        parent, name = destination
        path = "/".join(filter(None, destination))
        # Serialize the check and publication: even simultaneous MOVEs cannot replace a winner.
        with self.server.parity_lock:
            if stage not in self.server.parity_upload_staging:
                status = 404
            elif (path in self.server.parity_uploads
                  or (name in dict(TREE[parent]) and not (path == NOTES and self.server.parity_notes_deleted))):
                status = 412
            else:
                self.server.parity_uploads[path] = self.server.parity_upload_staging.pop(stage)
                status = 201
        self._status(status)

    def do_DELETE(self):
        stage = self._staging()
        with self.server.parity_lock:
            if stage is not None and self.server.parity_upload_staging.pop(stage, None) is not None:
                status = 204
            elif self._path() == NOTES and not self.server.parity_notes_deleted:
                self.server.parity_notes_deleted = True
                status = 204
            else:
                status = 404
        self._status(status)

    def log_message(self, _format, *_args):
        pass


class FixtureServer(ThreadingHTTPServer):
    def __init__(self, server_address, handler=Handler):
        if server_address[0] != "127.0.0.1":
            raise ValueError("The disposable WebDAV fixture only binds to 127.0.0.1")
        super().__init__(server_address, handler)
        self.parity_lock = Lock()
        self.parity_notes_deleted = False
        self.parity_gets = set()
        self.parity_upload_staging = {}
        # Published fixture-relative paths (no leading slash) mapped to their exact bytes.
        self.parity_uploads = {}


def serve(port):
    with FixtureServer(("127.0.0.1", port)) as server:
        print(f"Remote Files parity fixture listening on 127.0.0.1:{port}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=47659)
    serve(parser.parse_args().port)
