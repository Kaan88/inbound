"""Host-only pulse checks; no network or device access."""
import hashlib
import importlib.util
import io
from pathlib import Path
from threading import Lock
from types import SimpleNamespace
from unittest import TestCase, main, mock

spec = importlib.util.spec_from_file_location("c3_fixture", Path(__file__).with_name("remote-files-parity-fixture.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)

class PulseTests(TestCase):
    def handler(self, enabled, name="session-0412.flac"):
        handler = fixture.Handler.__new__(fixture.Handler)
        handler.path = "/" + fixture.RECORDINGS + "/" + name
        handler.server = SimpleNamespace(parity_lock=Lock(), parity_uploads={}, parity_notes_deleted=False,
                                         parity_gets=set(), parity_pulse=enabled, parity_pulsed_bytes=0)
        handler.wfile = io.BytesIO()
        handler.send_response = lambda *args: None
        handler.send_header = lambda *args: None
        handler.end_headers = lambda: None
        return handler

    def test_opt_in_pulse_preserves_payload_and_counts_actual_writes(self):
        h = self.handler(True)
        with mock.patch.object(fixture.time, "sleep") as waits:
            h._serve_file(True)
        self.assertEqual(hashlib.sha256(h.wfile.getvalue()).hexdigest(), fixture.HASHES["session-0412.flac"])
        self.assertEqual(h.server.parity_pulsed_bytes, len(fixture.FILES["session-0412.flac"]))
        self.assertEqual(waits.call_count, len(fixture.FILES["session-0412.flac"]) // 16384 - 1)
        self.assertEqual(h.server.parity_gets, {"session-0412.flac"})

    def test_normal_and_head_do_not_pulse(self):
        h = self.handler(False)
        with mock.patch.object(fixture.time, "sleep") as waits:
            h._serve_file(True)
        waits.assert_not_called()
        self.assertEqual(h.server.parity_pulsed_bytes, 0)
        head = self.handler(True)
        with mock.patch.object(fixture.time, "sleep") as waits:
            head._serve_file(False)
        waits.assert_not_called()
        self.assertEqual(head.server.parity_pulsed_bytes, 0)
        self.assertEqual(head.wfile.getvalue(), b"")

if __name__ == "__main__": main()
