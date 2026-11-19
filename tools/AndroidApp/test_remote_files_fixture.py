"""Tests for the disposable WebDAV fixture; no external files or services."""

import hashlib
import http.client
import importlib.util
from pathlib import Path
import threading
import unittest
import xml.etree.ElementTree as ET
from http.server import ThreadingHTTPServer


spec = importlib.util.spec_from_file_location(
    "remote_files_fixture", Path(__file__).with_name("remote-files-fixture.py")
)
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


class QuietHandler(fixture.Handler):
    def log_message(self, format, *args):
        pass


class FixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), QuietHandler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def request(self, method, path, headers=None):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request(method, path, headers=headers or {})
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def test_listing_matches_download(self):
        status, headers, body = self.request("PROPFIND", "/")
        self.assertEqual(status, 207)
        self.assertEqual(int(headers["Content-Length"]), len(body))
        root = ET.fromstring(body)
        responses = root.findall("{DAV:}response")
        self.assertEqual(
            [item.findtext("{DAV:}href") for item in responses],
            ["/", "/inbound-remote-files.txt"],
        )
        self.assertEqual(
            responses[1].findtext(".//{DAV:}getcontentlength"), "3"
        )

    def test_get_and_head(self):
        status, headers, body = self.request("GET", fixture.FILE_PATH)
        self.assertEqual(status, 200)
        self.assertEqual(int(headers["Content-Length"]), 3)
        self.assertEqual(len(body), 3)
        self.assertEqual(
            hashlib.sha256(body).hexdigest(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        )
        head_status, head_headers, head_body = self.request("HEAD", fixture.FILE_PATH)
        self.assertEqual(head_status, 200)
        self.assertEqual(head_headers["Content-Length"], headers["Content-Length"])
        self.assertEqual(head_headers["Last-Modified"], headers["Last-Modified"])
        self.assertEqual(head_body, b"")

    def test_propfind_depth_zero(self):
        for path, href in (("/", "/"), (fixture.FILE_PATH, fixture.FILE_PATH)):
            with self.subTest(path=path):
                status, headers, body = self.request(
                    "PROPFIND", path, headers={"Depth": "0"}
                )
                self.assertEqual(status, 207)
                self.assertEqual(int(headers["Content-Length"]), len(body))
                responses = ET.fromstring(body).findall("{DAV:}response")
                self.assertEqual(len(responses), 1)
                self.assertEqual(responses[0].findtext("{DAV:}href"), href)
                if path == "/":
                    self.assertIsNotNone(
                        responses[0].find(".//{DAV:}resourcetype/{DAV:}collection")
                    )
                else:
                    self.assertEqual(
                        responses[0].findtext(".//{DAV:}getcontentlength"), "3"
                    )
                    self.assertEqual(
                        responses[0].findtext(".//{DAV:}getlastmodified"),
                        "Thu, 19 Nov 2026 00:00:00 GMT",
                    )

    def test_propfind_depth_one_preserves_listing(self):
        status, _, body = self.request("PROPFIND", "/", headers={"Depth": "1"})
        self.assertEqual(status, 207)
        responses = ET.fromstring(body).findall("{DAV:}response")
        self.assertEqual(
            [item.findtext("{DAV:}href") for item in responses],
            ["/", fixture.FILE_PATH],
        )

    def test_unknown_paths_are_not_served(self):
        for method, path in (
            ("GET", "/"),
            ("HEAD", "/missing"),
            ("GET", fixture.FILE_PATH + "?extra=1"),
            ("PROPFIND", "/missing"),
        ):
            with self.subTest(method=method, path=path):
                self.assertEqual(self.request(method, path)[0], 404)


if __name__ == "__main__":
    unittest.main()
