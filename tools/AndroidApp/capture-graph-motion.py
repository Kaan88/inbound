"""Record a real local transfer's graph response to a held and resumed byte stream.

The video and native-sized frames are evidence for visual inspection, not an
automatic verdict on smoothness or headline stability. Only the dedicated API 33
emulator, a fresh isolated probe, and a loopback fixture are supported.
"""
import argparse
import hashlib
import importlib.machinery
import json
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

import cv2

HERE = Path(__file__).resolve().parent
states = importlib.machinery.SourceFileLoader("graph_motion_states", str(HERE / "capture-download-states-parity.py")).load_module()
require = states.require


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", required=True)
    parser.add_argument("--aapt", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    args.active_only = True
    args.details_flow = args.mixed_a2 = args.unattended_b5 = args.capture_mixed_a3 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    remote = f"/data/local/tmp/inbound-graph-motion-{cap.nonce}.mp4"
    target = "/sdcard/Download/" + cap.names["active"]
    require(re.fullmatch(r"/data/local/tmp/inbound-graph-motion-[0-9a-f]{10}\.mp4", remote), "Invalid owned recording path")
    require(re.fullmatch(r"/sdcard/Download/inbound-a2-active-[0-9a-f]{10}\.bin", target), "Invalid fixture target")
    owned_recording = False
    transfer_started = False
    recorder = None

    def absent(path):
        return cap.shell(f"if [ -e '{path}' ] || [ -L '{path}' ]; then echo PRESENT; else echo ABSENT; fi").strip() == "ABSENT"

    def ui(key):
        tree = smoke.tree()
        rate = [n.get("text", "") for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/summary_rate")]
        graph = [n.get("bounds", "") for n in tree.iter("node") if n.get("resource-id", "").endswith(":id/summary_graph")]
        require(len(rate) == len(graph) == 1 and graph[0], "Graph or headline not visible")
        require(any(n.get("content-desc", "").startswith(cap.names["active"] + ". ") for n in tree.iter("node")),
                "Expected real owned transfer row")
        ET.ElementTree(tree).write(cap.evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)
        return {"headline": rate[0], "graph_bounds": graph[0], "fixture_bytes": cap.sent["active"]}

    def finish_and_remove():
        cap.capture_gate.set()
        states.CHUNK_DELAY_SECONDS = .001
        if not transfer_started:
            return
        deadline = time.monotonic() + 90
        expected = hashlib.sha256(cap.fixture_bytes).hexdigest()
        while time.monotonic() < deadline:
            digest = cap.shell(f"if [ -f '{target}' ]; then sha256sum '{target}'; fi").strip().split()
            if digest[:1] == [expected]:
                cap.shell("rm", "-f", target)
                require(absent(target), "Owned completed file remains")
                cap.log("Cleanup PASS: removed exact hash-verified fixture output")
                return
            if digest:
                raise RuntimeError("Unexpected fixture output hash; retained")
            time.sleep(.5)
        raise RuntimeError("Fixture did not finish; any partial was retained for inspection")

    try:
        cap.preflight()
        require(absent(target) and absent(remote), "Owned fixture target or recording already exists")
        cap.start_fixture()
        cap.install_and_launch()
        cap.started = True
        # The isolated probe's add_url hook creates a genuine local HTTP job.
        transfer_started = True
        cap.adb("shell", "am", "start", "-W", "-n", states.ACTIVITY, "--ez", "skip_first_run", "true",
                "--es", "add_url", f"http://127.0.0.1:{cap.port}/active")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline and cap.sent["active"] < 512 * 1024:
            time.sleep(.2)
        require(cap.sent["active"] == 512 * 1024, "Fixture did not reach the real 512 KiB hold")
        before = ui("graph-before")
        owned_recording = True
        start = time.monotonic()
        recorder = subprocess.Popen([args.adb, "-s", args.serial, "shell", "screenrecord", "--time-limit", "10", remote],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        time.sleep(3)
        require(recorder.poll() is None, "Screen recording stopped before stream release")
        release_offset = time.monotonic() - start
        held_bytes = cap.sent["active"]
        cap.capture_gate.set()
        _, error = recorder.communicate(timeout=20)
        require(recorder.returncode == 0, "Screen recording failed: " + error.decode("utf-8", "replace")[:200])
        recorder = None
        after_bytes = cap.sent["active"]
        require(after_bytes > held_bytes, "Fixture bytes did not advance after release")
        video_path = cap.evidence / "graph-motion.mp4"
        video_path.write_bytes(cap.adb("exec-out", "cat", remote, raw=True))
        video = cv2.VideoCapture(str(video_path))
        require(video.isOpened(), "Cannot decode the recording")
        fps = video.get(cv2.CAP_PROP_FPS)
        require(1 <= fps <= 120, "Invalid recording frame rate")
        frames = cap.evidence / "graph-motion-frames"
        frames.mkdir()
        count = saved = 0
        try:
            while True:
                ok, frame = video.read()
                if not ok:
                    break
                if count % 2 == 0:
                    require(cv2.imwrite(str(frames / f"frame-{count:04d}.png"), frame), "Could not save frame")
                    saved += 1
                count += 1
        finally:
            video.release()
        require(count >= 10 and saved >= 5, "Too few recorded frames")
        (cap.evidence / "graph-motion.json").write_text(json.dumps({
            "before": before, "fixture_bytes_after": after_bytes, "held_bytes": held_bytes,
            "release_host_offset_seconds": release_offset, "fps": fps, "frames": count,
            "saved_every_frames": 2,
            "limits": "Offsets are measured from adb launch, not video timestamps. Inspect adjacent native frames and headline text; no automated motion or headline-stability verdict is claimed."
        }, indent=2) + "\n", encoding="utf-8")
        cap.log("Recorded real held/resumed download and native frames; visual review remains required")
    finally:
        errors = []
        if recorder is not None:
            try:
                recorder.communicate(timeout=15)
            except Exception as exc:
                recorder.terminate()
                errors.append("recorder: " + str(exc))
        try:
            finish_and_remove()
        except Exception as exc:
            errors.append("fixture output: " + str(exc))
        if owned_recording:
            try:
                cap.shell("rm", "-f", "--", remote)
                require(absent(remote), "Owned recording remains")
            except Exception as exc:
                errors.append("recording cleanup: " + str(exc))
        try:
            cap.clean()
        except Exception as exc:
            errors.append("probe cleanup: " + str(exc))
        if errors:
            raise RuntimeError("; ".join(errors))


if __name__ == "__main__":
    main()
