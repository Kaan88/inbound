"""Host-only safety checks for the C5/C6 paired-host fixture."""

import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("paired-host-parity-fixture.py")
SPEC = importlib.util.spec_from_file_location("paired_host_fixture", SCRIPT)
FIXTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FIXTURE)


class PairedHostFixtureTests(unittest.TestCase):
    def test_payload_is_deterministic_and_handler_only_serves_known_path(self):
        self.assertEqual(len(FIXTURE.PAYLOAD), 1024 * 1024)
        self.assertEqual(FIXTURE.PAYLOAD_SHA256, FIXTURE.hashlib.sha256(FIXTURE.PAYLOAD).hexdigest())
        self.assertEqual(FIXTURE.PAYLOAD_PATH, "/fixture/c5-c6.bin")

    def test_no_secrets_are_written_to_fixture_source_or_evidence_path(self):
        text = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("secrets.token_urlsafe", text)
        self.assertIn("stdout=subprocess.DEVNULL", text)
        self.assertIn('"password": password', text)  # transient runner handoff only
        self.assertNotIn("write_text(", text)
        self.assertIn("owned.cleanup()", text)

    def test_seed_requires_authenticated_correlated_acceptance(self):
        descriptor = {"base_url": "http://127.0.0.1:18100", "password": "fake-case",
                      "payload_url": "http://127.0.0.1:18101/fixture/c5-c6.bin"}
        requests = []

        def respond(request, timeout):
            requests.append(request)
            data = json.loads(request.data)
            if request.full_url.endswith("/api/pair"):
                self.assertEqual(data["role"], "remoteClient")
                return io.BytesIO(b'{"role":"remoteClient","token":"fake-token"}')
            self.assertEqual(request.get_header("Authorization"), "Bearer fake-token")
            self.assertEqual(data["payload"]["fileName"], "c5-c6.bin")
            self.assertNotIn("password", data["payload"])
            return io.BytesIO(json.dumps({"type": "job.addResult", "replyTo": data["id"],
                                           "payload": {"accepted": True}}).encode())

        with patch.object(FIXTURE, "urlopen", respond):
            FIXTURE.seed_download(descriptor)
        self.assertEqual(len(requests), 2)


if __name__ == "__main__":
    unittest.main()
