"""UI-only paired Remote Files download proof, called by an owned fixture runner.

run(smoke, *, host_name, host_origin, webdav_url, owned_root, destination,
    capabilities, get_count, verify_refresh=False, get_listing_count=None) -> dict

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
        capabilities, get_count, verify_refresh=False, get_listing_count=None, verify_transfers=False,
        verify_delete=False, delete_state=None, require_no_transfers=None, verify_location_remove=None, verify_add_validation=None, verify_s3_validation=None, verify_sftp_validation=None, verify_cloud_validation=None):
    """Use actual paired-Host UI for an owned download or location removal."""
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

    def enter(label, value, *, sensitive=False):
        node, _ = smoke.wait_content_description(lambda d: d == label, label)
        require(node.get("class", "").endswith("EditText") and node.get("enabled") == "true",
                "Expected an enabled editable field: " + label)
        smoke.tap(node)
        try:
            smoke.shell("input", "text", "'" + value.replace(" ", "%s") + "'")
        except Exception:
            if sensitive:
                raise RuntimeError("Could not enter the disposable access key") from None
            raise
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
    def capture_label(key, label, tree):
        import io
        from PIL import Image

        nodes = [n for n in tree.iter("node") if n.get("text") == label]
        require(len(nodes) == 1, "Validation message missing or ambiguous")
        match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", nodes[0].get("bounds", ""))
        require(match is not None, "Validation bounds missing")
        l,t,r,b = map(int, match.groups())
        for field in tree.iter("node"):
            if field.get("password") == "true" or field.get("class", "").endswith("EditText"):
                m = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", field.get("bounds", ""))
                if m:
                    x1,y1,x2,y2=map(int,m.groups())
                    require(r <= x1 or x2 <= l or b <= y1 or y2 <= t, "Validation crop overlaps a form field")
        with Image.open(io.BytesIO(smoke.adb("exec-out", "screencap", "-p", raw=True))) as image:
            require(0 <= l < r <= image.width and 0 <= t < b <= image.height, "Invalid validation crop")
            image.crop((l,t,r,b)).save(evidence / (key + ".png"))
        ET.ElementTree(ET.Element("validation", {"text":label})).write(evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)

    if verify_cloud_validation is not None:
        types = (("Google Drive", "drive", "Owned Drive"), ("OneDrive", "onedrive", "Owned OneDrive"), ("Dropbox", "dropbox", "Owned Dropbox"))
        expected = {}
        previous = "WebDAV"
        verify_cloud_validation(expected)
        enter("Location name", "Cancel Draft")
        tap("Cancel")
        add, _ = smoke.wait_content_description(lambda d: d == "Add remote location", "Add remote location")
        smoke.tap(add); smoke.wait_node("Add location")
        field, _ = smoke.wait_content_description(lambda d: d == "Location name", "Location name")
        require(field.get("text", "") in ("", "Location name"), "Cancelled location name survived reopening")
        verify_cloud_validation(expected)
        for index, (title, kind, name) in enumerate(types):
            if index:
                add, _ = smoke.wait_content_description(lambda d: d == "Add remote location", "Add remote location")
                smoke.tap(add); smoke.wait_node("Add location")
                if previous != "WebDAV":
                    tap("Location type: " + previous)
                    smoke.tap(smoke.wait_node("WebDAV")[0])
                    smoke.wait_node("Location type: WebDAV")
            field, _ = smoke.wait_content_description(lambda d: d == "Location name", "Location name")
            require(field.get("text", "") in ("", "Location name"), "New location form did not start with a blank name")
            enter("Location name", name)
            if index == 0:
                address = "WebDAV address (https://…)"
                enter(address, webdav_url)
                tap("Location type: WebDAV")
                smoke.tap(smoke.wait_node("WebDAV")[0])
                field, _ = smoke.wait_content_description(lambda d: d == address, address)
                require(field.get("text") == webdav_url, "Selecting the current type changed the draft address")
            tap("Location type: WebDAV")
            smoke.tap(smoke.wait_node(title)[0])
            smoke.wait_node("Location type: " + title)
            field, tree = smoke.wait_content_description(lambda d: d == "Location name", "Location name")
            require(field.get("text") == name, "Location name was not preserved across type change")
            nodes = list(tree.iter("node"))
            require(any(n.get("text") == "Save the location, then choose Sign in from Remote Files." for n in nodes), "Cloud sign-in guidance missing")
            disclosure = "When you sign in, " + host_name + " will store the authorization."
            require(any(n.get("text") == disclosure for n in nodes), "Missing disclosure for the later sign-in step")
            folder = [n for n in nodes if n.get("content-desc") == "Download folder on the selected Host (optional)"]
            require(len(folder) == 1 and folder[0].get("text") == "Host download folder (optional)",
                    "The shorter folder hint or its full accessible label is missing")
            fields = [n for n in nodes if n.get("class", "").endswith("EditText")]
            require(all(n.get("content-desc") in ("Location name", "Download folder on the selected Host (optional)") and n.get("password") != "true" for n in fields),
                    "Cloud setup unexpectedly requests credentials")
            save_state("c3-cloud-" + kind + "-form", tree)
            verify_cloud_validation(expected)
            tap("Save location")
            _, tree = smoke.wait_content_description(lambda d: d == name + ". Sign-in required. Sign in.", "unsigned cloud location", timeout=45)
            expected[name] = kind
            verify_cloud_validation(expected)
            for existing in expected:
                require(any(n.get("content-desc") == existing + ". Sign-in required. Sign in." for n in tree.iter("node")),
                        "Unsigned cloud card disappeared or changed")
            save_state("c3-cloud-" + kind + "-saved", tree)
            previous = title
        return {"cloud_validation": True}
    if verify_sftp_validation is not None:
        tap("Location type: WebDAV")
        smoke.tap(smoke.wait_node("SFTP")[0])
        smoke.wait_node("Location type: SFTP")
        enter("Location name", LOCATION)
        port, _ = smoke.wait_content_description(lambda d: d == "Port", "Port")
        require(port.get("text") == "22", "SFTP default port is not 22")
        verify_sftp_validation("rejected")
        def error(label, key):
            tap("Save location")
            _, tree = smoke.wait_node(label)
            capture_label(key, label, tree)
            verify_sftp_validation("rejected")
        def cleared(label):
            require(not any(n.get("text") == label for n in smoke.tree().iter("node")), "Corrected SFTP error remained visible")
        error("Enter an SFTP server address.", "c3-sftp-missing-server")
        enter("SFTP server name or address", "127.0.0.1")
        cleared("Enter an SFTP server address.")
        def replace_port(value):
            node, _ = smoke.wait_content_description(lambda d: d == "Port", "Port")
            old = node.get("text", "")
            require(old.isdigit(), "Expected numeric SFTP port")
            smoke.tap(node); smoke.shell("input", "keyevent", "123")
            smoke.shell("input", "keyevent", *(["67"] * len(old)))
            smoke.shell("input", "text", value)
            node, _ = smoke.wait_content_description(lambda d: d == "Port", "Port")
            require(node.get("text") == value, "SFTP port did not round-trip")
            smoke.shell("input", "keyevent", "4")
        replace_port("65536")
        error("Enter a port from 1 to 65535.", "c3-sftp-invalid-port")
        replace_port(str(dav.port))
        cleared("Enter a port from 1 to 65535.")
        error("Enter an SFTP user name.", "c3-sftp-missing-user")
        enter("SFTP user name", "fixtureuser", sensitive=True)
        cleared("Enter an SFTP user name.")
        tap("Save location")
        _, tree = smoke.wait_content_description(lambda d: d.startswith(LOCATION + ". SFTP ·"), "saved SFTP card", timeout=45)
        verify_sftp_validation("saved")
        require(not any(n.get("password") == "true" or n.get("class", "").endswith("EditText")
                        or "fixtureuser" in (n.get("text", "") + n.get("content-desc", ""))
                        for n in tree.iter("node")), "Refusing credential-bearing saved card")
        save_state("c3-sftp-saved", tree)
        return {"sftp_validation": True}
    if verify_s3_validation is not None:
        def chooser_capture(key, tree, labels):
            nodes = list(tree.iter("node"))
            for field in nodes:
                if field.get("class", "").endswith("EditText"):
                    require(field.get("text", "") in ("", field.get("content-desc", "")), "Refusing nonblank form capture")
            for label in labels:
                require(sum(n.get("text") == label for n in nodes) == 1, "Missing or ambiguous chooser option: " + label)
            (evidence / (key + ".png")).write_bytes(smoke.adb("exec-out", "screencap", "-p", raw=True))
            root = ET.Element("chooser")
            for label in labels: ET.SubElement(root, "option", {"text": label})
            ET.ElementTree(root).write(evidence / (key + ".xml"), encoding="utf-8", xml_declaration=True)
        tap("Location type: WebDAV")
        s3, tree = smoke.wait_node("S3")
        chooser_capture("c3-s3-types", tree, ("WebDAV", "Google Drive", "OneDrive", "Dropbox", "S3", "SFTP"))
        smoke.tap(s3)
        smoke.wait_node("Location type: S3")
        provider, tree = smoke.wait_content_description(lambda d: d == "S3 provider", "S3 provider")
        require(any(n.get("text") == "Amazon S3" for n in provider.iter("node")), "Default provider is not Amazon S3")
        endpoint, _ = smoke.wait_content_description(lambda d: d == "S3 endpoint (optional for Amazon S3)", "S3 endpoint")
        require(endpoint.get("text", "") in ("", endpoint.get("content-desc", "")), "Default endpoint is not blank")
        smoke.tap(provider)
        other, tree = smoke.wait_node("Other S3-compatible service")
        chooser_capture("c3-s3-providers", tree, ("Amazon S3", "Cloudflare R2", "Wasabi", "MinIO", "Other S3-compatible service"))
        smoke.tap(other)
        enter("Location name", LOCATION)
        tap("Save location")
        message = "Enter the access key ID and secret access key."
        _, tree = smoke.wait_node(message); capture_label("c3-s3-missing-keys", message, tree)
        verify_s3_validation("rejected")
        enter("Access key ID", "INBOUNDTESTKEY", sensitive=True)
        _, tree = smoke.wait_node(message)
        capture_label("c3-s3-access-id-only", message, tree)
        verify_s3_validation("rejected")
        secret, _ = smoke.wait_content_description(lambda d: d == "Secret access key", "Secret access key")
        require(secret.get("password") == "true" and secret.get("enabled") == "true", "Secret field is not masked")
        smoke.tap(secret)
        try:
            smoke.shell("input", "text", "inbound-test-secret-8813")
        except Exception:
            raise RuntimeError("Could not enter the disposable secret") from None
        smoke.shell("input", "keyevent", "4")
        require(not any(n.get("text") == "Enter the access key ID and secret access key." for n in smoke.tree().iter("node")),
                "Missing-key error did not clear after both keys were entered")
        tap("Save location")
        message = "Enter the S3 endpoint for this provider."
        _, tree = smoke.wait_node(message); capture_label("c3-s3-missing-endpoint", message, tree)
        verify_s3_validation("rejected")
        provider, _ = smoke.wait_content_description(lambda d: d == "S3 provider", "S3 provider")
        smoke.tap(provider); smoke.tap(smoke.wait_node("Amazon S3")[0])
        require(not any(n.get("text") == message for n in smoke.tree().iter("node")),
                "Endpoint error did not clear after switching to Amazon S3")
        provider, _ = smoke.wait_content_description(lambda d: d == "S3 provider", "S3 provider")
        smoke.tap(provider); smoke.tap(smoke.wait_node("Other S3-compatible service")[0])
        tap("Save location")
        _, tree = smoke.wait_node(message); capture_label("c3-s3-missing-endpoint-rechecked", message, tree)
        verify_s3_validation("rejected")
        enter("S3 endpoint (optional for Amazon S3)", webdav_url)
        warning = "Allow HTTP: not encrypted. Credentials and files can be read or changed in transit."
        checkbox, _ = smoke.wait_node(warning)
        require(checkbox.get("checked") == "false", "HTTP consent unexpectedly preselected")
        smoke.tap(checkbox)
        checkbox, _ = smoke.wait_node(warning)
        require(checkbox.get("checked") == "true", "HTTP consent not selected")
        tap("Save location")
        _, tree = smoke.wait_content_description(lambda d: d.startswith(LOCATION + ". S3 ·"), "saved S3 card", timeout=45)
        verify_s3_validation("saved")
        # No credential input or synthetic value may be present in retained card evidence.
        require(not any(n.get("password") == "true" or n.get("class", "").endswith("EditText")
                        or "INBOUNDTESTKEY" in (n.get("text", "") + n.get("content-desc", ""))
                        or "inbound-test-secret-8813" in (n.get("text", "") + n.get("content-desc", ""))
                        for n in tree.iter("node")), "Refusing credential-bearing saved card")
        save_state("c3-s3-saved", tree)
        return {"s3_validation": True}
    if verify_add_validation is not None:
        label = "WebDAV address (https://…)"
        warning = "Allow HTTP: not encrypted. Credentials and files can be read or changed in transit."
        def set_address(value):
            node, _ = smoke.wait_content_description(lambda d: d == label, label)
            require(node.get("class", "").endswith("EditText"), "Address field unavailable")
            previous = node.get("text", "")
            smoke.tap(node)
            smoke.shell("input", "keyevent", "123")
            if previous: smoke.shell("input", "keyevent", *(["67"] * len(previous)))
            smoke.shell("input", "text", "'" + value + "'")
            current, _ = smoke.wait_content_description(lambda d: d == label, label)
            require(current.get("text") == value, "Address entry did not round-trip")
            smoke.shell("input", "keyevent", "4")
        def consent(expected):
            node, _ = smoke.wait_node(warning)
            require(node.get("checkable") == "true" and node.get("checked") == str(expected).lower(),
                    "HTTP acknowledgement state mismatch")
            return node
        def rejected(message, key):
            tap("Save location")
            _, tree = smoke.wait_node(message)
            capture_label(key, message, tree)
            verify_add_validation("rejected")
        verify_add_validation("rejected")
        invalid = webdav_url + "?bad=1"
        unsupported = "ftp://127.0.0.1:" + str(dav.port) + "/"
        message = "Enter an HTTP or HTTPS address without credentials, a query, or a fragment."
        missing_name = "Enter a location name."
        rejected(missing_name, "c3-add-name-required")
        set_address(invalid)
        smoke.wait_node(missing_name)
        enter("Location name", LOCATION)
        require(not any(n.get("text") == missing_name for n in smoke.tree().iter("node")),
                "Entering a location name did not clear its validation")
        consent(False)
        rejected(message, "c3-add-invalid")
        set_address(unsupported); consent(False); rejected(message, "c3-add-unsupported")
        http_message = "HTTP is not encrypted. Tick the warning to allow it."
        set_address(webdav_url)
        consent(False)
        rejected(http_message, "c3-add-no-consent")
        smoke.tap(consent(False))
        consent(True)
        set_address(webdav_url + "different")
        consent(False)
        rejected(http_message, "c3-add-address-changed")
        set_address(webdav_url)
        consent(False)
        verify_add_validation("rejected")
        smoke.tap(consent(False)); consent(True)
        tap("Save location")
        _, tree = smoke.wait_content_description(lambda d: d.startswith(LOCATION + ". WebDAV ·"), "saved location", timeout=45)
        verify_add_validation("saved")
        save_state("c3-add-saved", tree)
        return {"add_validation": True}
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
    if verify_location_remove is not None:

        def card():
            node, tree = smoke.wait_content_description(lambda d: d.startswith(LOCATION + ". WebDAV ·"), "owned WebDAV card")
            require(node.get("long-clickable") == "true", "Owned location cannot be long pressed")
            return node, tree
        def menu(action):
            node, _ = card()
            match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
            require(match is not None, "Location card bounds unavailable")
            l,t,r,b = map(int, match.groups()); x=(l+r)//2; y=(t+b)//2
            smoke.shell("input", "swipe", str(x), str(y), str(x), str(y), "900")
            entry, tree = smoke.wait_node("Remove location", resource="title")
            # PopupMenu exposes its label while the ListView handles the tap.
            require(entry.get("enabled") == "true", "Remove menu unavailable")
            save_state("c3-remove-menu-" + action, tree)
            smoke.tap(entry)
            _, tree = smoke.wait_node("Remove location?")
            labels = [n.get("text", "") for n in tree.iter("node")]
            require("Remove " + LOCATION + " from " + host_name + "? Remote files will not be deleted." in labels, "Remove dialog identity mismatch")
            save_state("c3-remove-confirmation-" + action, tree)

        _, initial = card()
        verify_location_remove("before")
        save_state("c3-remove-initial", initial)
        menu("cancel")
        tap("Cancel")
        _, canceled = card()
        verify_location_remove("cancel")
        save_state("c3-remove-canceled", canceled)
        menu("confirm")
        tap("Remove")
        verify_location_remove("removed")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            tree = smoke.tree()
            desc = [n.get("content-desc", "") for n in tree.iter("node")]
            if not any(d.startswith(LOCATION + ". WebDAV ·") for d in desc) and any(d.startswith("C3 sibling. WebDAV ·") for d in desc):
                save_state("c3-remove-completed", tree)
                return {"location_remove": True}
            time.sleep(.3)
        raise RuntimeError("Location removal did not refresh the exact cards")
    tap_description(lambda d: d.startswith(LOCATION + ". WebDAV ·"), "owned WebDAV location")
    for part in ("media", "recordings", "2026"):
        tap_description(lambda d, part=part: d == part + ", folder. Open", part + " fixture folder")
    delete_proof = None
    if verify_delete:
        require(callable(delete_state) and callable(require_no_transfers), "Delete proof requires owned fixture and Host checks")
        require_no_transfers()
        before = delete_state()
        require(before == {"notes_attempts": 0, "other_attempts": 0, "notes_accepted": 0, "notes_deleted": False, "payload_gets": 0}
                and get_count(FILE) == 0 and not output.exists() and not output.is_symlink(),
                "Owned delete fixture is not fresh")
        tree = smoke.tree()
        descriptions = [n.get("content-desc", "") for n in tree.iter("node")]
        require(sum(d.startswith("Select notes.pdf, ") for d in descriptions) == 1
                and sum(d.startswith("Select " + FILE + ", ") for d in descriptions) == 1,
                "Expected both distinct fixture files before delete")
        tap_description(lambda d: d.startswith("Select notes.pdf, "), "owned notes file")
        _, selected_notes = smoke.wait_content_description(lambda d: d.startswith("Selected notes.pdf, "), "selected notes file")
        require(sum(n.get("content-desc", "").startswith("Selected ") for n in selected_notes.iter("node")) == 1,
                "Expected exactly notes.pdf selected")
        save_state("c4-paired-delete-selection", selected_notes)
        def delete_dialog():
            _, dialog = smoke.wait_node("Delete notes.pdf?")
            text = "\n".join(n.get("text", "") for n in dialog.iter("node"))
            require("Host: " + host_name + " (" + host_origin.rstrip("/") + ")" in text
                    and LOCATION + " · /media/recordings/2026" in text,
                    "Delete confirmation did not identify the owned Host and folder")
            return dialog

        tap_description(lambda d: d == "Delete selected remote files", "delete selection")
        save_state("c4-paired-delete-confirmation", delete_dialog())
        tap("Cancel")
        _, canceled = smoke.wait_content_description(lambda d: d.startswith("Selected notes.pdf, "), "notes retained after Cancel")
        require(delete_state() == before and get_count(FILE) == 0 and not output.exists() and not output.is_symlink(),
                "Cancel caused a fixture deletion or payload request")
        require_no_transfers()
        save_state("c4-paired-delete-canceled", canceled)
        tap_description(lambda d: d == "Delete selected remote files", "delete selection after Cancel")
        delete_dialog()
        require(delete_state() == before, "Fixture changed before confirming Delete")
        tap("Delete")
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline:
            tree = smoke.tree()
            descriptions = [n.get("content-desc", "") for n in tree.iter("node")]
            current = delete_state()
            if (current == {"notes_attempts": 1, "other_attempts": 0, "notes_accepted": 1, "notes_deleted": True, "payload_gets": 0}
                    and not any("notes.pdf" in d for d in descriptions)
                    and sum(d.startswith("Select " + FILE + ", ") for d in descriptions) == 1
                    and not any(d.startswith("Selected ") for d in descriptions)):
                break
            time.sleep(.3)
        else:
            raise RuntimeError("Confirmed delete did not remove only owned notes and refresh selection")
        require(get_count(FILE) == 0 and not output.exists() and not output.is_symlink(),
                "Delete unexpectedly requested or wrote the download fixture")
        require_no_transfers()
        save_state("c4-paired-delete-completed", tree)
        delete_proof = {"cancel_requests": 0, "confirmed_notes_requests": 1, "other_delete_requests": 0}
        smoke.log("PASS C4 paired delete: Cancel preserved notes; Confirm removed only notes and cleared selection.")
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
    if verify_transfers:
        require(get_count(FILE) == 0 and not output.exists() and not output.is_symlink(),
                "Transfers precondition changed fixture output")
        tap_description(lambda d: d == "More options", "folder options")
        transfer_item, menu = smoke.wait_node("Transfers", resource="title")
        lists = [node for node in menu.iter("node") if node.get("class") == "android.widget.ListView"
                 and any(item is transfer_item for item in node.iter("node"))
                 and any(item.get("text") == "Refresh folder" for item in node.iter("node"))]
        require(len(lists) == 1 and transfer_item.get("class") == "android.widget.TextView"
                and transfer_item.get("enabled") == "true", "Expected enabled native Transfers menu item")
        save_state("c4-paired-transfers-menu", menu)
        smoke.tap(transfer_item)
        _, empty = smoke.wait_node("No transfers on this Host.")
        labels = [n.get("text", "") for n in empty.iter("node")]
        require(labels.count("Transfers") == 1 and labels.count("Host: " + host_name + " · State as of last refresh") == 1
                and labels.count("No transfers on this Host.") == 1, "Unexpected empty Transfers screen or Host")
        save_state("c4-paired-transfers-empty", empty)
        tap("‹  Back")
        _, returned = smoke.wait_content_description(lambda d: d.startswith("Selected " + FILE + ", "), "same folder selection after Transfers")
        require(any(n.get("text") == "2026" for n in returned.iter("node")) and
                sum(n.get("content-desc", "").startswith("Selected ") for n in returned.iter("node")) == 1
                and get_count(FILE) == 0 and not output.exists() and not output.is_symlink(),
                "Transfers navigation changed folder, selection or fixture output")
        save_state("c4-paired-transfers-return", returned)
        action = [n for n in returned.iter("node") if n.get("text") == "To " + host_name
                  and n.get("enabled") == "true" and n.get("clickable") == "true"]
        require(len(action) == 1, "Missing Host download after returning from Transfers")
    refresh_proof = None
    if verify_refresh:
        require(callable(get_listing_count), "Refresh proof requires a scoped listing counter")
        before_listing = get_listing_count()
        require(before_listing > 0 and get_count(FILE) == 0
                and not output.exists() and not output.is_symlink(),
                "Refresh preconditions were not met")
        tap_description(lambda d: d == "More options", "folder options")
        refresh, menu = smoke.wait_node("Refresh folder", resource="title")
        menu_lists = [node for node in menu.iter("node") if node.get("class") == "android.widget.ListView"
                      and any(item is refresh for item in node.iter("node"))
                      and any(item.get("text") == "Transfers" for item in node.iter("node"))]
        require(len(menu_lists) == 1 and refresh.get("class") == "android.widget.TextView"
                and refresh.get("enabled") == "true", "Expected the enabled native Refresh folder menu label")
        save_state("c4-paired-refresh-menu", menu)
        # PopupMenu exposes an enabled title but keeps click handling on its ListView rows.
        smoke.tap(refresh)
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline:
            tree = smoke.tree()
            nodes = list(tree.iter("node"))
            texts = [n.get("text", "") for n in nodes]
            descriptions = [n.get("content-desc", "") for n in nodes]
            actions = [n for n in nodes if n.get("text") == "To " + host_name]
            if (get_listing_count() > before_listing and "2026" in texts
                    and sum(d.startswith("Select " + FILE + ", ") for d in descriptions) == 1
                    and not any(d.startswith("Selected ") for d in descriptions)
                    and any(t.startswith("0 files \u00b7 ") for t in texts)
                    and len(actions) == 1 and actions[0].get("enabled") == "false"):
                break
            time.sleep(.3)
        else:
            raise RuntimeError("Refresh did not reload the fixture folder and clear selection")
        require(get_count(FILE) == 0 and not output.exists() and not output.is_symlink(),
                "Refresh requested the payload or created Host output")
        save_state("c4-paired-refresh-cleared", tree)
        tap_description(lambda d: d.startswith("Select " + FILE + ", "), "fixture file after refresh")
        _, tree = smoke.wait_content_description(lambda d: d.startswith("Selected " + FILE + ", "),
                                                 "selected fixture file after refresh")
        require(any(n.get("text") == "To " + destination_text for n in tree.iter("node")),
                "Refresh changed the exact Host destination")
        require(sum(n.get("content-desc", "").startswith("Selected ") for n in tree.iter("node")) == 1,
                "Reselecting after refresh did not leave exactly one file selected")
        action = [n for n in tree.iter("node") if n.get("text") == "To " + host_name
                  and n.get("enabled") == "true" and n.get("clickable") == "true"]
        require(len(action) == 1, "Expected the refreshed enabled Host download action")
        refresh_proof = {"folder": "media/recordings/2026", "listing_before": before_listing,
                         "listing_after": get_listing_count(), "payload_gets_before_download": get_count(FILE)}
        smoke.log("PASS C4 refresh: exact folder re-listed, selection cleared, no payload GET or output.")
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
    if verify_transfers:
        deadline = time.monotonic() + 45
        completed = None
        while time.monotonic() < deadline:
            tap("Refresh transfers")
            _, candidate = smoke.wait_node("Refresh transfers", timeout=20)
            texts = [n.get("text", "") for n in candidate.iter("node")]
            descriptions = [n.get("content-desc", "") for n in candidate.iter("node")]
            combined = "\n".join(texts + descriptions)
            if ("Host: " + host_name + " · State as of last refresh" in texts and
                    "download · " + LOCATION in combined and "done · 1 copied, 0 skipped, 0 failed of 1" in combined):
                completed = candidate
                break
            time.sleep(.4)
        require(completed is not None, "Explicit refresh did not show one completed paired transfer")
        save_state("c4-paired-transfers-completed", completed)
    result = {"file": FILE, "destination": destination_text, "get_count_before": before}
    if refresh_proof is not None:
        result["refresh"] = refresh_proof
    if delete_proof is not None:
        result["delete"] = delete_proof
    return result
