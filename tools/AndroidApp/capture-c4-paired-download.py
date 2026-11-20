"""Run a real paired-Host WebDAV download on isolated loopback fixtures."""
import argparse
from collections import Counter
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
import importlib.util
import ipaddress
import json
from pathlib import Path
import re
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import HTTPSHandler, ProxyHandler, Request, build_opener
from zipfile import ZipFile

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID
from websockets.sync.client import connect

HERE = Path(__file__).resolve().parent
HOST_NAME = "Parity C4 Host"
PACKAGE = "app.inbound.android.rcloneprobe"
RCLONE_ARCHIVE_HASH = "8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc"
RCLONE_EXE = "rclone-v1.76.0-windows-amd64/rclone.exe"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def extract_rclone(archive, temp):
    require(hashlib.sha256(archive.read_bytes()).hexdigest() == RCLONE_ARCHIVE_HASH,
            "The cached Windows rclone archive does not match the project pin")
    binary = temp / "rclone.exe"
    with ZipFile(archive) as bundle, bundle.open(RCLONE_EXE) as source, binary.open("xb") as target:
        shutil.copyfileobj(source, target)
    return binary


@contextmanager
def tls_relay(node, upstream_port, listen_port, temp):
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Inbound isolated C4 fixture")])
    now = datetime.now(timezone.utc)
    cert = (x509.CertificateBuilder()
            .subject_name(subject).issuer_name(subject).public_key(key.public_key())
            .serial_number(x509.random_serial_number())
            .not_valid_before(now - timedelta(minutes=5))
            .not_valid_after(now + timedelta(hours=1))
            .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address("127.0.0.1"))]), False)
            .add_extension(x509.BasicConstraints(ca=True, path_length=0), True)
            .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=False, key_encipherment=True,
                                         data_encipherment=False, key_agreement=False, key_cert_sign=True,
                                         crl_sign=True, encipher_only=False, decipher_only=False), True)
            .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), False)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), False)
            .sign(key, hashes.SHA256()))
    key_path = temp / "fixture-key.pem"
    cert_path = temp / "fixture-cert.pem"
    key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                           serialization.NoEncryption()))
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    tls = ssl.create_default_context(cafile=str(cert_path))
    opener = build_opener(ProxyHandler({}), HTTPSHandler(context=tls))
    origin = f"https://127.0.0.1:{listen_port}"
    child = subprocess.Popen([node, str(HERE / "pairing-tls-fixture" / "relay.js"),
                              "--key", str(key_path), "--cert", str(cert_path),
                              "--listen-host", "127.0.0.1", "--listen-port", str(listen_port),
                              "--upstream-port", str(upstream_port)],
                             stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            require(child.poll() is None, "The owned loopback TLS relay exited")
            try:
                with opener.open(origin + "/health", timeout=2) as response:
                    require(response.status == 200, "The owned TLS endpoint did not reach the fixture Server")
                break
            except (OSError, URLError):
                time.sleep(.2)
        else:
            raise RuntimeError("The owned TLS relay did not become healthy with its exact certificate")
        yield origin, cert.fingerprint(hashes.SHA256()).hex(), tls, opener
    finally:
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=5)


def host_capabilities(origin, password, tls, opener):
    request = Request(origin + "/api/pair", data=json.dumps({
        "role": "remoteClient", "password": password, "name": "Owned C4 capability check",
    }).encode("utf-8"), headers={"Content-Type": "application/json"})
    with opener.open(request, timeout=10) as response:
        paired = json.load(response)
    token = paired.get("token")
    require(paired.get("role") == "remoteClient" and token,
            "The fixture Host refused the paired capability check")
    ws_origin = "wss://" + urlsplit(origin).netloc
    with connect(ws_origin + "/api/ws", ssl=tls, proxy=None, open_timeout=10,
                 additional_headers={"Authorization": "Bearer " + token}) as socket:
        hello = json.loads(socket.recv(timeout=10))
    require(hello.get("type") == "server.hello", "Expected an actual fixture Host capability response")
    return (hello.get("payload") or {}).get("capabilities", [])


def pair_probe(smoke, origin, password, expected_fingerprint, *, host_name=HOST_NAME):
    chip, _ = smoke.wait_content_description(
        lambda desc: desc == "Downloads shown from This phone. Change host", "fresh local Host")
    smoke.tap(chip)
    smoke.tap(smoke.wait_node("Connect to another Inbound", resource="host_connect")[0])
    smoke.wait_node("Connect", resource="pair_btn")

    def enter(resource, value, *, secret=False):
        nodes = [n for n in smoke.tree().iter("node")
                 if n.get("resource-id") == PACKAGE + ":id/" + resource]
        require(len(nodes) == 1, "Expected one enabled probe pairing field")
        require(nodes[0].get("enabled") == "true", "Probe pairing field is disabled")
        smoke.tap(nodes[0])
        try:
            smoke.shell("input", "text", value)
        except Exception:
            if secret:
                raise RuntimeError("Could not enter the disposable fixture pairing value") from None
            raise
        smoke.shell("input", "keyevent", "4")

    require(re.fullmatch(r"[A-Za-z0-9-]+", password), "The generated pairing value has unexpected characters")
    enter("pair_address", origin)
    enter("pair_password", password, secret=True)
    button, _ = smoke.wait_node("Connect", resource="pair_btn")
    require(button.get("enabled") == "true", "HTTPS pairing did not enable Connect")
    smoke.tap(button)
    trust, tree = smoke.wait_node("Trust this certificate for this server", resource="pair_trust_cert", timeout=30)
    certificate = [n.get("text", "") for n in tree.iter("node")
                   if n.get("resource-id") == PACKAGE + ":id/pair_cert"]
    require(len(certificate) == 1, "The probe did not show one pairing certificate")
    line = re.search(r"(?im)^SHA-256:\s*([a-f0-9:\- ]+)\s*$", certificate[0])
    observed = re.sub(r"[:-]|\s", "", line.group(1)).lower() if line else ""
    require(observed == expected_fingerprint and trust.get("checked") == "false",
            "Pairing refused because the shown certificate is not the owned TLS fingerprint")
    smoke.tap(trust)
    button, _ = smoke.wait_node("Connect", resource="pair_btn")
    require(button.get("enabled") == "true", "Verified certificate acknowledgement did not enable Connect")
    smoke.tap(button)
    smoke.wait_node("Files are saved on " + host_name, timeout=40)
    smoke.log("PASS pairing: Android matched and pinned the exact disposable HTTPS certificate.")


def run(args):
    states = load("c4_states", "capture-download-states-parity.py")
    paired = load("c4_host", "paired-host-parity-fixture.py")
    dav = load("c4_webdav", "remote-files-parity-fixture.py")
    ui = load("c4_ui", "paired-remote-files-ui.py")
    args.active_only = True
    args.details_flow = args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)
    counts, listing_counts, delete_counts, lock = Counter(), Counter(), Counter(), threading.Lock()
    tracked_folder = dav.RECORDINGS + "/"

    class CountedHandler(dav.Handler):
        def do_PROPFIND(self):
            if self._path() == dav.RECORDINGS and self.headers.get("Depth", "infinity") != "0":
                with lock:
                    listing_counts["recordings"] += 1
            super().do_PROPFIND()

        def do_GET(self):
            with lock:
                delete_counts["all_gets"] += 1
            super().do_GET()

        def do_DELETE(self):
            with lock:
                delete_counts["notes" if self._path() == dav.NOTES else "other"] += 1
            super().do_DELETE()

        def _status(self, status):
            if self.command == "DELETE" and self._path() == dav.NOTES and status == 204:
                with lock:
                    delete_counts["accepted"] += 1
            super()._status(status)

        def _serve_file(self, send_body):
            path = self._path()
            if send_body and path.startswith(tracked_folder):
                name = path.removeprefix(tracked_folder)
                if name in dav.FILES:
                    with lock:
                        counts[name] += 1
            super()._serve_file(send_body)

    def get_count(name):
        with lock:
            return counts[name]

    def listing_count():
        with lock:
            return listing_counts["recordings"]

    def delete_state():
        with lock:
            notes_attempts, other_attempts, accepted = delete_counts["notes"], delete_counts["other"], delete_counts["accepted"]
            payload_gets = delete_counts["all_gets"]
        with server.parity_lock:
            deleted = server.parity_notes_deleted
        return {"notes_attempts": notes_attempts, "other_attempts": other_attempts, "notes_accepted": accepted,
                "notes_deleted": deleted, "payload_gets": payload_gets}

    def phone_count():
        return len(smoke.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines())

    def phone_target_absent():
        return smoke.shell(
            "if [ -e '/sdcard/Download/" + ui.FILE + "' ] || [ -L '/sdcard/Download/" +
            ui.FILE + "' ]; then echo present; fi").strip() != "present"

    require(phone_target_absent(), "Exact paired fixture filename already exists on the emulator")
    baseline = phone_count()
    with tempfile.TemporaryDirectory(prefix="inbound-c4-relay-") as scratch:
        temp = Path(scratch).resolve()
        rclone = extract_rclone(args.rclone_zip, temp)
        with paired.running_fixture(args.server_dll, name=HOST_NAME, rclone=rclone) as fixture:
            root = Path(fixture["download_root"]).resolve(strict=True)
            destination, decoy = root / "C4-selected", root / "C4-decoy"
            destination.mkdir()
            decoy.mkdir()
            server = dav.FixtureServer(("127.0.0.1", 0), CountedHandler)
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            try:
                worker.start()
                listener = paired.ephemeral_port()
                upstream = urlsplit(fixture["base_url"]).port
                with tls_relay(args.node, upstream, listener, temp) as (origin, fingerprint, tls, opener):
                    endpoint = f"tcp:{listener}"
                    cap.adb("reverse", "--no-rebind", endpoint, endpoint)
                    smoke.port = listener
                    smoke.reversed = True
                    site_request = None
                    paired_ready = False
                    try:
                        capabilities = host_capabilities(origin, fixture["password"], tls, opener)
                        require({"sites", "sitesSafeSingleDownload"}.issubset(capabilities),
                                "The disposable Server build lacks the paired safe-download capabilities")
                        cap.install_and_launch()
                        smoke.installed = cap.installed
                        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY)
                        smoke.started = True
                        smoke.tap(smoke.wait_node("Start downloading", resource="fr_start")[0])
                        smoke.wait_node("Search downloads")
                        pair_probe(smoke, origin, fixture["password"], fingerprint)
                        paired_ready = True
                        site_request = paired.fixture_session(fixture)
                        settings = site_request("sites.get", {})
                        config = settings.get("payload") or {}
                        require(settings.get("type") == "sites.status" and config.get("enabled") is False
                                and config.get("ownRclonePath") == str(rclone)
                                and config.get("autoUpdateRclone") is False,
                                "The disposable Host did not start with its pinned rclone and Sites off")
                        def require_no_transfers():
                            reply = site_request("sites.transfer.list", {})
                            require(reply.get("type") == "sites.transfer.listResult" and
                                    (reply.get("payload") or {}).get("transfers") == [],
                                    "Unexpected paired Host transfer before the normal download")
                        result = ui.run(smoke, host_name=HOST_NAME, host_origin=origin,
                                        webdav_url=f"http://127.0.0.1:{server.server_port}/",
                                        owned_root=root, destination=destination,
                                        capabilities=capabilities, get_count=get_count,
                                        verify_refresh=args.verify_folder_refresh,
                                        get_listing_count=listing_count, verify_transfers=args.verify_folder_transfers,
                                        verify_delete=args.verify_folder_delete, delete_state=delete_state, require_no_transfers=require_no_transfers)
                        file_name = result["file"]
                        output = destination / file_name
                        expected = dav.HASHES[file_name]
                        deadline = time.monotonic() + 40
                        while time.monotonic() < deadline:
                            if output.is_file() and not output.is_symlink() and hashlib.sha256(output.read_bytes()).hexdigest() == expected:
                                break
                            time.sleep(.35)
                        else:
                            raise RuntimeError("The exact WebDAV output did not finish at the chosen Server destination")
                        with lock:
                            observed = dict(counts)
                        require(observed == {file_name: 1} and result["get_count_before"] == 0,
                                "Expected exactly one selected WebDAV payload request after confirmation")
                        require(set(destination.iterdir()) == {output} and not any(decoy.iterdir())
                                and phone_count() == baseline and phone_target_absent(),
                                "The payload was not isolated to the selected Server child")
                        transfer_proof = None
                        if args.verify_folder_transfers:
                            reply = site_request("sites.transfer.list", {})
                            transfers = (reply.get("payload") or {}).get("transfers", [])
                            require(reply.get("type") == "sites.transfer.listResult" and len(transfers) == 1,
                                    "Expected exactly one paired Host transfer")
                            transfer = transfers[0]
                            require(transfer.get("id") and transfer.get("siteName") == ui.LOCATION
                                    and transfer.get("direction") == "download" and transfer.get("state") == "done"
                                    and transfer.get("files") == transfer.get("filesDone") == 1
                                    and transfer.get("filesSkipped") == transfer.get("filesFailed") == 0
                                    and transfer.get("localFolder") == str(destination),
                                    "Paired Host transfer snapshot did not match the completed copy")
                            completed_tree = ET.parse(smoke.evidence / "c4-paired-transfers-completed.xml").getroot()
                            require(any("ID " + transfer["id"] in (n.get("text", "") + n.get("content-desc", ""))
                                        for n in completed_tree.iter("node")), "Transfers UI did not show the Host batch ID")
                            transfer_proof = {"id": transfer["id"], "state": "done", "files_copied": 1}
                        status = site_request("sites.get", {})
                        data = status.get("payload") or {}
                        require(status.get("type") == "sites.status" and data.get("enabled") is True
                                and data.get("rclone") == "running" and data.get("rcloneVersion") == "1.76.0",
                                "The disposable Server did not report its Remote Files backend running")
                        proof = {
                            "host": HOST_NAME, "tls_pinned": True, "destination_child": destination.name,
                            "file": file_name, "bytes": output.stat().st_size, "sha256": expected,
                            "payload_gets": observed, "decoy_empty": True, "phone_count_unchanged": True,
                            "phone_target_absent": True, "rclone_updates_disabled": True,
                        }
                        if transfer_proof is not None:
                            proof["folder_transfers"] = transfer_proof
                        if "delete" in result:
                            proof["folder_delete"] = result["delete"]
                        if "refresh" in result:
                            proof["refresh"] = result["refresh"]
                        (smoke.evidence / "c4-paired-proof.json").write_text(
                            json.dumps(proof, indent=2) + "\n", encoding="utf-8")
                        smoke.log("PASS C4: one pinned paired-Host WebDAV download matched the exact chosen child and hash.")
                    except Exception:
                        if paired_ready:
                            try:
                                tree = smoke.tree()
                                for node in tree.iter("node"):
                                    if node.get("password") == "true":
                                        node.set("text", "")
                                        node.set("content-desc", "")
                                ET.ElementTree(tree).write(smoke.evidence / "c4-paired-failure.xml",
                                                           encoding="utf-8", xml_declaration=True)
                            except Exception:
                                pass
                        raise
                    finally:
                        pending = sys.exc_info()[0] is not None
                        if cap.installed:
                            smoke.installed = True
                        errors = []
                        if site_request is not None:
                            try:
                                status = site_request("sites.get", {})
                                if (status.get("payload") or {}).get("enabled") is True:
                                    stopped = site_request("sites.setEnabled", {"enabled": False})
                                    require(stopped.get("type") == "sites.status"
                                            and (stopped.get("payload") or {}).get("rclone") == "off",
                                            "The fixture Host did not confirm rclone stopped")
                            except Exception:
                                errors.append("fixture rclone shutdown")
                        try:
                            smoke.clean(include_remote_files_payload=False)
                        except Exception:
                            errors.append("paired probe cleanup")
                        if errors:
                            message = "Paired fixture cleanup incomplete: " + ", ".join(errors)
                            if not pending:
                                raise RuntimeError(message)
                            smoke.log(message)
            finally:
                if worker.is_alive():
                    server.shutdown()
                    worker.join(timeout=5)
                server.server_close()
    smoke.log("Cleanup PASS: owned probe, reverse, WebDAV server, TLS relay and disposable Server stopped.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--server-dll", type=Path, required=True)
    parser.add_argument("--rclone-zip", type=Path, required=True)
    parser.add_argument("--node", default="node")
    parser.add_argument("--verify-folder-delete", action="store_true",
                        help="Verify paired fixture Delete Cancel and Confirm")
    parser.add_argument("--verify-folder-transfers", action="store_true",
                        help="Verify folder Transfers empty and completed paired Host states")
    parser.add_argument("--verify-folder-refresh", action="store_true",
                        help="Verify the selected paired WebDAV folder reloads and clears selection")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
