"""Local Android link-intake fixture; run manually, never exposed beyond loopback."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
import hashlib
import re
import time

PAYLOAD = bytes(range(256)) * 4096
ETAG = '"inbound-fixture-v1"'
HTML = Path(__file__).with_name('page.html').read_bytes()
MIMES = {'a.zip': 'application/zip', 'b.pdf': 'application/pdf', 'c.jpg': 'image/jpeg',
         'auth.zip': 'application/zip', 'expired.zip': 'application/zip', 'slow.zip': 'application/zip', 'post.zip': 'application/zip'}

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
        route = 'files' if path.startswith('/files/') and name in ('a.zip', 'b.pdf', 'c.jpg', 'slow.zip', 'post.zip') else {
            '/page': 'page', '/signin': 'signin', '/auth.zip': 'auth', '/expired.zip': 'expired'
        }.get(path, 'unknown')
        cookie = self.headers.get('Cookie', '')
        present = any(part.strip() == 'inbound_test=yes' for part in cookie.split(';'))
        requested = self.headers.get('Range', '')
        # Only a valid byte range is logged; never echo arbitrary header content.
        match = re.fullmatch(r'bytes=(\d{1,12})-(\d{0,12})', requested)
        range_log = match.group(0) if match else ('INVALID' if requested else 'NONE')
        print(f'{self.command} {route} Cookie {"PRESENT" if present else "ABSENT"} Range {range_log}', flush=True)
        if route == 'signin':
            self.send_response(303); self.send_header('Set-Cookie', 'inbound_test=yes; Path=/; HttpOnly; SameSite=Lax')
            self.send_header('Location', '/page'); self.send_header('Content-Length', '0'); self.end_headers(); return
        if route == 'page':
            self.send_response(200); self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(HTML))); self.end_headers()
            if body: self.wfile.write(HTML)
            return
        if route == 'unknown': self.empty(404); return
        if route == 'auth' and not present: self.empty(403); return
        if route == 'expired' and parse_qs(parsed.query).get('token') != ['new']: self.empty(403); return
        if name == 'slow.zip' and self.command == 'HEAD': time.sleep(6)
        start, end = 0, len(PAYLOAD) - 1
        if requested:
            if not match: self.unsatisfied(); return
            start = int(match.group(1)); end = min(int(match.group(2)) if match.group(2) else end, end)
            if start > end or start >= len(PAYLOAD): self.unsatisfied(); return
        self.send_response(206 if requested else 200)
        self.send_header('Content-Type', MIMES[name]); self.send_header('Content-Disposition', f'attachment; filename="{name}"')
        self.send_header('Content-Length', str(end - start + 1)); self.send_header('Accept-Ranges', 'bytes'); self.send_header('ETag', ETAG)
        if requested: self.send_header('Content-Range', f'bytes {start}-{end}/{len(PAYLOAD)}')
        self.end_headers()
        if body:
            slow = route == 'expired' and parse_qs(parsed.query).get('slow') == ['1']
            try:
                for offset in range(start, end + 1, 16384):
                    self.wfile.write(PAYLOAD[offset:min(offset + 16384, end + 1)])
                    if slow: self.wfile.flush(); time.sleep(0.05)
            except (BrokenPipeError, ConnectionResetError): pass

    def empty(self, status):
        self.send_response(status); self.send_header('Content-Length', '0'); self.end_headers()
    def unsatisfied(self):
        self.send_response(416); self.send_header('Content-Range', f'bytes */{len(PAYLOAD)}'); self.send_header('Content-Length', '0'); self.end_headers()

if __name__ == '__main__':
    print('Fixture listening on 127.0.0.1:47612; payload SHA-256 ' + hashlib.sha256(PAYLOAD).hexdigest(), flush=True)
    ThreadingHTTPServer(('127.0.0.1', 47612), Handler).serve_forever()
