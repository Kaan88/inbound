"""UI-only synthetic paired B3 proof; caller owns fixtures, pairing and cleanup.

run(smoke, *, host_name, host_origin, report_origin, on_consent, after_cancel, before_submit)
requires a fresh probe on the Downloads screen with an already selected HTTPS
Host. report_origin is the exact loopback HTTP fixture origin, mapped to the
same source URL on Android and the Host. on_consent() is called while the
first consent is visible and returns opaque state (Host jobs and safe request
counters); after_cancel(state) must raise unless no job was admitted and no
post-baseline authenticated report request occurred. Both must be read-only,
never log headers or values, and use bounded checks. The caller verifies the
single new Host job and final hash after return. No caller should persist
secrets, or dump failure UI while a pairing form is open.
"""
import re
import time
import xml.etree.ElementTree as ET
from urllib.parse import urlsplit


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def save_state(smoke, name, tree):
    for n in tree.iter("node"):
        require(n.get("password") != "true", "Refusing evidence from a credentials form")
        value = n.get("text", "") + " " + n.get("content-desc", "")
        require("inbound_test" not in value.lower(), "Refusing evidence containing fixture session data")
        addresses = re.findall(r"""https?://[^\s<>()"']+""", value, re.I)
        require(all(not urlsplit(address).query and not urlsplit(address).fragment for address in addresses),
                "Refusing evidence containing an address query or fragment")
    (smoke.evidence / (name + ".png")).write_bytes(smoke.adb("exec-out", "screencap", "-p", raw=True))
    ET.ElementTree(tree).write(smoke.evidence / (name + ".xml"), encoding="utf-8", xml_declaration=True)


def run(smoke, *, host_name, host_origin, report_origin, on_consent, after_cancel, before_submit,
        file_name="report-q3-2026.xlsx", paced=False):
    origin = urlsplit(report_origin)
    require(origin.scheme == "http" and origin.hostname == "127.0.0.1" and origin.port
            and not origin.username and not origin.password and origin.path in ("", "/")
            and not origin.query and not origin.fragment,
            "Expected the exact synthetic loopback report origin")
    host = urlsplit(host_origin)
    require(host.scheme == "https" and host.hostname == "127.0.0.1" and host.port
            and not host.username and not host.password and host.path in ("", "/")
            and not host.query and not host.fragment,
            "Expected the exact disposable loopback HTTPS Host")
    host_origin = host_origin.rstrip("/")
    require(re.fullmatch(r"[A-Za-z0-9 _.-]{1,48}", host_name) is not None,
            "Invalid fixture Host name")
    require(callable(on_consent) and callable(after_cancel) and callable(before_submit), "Consent checks are required")

    def tap(label, resource=None):
        node, _ = smoke.wait_node(label, resource=resource)
        require(node.get("enabled", "true") == "true", "Disabled expected action: " + label)
        smoke.tap(node)

    def web_link(label):
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            nodes = [n for n in smoke.matches(smoke.tree(), label) if n.get("clickable") == "true"]
            require(len(nodes) <= 1, "Ambiguous fixture link: " + label)
            if nodes:
                smoke.tap(nodes[0])
                return
            time.sleep(.3)
        raise RuntimeError("Fixture link not visible: " + label)

    def navigate(path):
        _, tree = smoke.wait_node("Browser options", timeout=20)
        fields = [n for n in tree.iter("node") if n.get("class") == "android.widget.EditText"
                  and n.get("package") == "app.inbound.android.rcloneprobe"
                  and n.get("text", "").startswith(report_origin.rstrip("/") + "/")
                  and not n.get("resource-id", "").endswith(":id/search_input")]
        require(len(fields) == 1, "Expected one Browser address field")
        smoke.tap(fields[0])
        smoke.shell("input", "keycombination", "KEYCODE_CTRL_LEFT", "KEYCODE_A")
        smoke.shell("input", "text", report_origin.rstrip("/") + path)
        tap("Browser options")
        tap("Go")


    smoke.wait_content_description(lambda d: d == f"Downloads shown from {host_name}. Change host",
                                   "selected synthetic Host")
    tap("Add", "fab")
    tap("More download sources", "entry_browser")
    tap("Browse and catch a download")
    smoke.wait_node("Browser options", timeout=25)
    smoke.enter("https://example.com", report_origin.rstrip("/") + "/page")
    tap("Browser options")
    tap("Go")
    web_link("Sign in")
    ready = "Authenticated archive" if paced else "Inbound link fixture"
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if smoke.matches(smoke.tree(), ready):
            break  # WebView may expose both page title and heading.
        time.sleep(.3)
    else:
        raise RuntimeError("Synthetic sign-in page did not return to its fixture")
    if not paced:
        navigate("/reports")
    link = "Authenticated archive" if paced else file_name

    def confirm_recipient():
        _, tree = smoke.wait_node("Send sign-in to this server?")
        expected = f"Send sign-in cookies for 1 selected download(s) to {host_name} ({host_origin})?"
        messages = [node.get("text", "").partition("\n")[0] for node in tree.iter("node")
                    if node.get("resource-id") == "android:id/message"]
        require(messages == [expected], "Consent did not identify the exact HTTPS Host and single file")
        return tree

    web_link(link)
    tree = confirm_recipient()
    baseline = on_consent()
    save_state(smoke, "b3-paired-consent", tree)
    tap("Cancel")
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        tree = smoke.tree()
        if not smoke.matches(tree, "Send sign-in to this server?"):
            break
        time.sleep(.2)
    else:
        raise RuntimeError("Consent dialog remained open after Cancel")
    smoke.wait_node("Browser options")
    after_cancel(baseline)
    web_link(link)
    tree = confirm_recipient()
    on_consent()  # The Host has received no report request immediately before either consent action.
    save_state(smoke, "b3-paired-send-consent", tree)
    tap("Send for these files")
    _, tree = smoke.wait_node(file_name, resource="caught_name")
    nodes = list(tree.iter("node"))
    privacy = [n.get("text", "") for n in nodes if n.get("resource-id", "").endswith(":id/caught_privacy")]
    destination = [n.get("text", "") for n in nodes if n.get("resource-id", "").endswith(":id/caught_destination")]
    require(privacy == [f"Sign-in cookies will be sent to {host_name} for this download."],
            "Expected remote sign-in wording")
    require(len(destination) == 1 and destination[0].startswith(host_name + " · ")
            and "This phone" not in destination[0], "Expected the selected Host destination")
    if paced:
        tap("More options", "caught_more")
        tap("Connections", "caught_connections")
        option, _ = smoke.wait_node("1", resource="text1")
        require(option.get("package") == "app.inbound.android.rcloneprobe"
                and option.get("enabled") == "true", "Expected an enabled spinner value of one")
        smoke.tap(option)
        detail = lambda d: d.endswith(" 1 connection")
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            tree = smoke.tree()
            if any(node.get("resource-id", "").endswith(":id/caught_detail")
                   and detail(node.get("text", "")) for node in tree.iter("node")):
                break
            time.sleep(.3)
        else:
            raise RuntimeError("The caught sheet did not show one connection")
        tap("Fewer options", "caught_more")
        _, tree = smoke.wait_node(file_name, resource="caught_name")
    save_state(smoke, "b3-paired-caught", tree)
    before_submit()
    tap("Download", "caught_submit")  # Exactly one submission; caller checks admission and hash.
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        tree = smoke.tree()
        if not any(n.get("resource-id", "").endswith(":id/caught_submit") for n in tree.iter("node")):
            return
        if any(n.get("resource-id", "").endswith(":id/caught_error") and n.get("text") for n in tree.iter("node")):
            raise RuntimeError("The probe kept the caught sheet open with a submission error")
        time.sleep(.3)
    raise RuntimeError("The probe kept the caught sheet open after the single Download tap")
