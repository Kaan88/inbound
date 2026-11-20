"""Local Android link-intake fixture; run manually, never exposed beyond loopback."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
import io
import zipfile
import hashlib
import re
import threading
import time

PAYLOAD = bytes(range(256)) * 4096
PARTIAL_PAYLOAD = bytes(range(256)) * 65536
ETAG = '"inbound-fixture-v1"'
PARTIAL_ETAG = '"inbound-partial-v1"'
PARTIAL_STATE = {"expired": False, "ranges": [], "lock": threading.Lock()}
REPORT_STATE = {"authenticated_gets": 0, "lock": threading.Lock()}
HTML = Path(__file__).with_name('page.html').read_bytes()
MIMES = {'report-q3-2026.xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'a.zip': 'application/zip', 'b.pdf': 'application/pdf', 'c.jpg': 'image/jpeg',
         'auth.zip': 'application/zip', 'expired.zip': 'application/zip', 'slow.zip': 'application/zip', 'post.zip': 'application/zip'}

def archive_files():
    files = {}
    for index, name in enumerate(("field-recordings-vol1", "field-recordings-vol2", "field-recordings-vol3",
                                  "field-recordings-vol4", "liner-notes", "samples-preview"), 1):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            archive.writestr("sample.bin", PAYLOAD[:index * 32768])
        files[name + ".zip"] = stream.getvalue()
    return files


ARCHIVES = archive_files()
ARCHIVE_PAGE = ('<!doctype html><meta name="viewport" content="width=device-width"><title>Field recordings</title>'
                '<h1>Field recordings</h1>' + ''.join(
                    f'<p><a href="/recordings/{name}" data-size-bytes="{len(data)}">{name}</a></p>'
                    for name, data in ARCHIVES.items()) +
                '<a href="/files/c.jpg">Cover image</a><a href="/files/b.pdf">Readme</a>').encode()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass  # Never emit paths, query strings, headers or client data from base class.

    def do_HEAD(self): self.handle_request(False)
    def do_GET(self): self.handle_request(True)
    def do_POST(self):
        size = min(int(self.headers.get('Content-Length', '0')), 1048576)
        self.rfile.read(size)
        self.handle_request(True)

    def handle_request(self, body):
        parsed = urlsplit(self.path)
        path = parsed.path
        name = path.rsplit('/', 1)[-1]
        route = 'partial' if path in ('/partial/old.zip', '/partial/new.zip') else 'files' if path.startswith('/files/') and name in ('a.zip', 'b.pdf', 'c.jpg', 'slow.zip', 'post.zip') else {
            '/reports': 'reports', '/reports/report-q3-2026.xlsx': 'report', '/page': 'page', '/signin': 'signin', '/auth.zip': 'auth', '/expired.zip': 'expired'
        }.get(path, 'unknown')
        cookie = self.headers.get('Cookie', '')
        present = any(part.strip() == 'inbound_test=yes' for part in cookie.split(';'))
        requested = self.headers.get('Range', '')
        # Only a valid byte range is logged; never echo arbitrary header content.
        match = re.fullmatch(r'bytes=(\d{1,12})-(\d{0,12})', requested)
        range_log = match.group(0) if match else ('INVALID' if requested else 'NONE')
        print(f'{self.command} {route} Cookie {"PRESENT" if present else "ABSENT"} Range {range_log}', flush=True)
        if path == "/partial/page":
            page = (b'<!doctype html><meta name="viewport" content="width=device-width">'
                    b'<title>Replacement fixture</title><a href="/partial/new.zip">'
                    b'Catch replacement</a>')
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(page)))
            self.end_headers()
            if body: self.wfile.write(page)
            return
        if path == "/field-recordings":
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(ARCHIVE_PAGE)))
            self.end_headers()
            if body:
                self.wfile.write(ARCHIVE_PAGE)
            return
        if path == "/recordings/" + name and name in ARCHIVES:
            self.serve_payload(ARCHIVES[name], requested, '"' + name + '"', name, body)
            return
        if route == 'partial':
            with PARTIAL_STATE["lock"]:
                expired = PARTIAL_STATE["expired"] and path == '/partial/old.zip'
                if requested and self.command == 'GET':
                    PARTIAL_STATE["ranges"].append((path, range_log))
            if expired:
                self.empty(410)
                return
            payload = PARTIAL_PAYLOAD
            self.serve_payload(payload, requested, PARTIAL_ETAG, name, body, slow=True)
            return
        if route in ('reports', 'report') and not present:
            self.empty(403)
            return
        if route == 'report':
            if self.command == 'GET' and present:
                with REPORT_STATE["lock"]:
                    REPORT_STATE["authenticated_gets"] += 1
            report = Path(__file__).with_name(name).read_bytes()
            self.serve_payload(report, requested, '"quarterly-report-v1"', name, body)
            return
        if route == 'signin':
            self.send_response(303); self.send_header('Set-Cookie', 'inbound_test=yes; Path=/; HttpOnly; SameSite=Lax')
            self.send_header('Location', '/page'); self.send_header('Content-Length', '0'); self.end_headers(); return
        if route in ('page', 'reports'):
            page = HTML if route == 'page' else Path(__file__).with_name('reports.html').read_bytes()
            self.send_response(200); self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(page))); self.end_headers()
            if body: self.wfile.write(page)
            return
        if route == 'unknown': self.empty(404); return
        if route == 'auth' and not present: self.empty(403); return
        if route == 'expired':
            token = parse_qs(parsed.query).get('token')
            if token == ['gone']: self.empty(410); return
            if token != ['new']: self.empty(403); return
        if name == 'slow.zip' and self.command == 'HEAD': time.sleep(6)
        slow = route == 'expired' and parse_qs(parsed.query).get('slow') == ['1']
        self.serve_payload(PAYLOAD, requested, ETAG, name, body, slow=slow)

    def serve_payload(self, payload, requested, etag, name, body, slow=False):
        match = re.fullmatch(r'bytes=(\d{1,12})-(\d{0,12})', requested)
        start, end = 0, len(payload) - 1
        if requested:
            if not match: self.unsatisfied(len(payload)); return
            start = int(match.group(1)); end = min(int(match.group(2)) if match.group(2) else end, end)
            if start > end or start >= len(payload): self.unsatisfied(len(payload)); return
        self.send_response(206 if requested else 200)
        self.send_header('Content-Type', MIMES.get(name, 'application/zip')); self.send_header('Content-Disposition', f'attachment; filename="{name}"')
        self.send_header('Content-Length', str(end - start + 1)); self.send_header('Accept-Ranges', 'bytes'); self.send_header('ETag', etag)
        if requested: self.send_header('Content-Range', f'bytes {start}-{end}/{len(payload)}')
        self.end_headers()
        if body:
            try:
                chunk = 16384
                for offset in range(start, end + 1, chunk):
                    self.wfile.write(payload[offset:min(offset + chunk, end + 1)])
                    if slow:
                        self.wfile.flush()
                        if name == 'old.zip' and offset - start >= 1024 * 1024 and (offset - start) % (128 * 1024) == 0:
                            time.sleep(4)
                        elif name == 'old.zip':
                            time.sleep(0.04)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass

    def empty(self, status):
        self.send_response(status); self.send_header('Content-Length', '0'); self.end_headers()
    def unsatisfied(self, size):
        self.send_response(416); self.send_header('Content-Range', f'bytes */{size}'); self.send_header('Content-Length', '0'); self.end_headers()

if __name__ == '__main__':
    print('Fixture listening on 127.0.0.1:47612; payload SHA-256 ' + hashlib.sha256(PAYLOAD).hexdigest(), flush=True)
    ThreadingHTTPServer(('127.0.0.1', 47612), Handler).serve_forever()
