"""Host-only checks for C1/C2/C5/C6 references and comparison output."""

import importlib.util
from pathlib import Path
import tempfile
import unittest


HERE = Path(__file__).parent
SPEC = importlib.util.spec_from_file_location(
    "queue_host_screen_parity_capture", HERE / "capture-queue-host-parity.py")
CAPTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAPTURE)


class QueueHostScreenParityTests(unittest.TestCase):
    def test_editor_has_stable_ids_for_existing_c2_controls(self):
        ids = (CAPTURE.ROOT / "src/DownloadManager.Android/Resources/values/queue_editor_ids.xml").read_text()
        screen = (CAPTURE.ROOT / "src/DownloadManager.Android/Screens/QueueEditorScreen.cs").read_text()
        for name in ("queue_editor_unmetered", "queue_editor_charging",
                     "queue_editor_day_mon", "queue_editor_day_tue",
                     "queue_editor_day_wed", "queue_editor_day_thu",
                     "queue_editor_day_fri", "queue_editor_day_sat",
                     "queue_editor_day_sun"):
            with self.subTest(name=name):
                self.assertIn(f'name="{name}"', ids)
                self.assertIn(f"Resource.Id.{name}", screen)

    def test_requested_states_map_to_approved_references(self):
        expected = {
            "c1-empty": "c1-queues.png",
            "c1-populated-stopped": "c1-queues.png",
            "c1-populated-running": "c1-queues.png",
            "c1-queue-actions": "c1-queues.png",
            "c2-no-schedule": "c2-schedule.png",
            "c2-configured": "c2-schedule.png",
            "c2-editor-footer": "c2-schedule.png",
            "c6-unpaired": None,
            "c6-pairing-form": None,
            "c6-paired": "c6-remote-host.png",
            "c5-phone-selected": "c5-switch-host.png",
            "c5-paired-selected": "c5-switch-host.png",
        }
        self.assertEqual([key for key, _, _ in CAPTURE.CAPTURES], list(expected))
        self.assertEqual({key: ref for key, ref, _ in CAPTURE.CAPTURES}, expected)
        for reference in set(expected.values()):
            if reference is None:
                continue
            with self.subTest(reference=reference):
                self.assertTrue((CAPTURE.MOCKUPS / reference).is_file())

    def test_pairing_keeps_reference_and_capture_bytes_unchanged(self):
        from PIL import Image
        helper = CAPTURE.load_module("queue_host_parity_pair_test", CAPTURE.PAIR_PATH)
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            reference, capture, pair = (
                folder / "reference.png", folder / "capture.png", folder / "pair.png")
            Image.new("RGB", (1236, 2676), "black").save(reference)
            Image.new("RGB", (1080, 2400), "white").save(capture)
            original_bytes, capture_bytes = reference.read_bytes(), capture.read_bytes()
            size = helper.make_pair(reference, capture, pair, "C1")
            self.assertTrue(pair.is_file())
            self.assertEqual(size[1], 1242)
            self.assertEqual(reference.read_bytes(), original_bytes)
            self.assertEqual(capture.read_bytes(), capture_bytes)

    def test_unattended_navigation_is_opt_in_and_creates_only_named_queue(self):
        import inspect
        source = inspect.getsource(CAPTURE.Capture.navigate_c1_c2)
        self.assertIn('"nav_queues"', source)
        self.assertIn('"c1-empty"', source)
        self.assertIn('"queue_new"', source)
        self.assertIn('"queue_editor_save"', source)
        self.assertIn("disposable queue created above", source)
        main_source = inspect.getsource(CAPTURE.main)
        self.assertIn("--unattended-c1-c2", main_source)
        self.assertIn('"c5-phone-selected"', main_source)
        run_source = inspect.getsource(CAPTURE.Capture.run)
        self.assertIn('"c1-populated-stopped"', run_source)

    def test_configured_c2_is_a_separate_night_only_no_workload_lane(self):
        import inspect
        parser_source = inspect.getsource(CAPTURE.main)
        self.assertIn("--unattended-configured-c2", parser_source)
        self.assertIn('args.queue_name = "Night"', parser_source)
        run_source = inspect.getsource(CAPTURE.Capture.run)
        self.assertIn('"c2-configured", "c2-editor-footer"', run_source)
        configure = inspect.getsource(CAPTURE.Capture.configure_c2)
        for token in ("01:00", "07:00", "queue_editor_day_sat",
                      "queue_editor_day_sun", "queue_editor_charging",
                      "queue_editor_unmetered", "checked"):
            with self.subTest(token=token):
                self.assertIn(token, configure)
        self.assertIn("0 files", configure)
        self.assertIn("Weekdays schedule is enabled after save/reopen", configure)

    def test_native_picker_fails_closed_on_unexpected_tree(self):
        import inspect
        picker = inspect.getsource(CAPTURE.Capture._time_picker)
        for selector in ("timePicker", "hours", "minutes", "radial_picker",
                         "am_label", "pm_label", "button1"):
            with self.subTest(selector=selector):
                self.assertIn(selector, picker)
        self.assertIn('get("clickable")', picker)
        self.assertIn("bounds", picker)


if __name__ == "__main__":
    unittest.main()
