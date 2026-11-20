"""Host-only checks for core-screen references and side-by-side pairing."""

import importlib.util
from pathlib import Path
import tempfile
import unittest


HERE = Path(__file__).parent
SPEC = importlib.util.spec_from_file_location(
    "core_screen_parity_capture", HERE / "capture-core-parity.py")
CAPTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAPTURE)


class CoreScreenParityTests(unittest.TestCase):
    def test_all_requested_screens_have_approved_references(self):
        keys = [entry[0] for entry in CAPTURE.CAPTURES]
        self.assertEqual(keys, ["a0", "a1", "a4", "a5", "c7", "c8"])
        for _, reference, _ in CAPTURE.CAPTURES:
            with self.subTest(reference=reference):
                self.assertTrue((CAPTURE.MOCKUPS / reference).is_file())

    def test_pairing_keeps_reference_and_capture_bytes_unchanged(self):
        from PIL import Image
        helper_spec = importlib.util.spec_from_file_location(
            "core_screen_parity_pair_helper",
            HERE / "capture-remote-files-parity.py")
        helper = importlib.util.module_from_spec(helper_spec)
        helper_spec.loader.exec_module(helper)
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            reference, capture, pair = (
                folder / "reference.png", folder / "capture.png", folder / "pair.png")
            Image.new("RGB", (1236, 2676), "black").save(reference)
            Image.new("RGB", (1080, 2400), "white").save(capture)
            reference_bytes, capture_bytes = reference.read_bytes(), capture.read_bytes()
            size = helper.make_pair(reference, capture, pair, "A0")
            self.assertTrue(pair.is_file())
            self.assertEqual(size[1], 1242)
            self.assertEqual(reference.read_bytes(), reference_bytes)
            self.assertEqual(capture.read_bytes(), capture_bytes)

    def test_unattended_mode_is_opt_in_and_supported_sequence_excludes_populated_states(self):
        import argparse
        parser = argparse.ArgumentParser()
        parser.add_argument("--unattended", action="store_true")
        self.assertFalse(parser.parse_args([]).unattended)
        self.assertTrue(parser.parse_args(["--unattended"]).unattended)

        capture = CAPTURE.Capture.__new__(CAPTURE.Capture)
        capture.args = argparse.Namespace(unattended=True)
        capture.log = lambda message: messages.append(message)
        self.assertEqual(set(CAPTURE.SELECTORS), {
            "first_run_start", "add", "add_link", "new_close", "settings"})
        self.assertTrue(all(selector.startswith(CAPTURE.APP_ID)
                            for selector in CAPTURE.SELECTORS.values()))
        messages = []
        self.assertEqual(CAPTURE.CAPTURES[-1][0], "c8")
        self.assertIn("rcloneprobe:id/fr_start", CAPTURE.SELECTORS["first_run_start"])


if __name__ == "__main__":
    unittest.main()
