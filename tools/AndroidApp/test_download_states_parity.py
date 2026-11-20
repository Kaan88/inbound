"""Host-only inventory and fixture checks for A2/A3/A6/A7 capture tooling."""

import hashlib
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock


HERE = Path(__file__).parent
SPEC = importlib.util.spec_from_file_location(
    "download_states_parity_capture", HERE / "capture-download-states-parity.py")
CAPTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAPTURE)


class DownloadStatesParityTests(unittest.TestCase):
    def test_every_checkpoint_has_the_approved_reference(self):
        expected = [
            ("a2-progress", "a2-downloads-later.png"),
            ("a2-queued", "a2-downloads-later.png"),
            ("a6-active-details", "a6-details.png"),
            ("a7-notification", "a7-background.png"),
            ("a3-finished-snackbar", "a3-downloads-finished.png"),
            ("a6-finished-details", "a6-details.png"),
        ]
        self.assertEqual([(key, reference) for key, reference, _ in CAPTURE.CAPTURES], expected)
        for _, reference, _ in CAPTURE.CAPTURES:
            with self.subTest(reference=reference):
                self.assertTrue((CAPTURE.MOCKUPS / reference).is_file())

    def test_timed_fixture_is_deterministic_and_sized_for_capture(self):
        payload = CAPTURE.fixture_bytes()
        self.assertEqual(len(payload), 16 * 1024 * 1024)
        self.assertEqual(hashlib.sha256(payload).hexdigest(),
                         hashlib.sha256(CAPTURE.fixture_bytes()).hexdigest())
        self.assertEqual(CAPTURE.CHUNK_SIZE, 16 * 1024)
        self.assertGreater(CAPTURE.CHUNK_DELAY_SECONDS, 0)

    def test_runner_is_scoped_to_the_dedicated_isolated_emulator(self):
        source = (HERE / "capture-download-states-parity.py").read_text(encoding="utf-8")
        self.assertIn('AVD = "inbound33"', source)
        self.assertIn('API = "33"', source)
        self.assertIn('PACKAGE = "app.inbound.android.rcloneprobe"', source)
        self.assertNotIn('"app.inbound.android"', source)
        self.assertIn('"--remove", f"tcp:{self.port}"', source)

    def test_unattended_navigation_is_explicit_and_limited_to_a7(self):
        source = (HERE / "capture-download-states-parity.py").read_text(encoding="utf-8")
        self.assertIn('"--unattended-a7-navigation"', source)
        self.assertIn('key == "a7-notification" and self.args.unattended_a7_navigation', source)
        self.assertIn('"KEYCODE_HOME"', source)
        self.assertIn('"expand-notifications"', source)

    def test_mixed_a2_is_opt_in_and_has_four_real_fixture_keys(self):
        source = (HERE / "capture-download-states-parity.py").read_text(encoding="utf-8")
        self.assertIn('"--mixed-a2"', source)
        for key in ("active", "queued", "paused", "finished"):
            self.assertIn(f'"{key}": f"inbound-a2-{key}-', source)
        self.assertIn("resume the paused job in the app", source)
        self.assertIn('"a2-mixed-graph"', source)
        self.assertIn("self.verify_files(tuple(self.names))", source)
        self.assertIn('"b5-select", "b5-select.png"', source)
        self.assertIn("--capture-b5", source)

    def test_standalone_b5_is_separate_and_fails_closed_on_unidentified_rows(self):
        source = (HERE / "capture-download-states-parity.py").read_text(encoding="utf-8")
        self.assertIn('"--unattended-b5"', source)
        for key in ("selected-active", "selected-paused", "selected-queued",
                    "other-active", "other-finished"):
            self.assertIn(key, source)
        self.assertIn("all(len(matching(nodes, name)) == 1 for name in expected)", source)
        self.assertIn("selected(nodes, 3)", source)
        self.assertIn("Could not uniquely identify the enabled bulk overflow control", source)
        self.assertIn('"b5-unattended", "b5-select.png"', source)
        self.assertIn('"selected-active": "downloading"', source)
        self.assertIn('"selected-paused": "paused"', source)
        self.assertIn('"selected-queued": "queued"', source)
        self.assertIn('"other-active": "downloading"', source)
        self.assertIn('"other-finished": "finished"', source)
        self.assertIn('len(menu_entry(ns, "Move to queue")) == 1', source)
        self.assertIn("verify_b5_finished_payload()", source)
        self.assertIn("B5 cleanup: finished fixture payload hash mismatch", source)
        self.assertIn("cleanup_b5_finished_payload()", source)
        self.assertIn('if result.startswith(f"MISSING:{name}")', source)
        self.assertEqual(CAPTURE.B5_KEYS, (
            "selected-active", "selected-paused", "selected-queued",
            "other-active", "other-finished"))
        self.assertIn('keys = B5_KEYS', source)
        self.assertIn('self.wait_for_completion("other-finished")', source)
        self.assertIn("if args.unattended_b5 and (args.mixed_a2", source)
        self.assertIn('if getattr(args, "unattended_b5", False)', source)
        self.assertIn('again[0].get("enabled") == "false"', source)
        self.assertIn('if capture.args.unattended_b5 and key in capture.b5_release:', source)

    def test_b5_payloads_are_distinct_and_absent_cleanup_is_harmless(self):
        self.assertNotEqual(hashlib.sha256(CAPTURE.fixture_bytes("other-finished")).digest(),
                            hashlib.sha256(CAPTURE.fixture_bytes("selected-active")).digest())
        capture = CAPTURE.Capture.__new__(CAPTURE.Capture)
        capture.names = {"other-finished": "finished.bin"}
        capture.shell = Mock(return_value="MISSING:finished.bin")
        capture.log = Mock()
        capture.verify_b5_finished_payload(allow_missing=True)
        capture.shell.assert_called_once()
        capture.log.assert_called_once()

    def test_b5_hash_mismatch_refuses_deletion(self):
        capture = CAPTURE.Capture.__new__(CAPTURE.Capture)
        capture.names = {"other-finished": "finished.bin"}
        capture.shell = Mock(return_value="wronghash  /sdcard/Download/finished.bin")
        with self.assertRaisesRegex(RuntimeError, "finished fixture payload hash mismatch"):
            capture.verify_b5_finished_payload()
        capture.shell.assert_called_once()


if __name__ == "__main__":
    unittest.main()
