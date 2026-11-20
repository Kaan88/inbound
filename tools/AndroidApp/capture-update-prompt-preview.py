"""Capture the isolated test-only update prompt. This is not an update/install test."""
import argparse
import hashlib
import io
import importlib.util
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
PACKAGE = 'app.inbound.android.rcloneprobe'
ACTIVITY = PACKAGE + '/app.inbound.android.MainActivity'

def require(ok, message):
    if not ok: raise RuntimeError(message)

def load_smoke():
    spec = importlib.util.spec_from_file_location('update_prompt_smoke', HERE / 'smoke-remote-files-ui.py')
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module

def run(args):
    require(re.fullmatch(r'[0-9a-fA-F]{64}', args.apk_sha256) is not None, 'Expected exact APK SHA-256')
    apk = Path(args.apk).resolve(strict=True)
    require(hashlib.sha256(apk.read_bytes()).hexdigest() == args.apk_sha256.lower(), 'Probe APK SHA-256 mismatch')
    verified = subprocess.run([args.apksigner, 'verify', str(apk)], capture_output=True, text=True, timeout=45)
    require(verified.returncode == 0, 'APK signature verification failed')
    smoke = load_smoke().Smoke(args)
    smoke.preflight(include_remote_files_payload=False)

    def capture(name, tree):
        (smoke.evidence / (name + '.png')).write_bytes(smoke.adb('exec-out', 'screencap', '-p', raw=True))
        ET.ElementTree(tree).write(smoke.evidence / (name + '.xml'), encoding='utf-8', xml_declaration=True)

    def prompt(kind):
        smoke.adb('shell', 'am', 'start', '-W', '-n', ACTIVITY, '--es', 'update_prompt_preview', kind)
        smoke.started = True
        _, tree = smoke.wait_node('Inbound 0.0.6 is ready', timeout=30)
        texts = [n.get('text', '') for n in tree.iter('node')]
        require('Version 0.0.6 · 20.6 MB' in texts and 'What’s new' in texts,
                'Preview metadata or changelog title missing')
        for label in ('Later', 'Install'):
            node, _ = smoke.wait_node(label)
            require(node.get('enabled') == 'true' and node.get('clickable') == 'true', label + ' unavailable')
        return smoke.tree()

    def settings_return(name):
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            tree = smoke.tree()
            nodes = list(tree.iter('node'))
            title = [n for n in nodes if n.get('resource-id', '').endswith(':id/settings_title') and n.get('text') == 'Settings']
            tab = [n for n in nodes if n.get('resource-id', '').endswith(':id/nav_settings') and n.get('selected') == 'true']
            if len(title) == 1 and len(tab) == 1 and not smoke.matches(tree, 'Inbound 0.0.6 is ready'):
                capture(name, tree)
                return
            time.sleep(.25)
        raise RuntimeError('Preview did not return to the selected Settings screen')

    def dismiss_later(name):
        smoke.tap(smoke.wait_node('Later')[0])
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            tree = smoke.tree()
            if not smoke.matches(tree, 'Inbound 0.0.6 is ready'):
                settings_return(name)
                return
            time.sleep(.25)
        raise RuntimeError('Later did not close the preview')

    try:
        smoke.adb('install', str(apk)); smoke.installed = True
        require(f'package:{PACKAGE}' in smoke.shell('pm', 'list', 'packages', PACKAGE), 'Probe install failed')
        smoke.adb('shell', 'am', 'start', '-W', '-n', ACTIVITY, '--ez', 'skip_first_run', 'true')
        smoke.started = True
        settings, _ = smoke.wait_node('Settings', resource='nav_settings', timeout=30)
        smoke.tap(settings)
        smoke.wait_node('Catching links', timeout=30)
        tree = prompt('short')
        require(any('A clearer update prompt' in n.get('text', '') and 'settings are kept' in n.get('text', '')
                    for n in tree.iter('node')), 'Short preview notes absent')
        capture('update-preview-short', tree); dismiss_later('update-preview-short-return')
        tree = prompt('long')
        notes = [n for n in tree.iter('node') if 'Example change 1:' in n.get('text', '')
                 and 'Example change 20:' in n.get('text', '')]
        require(len(notes) == 1, 'Long preview does not contain all 20 fixture notes')
        capture('update-preview-long-top', tree)
        top_png = (smoke.evidence / 'update-preview-long-top.png').read_bytes()
        scrolls = [n for n in tree.iter('node') if n.get('scrollable') == 'true' and any(
            'Example change 20:' in child.get('text', '') for child in n.iter('node'))]
        require(scrolls, 'Long notes scroll region missing')
        # The innermost scroll region contains the notes; no system or settings scrolling.
        target = scrolls[-1]
        match = re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', target.get('bounds', ''))
        require(match is not None, 'Notes region bounds unavailable')
        left, top, right, bottom = map(int, match.groups())
        require(bottom - top >= 40, 'Notes region is too small to scroll safely')
        x = (left + right) // 2
        for _ in range(12):
            smoke.shell('input', 'swipe', str(x), str(bottom - 8), str(x), str(top + 8), '250')
        tree = smoke.tree()
        for label in ('Later', 'Install'):
            node, _ = smoke.wait_node(label)
            require(node.get('enabled') == 'true' and node.get('clickable') == 'true', label + ' unavailable after scroll')
        capture('update-preview-long-bottom', tree)
        from PIL import Image, ImageChops
        with Image.open(io.BytesIO(top_png)) as first, Image.open(smoke.evidence / 'update-preview-long-bottom.png') as last:
            require(first.size == last.size and 0 <= left < right <= first.width and 0 <= top < bottom <= first.height,
                    'Invalid screenshot or notes scroll bounds')
            difference = ImageChops.difference(first.convert('RGB').crop((left, top, right, bottom)),
                                               last.convert('RGB').crop((left, top, right, bottom)))
            require(difference.getbbox() is not None, 'Notes scroll region did not visibly change')
        smoke.log('Long-note bottom screenshot requires visual confirmation that Example change 20 is visible; XML alone contains full text.')
        dismiss_later('update-preview-long-return')
        tree = prompt('none')
        require(any(n.get('text') == 'Release notes aren’t available for this update.' for n in tree.iter('node')),
                'No-notes fallback missing')
        capture('update-preview-no-notes', tree)
        smoke.shell('input', 'keyevent', '4')
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if not smoke.matches(smoke.tree(), 'Inbound 0.0.6 is ready'): break
            time.sleep(.25)
        else: raise RuntimeError('Back did not cancel the preview')
        settings_return('update-preview-no-notes-return')
        if args.verify_preview_install:
            prompt('short')
            smoke.tap(smoke.wait_node('Install')[0])
            # Android may omit transient Toasts from uiautomator; fail closed if not observed.
            node, tree = smoke.wait_node('Preview only; no installation started.', timeout=4)
            capture('update-preview-install-toast', tree)
            smoke.log('Preview-only Install toast observed; no installation is initiated by the test hook.')
        smoke.log('Preview-only prompt captures complete; signed update service and installer were not exercised.')
    finally:
        smoke.clean(include_remote_files_payload=False)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--adb', default='adb'); p.add_argument('--aapt')
    p.add_argument('--apksigner', required=True); p.add_argument('--apk-sha256', required=True)
    p.add_argument('--serial', required=True); p.add_argument('--apk', required=True)
    p.add_argument('--evidence', type=Path, required=True)
    p.add_argument('--verify-preview-install', action='store_true')
    run(p.parse_args())

if __name__ == '__main__': main()
