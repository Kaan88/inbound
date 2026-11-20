"""Verify real paired consent using an isolated HTTPS Host and synthetic report."""
import argparse
from collections import Counter
from contextlib import ExitStack
import hashlib
from http.server import ThreadingHTTPServer
import importlib.util
import json
import stat
from pathlib import Path
import tempfile
import threading
import time
from urllib.parse import urlsplit

HERE = Path(__file__).resolve().parent
HOST_NAME = "Parity B3 Host"
REPORT_NAME = "report-q3-2026.xlsx"
REPORT_ROUTE = "/reports/" + REPORT_NAME


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def run(args):
    tls_fixture = load("b3_owned_tls", HERE / "capture-c4-paired-download.py")
    paired = load("b3_owned_host", HERE / "paired-host-parity-fixture.py")
    states = load("b3_owned_probe", HERE / "capture-download-states-parity.py")
    link_fixture = load("b3_owned_report", HERE / "link-fixture" / "server.py")
    ui = load("b3_paired_ui", HERE / "paired-b3-ui.py")
    args.active_only = True
    args.details_flow = args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)
    baseline_phone = len(smoke.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines())
    target_phone = "/sdcard/Download/" + REPORT_NAME

    def phone_target_absent():
        return smoke.shell(f"if [ -e '{target_phone}' ] || [ -L '{target_phone}' ]; then echo present; fi").strip() != "present"

    require(phone_target_absent(), "The exact synthetic report already exists on the emulator")
    fixture_file = HERE / "link-fixture" / REPORT_NAME
    expected = hashlib.sha256(fixture_file.read_bytes()).hexdigest()
    counts, lock = Counter(), threading.Lock()
    counter_names = (
        "authenticated_head", "authenticated_get_range", "authenticated_get_full", "authenticated_post",
        "rejected_head", "rejected_get", "rejected_post",
    )

    class HostReportHandler(link_fixture.Handler):
        def handle_request(self, body):
            if urlsplit(self.path).path == REPORT_ROUTE and self.command in ("HEAD", "GET", "POST"):
                authenticated = any(part.strip() == "inbound_test=yes"
                                    for part in self.headers.get("Cookie", "").split(";"))
                method = self.command.lower()
                if authenticated and method == "get":
                    method += "_range" if self.headers.get("Range") else "_full"
                name = ("authenticated_" if authenticated else "rejected_") + method
                with lock:
                    counts[name] += 1
            super().handle_request(body)

    def host_report_counts():
        with lock:
            return {name: counts[name] for name in counter_names}

    # Avoid even sanitized fixture request output; only aggregated observations are retained.
    link_fixture.print = lambda *_args, **_kwargs: None
    with ExitStack() as stack:
        temp = Path(stack.enter_context(tempfile.TemporaryDirectory(prefix="inbound-b3-relay-")))
        host = stack.enter_context(paired.running_fixture(args.server_dll, name=HOST_NAME))
        host_root = Path(host["download_root"]).resolve(strict=True)
        require(not any(host_root.iterdir()), "Disposable Host destination is not fresh and empty")
        def exact_host_output(job):
            # The Host may put a caught report in its automatic category folder.
            # Inspect only a reported path inside this fresh, disposable root.
            reported = job.get("finalFilePath")
            directory = job.get("destinationDirectory")
            if not isinstance(reported, str) or not isinstance(directory, str):
                return None
            candidate = Path(reported)
            if not candidate.is_absolute() or not candidate.is_relative_to(host_root):
                return None
            relative = candidate.relative_to(host_root)
            if (len(relative.parts) not in (1, 2) or ".." in relative.parts
                    or candidate.name != REPORT_NAME or candidate.parent != Path(directory)):
                return None
            if candidate.parent != host_root:
                parent = candidate.parent.lstat()
                if not stat.S_ISDIR(parent.st_mode) or getattr(parent, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    return None
            if candidate.is_symlink() or not candidate.is_file() or candidate.resolve() != candidate:
                return None
            return candidate

        def start_report_server(handler):
            server = stack.enter_context(ThreadingHTTPServer(("127.0.0.1", 0), handler))
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            stack.callback(worker.join, 5)
            stack.callback(server.shutdown)
            return server.server_port

        # Android sees the same source URL as the Host, through its own
        # reverse to a different listener. Only the Host listener is counted.
        host_report_port = start_report_server(HostReportHandler)
        browser_report_port = start_report_server(link_fixture.Handler)
        report_origin = f"http://127.0.0.1:{host_report_port}"
        listener = paired.ephemeral_port()
        host_origin, fingerprint, _, _ = stack.enter_context(tls_fixture.tls_relay(
            args.node, urlsplit(host["base_url"]).port, listener, temp))

        request = paired.fixture_session(host)

        def host_jobs():
            reply = request("jobs.snapshot", {})
            jobs = (reply.get("payload") or {}).get("jobs")
            require(reply.get("type") == "jobs.snapshot" and isinstance(jobs, list),
                    "Actual disposable Host job snapshot unavailable")
            return jobs

        def no_submission():
            require(not host_jobs() and not any(host_root.iterdir()),
                    "A paired Host job or report appeared before the actual Download tap")

        no_submission()

        def remove_reverse(endpoint):
            cap.adb("reverse", "--remove", endpoint)

        for device_port, local_port in ((listener, listener), (host_report_port, browser_report_port)):
            endpoint = f"tcp:{device_port}"
            cap.adb("reverse", "--no-rebind", endpoint, f"tcp:{local_port}")
            stack.callback(remove_reverse, endpoint)

        def clean_probe():
            smoke.installed = cap.installed
            smoke.clean(include_remote_files_payload=False)

        stack.callback(clean_probe)
        cap.install_and_launch()
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY)
        smoke.started = True
        smoke.tap(smoke.wait_node("Start downloading", resource="fr_start")[0])
        smoke.wait_node("Search downloads")
        tls_fixture.pair_probe(smoke, host_origin, host["password"], fingerprint, host_name=HOST_NAME)
        submitted_at = None

        def on_consent():
            no_submission()
            initial = host_report_counts()
            require(not any(initial.values()), "The Host contacted the report before consent")
            return initial

        def after_cancel(baseline):
            deadline = time.monotonic() + 2.5
            while time.monotonic() < deadline:
                no_submission()
                require(host_report_counts() == baseline,
                        "A Host report request happened after the first consent was cancelled")
                time.sleep(.25)
            smoke.log("PASS B3 Cancel: the paired Host remained empty and no Host report requests followed consent cancellation.")

        def before_submit():
            nonlocal submitted_at
            no_submission()
            submitted_at = host_report_counts()

        try:
            ui.run(smoke, host_name=HOST_NAME, host_origin=host_origin, report_origin=report_origin,
                   on_consent=on_consent, after_cancel=after_cancel, before_submit=before_submit)
        except Exception:
            # Preserve only the probe's resource and visibility information on
            # failure. No field text, addresses, cookie values or tokens.
            try:
                controls = [{key: node.get(key) for key in ("resource-id", "class", "enabled", "clickable")}
                            for node in smoke.tree().iter("node") if node.get("package") == states.PACKAGE]
                (smoke.evidence / "b3-paired-failure-controls.json").write_text(
                    json.dumps(controls, indent=2) + "\n", encoding="utf-8")
            except Exception:
                pass
            raise
        require(submitted_at is not None, "The actual submission was not observed")
        deadline = time.monotonic() + 50
        while time.monotonic() < deadline:
            jobs = host_jobs()
            require(len(jobs) <= 1, "More than one paired Host job was admitted")
            delta = {kind: current - submitted_at[kind] for kind, current in host_report_counts().items()}
            if len(jobs) == 1 and str(jobs[0].get("state", "")).lower() == "completed":
                output = exact_host_output(jobs[0])
                if output is not None and delta["authenticated_get_range"] + delta["authenticated_get_full"] > 0:
                    break
            time.sleep(.4)
        else:
            safe_states = {"queued", "connecting", "downloading", "paused", "completed", "failed", "cancelled"}
            diagnostics = {"host_jobs": len(jobs), "jobs": [], "post_submit_host_requests": delta,
                           "owned_output_count": len(list(host_root.iterdir()))}
            for candidate in jobs:
                state = str(candidate.get("state", "")).lower()
                failure = candidate.get("failure") or {}
                diagnostics["jobs"].append({
                    "exact_name": candidate.get("fileName") == REPORT_NAME,
                    "exact_url": candidate.get("url") == report_origin + REPORT_ROUTE,
                    "state": state if state in safe_states else "other",
                    "received_bytes": candidate.get("receivedBytes"),
                    "direct_root_destination": str(candidate.get("destinationDirectory", "")).casefold() == str(host_root).casefold(),
                    "http_status": failure.get("httpStatus") if isinstance(failure.get("httpStatus"), int) else None,
                })
            (smoke.evidence / "b3-paired-incomplete.json").write_text(
                json.dumps(diagnostics, indent=2) + "\n", encoding="utf-8")
            raise RuntimeError("The exact authenticated paired Host output did not complete")
        job = jobs[0]
        require(job.get("id") and job.get("fileName") == REPORT_NAME and job.get("url") == report_origin + REPORT_ROUTE,
                "The completed job does not identify this exact synthetic report")
        root_entry = output if output.parent == host_root else output.parent
        require(set(host_root.iterdir()) == {root_entry} and set(output.parent.iterdir()) == {output}
                and hashlib.sha256(output.read_bytes()).hexdigest() == expected,
                "The paired Host report did not match its owned destination and hash")
        require(phone_target_absent() and baseline_phone ==
                len(smoke.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()),
                "Phone Downloads changed during the paired-only report transfer")
        (smoke.evidence / "b3-paired-proof.json").write_text(json.dumps({
            "paired_https_pin_verified": True, "host_requests_before_consent": 0,
            "cancel_added_jobs": 0, "cancel_host_report_requests": 0, "cancel_observation_seconds": 2.5,
            "host_requests_after_send_before_download": submitted_at,
            "jobs_after_download": len(jobs), "file": REPORT_NAME, "bytes": output.stat().st_size,
            "sha256": expected, "post_submit_host_requests": delta, "phone_target_absent": True,
            "owned_destination_depth": len(output.relative_to(host_root).parts) - 1,
        }, indent=2) + "\n", encoding="utf-8")
        smoke.log("PASS B3 paired consent: one authenticated Host report completed with its exact SHA-256.")
    smoke.log("Cleanup PASS: only the owned probe, two mappings, relay, fixtures and disposable Host state were removed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--server-dll", type=Path, required=True)
    parser.add_argument("--node", default="node")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
