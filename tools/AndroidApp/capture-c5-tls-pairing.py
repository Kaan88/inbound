"""C5 disposable TLS pairing warning and certificate pin; no real Host or credentials."""
import argparse
from contextlib import ExitStack
import hashlib
import importlib.util
from pathlib import Path
import re
import tempfile
import time
import xml.etree.ElementTree as ET
from urllib.parse import urlsplit

HERE = Path(__file__).resolve().parent
PACKAGE = 'app.inbound.android.rcloneprobe'
ACTIVITY = PACKAGE + '/app.inbound.android.MainActivity'
HOST = 'Parity C4 Host'

def require(ok, message):
    if not ok: raise RuntimeError(message)

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, HERE / path)
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m); return m

def run(args):
    c4 = load('c5_tls_pair_c4', 'capture-c4-paired-download.py')
    states = load('c5_tls_pair_states', 'capture-download-states-parity.py')
    paired = load('c5_tls_pair_fixture', 'paired-host-parity-fixture.py')
    args.active_only = True; args.details_flow = args.unattended_b5 = False
    cap = states.Capture(args); smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)

    def nodes(resource):
        return [n for n in smoke.tree().iter('node') if n.get('resource-id', '') == PACKAGE + ':id/' + resource]
    def unique(resource):
        found = nodes(resource); require(len(found) == 1, 'Expected one control: ' + resource); return found[0]
    def tap_text(label, resource=None):
        node, _ = smoke.wait_node(label, resource=resource); require(node.get('enabled') != 'false', 'Disabled control: ' + label); smoke.tap(node)
    def replace(resource, value):
        field = unique(resource); smoke.tap(field)
        smoke.shell('input', 'keycombination', 'KEYCODE_CTRL_LEFT', 'KEYCODE_A')
        if value:
            try:
                smoke.shell('input', 'text', value)
            except Exception:
                if resource == 'pair_password':
                    raise RuntimeError('Could not enter the disposable pairing value') from None
                raise
        else:
            smoke.shell('input', 'keyevent', 'KEYCODE_DEL')
        smoke.shell('input', 'keyevent', '4')
    def password_empty():
        field = unique('pair_password')
        # Android may expose the hint for an empty password; never accept bullets or a value.
        return field.get('text', '') in ('', 'Password of that Inbound')
    def safe_capture(key):
        tree = smoke.tree()
        password = [n for n in tree.iter('node') if n.get('resource-id', '') == PACKAGE + ':id/pair_password']
        require(len(password) == 1 and password[0].get('text', '') in ('', 'Password of that Inbound'),
                'Refusing evidence with a populated pairing password')
        for n in tree.iter('node'):
            if n.get('password') == 'true': n.set('text', ''); n.set('content-desc', '')
        save_artifact(key, tree)

    def save_artifact(key, tree):
        (smoke.evidence / (key + '.png')).write_bytes(smoke.adb('exec-out', 'screencap', '-p', raw=True))
        ET.ElementTree(tree).write(smoke.evidence / (key + '.xml'), encoding='utf-8', xml_declaration=True)

    def save_switcher(key, tree):
        require(not any(n.get('password') == 'true' or ':id/pair_' in n.get('resource-id', '')
                        for n in tree.iter('node')), 'Refusing evidence while pairing fields are visible')
        save_artifact(key, tree)

    def chip(name):
        node, _ = smoke.wait_content_description(lambda d: d == f'Downloads shown from {name}. Change host', 'selected Host'); smoke.tap(node)
    def open_form():
        chip('This phone'); tap_text('Connect to another Inbound', 'host_connect')
        smoke.wait_node('Connect', resource='pair_btn')
    def cert_observed(expected):
        trust, tree = smoke.wait_node('Trust this certificate for this server', resource='pair_trust_cert', timeout=30)
        certificates = [n.get('text', '') for n in tree.iter('node')
                        if n.get('resource-id', '') == PACKAGE + ':id/pair_cert']
        require(len(certificates) == 1, 'Expected one visible fixture certificate')
        certificate = certificates[0]
        warning = [n for n in tree.iter('node') if n.get('resource-id') == PACKAGE + ':id/pair_error']
        require(len(warning) == 1 and "doesn't trust the server's certificate" in warning[0].get('text', ''),
                'Missing certificate warning explanation')
        cert_node = next(n for n in tree.iter('node') if n.get('resource-id') == PACKAGE + ':id/pair_cert')
        warning_bottom = int(re.findall(r',(\d+)\]', warning[0].get('bounds', ''))[1])
        cert_top = int(re.findall(r',(\d+)\]', cert_node.get('bounds', ''))[0])
        require(warning_bottom <= cert_top, 'The warning must precede its fingerprint')
        line = re.search(r'(?im)^SHA-256:\s*([a-f0-9:\- ]+)\s*$', certificate)
        observed = re.sub(r'[:-]|\s', '', line.group(1)).lower() if line else ''
        require(observed == expected and trust.get('checked') == 'false', 'Untrusted certificate fingerprint or checkbox mismatch')
        button, _ = smoke.wait_node('Connect', resource='pair_btn')
        require(button.get('enabled') == 'false', 'Connect remained enabled before certificate trust')
    def enter_values(origin, password):
        replace('pair_address', origin); replace('pair_password', password)
        button, _ = smoke.wait_node('Connect', resource='pair_btn')
        require(button.get('enabled') == 'true', 'Fixture pairing is unavailable')
        smoke.tap(button)
    def verify_only_phone():
        chip('This phone')
        _, tree = smoke.wait_node('Where downloads run')
        rows = [n.get('text') for n in tree.iter('node')
                if n.get('resource-id') == PACKAGE + ':id/host_name']
        require(rows == ['This phone'], 'Cancel left an unexpected Host row')
        require(any(n.get('content-desc', '').startswith('This phone, ') and ', shown now' in n.get('content-desc', '')
                    for n in tree.iter('node')), 'Phone is not selected after Cancel')
        save_switcher('c5-tls-cancelled', tree)
        smoke.shell('input', 'keyevent', '4')
        smoke.wait_content_description(lambda d: d == 'Downloads shown from This phone. Change host', 'phone Host')

    with ExitStack() as stack:
        temp = Path(stack.enter_context(tempfile.TemporaryDirectory(prefix='inbound-c5-tls-')))
        owned = stack.enter_context(paired.running_fixture(args.server_dll, name=HOST))
        port = paired.ephemeral_port(); endpoint = f'tcp:{port}'
        origin, fingerprint, _, _ = stack.enter_context(c4.tls_relay(args.node, urlsplit(owned['base_url']).port, port, temp))
        require(not any(endpoint in line.split() for line in smoke.adb('reverse', '--list').splitlines()), 'Owned reverse port already in use')
        smoke.adb('reverse', '--no-rebind', endpoint, endpoint); smoke.port = port; smoke.reversed = True
        try:
            cap.install_and_launch(); smoke.installed = cap.installed
            smoke.adb('shell', 'am', 'start', '-W', '-n', ACTIVITY); smoke.started = True
            tap_text('Start downloading', 'fr_start'); smoke.wait_node('Search downloads')
            open_form(); enter_values(origin, owned['password']); cert_observed(fingerprint)
            replace('pair_password', '')
            require(password_empty(), 'Could not clear fixture password')
            safe_capture('c5-tls-warning')
            smoke.shell('input', 'keyevent', '4')
            smoke.wait_content_description(lambda d: d == 'Downloads shown from This phone. Change host', 'phone after pairing cancel')
            verify_only_phone()
            open_form(); enter_values(origin, owned['password']); cert_observed(fingerprint)
            # Check the consent without submitting, then edit the address.
            tap_text('Trust this certificate for this server', 'pair_trust_cert')
            require(unique('pair_trust_cert').get('checked') == 'true' and unique('pair_btn').get('enabled') == 'true',
                    'Checking trust did not enable Connect')
            replace('pair_address', origin + '/')
            require(not nodes('pair_cert') and not nodes('pair_trust_cert') and not nodes('pair_error'),
                    'Address edit left the prior certificate warning or consent visible')
            replace('pair_address', origin)
            button, _ = smoke.wait_node('Connect', resource='pair_btn')
            require(button.get('enabled') == 'true', 'Reconnect after address edit unavailable')
            smoke.tap(button); cert_observed(fingerprint)
            replace('pair_password', '')
            safe_capture('c5-tls-warning-retry')
            replace('pair_password', owned['password'])
            trust, _ = smoke.wait_node('Trust this certificate for this server', resource='pair_trust_cert')
            require(trust.get('checked') == 'false' and smoke.wait_node('Connect', resource='pair_btn')[0].get('enabled') == 'false',
                    'Trust gate was not restored after address edit')
            smoke.tap(trust)
            button, _ = smoke.wait_node('Connect', resource='pair_btn')
            require(button.get('enabled') == 'true', 'Connect did not enable after exact trust')
            smoke.tap(button); smoke.wait_node('Files are saved on ' + HOST, timeout=40)
            chip(HOST)
            _, tree = smoke.wait_content_description(lambda d: d.startswith(HOST + ', ') and ', shown now' in d, 'paired selected Host')
            rows = [n.get('text') for n in tree.iter('node')
                    if n.get('resource-id') == PACKAGE + ':id/host_name']
            require(rows == ['This phone', HOST], 'Pairing did not add exactly the disposable Host')
            save_switcher('c5-tls-paired', tree)
            screenshot = smoke.evidence / 'c5-tls-paired.png'
            helper = load('c5_tls_pair_comparison', 'capture-remote-files-parity.py')
            helper.make_pair(HERE.parent.parent / 'docs' / 'plans' / 'android-mockups' / 'c5-switch-host.png', screenshot,
                             smoke.evidence / 'c5-tls-paired-pair.png', 'C5 TLS paired')
            smoke.log('PASS C5: synthetic certificate warning, cancel, address invalidation, exact pin and paired selection.')
        finally:
            if cap.installed: smoke.installed = True
            smoke.clean(include_remote_files_payload=False)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--adb', default='adb'); p.add_argument('--aapt'); p.add_argument('--serial', required=True)
    p.add_argument('--apk', required=True); p.add_argument('--evidence', required=True, type=Path)
    p.add_argument('--server-dll', required=True, type=Path); p.add_argument('--node', default='node')
    run(p.parse_args())

if __name__ == '__main__': main()
