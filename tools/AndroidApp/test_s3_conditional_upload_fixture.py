"""Focused contract tests for the synthetic S3 conditional-write fixture."""

import importlib.util
from pathlib import Path
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen


PATH = Path(__file__).with_name("s3-conditional-upload-fixture.py")
SPEC = importlib.util.spec_from_file_location("s3_fixture", PATH)
FIXTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FIXTURE)


class ConditionalUploadTests(unittest.TestCase):
    def setUp(self):
        self.state = FIXTURE.S3Fixture()
        self.server = FIXTURE.serve(fixture=self.state)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.state.race_release.set()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def put(self, key, body):
        request = Request(
            f"{self.url}/bucket/{key}?x-id=PutObject",
            data=body,
            headers={
                "If-None-Match": "*",
                "Authorization": "AWS4-HMAC-SHA256 Credential=test/20261120/loopback/s3/aws4_request, SignedHeaders=host;if-none-match, Signature=" + "a" * 64,
            },
            method="PUT",
        )
        return urlopen(request, timeout=5).status

    def test_conditional_put_commits_exact_bytes_and_records_expected_wire_shape(self):
        self.assertEqual(self.put("new.bin", b"new payload"), 200)
        self.assertEqual(self.state.objects["new.bin"], b"new payload")
        record, = self.state.puts
        self.assertEqual(record["path"], "/bucket/new.bin?x-id=PutObject")
        self.assertEqual(record["if_none_match"], "*")
        self.assertTrue(record["authorization_shape"])
        self.assertEqual(record["bytes"], b"new payload")

    def test_race_winner_is_preserved_and_upload_is_not_retried(self):
        self.state.race_enabled = True
        result = []

        def upload():
            try:
                result.append(self.put("race.bin", b"loser"))
            except HTTPError as error:
                result.append(error.code)

        worker = threading.Thread(target=upload)
        worker.start()
        self.assertTrue(self.state.race_arrived.wait(5))
        self.state.competitor_write("race.bin", b"winner")
        self.state.race_release.set()
        worker.join(5)
        self.assertFalse(worker.is_alive())
        self.assertEqual(result, [412])
        self.assertEqual(self.state.objects["race.bin"], b"winner")
        self.assertEqual(len(self.state.puts), 1)
        self.assertEqual(self.state.puts[0]["bytes"], b"loser")

    def test_read_only_browsing_methods_serve_fixture_object(self):
        self.assertEqual(urlopen(f"{self.url}/bucket/pub/readme.txt").read(),
                         b"synthetic S3 fixture\n")
        listing = urlopen(f"{self.url}/bucket").read()
        self.assertIn(b"<Key>pub/readme.txt</Key>", listing)
        head = urlopen(Request(f"{self.url}/bucket/pub/readme.txt", method="HEAD"))
        self.assertEqual(head.headers["Content-Length"], "21")

    def test_fixture_refuses_non_loopback_bind(self):
        with self.assertRaises(ValueError):
            FIXTURE.serve(host="0.0.0.0")

    def test_bucket_and_folder_navigation_use_s3_prefix_semantics(self):
        self.assertIn(b"<Name>bucket</Name>", urlopen(f"{self.url}/").read())
        root = urlopen(f"{self.url}/bucket?delimiter=%2F").read()
        self.assertIn(b"<CommonPrefixes><Prefix>pub/</Prefix>", root)
        self.assertNotIn(b"readme.txt", root)
        folder = urlopen(f"{self.url}/bucket?delimiter=%2F&prefix=pub%2F").read()
        self.assertIn(b"<Key>pub/readme.txt</Key>", folder)
        self.assertNotIn(b"<CommonPrefixes>", folder)


if __name__ == "__main__":
    unittest.main()
