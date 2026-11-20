"""Verify A5's local FolderSheet with one empty, owned emulator child folder.

Uses the same phone-local folder browser as A6/C2. This is not an Android SAF
picker proof. The form is canceled without submitting a download.
"""
import argparse
import importlib.machinery
import json
from pathlib import Path
import uuid
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
core = importlib.machinery.SourceFileLoader("a5_local_core", str(HERE / "capture-core-parity.py")).load_module()


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def run(args):
    cap = core.Capture(args)
    smoke = cap.smoke
    name = "inbound-a5-folder-" + uuid.uuid4().hex
    path = "/sdcard/Download/" + name
    created = False

    def absent():
        return smoke.shell(f"if [ -e '{path}' ] || [ -L '{path}' ]; then echo PRESENT; else echo ABSENT; fi").strip() == "ABSENT"

    def tap(label, resource=None):
        node, _ = smoke.wait_node(label, resource=resource)
        require(node.get("enabled", "true") == "true" and node.get("clickable") == "true", "Action unavailable: " + label)
        smoke.tap(node)

    def resource(resource):
        nodes = [n for n in smoke.tree().iter("node") if n.get("resource-id", "").endswith(":id/" + resource)]
        require(len(nodes) == 1, "Expected unique resource: " + resource)
        return nodes[0]

    def picker(suffix):
        return smoke.wait_content_description(lambda d: d.startswith("Folder ") and d.endswith(suffix), "folder " + suffix)[1]

    def draft(expected):
        node, tree = smoke.wait_content_description(lambda d: d.startswith("Save to ") and d.endswith(expected),
                                                    "draft destination")
        require(node.get("resource-id", "").endswith(":id/save_to"), "Unexpected destination control")
        require(not smoke.matches(tree, "Save here"), "Folder sheet still open")
        return tree

    def capture(key, tree):
        require(not any(n.get("password") == "true" for n in tree.iter("node")), "Credentials visible")
        (smoke.evidence / (key + ".png")).write_bytes(smoke.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(tree).write(smoke.evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)

    try:
        cap.check()
        require(absent(), "Owned child path already exists")
        smoke.shell("mkdir", path)
        created = True
        require(not absent() and not smoke.shell("ls", "-A", path).strip(), "Could not create empty owned child")
        cap.launch()
        tap("Start downloading", "fr_start")
        smoke.wait_node("Search downloads")
        tap("Add", "fab")
        link = resource("entry_link")
        require(link.get("enabled") == "true" and link.get("clickable") == "true", "Link source unavailable")
        smoke.tap(link)
        smoke.wait_node("New download")
        save = resource("save_to")
        original = save.get("content-desc", "")
        require(original.startswith("Save to ") and original.endswith("/Download"), "Unexpected initial destination")
        require(save.get("enabled") == "true" and save.get("clickable") == "true", "Local folder field unavailable")
        smoke.tap(save)
        picker("/Download")
        child, _ = smoke.wait_content_description(lambda d: d == name, "owned child folder")
        require(child.get("enabled", "true") == "true" and child.get("clickable") == "true", "Child unavailable")
        smoke.tap(child)
        capture("a5-local-folder-child", picker("/" + name))
        tap("Save here")
        selected = draft("/" + name)
        capture("a5-local-folder-selected", selected)

        smoke.tap(resource("save_to"))
        picker("/" + name)
        up, _ = smoke.wait_content_description(lambda d: d == "Up one folder", "parent folder action")
        require(up.get("enabled") == "true" and up.get("clickable") == "true", "Parent navigation unavailable")
        smoke.tap(up)
        picker("/Download")
        tap("Cancel")
        returned = draft("/" + name)
        require(resource("save_to").get("content-desc") != original, "Saved selection was not retained")
        capture("a5-local-folder-cancel-return", returned)
        tap("Cancel", "new_cancel")
        _, tree = smoke.wait_node("Search downloads")
        require(any(n.get("text") == "All 0" for n in tree.iter("node")), "Unexpected download admitted")
        require(not smoke.shell("ls", "-A", path).strip(), "Owned child no longer empty")
        (smoke.evidence / "a5-local-folder-proof.json").write_text(json.dumps({
            "folder": name, "save_here_updated_draft": True, "parent_then_cancel_preserved_selection": True,
            "form_cancelled": True, "jobs": 0, "child_empty": True}, indent=2) + "\n", encoding="utf-8")
        cap.log("PASS A5 local FolderSheet: child selected; parent and Cancel preserved the draft; no download admitted")
    finally:
        try:
            cap.clean()
        finally:
            if created:
                require(not smoke.shell("ls", "-A", path).strip(), "Owned child not empty; retained")
                smoke.shell("rmdir", path)
                require(absent(), "Owned child remains")
                cap.log("Cleanup PASS: removed only the empty owned child folder")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--aapt")
    parser.add_argument("--serial", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    run(parser.parse_args())


if __name__ == "__main__":
    main()
