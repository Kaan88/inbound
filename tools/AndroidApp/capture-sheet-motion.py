"""Capture isolated A4/C5 sheet motion on inbound33, without changing clipboard content.

The runner refuses an existing probe and any non-owned A4 clipboard offer. It uses
the actual Add/Host controls, records the screen during opening and Back, and
restores the exact original animator_duration_scale, including an absent value.
Only the unique probe, UI dump and recordings created by this run are removed.
"""
import argparse
import importlib.machinery
import json
import re
import subprocess
import time
from pathlib import Path

import cv2


HERE = Path(__file__).resolve().parent
core = importlib.machinery.SourceFileLoader("motion_core_preflight", str(HERE / "capture-core-parity.py")).load_module()
require = core.require
KEY = "animator_duration_scale"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", required=True)
    parser.add_argument("--aapt", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    cap = core.Capture(args)
    smoke = cap.smoke
    original = None
    changed = False
    checked = False
    owned_recordings = []
    recorder = None

    def scale():
        return cap.shell("settings", "get", "global", KEY).strip()

    def set_scale(value):
        cap.shell("settings", "put", "global", KEY, value)
        observed = scale()
        require(observed != "null" and float(observed) == float(value), "Could not set requested animator scale")

    def clean_dump():
        require(re.fullmatch(r"/data/local/tmp/remote-files-ui-[0-9a-f]{8}\.xml", smoke.xml_path) is not None,
                "Refusing unexpected UI dump path")
        cap.shell("rm", "-f", "--", smoke.xml_path)

    def tree():
        try:
            return smoke.tree()
        finally:
            clean_dump()

    def unique(predicate, label):
        nodes = [n for n in tree().iter("node") if predicate(n)]
        require(len(nodes) == 1, f"Expected one {label}, found {len(nodes)}")
        return nodes[0]

    def visible(node):
        return node.get("enabled") == "true" and node.get("clickable") == "true"

    def controls():
        add = unique(lambda n: n.get("resource-id") == core.APP_ID + "fab" and visible(n), "Add control")
        host = unique(lambda n: n.get("content-desc") == "Downloads shown from This phone. Change host" and visible(n),
                      "phone Host control")
        return cap.smoke_module.center(add), cap.smoke_module.center(host)

    def check_add_safe(add):
        cap.shell("input", "tap", *map(str, add))
        time.sleep(1)
        state = tree()
        labels = [n.get("text", "") for n in state.iter("node")]
        require("Add a download" in labels, "Add sheet did not open")
        if "Link on the clipboard" in labels:
            offered = [n.get("text", "") for n in state.iter("node")
                       if n.get("resource-id") == core.APP_ID + "clip_text"]
            require(len(offered) == 1, "Clipboard offer text is ambiguous; no recording was made")
            match = re.fullmatch(r"127\.0\.0\.1:(\d{1,5})/owned-failed", offered[0])
            require(match is not None and 1 <= int(match.group(1)) <= 65535,
                    "Clipboard offer is not the authorized loopback fixture; no recording was made")
        cap.shell("input", "keyevent", "4")
        time.sleep(1)
        controls()

    def check_host(host):
        cap.shell("input", "tap", *map(str, host))
        time.sleep(1)
        state = tree()
        labels = [n.get("text", "") for n in state.iter("node")]
        require("Where downloads run" in labels and "This phone" in labels,
                "Local Host switcher did not open")
        require(not any(n.get("password") == "true" for n in state.iter("node")),
                "Unexpected pairing or password screen")
        cap.shell("input", "keyevent", "4")
        time.sleep(1)
        controls()

    def record(mode, sheet, point):
        nonlocal recorder
        name = f"{mode}-{sheet}"
        remote = f"/data/local/tmp/inbound-motion-{smoke.nonce}-{name}.mp4"
        require(re.fullmatch(r"/data/local/tmp/inbound-motion-[0-9a-f]{8}-(normal|reduced)-(add|host)\.mp4", remote) is not None,
                "Unexpected recording path")
        require(cap.shell("test", "!", "-e", remote) == "", "Recording path already exists")
        owned_recordings.append(remote)
        started = time.monotonic()
        recorder = subprocess.Popen([args.adb, "-s", args.serial, "shell", "screenrecord", "--time-limit", "3", remote],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            time.sleep(.6)
            require(recorder.poll() is None, "screenrecord exited before the action")
            opened = time.monotonic() - started
            cap.shell("input", "tap", *map(str, point))
            time.sleep(1)
            closed = time.monotonic() - started
            cap.shell("input", "keyevent", "4")
            _, error = recorder.communicate(timeout=8)
            require(recorder.returncode == 0, "screenrecord failed: " + error.decode("utf-8", "replace")[:300])
            recorder = None
            controls()  # Confirm the sheet is closed before copying the recording.
            if sheet == "add":
                # Refuse to retain the recording if any non-owned offer is visible now.
                # Clipboard is never set, cleared, pasted or saved by this runner.
                check_add_safe(point)
            else:
                check_host(point)
            movie = cap.evidence / (name + ".mp4")
            movie.write_bytes(cap.adb("exec-out", "cat", remote, raw=True))
            require(movie.stat().st_size > 0, "Recording is empty")
            video = cv2.VideoCapture(str(movie))
            require(video.isOpened(), "Cannot open captured video")
            fps = video.get(cv2.CAP_PROP_FPS)
            require(1 <= fps <= 120, "Recorded frame rate is unavailable")
            directory = cap.evidence / (name + "-frames")
            directory.mkdir()
            count = saved = 0
            try:
                while True:
                    ok, frame = video.read()
                    if not ok: break
                    if count % 2 == 0:
                        require(cv2.imwrite(str(directory / f"frame-{count:04d}.png"), frame), "Could not save frame")
                        saved += 1
                    count += 1
            finally:
                video.release()
            require(count >= 10 and saved >= 5, "Recording has too few frames")
            return {"mode": mode, "sheet": sheet, "fps": fps, "frame_count": count,
                    "sample_every_frames": 2, "open_host_seconds": opened, "back_host_seconds": closed,
                    "timing_note": "Host offsets start at adb launch, not encoded video PTS; use frames for visual inspection."}
        finally:
            if recorder is not None:
                try: recorder.communicate(timeout=5)
                except subprocess.TimeoutExpired:
                    recorder.terminate(); recorder.communicate(timeout=5)
                recorder = None

    try:
        cap.check()
        checked = True
        original = scale()
        require(original == "null" or re.fullmatch(r"(?:\d+(?:\.\d*)?|\.\d+)", original),
                "Animator scale has an unexpected value; refusing to change it")
        cap.launch()
        smoke.tap(smoke.wait_node("Start downloading", resource="fr_start")[0])
        smoke.wait_node("Search downloads", timeout=30)
        add, host = controls()
        check_add_safe(add)
        check_host(host)
        results = []
        for mode, value in (("normal", "1.0"), ("reduced", "0.0")):
            changed = True  # Restore even if the write succeeds but verification fails.
            set_scale(value)
            for sheet in ("add", "host"):
                add, host = controls()
                if sheet == "add": check_add_safe(add)
                else: check_host(host)
                results.append(record(mode, sheet, add if sheet == "add" else host))
        (cap.evidence / "motion-capture.json").write_text(json.dumps({
            "original_animator_scale": original, "recordings": results,
            "privacy": "Fresh isolated probe; A4 offer checked absent or matching the authorized loopback fixture before and after each Add capture. The displayed offer omits its scheme.",
        }, indent=2) + "\n", encoding="utf-8")
        cap.log("Captured four motion recordings and alternating frames; inspect the transitions before drawing conclusions.")
    finally:
        errors = []
        if recorder is not None:
            try: recorder.communicate(timeout=6)
            except Exception as exc: errors.append("recording completion: " + str(exc))
        if changed and original is not None:
            try:
                if original == "null": cap.shell("settings", "delete", "global", KEY)
                else: cap.shell("settings", "put", "global", KEY, original)
                require(scale() == original, "Animator scale did not restore exactly")
                cap.log("Animator scale restored and verified exactly.")
            except Exception as exc: errors.append("animator scale restore: " + str(exc))
        for path in owned_recordings:
            try: cap.shell("rm", "-f", "--", path)
            except Exception as exc: errors.append("owned recording cleanup: " + str(exc))
        if checked:
            try: clean_dump()
            except Exception as exc: errors.append("owned UI dump cleanup: " + str(exc))
        try: cap.clean()
        except Exception as exc: errors.append("probe cleanup: " + str(exc))
        if errors: raise RuntimeError("; ".join(errors))


if __name__ == "__main__":
    main()
