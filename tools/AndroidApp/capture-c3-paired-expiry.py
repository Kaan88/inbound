"""Paired C3 expired-sign-in UI verifier using a synthetic Drive grant and owned Host."""
import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

HERE = Path(__file__).resolve().parent
PACKAGE = 'app.inbound.android.rcloneprobe'
HOST = 'Parity C3 Host'
SITE = 'Owned expired Drive'
ERROR = "Sign-in has run out. Sign in again to browse this location."


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def run(args):
    smoke_mod = load('c3_expired_smoke', HERE / 'smoke-remote-files-ui.py')
    c4 = load('c3_expired_pairing', HERE / 'capture-c4-paired-download.py')
    paired = load('c3_expired_host', HERE / 'paired-host-parity-fixture.py')
    args.apk = str(Path(args.apk).resolve(strict=True))
    smoke = smoke_mod.Smoke(args)
    smoke.preflight(include_remote_files_payload=False)
    counts = {'attempts': 0}
    lock = threading.Lock()
    class RejectProxy(BaseHTTPRequestHandler):
        def deny(self):
            with lock:
                counts['attempts'] += 1
            self.send_error(403)
        do_CONNECT = do_GET = do_POST = deny
        def log_message(self, *_): pass
    proxy = ThreadingHTTPServer(('127.0.0.1', 0), RejectProxy)
    proxy.daemon_threads = True
    thread = threading.Thread(target=proxy.serve_forever, daemon=True)
    thread.start()
    addr = f'http://127.0.0.1:{proxy.server_port}'
    names = ('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY','http_proxy','https_proxy','all_proxy','no_proxy')
    prior = {key: os.environ.get(key) for key in names}
    if not args.manual_live_auth:
        os.environ.update({key: ('127.0.0.1,localhost' if key.lower() == 'no_proxy' else addr) for key in names})
    installed = False
    reverse = None
    request = None
    errors = []
    def no_external():
        with lock:
            require(counts['attempts'] == 0, 'Unexpected provider/proxy request')
    def snapshot_empty(root):
        jobs = request('jobs.snapshot', {})
        transfers = request('sites.transfer.list', {})
        require(jobs.get('type') == 'jobs.snapshot' and (jobs.get('payload') or {}).get('jobs') == [], 'Owned Host acquired jobs')
        require(transfers.get('type') == 'sites.transfer.listResult' and (transfers.get('payload') or {}).get('transfers') == [], 'Owned Host acquired transfers')
        require(not any(root.iterdir()), 'Owned Host output appeared')
        no_external()
    def capture(name, tree):
        require(not any(n.get('password') == 'true' for n in tree.iter('node')), 'Refusing sensitive screen')
        (smoke.evidence / (name + '.png')).write_bytes(smoke.adb('exec-out', 'screencap', '-p', raw=True))
        ET.ElementTree(tree).write(smoke.evidence / (name + '.xml'), encoding='utf-8', xml_declaration=True)
    def assert_blank_sign_in(origin):
        _, dialog = smoke.wait_node('Sign in to ' + SITE)
        nodes = list(dialog.iter('node'))
        expected_destination = 'The token will be sent to ' + HOST + ' (' + origin.rstrip('/') + ')'
        require(any(expected_destination in node.get('text', '') for node in nodes),
                'Recovery dialog did not identify the exact pinned fixture Host')
        token_fields = [node for node in nodes if node.get('class', '').endswith('EditText')
                        and node.get('enabled') == 'true']
        require(len(token_fields) == 1, 'Recovery dialog did not expose exactly one editable token field')
        token_field = token_fields[0]
        require(token_field.get('password') == 'true', 'Android did not mark the token field as a password')
        require(token_field.get('text', '') in ('', 'Paste authorization token'),
                'Recovery token field did not remain empty')

    try:
        with tempfile.TemporaryDirectory(prefix='inbound-c3-expired-') as scratch:
            temp = Path(scratch).resolve()
            rclone = c4.extract_rclone(Path(args.rclone_zip).resolve(strict=True), temp)
            with paired.running_fixture(args.server_dll, name=HOST, rclone=rclone) as owned:
                root = Path(owned['download_root']).resolve(strict=True)
                request = paired.fixture_session(owned)
                reply = request('sites.setEnabled', {'enabled': True})
                require(reply.get('type') == 'sites.status' and (reply.get('payload') or {}).get('enabled') is True, 'Owned Sites did not enable')
                saved = request('sites.save', {'name': SITE, 'kind': 'drive'}).get('payload') or {}
                site = saved.get('site') or {}
                require(saved.get('ok') is True and site.get('id') and site.get('signedIn') is False, 'Fresh synthetic Drive was not created')
                site_id = site['id']
                token = json.dumps({'access_token': 'synthetic-fixture-no-account', 'token_type': 'Bearer', 'expiry': '2000-01-01T00:00:00Z'})
                signed = request('sites.signIn', {'id': site_id, 'token': token}).get('payload') or {}
                require(signed.get('ok') is True and (signed.get('site') or {}).get('signedIn') is True, 'Synthetic Drive grant not stored')
                token = None
                snapshot_empty(root)
                listener = paired.ephemeral_port()
                upstream = urlsplit(owned['base_url']).port
                with c4.tls_relay(args.node, upstream, listener, temp) as (origin, fingerprint, _tls, _opener):
                    endpoint = f'tcp:{listener}'
                    require(not any(endpoint in line.split() for line in smoke.adb('reverse','--list').splitlines()), 'Reverse port already owned')
                    smoke.adb('reverse','--no-rebind',endpoint,endpoint)
                    reverse = endpoint
                    try:
                        smoke.adb('install', args.apk); installed = smoke.installed = True
                    except subprocess.TimeoutExpired:
                        paths = smoke.shell('pm','path',PACKAGE).splitlines()
                        match = re.fullmatch(r'package:(/data/app/[A-Za-z0-9_./+=~-]+/base\.apk)', paths[0]) if len(paths) == 1 else None
                        if match and smoke.shell('sha256sum',match.group(1)).split() == [hashlib.sha256(Path(args.apk).read_bytes()).hexdigest(),match.group(1)]:
                            installed = smoke.installed = True
                        raise
                    smoke.adb('shell','am','start','-W','-n',PACKAGE + '/app.inbound.android.MainActivity'); smoke.started = True
                    smoke.tap(smoke.wait_node('Start downloading',resource='fr_start')[0])
                    smoke.wait_node('Search downloads')
                    c4.pair_probe(smoke, origin, owned['password'], fingerprint, host_name=HOST)
                    smoke.tap(smoke.wait_node('Remote Files',resource='nav_remote_files')[0])
                    card, tree = smoke.wait_content_description(lambda d: d == SITE + '. Signed in. Open location.', 'signed-in synthetic Drive', timeout=45)
                    require(not any(n.get('content-desc') == 'Sign in to ' + SITE for n in tree.iter('node')), 'Initial signed-in card unexpectedly offers sign-in')
                    snapshot_empty(root); capture('c3-cloud-signed-in',tree)
                    require(card.get('clickable') == 'true' and card.get('enabled') == 'true', 'Cloud location unavailable')
                    smoke.tap(card)
                    _, tree = smoke.wait_node(ERROR, timeout=45)
                    require(any(n.get('text') == SITE for n in tree.iter('node')), 'Browse error is not on owned location')
                    capture('c3-cloud-browse-rejected', tree)
                    snapshot_empty(root)

                    sign_in, tree = smoke.wait_node('Sign in')
                    require(sign_in.get('clickable') == 'true' and sign_in.get('enabled') == 'true'
                            and not smoke.matches(tree, 'Try again'),
                            'Expired folder did not expose its direct recovery action')
                    smoke.tap(sign_in)
                    assert_blank_sign_in(origin)
                    # Capture only the blank dialog image; never persist a token-field hierarchy.
                    (smoke.evidence / 'c3-cloud-sign-in-blank.png').write_bytes(
                        smoke.adb('exec-out', 'screencap', '-p', raw=True))
                    smoke.tap(smoke.wait_node('Cancel')[0])
                    _, tree = smoke.wait_node(ERROR)
                    require(not smoke.matches(tree, 'Sign in to ' + SITE) and len(smoke.matches(tree, 'Sign in')) == 1,
                            'Cancel did not restore the expired folder recovery')
                    capture('c3-cloud-recovery-cancel', tree)
                    snapshot_empty(root)
                    back, _ = smoke.wait_content_description(lambda d: d == 'Back', 'folder Back')
                    require(back.get('clickable') == 'true', 'Folder Back unavailable'); smoke.tap(back)
                    expired_label = SITE + '. Sign-in has run out. Sign in.'
                    card, tree = smoke.wait_content_description(lambda d: d == expired_label,
                                                                'expired Drive card Sign in action', timeout=40)
                    buttons = [n for n in tree.iter('node') if n.get('content-desc') == 'Sign in to ' + SITE
                               and n.get('enabled') == 'true']
                    require(len(buttons) == 1, 'Expired Drive did not expose its Sign in action')
                    capture('c3-cloud-expired', tree)
                    smoke.tap(card)
                    assert_blank_sign_in(origin)
                    if args.manual_live_auth:
                        # Leave the blank masked field for the operator. Do not dump the
                        # device or capture its screen while a real account is in use.
                        result_file = smoke.evidence / 'operator-result.txt'
                        require(not result_file.exists(), 'Operator result existed before handoff')
                        smoke.log('READY C3 real sign-in: the expired Drive card opened the blank token dialog on the pinned disposable Host.')
                        smoke.log(f'Pinned authorization executable: {rclone}')
                        expiry = time.monotonic() + 1200
                        while not result_file.is_file() and time.monotonic() < expiry:
                            time.sleep(1)
                        require(result_file.is_file(), 'Owner sign-in was not confirmed within 20 minutes')
                        require(result_file.read_text(encoding='utf-8').strip() == 'done', 'Owner stopped the live sign-in check')
                        proof = smoke.evidence / 'c3-real-sign-in-proof.json'
                        sanitized = {'expiredCardObserved': True, 'ownerConfirmedSignInAndBrowse': True,
                                     'providerBrowseSucceeded': None, 'storedProviderListing': False}
                        proof.write_text(json.dumps(sanitized, indent=2) + '\n', encoding='utf-8')
                        # The provider browse is intentional. Do not retain its payload,
                        # names, account identity, token or retry details.
                        check = paired.fixture_session(owned, timeout=60)
                        outcome = check('sites.browse', {'id': site_id, 'path': '/', 'limit': 1}).get('payload') or {}
                        require(outcome.get('ok') is True, 'Live provider browse did not confirm recovery')
                        require(not any(root.iterdir()), 'The sign-in check unexpectedly wrote a payload')
                        sanitized['providerBrowseSucceeded'] = True
                        proof.write_text(json.dumps(sanitized, indent=2) + '\n', encoding='utf-8')
                        smoke.log('PASS C3: the owner reauthorized the expired Drive location and the paired Host successfully browsed it.')
                        return
                    smoke.tap(smoke.wait_node('Cancel')[0])
                    _, tree = smoke.wait_content_description(lambda d: d == expired_label, 'expired card after Cancel')
                    capture('c3-cloud-expired-card-cancel', tree)
                    # Explicit Host browse recheck: no tokens or response payloads are retained.
                    outcome = request('sites.browse', {'id':site_id, 'path':'/', 'limit':100}).get('payload') or {}
                    require(outcome.get('ok') is False and outcome.get('problem') == 'signInNeeded', 'Host did not report signInNeeded')
                    listing = request('sites.list', {})
                    sites = (listing.get('payload') or {}).get('sites')
                    require(listing.get('type') == 'sites.listResult' and isinstance(sites, list) and len(sites) == 1
                            and sites[0].get('id') == site_id and sites[0].get('signedIn') is True,
                            'Cancel altered the stored fixture sign-in')
                    snapshot_empty(root)
                    (smoke.evidence / 'c3-expired-proof.json').write_text(json.dumps({'host':HOST,'location':SITE,'signedInInitially':True,'browseProblem':'signInNeeded','directRecoveryCancelled':True,'cardRecoveryCancelled':True,'proxyAttempts':0,'jobs':0,'transfers':0,'outputs':0},indent=2)+'\n',encoding='utf-8')
                    smoke.log('PASS C3: synthetic stored Drive grant rejected locally; owned paired Host shows expired state with no provider requests.')
    finally:
        pending = sys.exc_info()[0] is not None
        if installed:
            try:
                smoke.adb('uninstall',PACKAGE)
                require(f'package:{PACKAGE}' not in smoke.shell('pm','list','packages',PACKAGE), 'Probe still installed')
            except Exception: errors.append('probe uninstall')
        if reverse is not None:
            try: smoke.adb('reverse','--remove',reverse)
            except Exception: errors.append('owned reverse')
        if getattr(smoke,'validated_emulator',False):
            try: smoke.shell('rm','-f',smoke.xml_path)
            except Exception: errors.append('UI dump')
        for key, value in prior.items():
            if value is None: os.environ.pop(key,None)
            else: os.environ[key] = value
        try: proxy.shutdown(); thread.join(timeout=5); proxy.server_close()
        except Exception: errors.append('proxy')
        if errors:
            smoke.log('Cleanup incomplete: '+', '.join(errors))
            if not pending:
                raise RuntimeError('C3 cleanup incomplete')
        else:
            smoke.log('Cleanup PASS: isolated paired Host, probe, reverse and proxy disposed.')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb',default='adb'); parser.add_argument('--aapt')
    parser.add_argument('--serial',required=True); parser.add_argument('--apk',required=True)
    parser.add_argument('--server-dll',type=Path,required=True); parser.add_argument('--rclone-zip',type=Path,required=True)
    parser.add_argument('--node',default='node'); parser.add_argument('--evidence',type=Path,required=True)
    parser.add_argument('--manual-live-auth', action='store_true',
                        help='Let the owner reauthorize the disposable expired Drive and confirm with operator-result.txt')
    run(parser.parse_args())

if __name__ == '__main__': main()
