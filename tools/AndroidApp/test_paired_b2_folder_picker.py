"""Host-only scope and secret-handling checks for paired B2 smoke."""

import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).with_name("smoke-paired-b2-folder-picker.py")
SPEC = importlib.util.spec_from_file_location("paired_b2_folder_picker", SCRIPT)
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class PairedB2FolderPickerTests(unittest.TestCase):
    def test_isolated_device_and_exact_fixture_scope(self):
        self.assertEqual(RUNNER.AVD, "inbound33")
        self.assertEqual(RUNNER.API, "33")
        self.assertEqual(RUNNER.PACKAGE, "app.inbound.android.rcloneprobe")
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("DownloadManager.Server.dll", source)
        self.assertIn("running_fixture", source)
        self.assertIn('"B2-decoy-child", CHILD', source)
        self.assertIn("No real credentials or production host", source)

    def test_selected_destination_hash_gates_cleanup_and_no_pair_form_evidence(self):
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("caught_destination", source)
        self.assertIn("caught_change_destination", source)
        self.assertIn("sha256", source)
        self.assertLess(source.index("Cleanup refused"), source.index("target.unlink()"))
        self.assertIn("Never persist hierarchy/logcat while pairing fields may contain data", source)
        self.assertNotIn("failure-ui.xml", source)
        self.assertNotIn('self.adb("logcat"', source)

    def test_fixture_children_are_created_only_under_owned_host_root(self):
        import inspect
        source = inspect.getsource(RUNNER.PairedB2.pair)
        self.assertIn('Path(fixture["download_root"]) / name', source)
        self.assertIn("self.child_paths.append(path)", source)

    def test_archive_tap_uses_only_unique_clickable_duplicate_label(self):
        import xml.etree.ElementTree as ET

        tree = ET.fromstring(
            '<hierarchy><node text="Archive" clickable="false"/>'
            '<node text="Archive" clickable="true"/></hierarchy>')
        tapped = []

        class Smoke:
            @staticmethod
            def matches(candidate, label):
                return [node for node in candidate.iter("node")
                        if label in (node.get("text"), node.get("content-desc"))]

            @staticmethod
            def tap(node):
                tapped.append(node)

        run = RUNNER.PairedB2.__new__(RUNNER.PairedB2)
        run.smoke = Smoke()
        run.tree = lambda: tree
        run.tap_clickable_label("Archive")

        self.assertEqual(len(tapped), 1)
        self.assertEqual(tapped[0].get("clickable"), "true")


if __name__ == "__main__":
    unittest.main()
