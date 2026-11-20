"""Host-only safety checks for the C5/C6 paired-host fixture."""

import importlib.util
import io
import json
from pathlib import Path
from tempfile import TemporaryDirectory
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
        after_password_generation = text.partition('    password = "Parity-"')[2]
        self.assertTrue(after_password_generation)
        self.assertNotIn("write_text(", after_password_generation)
        self.assertIn("owned.cleanup()", text)

    def test_only_pinned_rclone_settings_are_written_in_fresh_fixture_data(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            dll = root / "DownloadManager.Server.dll"
            binary = root / "rclone.exe"
            dll.touch()
            binary.touch()
            with patch.object(FIXTURE.subprocess, "Popen") as start_server, patch.object(FIXTURE, "urlopen") as health:
                start_server.return_value.poll.return_value = None
                health.return_value.__enter__.return_value.status = 200

                with FIXTURE.running_fixture(dll, temp_root=root, rclone=binary) as descriptor:
                    data = Path(descriptor["data_directory"])
                    config_path = data / "sites" / "config.json"
                    regular_files = [path for path in data.parent.rglob("*") if path.is_file()]
                    self.assertEqual(regular_files, [config_path])
                    config = json.loads(config_path.read_text(encoding="utf-8"))
                    self.assertEqual(config, {
                        "enabled": False,
                        "ownRclonePath": str(binary.resolve()),
                        "autoUpdateRclone": False,
                    })
                    self.assertNotIn(descriptor["password"], config_path.read_text(encoding="utf-8"))
                    self.assertEqual(start_server.call_args.kwargs["env"]["DOWNLOADMANAGER_DATA"], str(data))

                self.assertFalse(data.parent.exists())
                start_server.return_value.terminate.assert_called_once()

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
