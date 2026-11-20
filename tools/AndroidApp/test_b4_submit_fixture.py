"""Host-only checks for the isolated B4 fixture data and opt-in boundary."""
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).with_name("capture-b4-submit.py")
SPEC = importlib.util.spec_from_file_location("b4_submit", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class B4FixtureTests(unittest.TestCase):
    def test_exact_selection_and_distinct_owned_payloads(self):
        items = MODULE.fixture_items("0123456789ab")
        self.assertEqual([item["selected"] for item in items], [True, True, False, False])
        self.assertEqual(len({item["name"] for item in items}), 4)
        self.assertEqual(len({item["sha256"] for item in items}), 4)
        self.assertTrue(all(item["name"].startswith("inbound-b4-0123456789ab-") for item in items))
        self.assertEqual(items, MODULE.fixture_items("0123456789ab"))

    def test_rejects_non_nonce_path_components(self):
        for nonce in ("../../x", "0123456789AB", "0123456789a", "a" * 13):
            with self.subTest(nonce=nonce), self.assertRaises(RuntimeError):
                MODULE.fixture_items(nonce)


if __name__ == "__main__": unittest.main()
