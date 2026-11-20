"""Host-only tests for the C3/C4 nested WebDAV fixture and Pillow pairing."""

from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.client
import importlib.util
from pathlib import Path
import tempfile
import threading
import unittest
import xml.etree.ElementTree as ET
from urllib.parse import quote


HERE = Path(__file__).parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


fixture = load("remote_files_parity_fixture", HERE / "remote-files-parity-fixture.py")
capture_tool = load("remote_files_parity_capture", HERE / "capture-remote-files-parity.py")
smoke_tool = load("remote_files_smoke_selectors", HERE / "smoke-remote-files-ui.py")


class ParityFixtureTests(unittest.TestCase):
    folder = "/media/recordings/2026"

    def setUp(self):
        self.server = fixture.FixtureServer(("127.0.0.1", 0))
        self.thread = threading.Thread(target=lambda: self.server.serve_forever(poll_interval=0.01), daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def request(self, method, path, headers=None, body=None):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def stage(self, number, body):
        path = f"{self.folder}/.inbound-upload-{number:032x}"
        self.assertEqual(self.request("PUT", path, {"If-None-Match": "*"}, body)[0], 201)
        return path

    def move_headers(self, path):
        host, port = self.server.server_address
        return {"Destination": f"http://{host}:{port}{path}", "Overwrite": "F"}

    def test_depth_one_lists_nested_tree_and_two_selected_files(self):
        status, _, body = self.request("PROPFIND", "/media/recordings/2026/")
        self.assertEqual(status, 207)
        root = ET.fromstring(body)
        hrefs = [item.findtext("{DAV:}href") for item in root.findall("{DAV:}response")]
        self.assertIn("/media/recordings/2026/masters", hrefs)
        self.assertIn("/media/recordings/2026/session-0412.flac", hrefs)
        self.assertIn("/media/recordings/2026/session-0419.flac", hrefs)
        self.assertEqual(len(hrefs), 8)

    def test_c4_folder_counts_and_chronological_listing_metadata(self):
        for folder, count in (("masters", 12), ("stems", 48)):
            with self.subTest(folder=folder):
                path = f"/media/recordings/2026/{folder}/"
                status, _, body = self.request("PROPFIND", path, {"Depth": "1"})
                self.assertEqual(status, 207)
                responses = ET.fromstring(body).findall("{DAV:}response")
                hrefs = [entry.findtext("{DAV:}href") for entry in responses]
                self.assertEqual(len(hrefs) - 1, count)
                self.assertEqual(len(hrefs), len(set(hrefs)))
                self.assertTrue(all(href.startswith(path) for href in hrefs[1:]))
                self.assertTrue(all(not href[len(path):].rstrip("/").count("/") for href in hrefs[1:]))

        status, _, body = self.request("PROPFIND", "/media/recordings/2026/")
        self.assertEqual(status, 207)
        entries = ET.fromstring(body).findall("{DAV:}response")
        dates = {}
        for entry in entries:
            name = entry.findtext("{DAV:}href").rsplit("/", 1)[-1]
            modified = entry.findtext(".//{DAV:}getlastmodified")
            if modified:
                dates[name] = modified
        self.assertLess(dates["session-0503.flac"], dates["notes.pdf"])
        self.assertEqual(
            [dates[name][:16] for name in ("session-0412.flac", "session-0419.flac",
                                           "session-0503.flac", "notes.pdf", "artwork.zip")],
            ["Sun, 12 Apr 2026", "Sun, 19 Apr 2026", "Sun, 03 May 2026",
             "Sun, 03 May 2026", "Sat, 09 May 2026"])

    def test_two_download_payloads_match_recorded_hashes(self):
        for name in ("session-0412.flac", "session-0419.flac"):
            with self.subTest(name=name):
                status, headers, body = self.request("GET", f"/media/recordings/2026/{name}")
                self.assertEqual(status, 200)
                self.assertEqual(int(headers["Content-Length"]), len(body))
                self.assertEqual(hashlib.sha256(body).hexdigest(), fixture.HASHES[name])
        self.assertEqual(self.server.parity_gets, {"session-0412.flac", "session-0419.flac"})

    def test_depth_zero_describes_a_file_without_returning_siblings(self):
        name = "session-0412.flac"
        path = f"/media/recordings/2026/{name}"
        status, _, body = self.request("PROPFIND", path, {"Depth": "0"})
        self.assertEqual(status, 207)
        (entry,) = ET.fromstring(body).findall("{DAV:}response")
        self.assertEqual(entry.findtext("{DAV:}href"), path)
        self.assertEqual(int(entry.findtext(".//{DAV:}getcontentlength")), len(fixture.FILES[name]))
        self.assertIsNone(entry.find(".//{DAV:}collection"))

    def test_depth_zero_describes_a_folder_without_returning_children(self):
        path = "/media/recordings/2026/masters/"
        status, _, body = self.request("PROPFIND", path, {"Depth": "0"})
        self.assertEqual(status, 207)
        (entry,) = ET.fromstring(body).findall("{DAV:}response")
        self.assertEqual(entry.findtext("{DAV:}href"), path)
        self.assertIsNotNone(entry.find(".//{DAV:}collection"))

    def test_delete_changes_only_the_disposable_notes_file(self):
        folder = "/media/recordings/2026/"
        try:
            self.assertEqual(self.request("DELETE", folder + "session-0412.flac")[0], 404)
            self.assertEqual(self.request("DELETE", folder + "notes.pdf")[0], 204)
            self.assertEqual(self.request("HEAD", folder + "notes.pdf")[0], 404)
            self.assertEqual(self.request("PROPFIND", folder + "notes.pdf", {"Depth": "0"})[0], 404)
            status, _, body = self.request("PROPFIND", folder)
            self.assertEqual(status, 207)
            self.assertNotIn(b"notes.pdf", body)
            self.assertIn(b"session-0412.flac", body)
        finally:
            self.server.parity_notes_deleted = False

    def test_staging_is_private_and_move_publishes_exact_bytes(self):
        payload = b"upload\x00payload\xff"
        stage = self.stage(1, payload)
        target = f"{self.folder}/{quote('take #1 & mix.wav')}"
        self.assertEqual(self.server.parity_uploads, {})
        self.assertEqual(self.request("PROPFIND", stage, {"Depth": "0"})[0], 404)
        self.assertEqual(self.request("GET", stage)[0], 404)
        self.assertNotIn(b".inbound-upload-", self.request("PROPFIND", self.folder)[2])
        self.assertEqual(self.request("MOVE", stage, self.move_headers(target))[0], 201)
        self.assertEqual(self.server.parity_uploads, {f"{self.folder[1:]}/take #1 & mix.wav": payload})
        self.assertEqual(self.request("DELETE", stage)[0], 404)
        self.assertEqual(self.request("DELETE", target)[0], 404)
        self.assertEqual(self.request("GET", target)[2], payload)
        self.assertEqual(self.request("HEAD", target)[1]["Content-Length"], str(len(payload)))
        for path in (target, self.folder):
            status, _, body = self.request("PROPFIND", path)
            self.assertEqual(status, 207)
            self.assertIn(target, [entry.findtext("{DAV:}href")
                                  for entry in ET.fromstring(body).findall("{DAV:}response")])
        with fixture.FixtureServer(("127.0.0.1", 0)) as other:
            self.assertEqual(other.parity_uploads, {})

    def test_move_conflict_never_replaces_existing_fixture_entry(self):
        stage = self.stage(1, b"would replace")
        for name in ("artwork.zip", "session-0412.flac", "masters"):
            with self.subTest(name=name):
                self.assertEqual(self.request("MOVE", stage, self.move_headers(f"{self.folder}/{name}"))[0], 412)
        self.assertEqual(self.server.parity_uploads, {})
        self.assertEqual(self.request("GET", f"{self.folder}/artwork.zip")[2],
                         fixture.FILES["artwork.zip"])
        self.assertEqual(self.request("GET", f"{self.folder}/session-0412.flac")[2],
                         fixture.FILES["session-0412.flac"])
        self.assertEqual(self.request("DELETE", stage)[0], 204)
        self.assertEqual(self.request("MOVE", stage, self.move_headers(f"{self.folder}/new.txt"))[0], 404)

    def test_competing_moves_have_one_winner_and_leave_loser_staged(self):
        payloads = (b"first", b"second")
        stages = [self.stage(index, payload) for index, payload in enumerate(payloads)]
        target = f"{self.folder}/competing.txt"
        move_headers = self.move_headers(target)
        with ThreadPoolExecutor(max_workers=2) as pool:
            statuses = tuple(pool.map(lambda stage: self.request("MOVE", stage, move_headers)[0], stages))
        self.assertCountEqual(statuses, (201, 412))
        self.assertEqual(self.server.parity_uploads, {target[1:]: payloads[statuses.index(201)]})
        self.assertEqual(self.request("DELETE", stages[statuses.index(412)])[0], 204)
        self.assertEqual(self.server.parity_upload_staging, {})

    def test_only_unique_valid_staging_names_accept_put(self):
        for name in ("upload.wav", ".inbound-upload-short", ".inbound-upload-" + "g" * 32):
            with self.subTest(name=name):
                self.assertEqual(self.request("PUT", f"{self.folder}/{name}", body=b"no")[0], 404)
        stage = self.stage(1, b"original")
        other = self.stage(2, b"other")
        self.assertEqual(self.request("PUT", stage, body=b"replacement")[0], 412)
        self.assertEqual(self.request("DELETE", other)[0], 204)
        target = f"{self.folder}/inbound-upload-proof.bin"
        self.assertEqual(self.request("MOVE", stage, self.move_headers(target))[0], 201)
        self.assertEqual(self.server.parity_uploads[target[1:]], b"original")

    def test_empty_upload_on_localhost_origin(self):
        origin = f"localhost:{self.server.server_port}"
        stage = f"{self.folder}/.inbound-upload-{'A' * 32}"
        target = f"{self.folder}/empty.txt"
        self.assertEqual(self.request("PUT", stage, {"Host": origin}, b"")[0], 201)
        self.assertEqual(self.request("MOVE", stage, {
            "Host": origin, "Overwrite": "F", "Destination": f"http://{origin}{target}",
        })[0], 201)
        self.assertEqual(self.server.parity_uploads[target[1:]], b"")
        self.assertEqual(self.request("GET", target)[0], 200)

    def test_move_rejects_wrong_origin_folder_path_or_overwrite(self):
        stage = self.stage(1, b"kept")
        headers = self.move_headers(f"{self.folder}/new.txt")
        invalid_headers = (
            {**headers, "Overwrite": "T"},
            {"Destination": headers["Destination"]},
            {"Overwrite": "F"},
            {**headers, "Destination": headers["Destination"].replace("http:", "https:")},
            {**headers, "Destination": headers["Destination"].replace("127.0.0.1", "example.invalid")},
            {**headers, "Destination": headers["Destination"].replace("127.0.0.1", "localhost")},
            {**headers, "Destination": f"http://127.0.0.1:1{self.folder}/new.txt"},
            self.move_headers(f"{self.folder}/masters/new.txt"),
            self.move_headers(f"{self.folder}/%2e%2e/new.txt"),
            self.move_headers(f"{self.folder}/some%2fnew.txt"),
            self.move_headers(stage),
        )
        for invalid in invalid_headers:
            with self.subTest(headers=invalid):
                self.assertEqual(self.request("MOVE", stage, invalid)[0], 400)
        self.assertEqual(self.server.parity_uploads, {})
        self.assertEqual(self.request("DELETE", stage)[0], 204)

    def test_pillow_pair_preserves_sources_and_writes_paired_capture(self):
        from PIL import Image
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            reference, capture, output = directory / "ref.png", directory / "capture.png", directory / "pair.png"
            Image.new("RGB", (100, 200), "red").save(reference)
            Image.new("RGB", (200, 100), "blue").save(capture)
            ref_bytes, capture_bytes = reference.read_bytes(), capture.read_bytes()
            size = capture_tool.make_pair(reference, capture, output, "C4")
            self.assertEqual(size, (3000, 1242))
            self.assertTrue(output.is_file())
            self.assertEqual(reference.read_bytes(), ref_bytes)
            self.assertEqual(capture.read_bytes(), capture_bytes)

    def test_s3_serve_command_matches_site_kind_integration_shape(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "served"
            command = capture_tool.Capture.s3_serve_command(
                r"C:\Program Files\rclone\rclone.exe", root, 47651,
                "INBOUNDTESTKEY", "inbound-test-secret-8813")
        self.assertEqual(command, [
            r"C:\Program Files\rclone\rclone.exe", "serve", "s3", str(root),
            "--addr", "127.0.0.1:47651", "--auth-key",
            "INBOUNDTESTKEY,inbound-test-secret-8813", "--cache-dir",
            str(root.parent / "server-cache"), "--config", "", "--dir-cache-time", "0s",
        ])

    def test_s3_walkthrough_targets_requested_ui_and_fixture(self):
        source = (HERE / "capture-remote-files-parity.py").read_text(encoding="utf-8")
        for required in (
            "Location type: WebDAV", "Other S3-compatible service",
            "S3 endpoint (optional for Amazon S3)", "eu-central",
            "Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.",
            "Backups. S3 · eu-central", "pub, folder.", "readme.txt",
            'parser.add_argument("--rclone"',
        ):
            with self.subTest(required=required):
                self.assertIn(required, source)

    def test_smoke_selectors_accept_dynamic_file_detail_and_transfer_summary(self):
        tree = ET.fromstring(
            '<hierarchy>'
            '<node content-desc="Select inbound-remote-files.txt, 3 B · 19 Nov"/>'
            '<node content-desc="No active transfers. Open transfers."/>'
            '</hierarchy>')
        smoke = smoke_tool.Smoke.__new__(smoke_tool.Smoke)
        smoke.tree = lambda: tree
        file_card, _ = smoke.wait_content_description_prefix(
            "Select inbound-remote-files.txt,")
        transfer_summary, _ = smoke.wait_content_description_suffix(". Open transfers.")
        self.assertEqual(file_card.get("content-desc"), "Select inbound-remote-files.txt, 3 B · 19 Nov")
        self.assertEqual(transfer_summary.get("content-desc"), "No active transfers. Open transfers.")


if __name__ == "__main__":
    unittest.main()
