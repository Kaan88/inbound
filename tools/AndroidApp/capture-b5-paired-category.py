"""Isolated paired Host B5 category proof. No private app data or real accounts."""
import argparse
from collections import Counter
from contextlib import ExitStack
import importlib.util
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import re
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from urllib.parse import urlsplit
import uuid

HERE = Path(__file__).resolve().parent

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

def require(ok, message):
    if not ok:
        raise RuntimeError(message)

def run(args):
    c4 = load('b5_category_c4', 'capture-c4-paired-download.py')
    states = load('b5_category_states', 'capture-download-states-parity.py')
    paired = load('b5_category_host', 'paired-host-parity-fixture.py')
    args.active_only = True
    args.details_flow = args.unattended_b5 = False
    cap = states.Capture(args)
    smoke = cap.smoke
    smoke.preflight(include_remote_files_payload=False)
    nonce = uuid.uuid4().hex[:10]
    names = [f'b5-category-{nonce}-{i}.bin' for i in range(5)]
    counts, lock = Counter(), threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def reject(self):
            with lock: counts[self.command] += 1
            self.send_response(503); self.send_header('Content-Length', '0'); self.end_headers()
        do_HEAD = reject
        do_GET = reject
        def log_message(self, *_args): pass

    def screenshot(name, tree):
        require(not any(n.get('password') == 'true' for n in tree.iter('node')),
                'Refusing evidence while a credential field is visible')
        (smoke.evidence / (name + '.png')).write_bytes(smoke.adb('exec-out', 'screencap', '-p', raw=True))
        ET.ElementTree(tree).write(smoke.evidence / (name + '.xml'), encoding='utf-8', xml_declaration=True)

    def wait(predicate, description):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            tree = smoke.tree()
            value = predicate(list(tree.iter('node')))
            if value is not None:
                return value, tree
            time.sleep(.3)
        raise RuntimeError('Expected ' + description)

    def exact_node(nodes, *, text=None, desc=None, rid=None):
        found = [n for n in nodes if (text is None or n.get('text') == text)
                 and (desc is None or n.get('content-desc') == desc)
                 and (rid is None or n.get('resource-id', '').endswith(':id/' + rid))]
        return found[0] if len(found) == 1 else None

    def menu():
        button, _ = wait(lambda ns: exact_node(ns, rid='sel_more'), 'selection overflow')
        require(button.get('enabled') == 'true' and button.get('clickable') == 'true', 'Selection menu unavailable')
        smoke.tap(button)
        option, tree = wait(lambda ns: exact_node(ns, desc='Change category'), 'Change category action')
        require(option.get('enabled') == 'true' and option.get('clickable') == 'true',
                'Change category action unavailable')
        screenshot('b5-category-menu', tree)
        smoke.tap(option)
        _, tree = wait(lambda ns: exact_node(ns, text='Change category'), 'category chooser')
        return tree

    with ExitStack() as stack:
        temp = Path(stack.enter_context(tempfile.TemporaryDirectory(prefix='inbound-b5-category-')))
        owned = stack.enter_context(paired.running_fixture(args.server_dll, name=c4.HOST_NAME))
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()

        def stop_listener():
            server.shutdown()
            worker.join(timeout=5)
            server.server_close()
            require(not worker.is_alive(), 'Fixture listener failed to stop')
        stack.callback(stop_listener)
        endpoint = f'tcp:{paired.ephemeral_port()}'
        port = int(endpoint.split(':')[1])
        upstream = urlsplit(owned['base_url']).port
        origin, fingerprint, _, _ = stack.enter_context(c4.tls_relay(args.node, upstream, port, temp))
        require(not any(endpoint in x.split() for x in smoke.adb('reverse', '--list').splitlines()), 'Reverse port already in use')
        smoke.adb('reverse', '--no-rebind', endpoint, endpoint)
        smoke.port = port; smoke.reversed = True
        try:
            cap.install_and_launch(); smoke.installed = cap.installed
            smoke.adb('shell', 'am', 'start', '-W', '-n', states.ACTIVITY); smoke.started = True
            smoke.tap(smoke.wait_node('Start downloading', resource='fr_start')[0])
            smoke.wait_node('Search downloads')
            c4.pair_probe(smoke, origin, owned['password'], fingerprint)
            request = paired.fixture_session(owned)
            ids = []
            urls = [f'http://127.0.0.1:{server.server_port}/{i}' for i in range(len(names))]
            for name, url in zip(names, urls, strict=True):
                reply = request('job.add', {'url': url, 'fileName': name,
                                            'startMode': 'later', 'connections': 1})
                payload = reply.get('payload') or {}
                job = payload.get('job') or {}
                require(reply.get('type') == 'job.addResult' and payload.get('accepted')
                        and not payload.get('duplicate') and job.get('id'), 'Owned Later job was not accepted')
                ids.append(job['id'])
            require(len(set(ids)) == 5, 'Expected five distinct fixture job IDs')
            def snapshot():
                reply = request('jobs.snapshot', {})
                jobs = (reply.get('payload') or {}).get('jobs', [])
                require(reply.get('type') == 'jobs.snapshot' and len(jobs) == 5, 'Expected five Host jobs')
                rows = {j.get('id'): j for j in jobs}
                require(set(rows) == set(ids), 'Unexpected Host job identities')
                for i, job_id in enumerate(ids):
                    j = rows[job_id]
                    require(j.get('fileName') == names[i] and j.get('url') == urls[i]
                            and str(j.get('state', '')).lower() == 'queued'
                            and str(j.get('startMode', '')).lower() == 'later' and j.get('receivedBytes') == 0,
                            'Fixture job no longer matches its deferred identity')
                    require('categoryIsAutomatic' in j, 'Host snapshot does not expose category state')
                with lock: require(not counts, 'A deferred fixture URL was requested')
                require(not any(Path(owned['download_root']).iterdir()), 'Deferred jobs produced Host output')
                return {job_id: (rows[job_id].get('categoryId'), rows[job_id].get('categoryIsAutomatic')) for job_id in ids}
            baseline = snapshot()
            require(baseline[ids[-1]] != ('video', False), 'Selected fixture must begin outside explicit Video category')
            def row(ns):
                found = [n for n in ns if n.get('clickable') == 'true' and n.get('content-desc', '').startswith(names[-1] + '. Queued')]
                return found[0] if len(found) == 1 else None
            node, _ = wait(row, 'exact queued target')
            bounds = re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', node.get('bounds', ''))
            require(bounds is not None, 'Target row has no bounds')
            l,t,r,b = map(int, bounds.groups()); x=(3*l+r)//4; y=(t+b)//2
            smoke.adb('shell', 'input', 'swipe', str(x), str(y), str(x), str(y), '900')
            def selected(nodes):
                target = row(nodes)
                return target if (target is not None and target.get('selected') == 'true'
                                  and exact_node(nodes, text='1 selected', rid='sel_title') is not None
                                  and exact_node(nodes, text='Change category') is None) else None

            _, tree = wait(selected, 'one selected fixture row')
            screenshot('b5-category-selection', tree)
            tree = menu()
            screenshot('b5-category-chooser', tree)
            smoke.tap(smoke.wait_node('Cancel')[0])
            _, tree = wait(selected, 'exact row remains selected after Cancel')
            require(snapshot() == baseline, 'Cancel changed a Host category')
            screenshot('b5-category-cancel', tree)
            menu()
            smoke.tap(smoke.wait_node('Video')[0])
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                current = snapshot()
                if current[ids[-1]] == ('video', False): break
                time.sleep(.3)
            else: raise RuntimeError('Host did not persist the selected Video category')
            require(all(current[j] == baseline[j] for j in ids[:-1]), 'An unselected category changed')
            def cleared(nodes):
                target = row(nodes)
                if (target is None or target.get('selected') != 'false'
                        or exact_node(nodes, rid='sel_title') is not None
                        or exact_node(nodes, text='Change category') is not None):
                    return None
                add = exact_node(nodes, text='Add', rid='fab')
                message = exact_node(nodes, text='1 download updated.', rid='snackbar_text')
                navigation = exact_node(nodes, rid='nav')
                if any(node is None for node in (add, message, navigation)):
                    return None

                def vertical_bounds(node):
                    coords = re.fullmatch(r'\[\d+,(\d+)\]\[\d+,(\d+)\]', node.get('bounds', ''))
                    return tuple(map(int, coords.groups())) if coords else None

                fab, feedback, nav = map(vertical_bounds, (add, message, navigation))
                if fab and feedback and nav and fab[1] < feedback[0] and feedback[1] < nav[0]:
                    return {'add_bottom': fab[1], 'feedback_top': feedback[0],
                            'feedback_bottom': feedback[1], 'navigation_top': nav[0]}
                return None

            geometry, tree = wait(cleared, 'selected job cleared and Add visible above native update feedback')
            screenshot('b5-category-confirmed', tree)
            require(snapshot() == current, 'Fixture changed after category verification')
            (smoke.evidence / 'b5-category-proof.json').write_text(json.dumps({'jobs': 5, 'selected_job': names[-1], 'selected_id': ids[-1], 'category': 'video', 'unchanged_other_categories': 4, 'heads': 0, 'gets': 0, 'feedback_bounds': geometry}, indent=2) + '\n', encoding='utf-8')
            smoke.log('PASS B5: Cancel preserved categories; one selected queued Host job changed to Video; four others unchanged; zero requests/output.')
        finally:
            if cap.installed: smoke.installed = True
            smoke.clean(include_remote_files_payload=False)

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--adb', default='adb'); p.add_argument('--aapt'); p.add_argument('--serial', required=True)
    p.add_argument('--apk', required=True); p.add_argument('--evidence', type=Path, required=True)
    p.add_argument('--server-dll', type=Path, required=True); p.add_argument('--node', default='node')
    run(p.parse_args())

if __name__ == '__main__': main()
