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
import uuid
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


def until(predicate, message, timeout=35):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value is not None and value is not False:
            return value
        time.sleep(.3)
    raise RuntimeError(message)


def pause_resume(smoke, ui, host_jobs, fixture, file_name, source_url, total):
    """Use the exact native row actions and confirm the selected Host's retained job."""
    def unique_job():
        jobs = host_jobs()
        require(len(jobs) <= 1, "More than one job appeared on the disposable Host")
        if not jobs:
            return None
        job = jobs[0]
        require(job.get("id") and job.get("fileName") == file_name and job.get("url") == source_url
                and job.get("requestedConnections") == 1, "The Host did not admit the exact one-connection job")
        return job

    accepted = until(unique_job, "The disposable Host did not admit the owned job")
    job_id = accepted["id"]

    def same_job():
        job = unique_job()
        require(job is not None and job["id"] == job_id, "The exact Host job identity changed")
        return job

    def row_action(label):
        expected = label + " " + file_name
        control, tree = smoke.wait_content_description(lambda text: text == expected, expected)
        require(control.get("resource-id", "").endswith(":id/action")
                and control.get("enabled") == "true" and control.get("clickable") == "true",
                "Expected an enabled action on the exact owned row")
        require(any(node.get("text") == "Files are saved on " + HOST_NAME for node in tree.iter("node")),
                "The Downloads screen did not identify the owned Host")
        return control, tree

    close, _ = smoke.wait_content_description(lambda text: text == "Close browser", "owned Browser close")
    smoke.tap(close)
    until(lambda: 0 < same_job()["receivedBytes"] < total, "The owned Host never saved a partial segment")
    pause, _ = row_action("Pause")
    smoke.tap(pause)
    fixture.continue_initial.set()

    def paused_state():
        job = same_job()
        snapshot = fixture.snapshot()
        received = job["receivedBytes"]
        if str(job["state"]).lower() == "paused" and 0 < received < total and snapshot["active_responses"] == 0:
            return received, snapshot
        return None

    received, before = until(paused_state, "The real Pause did not settle with partial bytes")
    _, tree = row_action("Resume")
    ui.save_state(smoke, "b3-paired-resume-paused", tree)
    time.sleep(3)
    require(paused_state() == (received, before), "Host or fixture progressed after Pause settled")
    require(any(r["method"] == "GET" and r["authenticated"] and r["body_bytes"] > 0 for r in before["requests"]),
            "Initial Host transfer did not use the synthetic sign-in")
    resume, _ = row_action("Resume")
    smoke.tap(resume)

    def later_range():
        requests = fixture.snapshot()["requests"][len(before["requests"]):]
        wanted = [received, total - 1]
        return wanted if any(req["method"] == "GET" and req["authenticated"]
                             and req["range"] == wanted and req["body_bytes"] > 0 for req in requests) else None

    requested = until(later_range, "Resume did not request the saved authenticated suffix")
    smoke.log(f"The same paired Host job resumed at byte {received}.")
    fixture.release.set()
    return job_id, received, requested, len(before["requests"])


def verify_resumed_payload(smoke, ui, fixture, transfer, completed, file_name, size):
    job_id, received, requested, request_count = transfer
    require(completed["id"] == job_id, "The completed Host job has a different identity")
    after = until(lambda: (snap if (snap := fixture.snapshot())["active_responses"] == 0 else None),
                  "Fixture responses did not close after the completed transfer")
    new_requests = after["requests"][request_count:]
    require(any(req["method"] == "GET" and req["authenticated"]
                and req["range"] == requested and req["body_bytes"] == size - received for req in new_requests),
            "The authenticated resume response did not supply its entire suffix")
    require(all(req["authenticated"] and (req["range"] == [0, 0] or req["range"] and req["range"][0] > 0)
                for req in new_requests if req["method"] == "GET" and req["body_bytes"] > 0),
            "A new body bypassed the authenticated continuation or restarted from zero")
    _, tree = smoke.wait_content_description(lambda text: text.startswith(file_name + ". Finished"),
                                              "same finished paired Host row")
    require(any(node.get("text") == "Files are saved on " + HOST_NAME for node in tree.iter("node")),
            "The finished view did not identify the paired Host")
    ui.save_state(smoke, "b3-paired-resume-finished", tree)
    return {"same_host_job": True, "requested_connections": 1, "paused_seconds": 3,
            "paused_host_bytes": received, "resumed_range": requested, "resumed_suffix_bytes": size - received}


def run(args):
    tls_fixture = load("b3_owned_tls", HERE / "capture-c4-paired-download.py")
    paired = load("b3_owned_host", HERE / "paired-host-parity-fixture.py")
    states = load("b3_owned_probe", HERE / "capture-download-states-parity.py")
    link_fixture = load("b3_owned_report", HERE / "link-fixture" / "server.py")
    ui = load("b3_paired_ui", HERE / "paired-b3-ui.py")
    paced = args.verify_resume
    file_name = "inbound-b3-auth-" + uuid.uuid4().hex[:12] + ".zip" if paced else REPORT_NAME
    report_route = "/auth-resume/" + file_name if paced else REPORT_ROUTE
    args.active_only = True
    args.details_flow = args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)
    if paced:
        read_tree = smoke.tree

        def fresh_tree():
            for attempt in range(4):
                try:
                    return read_tree()
                except RuntimeError as exc:
                    if str(exc) != "Android did not write a fresh UI hierarchy" or attempt == 3:
                        raise
                    time.sleep(.3)

        smoke.tree = fresh_tree
    baseline_phone = len(smoke.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines())
    target_phone = "/sdcard/Download/" + file_name

    def phone_target_absent():
        return smoke.shell(f"if [ -e '{target_phone}' ] || [ -L '{target_phone}' ]; then echo present; fi").strip() != "present"

    require(phone_target_absent(), "The exact synthetic report already exists on the emulator")
    payload = (hashlib.shake_256(b"Inbound paired authenticated resume fixture").digest(16 * 1024 * 1024)
               if paced else (HERE / "link-fixture" / REPORT_NAME).read_bytes())
    expected = hashlib.sha256(payload).hexdigest()
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

    resume_fixture = None

    def host_report_counts():
        if resume_fixture is not None:
            observed = Counter()
            for item in resume_fixture.snapshot()["requests"]:
                method = item["method"].lower()
                if item["authenticated"] and method == "get":
                    method += "_range" if item["range"] is not None else "_full"
                key = ("authenticated_" if item["authenticated"] else "rejected_") + method
                observed[key] += 1
            return {name: observed[name] for name in counter_names}
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
                    or candidate.name != file_name or candidate.parent != Path(directory)):
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
        if paced:
            module = load("b3_paced_auth", HERE / "b3-auth-resume-fixture.py")
            resume_fixture = stack.enter_context(module.serve(file_name, payload))
            browser_fixture = stack.enter_context(module.serve(file_name, payload))
            host_report_port = resume_fixture.server_address[1]
            browser_report_port = browser_fixture.server_address[1]
        else:
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
                   on_consent=on_consent, after_cancel=after_cancel, before_submit=before_submit,
                   file_name=file_name, paced=paced)
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
        source_url = report_origin + report_route
        transfer = None
        if resume_fixture is not None:
            try:
                transfer = pause_resume(smoke, ui, host_jobs, resume_fixture, file_name, source_url, len(payload))
            except Exception:
                (smoke.evidence / "b3-paired-resume-incomplete.json").write_text(
                    json.dumps(resume_fixture.snapshot(), indent=2) + "\n", encoding="utf-8")
                raise
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
                    "exact_name": candidate.get("fileName") == file_name,
                    "exact_url": candidate.get("url") == source_url,
                    "state": state if state in safe_states else "other",
                    "received_bytes": candidate.get("receivedBytes"),
                    "direct_root_destination": str(candidate.get("destinationDirectory", "")).casefold() == str(host_root).casefold(),
                    "http_status": failure.get("httpStatus") if isinstance(failure.get("httpStatus"), int) else None,
                })
            (smoke.evidence / "b3-paired-incomplete.json").write_text(
                json.dumps(diagnostics, indent=2) + "\n", encoding="utf-8")
            raise RuntimeError("The exact authenticated paired Host output did not complete")
        job = jobs[0]
        require(job.get("id") and job.get("fileName") == file_name and job.get("url") == source_url,
                "The completed job does not identify this exact synthetic report")
        root_entry = output if output.parent == host_root else output.parent
        require(set(host_root.iterdir()) == {root_entry} and set(output.parent.iterdir()) == {output}
                and hashlib.sha256(output.read_bytes()).hexdigest() == expected,
                "The paired Host report did not match its owned destination and hash")
        require(phone_target_absent() and baseline_phone ==
                len(smoke.shell("find", "/sdcard/Download", "-maxdepth", "1", "-type", "f").splitlines()),
                "Phone Downloads changed during the paired-only report transfer")
        proof = {
            "paired_https_pin_verified": True, "host_requests_before_consent": 0,
            "cancel_added_jobs": 0, "cancel_host_report_requests": 0, "cancel_observation_seconds": 2.5,
            "host_requests_after_send_before_download": submitted_at,
            "jobs_after_download": len(jobs), "file": file_name, "bytes": output.stat().st_size,
            "sha256": expected, "post_submit_host_requests": delta, "phone_target_absent": True,
            "owned_destination_depth": len(output.relative_to(host_root).parts) - 1,
        }
        if transfer is not None:
            proof["resume"] = verify_resumed_payload(smoke, ui, resume_fixture, transfer, job, file_name, len(payload))
        (smoke.evidence / "b3-paired-proof.json").write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
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
    parser.add_argument("--verify-resume", action="store_true",
                        help="Use a paced synthetic archive and verify normal paired Host Pause/Resume")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
