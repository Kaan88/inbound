"""UI-only paired Remote Files download proof, called by an owned fixture runner.

run(smoke, *, host_name, host_origin, webdav_url, owned_root, destination,
    capabilities, get_count) -> dict

`smoke` is the existing smoke-remote-files-ui.Smoke on a fresh probe with the
explicit TLS-paired Host already selected. `get_count(name)` must return the
cumulative number of GET requests for that exact WebDAV fixture filename; it
must not contain or log headers or credentials. The caller owns all fixture
lifetimes, ADB mappings, pairing, Server process, file hashing and cleanup.
"""
import re
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import urlsplit

FILE = "session-0412.flac"
LOCATION = "C4 fixture"


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def run(smoke, *, host_name, host_origin, webdav_url, owned_root, destination,
        capabilities, get_count):
    """Use the actual paired-Host UI through one confirmed safe download request."""
    host = urlsplit(host_origin)
    dav = urlsplit(webdav_url)
    require(host.scheme == "https" and host.hostname == "127.0.0.1" and host.port
            and not host.username and not host.password and host.path in ("", "/")
            and not host.query and not host.fragment, "Expected the owned loopback HTTPS Host origin")
    require(dav.scheme == "http" and dav.hostname == "127.0.0.1" and dav.port
            and not dav.username and not dav.password and dav.path in ("", "/")
            and not dav.query and not dav.fragment, "Expected the owned loopback WebDAV origin")
    require({"sites", "sitesSafeSingleDownload"}.issubset(set(capabilities)),
            "Selected Host does not advertise the required safe-download capabilities")
    require(re.fullmatch(r"[A-Za-z0-9 _.-]{1,48}", host_name) is not None,
            "Fixture Host name is invalid")
    root = Path(owned_root).resolve(strict=True)
    folder = Path(destination).resolve(strict=True)
    require(root.is_dir() and folder.is_dir() and folder.parent == root,
            "Destination must be an existing direct child of the owned Server root")
    require(not any(folder.iterdir()), "Owned destination must initially be empty")
    output = folder / FILE
    require(not output.exists() and not output.is_symlink(), "Exact destination already exists")
    # input text is passed through the Android shell. Reject all metacharacters;
    # single quotes protect Windows backslashes and %s represents spaces.
    destination_text = str(folder)
    require(re.fullmatch(r"[A-Za-z0-9_: /\\.\-]+", destination_text) is not None
            and "%" not in destination_text, "Destination cannot be safely entered on this device")
    evidence = smoke.evidence

    def tap(label, resource=None, timeout=30):
        node, tree = smoke.wait_node(label, timeout=timeout, resource=resource)
        require(node.get("enabled", "true") == "true" and node.get("clickable") == "true",
                "Expected an enabled action: " + label)
        smoke.tap(node)
        return tree

    def tap_description(predicate, description, timeout=30):
        node, tree = smoke.wait_content_description(predicate, description, timeout=timeout)
        require(node.get("enabled", "true") == "true" and node.get("clickable") == "true",
                "Expected an enabled action: " + description)
        smoke.tap(node)
        return tree

    def enter(label, value):
        node, _ = smoke.wait_content_description(lambda d: d == label, label)
        require(node.get("class", "").endswith("EditText") and node.get("enabled") == "true",
                "Expected an enabled editable field: " + label)
        smoke.tap(node)
        smoke.shell("input", "text", "'" + value.replace(" ", "%s") + "'")
        node, _ = smoke.wait_content_description(lambda d: d == label, label)
        require(node.get("text") == value, "Entered value did not round-trip in " + label)
        smoke.shell("input", "keyevent", "4")  # Close the input method, not the form.

    def save_state(key, tree):
        # The caller must use a fresh probe; never retain a credentials form or values.
        for node in tree.iter("node"):
            require(node.get("password") != "true", "Refusing evidence while a credentials form is visible")
        (evidence / (key + ".png")).write_bytes(smoke.adb("exec-out", "screencap", "-p", raw=True))
        ET.ElementTree(tree).write(evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)

    chip, _ = smoke.wait_content_description(
        lambda d: d == f"Downloads shown from {host_name}. Change host", "selected fixture Host")
    require(chip.get("enabled", "true") == "true", "Fixture Host chip is unavailable")
    tap("Remote Files", "nav_remote_files")
    deadline = time.monotonic() + 35
    while time.monotonic() < deadline:
        tree = smoke.tree()
        labels = {n.get("text", "") for n in tree.iter("node")}
        if "Remote Files is off on this Host." in labels:
            tap("Enable Remote Files")
            break
        if any(n.get("content-desc") == "Add remote location" for n in tree.iter("node")):
            break
        time.sleep(.3)
    else:
        raise RuntimeError("Remote Files did not become available on the selected Host")
    add, tree = smoke.wait_content_description(lambda d: d == "Add remote location", "Add remote location", timeout=45)
    require(any(n.get("text") == "Host: " + host_name for n in tree.iter("node")),
            "Remote Files did not show the expected selected Host")
    smoke.tap(add)
    smoke.wait_node("Add location")
    enter("Location name", LOCATION)
    enter("WebDAV address (https://…)", webdav_url)
    enter("Download folder on the selected Host (optional)", destination_text)
    checkbox, _ = smoke.wait_node("Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.")
    require(checkbox.get("checkable") == "true", "Expected the HTTP confirmation checkbox")
    if checkbox.get("checked") != "true":
        smoke.tap(checkbox)
    checkbox, _ = smoke.wait_node("Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.")
    require(checkbox.get("checked") == "true", "HTTP acknowledgement was not selected")
    tap("Save location")
    tap_description(lambda d: d.startswith(LOCATION + ". WebDAV ·"), "owned WebDAV location")
    for part in ("media", "recordings", "2026"):
        tap_description(lambda d, part=part: d == part + ", folder. Open", part + " fixture folder")
    tap_description(lambda d: d.startswith("Select " + FILE + ", "), "fixture file")
    _, tree = smoke.wait_content_description(lambda d: d.startswith("Selected " + FILE + ", "), "selected fixture file")
    selected = [n.get("content-desc", "") for n in tree.iter("node")
                if n.get("content-desc", "").startswith("Selected ")]
    require(len(selected) == 1 and selected[0].startswith("Selected " + FILE + ", "),
            "Expected exactly one selected fixture file")
    texts = [n.get("text", "") for n in tree.iter("node")]
    require("To " + destination_text in texts, "Footer did not show the exact selected Host destination")
    action = [n for n in tree.iter("node") if n.get("text") == "To " + host_name
              and n.get("enabled") == "true" and n.get("clickable") == "true"]
    require(len(action) == 1, "Expected the enabled targeted Host download action")
    save_state("c4-paired-selection", tree)
    before = get_count(FILE)
    require(before == 0 and not output.exists() and not output.is_symlink(), "Fixture payload was requested or destination appeared before confirmation")
    smoke.tap(action[0])

    def confirm_tree():
        _, tree = smoke.wait_node("Download file?")
        messages = "\n".join(n.get("text", "") for n in tree.iter("node"))
        require("File: " + FILE in messages and "Host: " + host_name + " (" + host_origin.rstrip("/") + ")" in messages
                and "Destination on this Host: " + destination_text in messages,
                "Confirmation did not identify the exact file, Host and destination")
        return tree

    save_state("c4-paired-confirmation", confirm_tree())
    tap("Cancel")
    smoke.wait_content_description(lambda d: d.startswith("Selected " + FILE + ", "), "selected file after Cancel")
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        require(get_count(FILE) == before and not output.exists() and not output.is_symlink(), "Cancel caused a fixture GET or Host output")
        time.sleep(.2)
    _, tree = smoke.wait_content_description(lambda d: d.startswith("Selected " + FILE + ", "), "retained selection")
    save_state("c4-paired-cancel", tree)
    tap("To " + host_name)
    confirm_tree()
    tap("Download")
    _, tree = smoke.wait_node("Transfers", timeout=35)
    save_state("c4-paired-submitted", tree)
    return {"file": FILE, "destination": destination_text, "get_count_before": before}
