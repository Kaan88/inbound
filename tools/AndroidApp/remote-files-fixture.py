"""Disposable loopback WebDAV listing and download for the Android Host probe."""

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import time


# SHA-256: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
PAYLOAD = b"abc"
FILE_PATH = "/inbound-remote-files.txt"

XML_START = b"""<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:">
"""
ROOT_RESPONSE = b"""  <D:response><D:href>/</D:href><D:propstat><D:prop>
    <D:resourcetype><D:collection/></D:resourcetype>
  </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
"""
FILE_RESPONSE = b"""  <D:response><D:href>/inbound-remote-files.txt</D:href><D:propstat><D:prop>
    <D:resourcetype/><D:getcontentlength>3</D:getcontentlength>
    <D:getlastmodified>Thu, 19 Nov 2026 00:00:00 GMT</D:getlastmodified>
  </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
"""
XML_END = b"</D:multistatus>"
LISTING = XML_START + ROOT_RESPONSE + FILE_RESPONSE + XML_END
ROOT_METADATA = XML_START + ROOT_RESPONSE + XML_END
FILE_METADATA = XML_START + FILE_RESPONSE + XML_END


class Handler(BaseHTTPRequestHandler):
    def serve_file(self, include_body):
        if self.path != FILE_PATH:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(PAYLOAD)))
        self.send_header("Last-Modified", "Thu, 19 Nov 2026 00:00:00 GMT")
        self.end_headers()
        if include_body:
            time.sleep(getattr(self.server, "get_delay", 0))
            self.wfile.write(PAYLOAD)

    def do_GET(self):
        self.serve_file(True)

    def do_HEAD(self):
        self.serve_file(False)

    def do_PROPFIND(self):
        if self.path == "/":
            body = ROOT_METADATA if self.headers.get("Depth") == "0" else LISTING
        elif self.path == FILE_PATH:
            body = FILE_METADATA
        else:
            self.send_error(404)
            return
        self.send_response(207)
        self.send_header("Content-Type", "application/xml; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=47658)
    parser.add_argument("--delay-get", type=int, choices=range(0, 11), default=0)
    args = parser.parse_args()
    with ThreadingHTTPServer(("127.0.0.1", args.port), Handler) as server:
        server.get_delay = args.delay_get
        server.serve_forever()
