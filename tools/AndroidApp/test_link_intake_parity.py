"""Host-only checks for B1-B6 capture inventory and fixture references."""

import importlib.util
import importlib.machinery
from http.server import ThreadingHTTPServer
from pathlib import Path
import threading
import urllib.error
import urllib.request
import unittest

HERE = Path(__file__).parent
SPEC = importlib.util.spec_from_file_location(
    "link_intake_capture", HERE / "capture-link-intake-parity.py")
CAPTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAPTURE)


class LinkIntakeParityTests(unittest.TestCase):
    def test_all_b_screens_and_chooser_have_approved_references(self):
        keys = [row[0] for row in CAPTURE.CAPTURES]
        self.assertEqual(keys, [
            "b1-chooser", "b1-caught", "b2", "b3", "b4", "b6"
        ])
        for _, reference, _ in CAPTURE.CAPTURES:
            if reference is None:
                continue
            with self.subTest(reference=reference):
                self.assertTrue((CAPTURE.MOCKUPS / reference).is_file())
        self.assertEqual(CAPTURE.CAPTURES[0][1], "b1-share.png")
        self.assertIsNone(CAPTURE.CAPTURES[1][1])

    def test_only_isolated_api33_probe_and_local_fixture_are_configured(self):
        self.assertEqual(CAPTURE.PACKAGE, "app.inbound.android.rcloneprobe")
        self.assertEqual(CAPTURE.AVD, "inbound33")
        self.assertEqual(CAPTURE.API, "33")
        self.assertTrue(CAPTURE.FIXTURE.is_file())
        fixture_source = CAPTURE.FIXTURE.read_text(encoding="utf-8")
        for route in ("/page", "/signin", "/auth.zip", "/expired.zip"):
            with self.subTest(route=route):
                self.assertIn(route, fixture_source)
        self.assertIn("Sharing link", CAPTURE.CAPTURES[0][2])

    def test_unattended_does_not_pair_page_selection_with_b5(self):
        import inspect
        source = inspect.getsource(CAPTURE.Capture.unattended_sequence)
        self.assertIn('self.capture("b4"', source)
        self.assertIn('self.capture("b4-selected", None', source)
        self.assertNotIn('"b5-select.png"', source)
        self.assertNotIn("b1-chooser", source)
        self.assertIn("UNVERIFIED", source)
        self.assertEqual(source.count('tap("Browser options")'), 2)

    def test_b6_is_separate_opt_in_and_uses_verified_exact_file_cleanup(self):
        import inspect
        source = inspect.getsource(CAPTURE.Capture.capture_b6)
        self.assertIn("token=gone", source)
        self.assertIn("token=new", source)
        self.assertIn("b6-failed-details", source)
        self.assertIn("b6-refresh-sheet", source)
        self.assertIn("sha256sum", source)
        self.assertIn('self.shell("rm", "--", target)', source)
        self.assertLess(source.index('require(digest == self.fixture_hash'),
                        source.index('self.shell("rm", "--", target)'))
        parser_source = inspect.getsource(CAPTURE.main)
        self.assertIn('"--capture-b6"', parser_source)
        self.assertIn("args.capture_b6 or args.capture_b6_partial", parser_source)

    def test_partial_b6_lane_is_separate_and_checks_range_hash_and_exact_cleanup(self):
        import inspect
        source = inspect.getsource(CAPTURE.Capture.capture_b6_partial)
        for text in ("PARTIAL_STATE", 'bounds_for("connections_block")', 'wait_node("1", resource="connections_value")',
                     "This link has expired", "replacement_ranges",
                     "sha256sum", 'self.shell("rm", "--", target)'):
            with self.subTest(text=text):
                self.assertIn(text, source)
        parser_source = inspect.getsource(CAPTURE.main)
        self.assertIn("--capture-b6-partial", parser_source)
        self.assertIn("args.capture_b6 and args.capture_b6_partial", parser_source)
        self.assertNotIn("Browser options", source)

    def test_details_stop_is_opt_in_and_requires_partial_unattended_lane(self):
        import contextlib
        import inspect
        import io
        import sys
        from unittest.mock import patch

        source = inspect.getsource(CAPTURE.Capture.capture_b6_partial)
        self.assertIn('wait_node("Stop", resource="hero_stop")', source)
        self.assertIn('stop.get("enabled") == "true"', source)
        self.assertIn(':id/hero_again', source)
        self.assertIn('else:\n            tap("Pause")', source)
        for flags in ([], ["--unattended"], ["--capture-b6-partial"]):
            with self.subTest(flags=flags), patch.object(sys, "argv", [
                "capture", "--serial", "test", "--apk", "test.apk",
                "--evidence", "test-evidence", "--verify-details-stop", *flags
            ]), contextlib.redirect_stderr(io.StringIO()) as stderr:
                with self.assertRaises(SystemExit) as result:
                    CAPTURE.main()
                self.assertEqual(result.exception.code, 2)
                self.assertIn("requires --unattended --capture-b6-partial", stderr.getvalue())

    def test_partial_fixture_expiry_preserves_same_payload_validator_and_range_support(self):
        fixture = importlib.machinery.SourceFileLoader(
            "link_intake_partial_fixture_test", str(CAPTURE.FIXTURE)).load_module()
        with fixture.PARTIAL_STATE["lock"]:
            fixture.PARTIAL_STATE["expired"] = False
            fixture.PARTIAL_STATE["ranges"].clear()
        server = ThreadingHTTPServer(("127.0.0.1", 0), fixture.Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            base = f"http://127.0.0.1:{server.server_port}"
            def get(path, headers=None):
                request = urllib.request.Request(base + path, headers=headers or {})
                return urllib.request.urlopen(request, timeout=5)

            with get("/partial/page") as page:
                self.assertEqual(page.headers.get_content_type(), "text/html")
                self.assertIn(b'<a href="/partial/new.zip">Catch replacement</a>', page.read())
            with fixture.PARTIAL_STATE["lock"]:
                self.assertEqual(fixture.PARTIAL_STATE["ranges"], [])

            request = urllib.request.Request(base + "/partial/old.zip", method="HEAD")
            with urllib.request.urlopen(request, timeout=5) as response:
                self.assertEqual(response.headers["ETag"], fixture.PARTIAL_ETAG)
                self.assertEqual(response.headers["Content-Length"], str(len(fixture.PARTIAL_PAYLOAD)))
            with fixture.PARTIAL_STATE["lock"]:
                fixture.PARTIAL_STATE["expired"] = True
            with self.assertRaises(urllib.error.HTTPError) as expired:
                get("/partial/old.zip", {"Range": "bytes=32768-49151"})
            self.assertEqual(expired.exception.code, 410)
            with get("/partial/new.zip", {"Range": "bytes=32768-49151"}) as response:
                self.assertEqual(response.status, 206)
                self.assertEqual(response.headers["ETag"], fixture.PARTIAL_ETAG)
                self.assertEqual(response.headers["Content-Range"],
                                 f"bytes 32768-49151/{len(fixture.PARTIAL_PAYLOAD)}")
                resumed = response.read()
            self.assertEqual(resumed, fixture.PARTIAL_PAYLOAD[32768:49152])
            self.assertEqual(
                __import__("hashlib").sha256(resumed).hexdigest(),
                __import__("hashlib").sha256(fixture.PARTIAL_PAYLOAD[32768:49152]).hexdigest())
            with fixture.PARTIAL_STATE["lock"]:
                self.assertIn(("/partial/new.zip", "bytes=32768-49151"),
                              fixture.PARTIAL_STATE["ranges"])
        finally:
            server.shutdown()
            server.server_close()
            worker.join()


if __name__ == "__main__":
    unittest.main()
