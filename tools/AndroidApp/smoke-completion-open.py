"""Batch-download two PNGs and check their grouped Open actions on a QA AVD."""

import argparse
import hashlib
import io
import re
import subprocess
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from PIL import Image


APP = 'app.inbound.android'
PHOTOS = 'com.google.android.apps.photos'
PERMISSION = 'android.permission.POST_NOTIFICATIONS'
GROUP = APP + '.FINISHED'
COLORS = {'red': (231, 32, 55), 'blue': (28, 81, 229)}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def png(rgb):
    buf = io.BytesIO()
    Image.new('RGB', (640, 480), rgb).save(buf, 'PNG')
    return buf.getvalue()


def center(node):
    left, top, right, bottom = map(int, re.findall(r'\d+', node.get('bounds', '')))
    return (left + right) // 2, (top + bottom) // 2


def named(node, label):
    return label in (node.get('text'), node.get('content-desc'))


def id_is(node, name):
    return node.get('resource-id', '').endswith(':id/' + name)


class Smoke:
    def __init__(self, args):
        self.args = args
        nonce = uuid.uuid4().hex[:8]
        self.names = {color: f'inbound-notice-{nonce}-{color}.png' for color in COLORS}
        self.xml_path = f'/data/local/tmp/inbound-notice-{nonce}.xml'
        self.out = args.evidence.resolve()
        self.out.mkdir(parents=True, exist_ok=False)
        self.permission_changed = False
        self.started = False
        self.opened_shade = False

    def log(self, line):
        print(line, flush=True)
        with (self.out / 'result.txt').open('a', encoding='utf-8') as log:
            log.write(line + '\n')

    def adb(self, *args, raw=False):
        result = subprocess.run([self.args.adb, '-s', self.args.serial, *args],
                                capture_output=True, timeout=18, check=True)
        return result.stdout if raw else result.stdout.decode('utf-8', errors='replace')

    def shell(self, *args):
        return self.adb('shell', *map(str, args))

    def focus(self):
        return next((line.strip() for line in self.shell('dumpsys', 'window').splitlines()
                     if 'mCurrentFocus=' in line), '')

    def tree(self, evidence=None):
        self.shell('uiautomator', 'dump', self.xml_path)
        data = self.adb('exec-out', 'cat', self.xml_path, raw=True)
        if evidence:
            # Save only fixture-related evidence, not the whole notification shade.
            tree = ET.fromstring(data)
            lines = [f'{n.get("bounds")} {n.get("text")} {n.get("resource-id")}' for n in tree.iter('node')
                     if n.get('text') in (*self.names.values(), 'Open')]
            (self.out / evidence).write_text('\n'.join(lines) + '\n', encoding='utf-8')
        return ET.fromstring(data)

    def tap(self, node):
        self.shell('input', 'tap', *center(node))

    def permission(self):
        state = re.search(r'POST_NOTIFICATIONS: granted=(true|false)', self.shell('dumpsys', 'package', APP))
        require(state is not None, 'Could not query notification permission')
        return state[1] == 'true'

    def preflight(self):
        require(re.fullmatch(r'emulator-\d+', self.args.serial), 'Refusing a non-emulator serial')
        require(self.adb('emu', 'avd', 'name').splitlines()[0] == self.args.avd, 'Wrong AVD')
        require(self.shell('getprop', 'ro.build.version.sdk').strip() == '33', 'Expected API 33')
        self.validated_emulator = True
        require(APP in self.focus(), 'Start from Inbound Downloads on the dedicated QA emulator')
        tree = self.tree()
        require(any(id_is(n, 'nav_downloads') and n.get('selected') == 'true' for n in tree.iter('node'))
                and any(named(n, 'This phone') for n in tree.iter('node'))
                and any(re.fullmatch(r'All \d+', n.get('text', '')) and n.get('checked') == 'true'
                        for n in tree.iter('node')),
                'Select This phone > Downloads > All without a dialog or search')
        require(any(named(n, 'Active 0') for n in tree.iter('node')), 'Existing transfers are active')
        self.baseline = self.counts(tree)
        require(len(self.baseline) == 2, 'Could not read All/Finished baseline')
        viewer = self.shell('cmd', 'package', 'query-activities', '--brief', '-a', 'android.intent.action.VIEW',
                            '-d', f'content://{APP}.files/test.png', '-t', 'image/png')
        require('1 activities found:' in viewer and PHOTOS in viewer, 'Expected Photos as the only PNG viewer')
        self.initial_permission = self.permission()
        self.log(f'API 33 {self.args.avd}; {self.baseline}; notifications granted={self.initial_permission}')

    @staticmethod
    def counts(tree):
        return dict(m.groups() for n in tree.iter('node')
                    if (m := re.fullmatch(r'(All|Finished) (\d+)', n.get('text', ''))))

    @staticmethod
    def one(tree, test, message):
        matches = [n for n in tree.iter('node') if test(n)]
        require(len(matches) == 1, message)
        return matches[0]

    def group(self, names=None):
        names = list(self.names.values()) if names is None else names
        self.shell('cmd', 'statusbar', 'expand-notifications')
        self.opened_shade = True
        for _ in range(3):
            tree = self.tree()
            titles = [self.one(tree, lambda n: n.get('text') == title, f'Notification not found: {title}')
                      for title in names]
            if all(n.get('resource-id') == 'android:id/title' for n in titles):
                return tree
            parents = {child: node for node in tree.iter() for child in node}
            group = parents[titles[0]]
            while not all(title in list(group.iter()) for title in titles):
                group = parents[group]
            # The collapsed title list starts below the group's own expand control.
            controls = [n for n in group.iter() if id_is(n, 'expand_button') and named(n, 'Expand')
                        and center(n)[1] < min(center(title)[1] for title in titles)]
            while not controls and group in parents:
                # SystemUI can place the header beside, not inside, the title list.
                group = parents[group]
                controls = [n for n in group.iter() if id_is(n, 'expand_button') and named(n, 'Expand')
                            and center(n)[1] < min(center(title)[1] for title in titles)]
            require(len(controls) == 1, 'Could not uniquely identify the fixture group header')
            self.tap(controls[0])
        raise RuntimeError('Fixture group did not expand')

    def notice_records(self):
        dump = self.shell('dumpsys', 'notification', '--noredact')
        blocks = re.split(r'\n\s+(?=NotificationRecord\()', dump)
        return {name: block.splitlines()[0] for block in blocks
                if f'groupKey={GROUP}' in block.split('\n', 1)[0]
                for name in self.names.values() if f'android.title=String ({name})' in block}

    def dismiss_notices(self):
        remaining = self.notice_records()
        while remaining:
            tree = self.group(list(remaining))
            title = next(iter(remaining))
            node = self.one(tree, lambda n: n.get('text') == title, f'Cannot dismiss {title}')
            x, y = center(node)
            _, _, width, _ = map(int, re.findall(r'\d+', next(tree.iter('node')).get('bounds', '')))
            self.shell('input', 'swipe', x, y, width - 1, y, 250)
            deadline = time.monotonic() + 4
            while title in self.notice_records():
                require(time.monotonic() < deadline, f'Fixture notification remains: {title}')
                time.sleep(0.2)
            remaining = self.notice_records()

    def child_control(self, tree, title, test):
        node = self.one(tree, lambda n: n.get('text') == title and n.get('resource-id') == 'android:id/title',
                        f'No expanded-group child titled {title}')
        parents = {child: parent for parent in tree.iter() for child in parent}
        for _ in range(10):
            if node not in parents:
                break
            node = parents[node]
            if any(n.get('text') in self.names.values() and n.get('text') != title for n in node.iter()):
                break  # Do not take another child's action.
            controls = [n for n in node.iter() if test(n)]
            if len(controls) == 1:
                return controls[0]
        return None

    def open_and_check(self, color):
        title = self.names[color]
        tree = self.group()
        action_test = lambda n: id_is(n, 'action0') and named(n, 'Open')
        action = self.child_control(tree, title, action_test)
        if action is None:
            expand = self.child_control(tree, title, lambda n: id_is(n, 'expand_button') and named(n, 'Expand'))
            require(expand is not None, f'No expand button for {title}')
            self.tap(expand)
            tree = self.tree()
            action = self.child_control(tree, title, action_test)
        require(action is not None, f'No Open action for {title}')
        (self.out / f'{color}-action.txt').write_text(f'{title} -> {action.get("bounds")} {action.get("text")}\n', encoding='utf-8')
        self.tap(action)
        self.opened_shade = False
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            focus = self.focus()
            if PHOTOS + '.pager.HostPhotoPagerActivity' in focus:
                data = self.adb('exec-out', 'screencap', '-p', raw=True)
                with Image.open(io.BytesIO(data)) as img:
                    rgb = img.convert('RGB').getpixel((img.width // 2, img.height // 2))
                (self.out / f'{color}-photos.png').write_bytes(data)
                if rgb == COLORS[color]:
                    self.log(f'PASS {title}: Open -> Photos, center RGB {rgb}')
                    return
            time.sleep(0.3)
        raise RuntimeError(f'Photos did not render expected {color} for {title}')

    def run(self):
        self.preflight()
        payloads = {name: png(COLORS[color]) for color, name in self.names.items()}

        class Handler(BaseHTTPRequestHandler):
            def serve(self, body):
                data = payloads.get(self.path.removeprefix('/'))
                if data is None:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header('Content-Type', 'image/png')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                if body:
                    self.wfile.write(data)

            def do_GET(self):
                self.serve(True)

            def do_HEAD(self):
                self.serve(False)

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(('127.0.0.1', 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                if not self.initial_permission:
                    self.shell('pm', 'grant', APP, PERMISSION)
                    self.permission_changed = True
                self.started = True
                urls = ' '.join(f'http://10.0.2.2:{server.server_port}/{name}' for name in payloads)
                self.shell(f"am start -W -a android.intent.action.SEND -t text/plain --es android.intent.extra.TEXT '{urls}' -p {APP}")
                tree = self.tree()
                self.one(tree, lambda n: named(n, '2 links will be added'), 'Expected one two-link batch')
                address = self.one(tree, lambda n: id_is(n, 'address'), 'Expected the batch address field')
                require(set(address.get('text', '').splitlines()) == set(urls.split()), 'Wrong batch URLs')
                self.tap(self.one(tree, lambda n: id_is(n, 'new_download_btn'), 'No batch Download button'))
                paths = [f'/sdcard/Download/{name}' for name in payloads]
                deadline = time.monotonic() + 25
                while True:
                    hashes = self.shell('test -f ' + ' && test -f '.join(paths) + ' && sha256sum ' + ' '.join(paths) + ' || true')
                    if all(path in hashes for path in paths):
                        break
                    require(time.monotonic() < deadline, 'Timed out waiting for finished files')
                    time.sleep(0.5)
                for name, payload in payloads.items():
                    digest = hashlib.sha256(payload).hexdigest()
                    require(f'{digest}  /sdcard/Download/{name}' in hashes, f'Wrong SHA-256 for {name}')
                    self.log(f'PASS SHA-256 {name}: {digest}')
                records = [line.strip() for line in self.shell('dumpsys', 'notification').splitlines()
                           if 'NotificationRecord(' in line and f'groupKey={GROUP}' in line]
                require(any(' id=1006 ' in line for line in records)
                        and sum('actions=1' in line for line in records) >= 2, 'Missing completion group/children')
                children = self.notice_records()
                require(set(children) == set(payloads), 'Group records do not contain both fixture titles')
                (self.out / 'group-records.txt').write_text('\n'.join(
                    [line for line in records if ' id=1006 ' in line] +
                    [f'{name}: {line}' for name, line in children.items()]) + '\n', encoding='utf-8')
                self.group()
                self.tree('group-titles.txt')
                self.log('PASS both fixture titles under the Inbound completion group')
                for color in COLORS:
                    self.open_and_check(color)
            finally:
                server.shutdown()
                thread.join()

    def clean(self):
        notice_error = None
        try:
            if self.started:
                try:
                    self.dismiss_notices()
                except Exception as exc:
                    notice_error = exc
                if self.opened_shade or 'NotificationShade' in self.focus():
                    self.shell('cmd', 'statusbar', 'collapse')
                self.shell('am', 'start', '-W', '-n', f'{APP}/.MainActivity')
                tree = self.tree()
                pending = any(id_is(n, 'new_cancel') for n in tree.iter('node'))
                if pending:
                    self.tap(self.one(tree, lambda n: id_is(n, 'new_cancel'), 'No batch Cancel button'))
                    tree = self.tree()
                names = [name for name in self.names.values()
                         if any(id_is(n, 'name') and named(n, name) for n in tree.iter('node'))]
                for i, name in enumerate(names):
                    node = self.one(tree, lambda n: id_is(n, 'name') and named(n, name), f'Cannot clean {name}')
                    x, y = center(node)
                    if i == 0:
                        self.shell('input', 'swipe', x, y, x, y, 750)
                    else:
                        self.tap(node)
                    tree = self.tree()
                if names:
                    require(any(named(n, f'{len(names)} selected') for n in tree.iter('node')), 'Unexpected cleanup selection')
                    self.tap(self.one(tree, lambda n: named(n, 'Delete selected'), 'No selection Delete button'))
                    tree = self.tree()
                    self.tap(self.one(tree, lambda n: n.get('text', '').startswith('Remove and delete'), 'No delete-files choice'))
                    self.tap(self.one(self.tree(), lambda n: named(n, 'Delete'), 'No confirmation Delete button'))
                require(self.counts(self.tree()) == self.baseline, 'Downloads baseline was not restored')
                for name in self.names.values():
                    require(self.shell(f'test ! -e /sdcard/Download/{name} && echo removed').strip() == 'removed',
                            f'Fixture file remains: {name}')
        finally:
            try:
                if self.permission_changed:
                    self.shell('pm', 'revoke', APP, PERMISSION)
                    require(not self.permission(), 'Notification permission not restored')
                    self.shell('am', 'start', '-W', '-n', f'{APP}/.MainActivity')
            finally:
                if getattr(self, 'validated_emulator', False):
                    self.shell('rm', '-f', self.xml_path)
        if notice_error:
            raise notice_error
        if self.started:
            require(APP in self.focus(), 'Inbound did not return to foreground')
            self.log(f'PASS cleanup: dismissed fixture notices, restored {self.baseline} and notification permission')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', default='adb')
    parser.add_argument('--serial', required=True)
    parser.add_argument('--avd', required=True)
    parser.add_argument('--evidence', type=Path, required=True, help='New evidence directory')
    smoke = Smoke(parser.parse_args())
    try:
        smoke.run()
    finally:
        smoke.clean()


if __name__ == '__main__':
    main()
