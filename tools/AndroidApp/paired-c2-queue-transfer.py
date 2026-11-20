"""Owned paired C2 queue-cap exercise after Android has saved the policy."""
from collections import Counter
from datetime import datetime
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import threading
import time
from urllib.request import ProxyHandler, build_opener
import uuid

SIZE = 6 * 1024 * 1024
CAP = 500000

def require(ok, message):
    if not ok: raise RuntimeError(message)

def run(capture, request, owned, queue_state):
    """Exercise exactly the saved Main policy; fixture and Host lifetimes belong to caller."""
    queues, schedules = queue_state()
    require(len(queues) == 1 and not schedules, 'Expected only fixture Main and no schedules')
    q = queues[0]
    require(q.get('name') == 'Main' and q.get('generation') and q.get('isRunning') is True
            and q.get('speedLimitBytesPerSecond') == CAP and q.get('maxConcurrentDownloads') == 2,
            'The Android-saved queue policy is not the expected active cap')
    def policy(queue):
        # Host-owned job order and quota counters can change as jobs run.
        ignored = {'jobOrder', 'quotaBytesUsed', 'quotaWindowStart'}
        return {key: value for key, value in queue.items() if key not in ignored}
    baseline = (policy(q), schedules)
    reply = request('settings.get', {})
    settings = (reply.get('payload') or {}).get('settings') or {}
    require(reply.get('type') == 'settings.result' and (reply.get('payload') or {}).get('ok') is True
            and settings.get('maxConcurrentDownloads', 0) >= 3
            and not settings.get('globalSpeedLimitBytesPerSecond'), 'Global settings would constrain this proof')
    reply = request('jobs.snapshot', {})
    root = Path(owned['download_root']).resolve(strict=True)
    require(reply.get('type') == 'jobs.snapshot' and (reply.get('payload') or {}).get('jobs') == []
            and root.is_dir() and not any(root.iterdir()), 'Expected an empty owned Host')
    nonce = uuid.uuid4().hex[:12]
    names = [f'c2-rate-{nonce}-{i}.bin' for i in range(3)]
    payloads = {f'/{i}': bytes([37 + i]) * SIZE for i in range(3)}
    payloads['/calibration'] = bytes([83]) * SIZE
    counts, lock = Counter(), threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def serve(self, body):
            data = payloads.get(self.path)
            if data is None: self.send_error(404); return
            with lock: counts[(self.path, self.command)] += 1
            self.send_response(200); self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('Content-Length', str(len(data))); self.end_headers()
            if body:
                try: self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError, OSError): pass
        def do_HEAD(self): self.serve(False)
        def do_GET(self): self.serve(True)
        def log_message(self, *_args): pass

    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True); worker.start()
    origin = f'http://127.0.0.1:{server.server_port}'
    started = time.monotonic()
    try:
        opener = build_opener(ProxyHandler({}))
        probe_start = time.monotonic()
        with opener.open(origin + '/calibration', timeout=15) as response: calibrated = response.read()
        probe_seconds = time.monotonic() - probe_start
        require(len(calibrated) == SIZE and calibrated == payloads['/calibration']
                and probe_seconds > 0 and SIZE / probe_seconds > CAP * 4,
                'Unthrottled loopback source did not exceed four times the configured cap')
        ids = []
        for i, name in enumerate(names):
            reply = request('job.add', {'url': origin + f'/{i}', 'fileName': name,
                    'queueId': q['id'], 'expectedQueueGeneration': q['generation'], 'requireQueue': True,
                    'startMode': 'queue', 'connections': 1})
            data = reply.get('payload') or {}; job = data.get('job') or {}
            require(reply.get('type') == 'job.addResult' and data.get('accepted')
                    and not data.get('duplicate') and job.get('id'), 'Queue did not admit the exact owned job')
            ids.append(job['id'])
        require(len(set(ids)) == 3, 'Expected distinct queue job IDs')
        def snapshot():
            reply = request('jobs.snapshot', {}); jobs = (reply.get('payload') or {}).get('jobs', [])
            require(reply.get('type') == 'jobs.snapshot', 'Host jobs snapshot unavailable')
            rows = {j.get('id'): j for j in jobs}
            require(len(rows) == len(jobs) == 3 and set(rows) == set(ids), 'Unexpected Host job identities')
            for i, job_id in enumerate(ids):
                j = rows[job_id]
                require(j.get('fileName') == names[i] and j.get('queueId') == q['id']
                        and j.get('isQueued') is True and j.get('totalBytes') in (None, 0, SIZE),
                        'Fixture job lost its queue identity or size')
            return rows
        def state(j): return str(j.get('state', '')).lower()
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline:
            rows = snapshot()
            active = [j for j in ids if state(rows[j]) in ('connecting', 'downloading')]
            pending = [j for j in ids if state(rows[j]) == 'queued']
            if len(active) == 2 and len(pending) == 1 and all((rows[j].get('receivedBytes') or 0) > 0 for j in active): break
            time.sleep(.25)
        else: raise RuntimeError('Did not observe two active queue jobs and one queued job')
        waiting = pending[0]; waiting_index = ids.index(waiting)
        require((rows[waiting].get('receivedBytes') or 0) == 0, 'Third job already received bytes')
        with lock: require(counts[(f'/{waiting_index}', 'GET')] == 0, 'Queued job issued a GET before a slot')
        begin = time.monotonic(); first = {j: rows[j].get('receivedBytes') or 0 for j in active}
        sample_deadline = begin + 8
        while time.monotonic() < sample_deadline:
            rows = snapshot()
            require(all(state(rows[j]) in ('connecting', 'downloading') for j in active)
                    and state(rows[waiting]) == 'queued' and (rows[waiting].get('receivedBytes') or 0) == 0,
                    'Expected two active jobs and one zero-byte queued job throughout the sample')
            with lock: require(counts[(f'/{waiting_index}', 'GET')] == 0, 'Queued job requested data before a slot')
            time.sleep(.35)
        finish = time.monotonic(); rows = snapshot()
        last = {j: rows[j].get('receivedBytes') or 0 for j in active}
        elapsed = finish - begin; total = sum(last[j] - first[j] for j in active)
        require(all(last[j] > first[j] for j in active), 'Both capped jobs must advance')
        require(0 < total <= CAP * elapsed + 1024 * 1024, 'Aggregate Host progress exceeded the cap tolerance')
        capture.log('C2 queue sample: two active jobs advanced within the cap tolerance; third remained queued at zero bytes.')
        deadline = time.monotonic() + 150
        while time.monotonic() < deadline:
            rows = snapshot()
            require(sum(state(rows[j]) in ('connecting', 'downloading', 'verifying') for j in ids) <= 2,
                    'More than two queue jobs became active')
            if all(state(rows[j]) == 'completed' for j in ids): break
            require(not any(state(rows[j]) in ('failed', 'cancelled') for j in ids), 'A queue fixture job failed')
            time.sleep(.5)
        else: raise RuntimeError('Three capped fixture jobs did not finish within 150 seconds')
        def instant(text):
            require(isinstance(text, str) and text, 'Missing Host job timestamp')
            return datetime.fromisoformat(text.replace('Z', '+00:00'))
        third_started = instant(rows[waiting].get('startedAt'))
        first_completed = min(instant(rows[j].get('completedAt')) for j in active)
        require(third_started >= first_completed, 'Third job started before an active job completed')
        hashes = {}
        for i, name in enumerate(names):
            path = root / name; expected = hashlib.sha256(payloads[f'/{i}']).hexdigest()
            require(path.is_file() and not path.is_symlink() and hashlib.sha256(path.read_bytes()).hexdigest() == expected,
                    'Completed fixture output did not match its exact hash')
            hashes[name] = expected
        require(set(root.iterdir()) == {root / name for name in names}, 'Unexpected Host output inventory')
        after_queues, after_schedules = queue_state()
        require(len(after_queues) == 1 and (policy(after_queues[0]), after_schedules) == baseline,
                'Queue configuration changed during transfer')
        with lock: require(all(counts[(f'/{i}', 'GET')] >= 1 for i in range(3)), 'Missing fixture GET')
        (capture.evidence / 'c2-paired-limits-transfer-proof.json').write_text(json.dumps({
            'configuredBytesPerSecond': CAP, 'queueConcurrency': 2, 'jobCount': 3,
            'sourceCalibrationBytes': SIZE, 'sourceCalibrationSeconds': probe_seconds,
            'sampleSeconds': elapsed, 'sampleBytes': total, 'sampleIndividualBytes': [last[j] - first[j] for j in active],
            'thirdJobZeroBeforeSlot': True, 'thirdStartedAfterCompletion': True,
            'fixtureHashes': hashes, 'totalSeconds': time.monotonic() - started}, indent=2) + '\n', encoding='utf-8')
        capture.log('PASS paired C2: the rate sample and slot wait matched the saved policy; three completed fixture hashes matched.')
    finally:
        server.shutdown(); worker.join(timeout=5); server.server_close()
        require(not worker.is_alive(), 'Owned speed fixture listener did not stop')
