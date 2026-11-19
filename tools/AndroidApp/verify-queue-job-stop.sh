#!/usr/bin/env bash
# Verify API 31–33 JobScheduler stop revokes the active queue-work lease and persists a stable partial checkpoint.
# The app, APK, download, ordinary job 4100, foreground service, and root-enabled emulator must already be running.
# This driver never starts the app, changes device settings, installs an APK, resumes work, or deletes files.
#
# Usage (Git Bash):
#   ANDROID_SERIAL=emulator-5554 ADB=/c/Users/.../Android/Sdk/platform-tools/adb.exe bash tools/AndroidApp/verify-queue-job-stop.sh 01234567-89ab-cdef-0123-456789abcdef
#
# It force-runs only the app's ordinary job 4100, stops its KeepAliveService, requests the JobScheduler timeout,
# and reads only the app's private persisted job/checkpoint/settings and crash-guard marker. Output is a safe,
# whitelisted JSON summary; raw job JSON, URLs, headers, settings contents, and marker bytes are never printed.

set -euo pipefail
export MSYS_NO_PATHCONV=1

usage() {
  echo "usage: ANDROID_SERIAL=emulator-<digits> ADB=/path/to/adb bash $0 <job-guid>" >&2
  exit 2
}

: "${ANDROID_SERIAL:?ANDROID_SERIAL=emulator-<digits> is required}"
: "${ADB:?ADB must be set to the adb executable path}"
[[ "$ANDROID_SERIAL" =~ ^emulator-[0-9]+$ ]] || usage
[[ $# -eq 1 ]] || usage
[[ "$1" =~ ^[[:xdigit:]]{8}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{12}$ ]] || usage
[[ -f "$ADB" ]] || { echo '{"result":"FAIL","stage":"preflight","assertion":"adb_path_missing"}'; exit 1; }
# Windows Python does not resolve MSYS /c paths passed through subprocess.
if command -v cygpath >/dev/null 2>&1; then
  ADB="$(cygpath -m "$ADB")"
fi
PYTHON="${PYTHON:-python}"
command -v "$PYTHON" >/dev/null 2>&1 || { echo '{"result":"FAIL","stage":"preflight","assertion":"python_missing"}'; exit 1; }

"$PYTHON" - "$ADB" "$ANDROID_SERIAL" "$1" <<'PY'
import json
import re
import subprocess
import sys
import time
import uuid

PKG = "app.inbound.android"
JOB_SERVICE_ID = "4100"
STATE_ROOT = "/data/user/0/app.inbound.android/files/host-state"
MIN_REMAINING = 131072
ADB, SERIAL, GUID_TEXT = sys.argv[1:4]

def emit(result, stage, assertion, **safe):
    payload = {"result": result, "stage": stage, "assertion": assertion}
    payload.update(safe)
    print(json.dumps(payload, separators=(",", ":"), sort_keys=True))

def fail(stage, assertion):
    emit("FAIL", stage, assertion)
    raise SystemExit(1)

try:
    job_id = uuid.UUID(GUID_TEXT)
except (ValueError, AttributeError):
    fail("preflight", "invalid_job_guid")
JOB_N = job_id.hex

def adb(*args, timeout=20, absent_service_ok=False, stop_service_ok=False):
    try:
        result = subprocess.run(
            [ADB, "-s", SERIAL, *args],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        raise RuntimeError("adb_command_failed")
    allowed_absence = absent_service_ok and result.returncode == 1 and "(nothing)" in result.stdout
    # Android's am stopservice uses nonzero shell statuses too; actual absence is verified below.
    allowed_stop = stop_service_ok and result.returncode in (1, 255)
    if result.returncode != 0 and not (allowed_absence or allowed_stop):
        raise RuntimeError("adb_command_failed_" + "_".join(args[:2]))
    return result.stdout.strip()

def adb_bytes(*args, timeout=20):
    try:
        result = subprocess.run(
            [ADB, "-s", SERIAL, *args],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        raise RuntimeError("adb_command_failed")
    if result.returncode != 0:
        raise RuntimeError("adb_command_failed_" + "_".join(args[:2]))
    return result.stdout

def read_json(path):
    try:
        return json.loads(adb_bytes("exec-out", "cat", path, timeout=10))
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise RuntimeError("persisted_json_unreadable")

def field(obj, name, default=None):
    if not isinstance(obj, dict):
        return default
    if name in obj:
        return obj[name]
    wanted = name.casefold()
    for key, value in obj.items():
        if isinstance(key, str) and key.casefold() == wanted:
            return value
    return default

def snapshot():
    job_path = f"{STATE_ROOT}/jobs/{JOB_N}.json"
    checkpoint_path = f"{STATE_ROOT}/checkpoints/{JOB_N}.json"
    settings_path = f"{STATE_ROOT}/settings.json"
    job = read_json(job_path)
    checkpoint = read_json(checkpoint_path)
    settings = read_json(settings_path)

    if field(checkpoint, "schemaVersion") != 1 or str(field(checkpoint, "jobId", "")).replace("-", "").casefold() != JOB_N:
        raise RuntimeError("checkpoint_identity_or_schema_invalid")

    if str(field(job, "id", "")).replace("-", "").casefold() != JOB_N:
        raise RuntimeError("job_identity_mismatch")
    segments = field(checkpoint, "segments", [])
    if not isinstance(segments, list):
        raise RuntimeError("checkpoint_invalid")
    completed = sum(int(field(segment, "completed", 0) or 0) for segment in segments)
    total = field(checkpoint, "totalBytes")
    if total is None:
        total = field(job, "totalBytes")
    total = int(total) if total is not None else None
    for segment in segments:
        start, end = int(field(segment, "start", -1)), int(field(segment, "end", -1))
        done = int(field(segment, "completed", -1))
        if total is None or not (0 <= start <= end < total and 0 <= done <= end - start + 1):
            raise RuntimeError("checkpoint_segment_bounds_invalid")

    queue_id = int(field(job, "queueId", -1))
    queues = field(settings, "queues", [])
    if not isinstance(queues, list):
        raise RuntimeError("settings_invalid")
    queue = next((q for q in queues if int(field(q, "id", -2)) == queue_id), None)
    if queue is None:
        raise RuntimeError("queue_missing")
    return {
        "state": str(field(job, "state", "")).casefold(),
        "received": int(field(job, "receivedBytes", 0) or 0),
        "completed": completed,
        "total": total,
        "is_queued": field(job, "isQueued") is True,
        "paused_by_user": field(job, "pausedByUser") is True,
        "queue_id": queue_id,
        "queue_running": field(queue, "isRunning") is True,
    }

def marker_session_present():
    raw = adb_bytes("exec-out", "cat", f"{STATE_ROOT}/crash-guard.txt", timeout=10)
    text = raw.decode("ascii", errors="strict")
    for line in text.splitlines():
        if line.startswith("session="):
            return bool(line.partition("=")[2].strip())
    raise RuntimeError("crash_marker_invalid")

def jobscheduler_state():
    return adb("shell", "cmd", "jobscheduler", "get-job-state", PKG, JOB_SERVICE_ID, timeout=10)

def is_active_job_state():
    output = jobscheduler_state()
    return re.search(r"\bactive\b", output, re.IGNORECASE) is not None

def keepalive_status():
    output = adb("shell", "dumpsys", "activity", "services", f"{PKG}/.KeepAliveService", timeout=15, absent_service_ok=True)
    has_service = "KeepAliveService" in output
    foreground = re.search(r"(?:mIsForeground|isForeground)=true|foregroundId=1001", output, re.IGNORECASE) is not None
    return has_service, foreground

def stop_log_lines():
    output = adb("logcat", "-d", "-s", "Inbound", "-v", "brief", timeout=20)
    return [line for line in output.splitlines() if "queue job stopped:" in line]

try:
    if adb("get-state", timeout=10) != "device":
        fail("preflight", "emulator_not_connected")
    if adb("shell", "id", "-u", timeout=10).strip() != "0":
        fail("preflight", "adb_root_required")
    try:
        sdk = int(adb("shell", "getprop", "ro.build.version.sdk", timeout=10))
    except ValueError:
        fail("preflight", "sdk_unavailable")
    if sdk < 31 or sdk > 33:
        fail("preflight", "unsupported_sdk")
    activities = adb("shell", "dumpsys", "activity", "activities", timeout=15)
    if any(PKG in line and ("mResumedActivity" in line or "topResumedActivity" in line) for line in activities.splitlines()):
        fail("precondition", "app_must_be_hidden")

    before = snapshot()
    if before["state"] != "downloading":
        fail("precondition", "job_not_downloading")
    if not before["is_queued"] or before["paused_by_user"]:
        fail("precondition", "job_not_active_queue_member")
    if not before["queue_running"]:
        fail("precondition", "queue_not_running")
    if before["total"] is None or before["total"] <= 0 or before["completed"] <= 0:
        fail("precondition", "checkpoint_not_partial")
    if before["total"] - before["completed"] < MIN_REMAINING:
        fail("precondition", "download_too_near_completion")
    if before["received"] <= 0:
        fail("precondition", "job_received_bytes_zero")
    if not marker_session_present():
        fail("precondition", "crash_marker_session_not_active")
    if not all(keepalive_status()):
        fail("precondition", "foreground_keepalive_not_active")
    baseline_stops = len(stop_log_lines())

    # Force only the app's designated ordinary persisted wake job and confirm it remains actively executing.
    adb("shell", "cmd", "jobscheduler", "run", "-f", PKG, JOB_SERVICE_ID, timeout=20)
    if not is_active_job_state():
        fail("force_run", "queue_job_4100_not_active_after_force")

    # Stop the existing foreground lease service; do not start the app or any service.
    adb("shell", "am", "stopservice", f"{PKG}/.KeepAliveService", timeout=15, stop_service_ok=True)
    stop_deadline = time.monotonic() + 5
    while time.monotonic() < stop_deadline and keepalive_status()[0]:
        time.sleep(0.25)
    if keepalive_status()[0]:
        fail("keepalive_stop", "keepalive_service_still_running")
    if not marker_session_present():
        fail("keepalive_stop", "crash_marker_cleared_while_queue_job_active")

    # Ask JobScheduler to stop the currently executing ordinary job.
    timeout_output = adb("shell", "cmd", "jobscheduler", "timeout", PKG, JOB_SERVICE_ID, timeout=20)
    if re.search(r"\btiming out\b", timeout_output, re.IGNORECASE) is None:
        fail("timeout", "jobscheduler_did_not_report_timing_out")

    deadline = time.monotonic() + 15
    stopped_log = None
    after = None
    while time.monotonic() < deadline:
        after = snapshot()
        new_stops = stop_log_lines()[baseline_stops:]
        for line in reversed(new_stops):
            match = re.search(r"queue job stopped: reason=([A-Za-z0-9_]+) detached=(True|False)", line)
            if match:
                stopped_log = {"callback": True, "detached": match.group(2).casefold() == "true"}
                break
        marker_empty = not marker_session_present()
        if after["state"] in ("queued", "paused") and stopped_log is not None and marker_empty:
            break
        time.sleep(0.5)

    if stopped_log is None:
        fail("timeout", "on_stop_log_not_observed")
    if not stopped_log["detached"]:
        fail("timeout", "on_stop_did_not_detach_run")
    if after is None or after["state"] not in ("queued", "paused"):
        fail("timeout", "job_not_queued_or_paused")
    if not after["is_queued"] or after["paused_by_user"]:
        fail("timeout", "job_membership_or_pause_state_changed")
    if not after["queue_running"]:
        fail("timeout", "queue_no_longer_running")
    if marker_session_present():
        fail("timeout", "crash_marker_session_not_cleared")

    # Require two checkpoint reads two seconds apart to be identical and still partial.
    first_bytes = after["completed"]
    first_total = after["total"]
    if first_total is None or first_total != before["total"] or first_bytes < before["completed"] or first_bytes <= 0 or first_bytes >= first_total:
        fail("checkpoint", "checkpoint_not_partial_after_stop")
    time.sleep(2)
    stable = snapshot()
    if stable["completed"] != first_bytes or stable["total"] != first_total:
        fail("checkpoint", "checkpoint_changed_after_stop")
    if stable["state"] not in ("queued", "paused") or not stable["is_queued"] or stable["paused_by_user"]:
        fail("checkpoint", "job_state_changed_during_stability_check")
    if not stable["queue_running"]:
        fail("checkpoint", "queue_stopped_during_stability_check")
    if marker_session_present():
        fail("checkpoint", "crash_marker_session_reappeared")

    emit(
        "PASS",
        "complete",
        "job_stop_revoked_queue_lease",
        jobId=str(job_id),
        state=stable["state"],
        completedBytes=stable["completed"],
        totalBytes=stable["total"],
        queueId=stable["queue_id"],
        isQueued=stable["is_queued"],
        queueIsRunning=stable["queue_running"],
        pausedByUser=stable["paused_by_user"],
        stopCallbackObserved=stopped_log["callback"],
        detached=stopped_log["detached"],
        checkpointUnchangedForSeconds=2,
        crashMarkerSessionEmpty=True,
        evidenceScope="observed_stop_and_checkpoint_only",
    )
except RuntimeError as error:
    fail("verification", str(error))
except SystemExit:
    raise
except Exception:
    fail("verification", "unexpected_error")
PY
