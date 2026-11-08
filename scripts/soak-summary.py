"""Summarises scripts/soak.sh runs: python scripts/soak-summary.py artifacts/soak/<run> [<run> ...] [--skip-minutes N]

For each run it reads samples.csv (outside view, once a minute) and gc.csv (the Host's own GC figures) and prints the
Host's and window's memory, handles, threads and CPU, and the Host's allocation rate and collections. The first
--skip-minutes (default 3) after the seeding are left out of the rates, so the seed burst does not count as steady state.
A trend line (least squares, per hour) shows growth that the first-to-last difference can hide.
"""
import csv
import datetime
import os
import sys


def parse_time(text):
    return datetime.datetime.fromisoformat(text.replace('Z', '+00:00'))


def slope_per_hour(points):
    if len(points) < 3:
        return float('nan')
    t0 = points[0][0]
    xs = [(t - t0).total_seconds() / 3600 for t, _ in points]
    ys = [v for _, v in points]
    mx, my = sum(xs) / len(xs), sum(ys) / len(ys)
    den = sum((x - mx) ** 2 for x in xs)
    return float('nan') if den == 0 else sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / den


def mb(value):
    return value / 1048576


def summarise(run, skip_minutes):
    name = os.path.basename(os.path.normpath(run))
    driver = open(os.path.join(run, 'driver.log'), encoding='utf-8').read().splitlines()
    gc_env = next((line.split('gc env:', 1)[1].strip() for line in driver if 'gc env:' in line), '')
    samples = list(csv.DictReader(open(os.path.join(run, 'samples.csv'), encoding='utf-8-sig')))
    run_rows = [r for r in samples if r['label'] == 'run']
    if not run_rows:
        print(f'{name}: no samples yet')
        return
    start = parse_time(run_rows[0]['utc']) + datetime.timedelta(minutes=skip_minutes)
    print(f'== {name}  GC env: {gc_env or "(defaults)"}')
    for proc in ('DownloadManager.Host', 'DownloadManager.Desktop.WinUI'):
        rows = [r for r in run_rows if r['name'] == proc and parse_time(r['utc']) >= start]
        pids = {r['pid'] for r in rows}
        if not rows:
            continue
        span_h = (parse_time(rows[-1]['utc']) - parse_time(rows[0]['utc'])).total_seconds() / 3600
        priv = [mb(int(r['privateBytes'])) for r in rows]
        trend = slope_per_hour([(parse_time(r['utc']), mb(int(r['privateBytes']))) for r in rows])
        cpu = (int(rows[-1]['cpuMs']) - int(rows[0]['cpuMs'])) / 1000
        print(f'  {proc} (pid {",".join(sorted(pids))}): {len(rows)} samples over {span_h * 60:.0f} min')
        print(f'    private MB first {priv[0]:.1f} last {priv[-1]:.1f} mean {sum(priv) / len(priv):.1f} max {max(priv):.1f}; '
              f'trend {trend:+.1f} MB/h')
        print(f'    handles {rows[0]["handles"]} -> {rows[-1]["handles"]} (max {max(int(r["handles"]) for r in rows)}), '
              f'threads {rows[0]["threads"]} -> {rows[-1]["threads"]} (max {max(int(r["threads"]) for r in rows)}); '
              f'CPU {cpu:.1f} s ({cpu / max(span_h * 60, 1e-9):.2f} s/min)')
    gc_path = os.path.join(run, 'gc.csv')
    if os.path.exists(gc_path):
        gc = [r for r in csv.DictReader(open(gc_path, encoding='utf-8-sig')) if parse_time(r['utc']) >= start]
        if len(gc) >= 2:
            a, b = gc[0], gc[-1]
            minutes = (parse_time(b['utc']) - parse_time(a['utc'])).total_seconds() / 60
            alloc = (int(b['totalAllocatedBytes']) - int(a['totalAllocatedBytes'])) / minutes
            heap = [mb(int(r['heapBytes'])) for r in gc]
            committed = [mb(int(r['committedBytes'])) for r in gc]
            print(f'  Host GC over {minutes:.0f} min: allocated {mb(alloc):.1f} MB/min; heap MB mean {sum(heap) / len(heap):.1f} '
                  f'max {max(heap):.1f} (trend {slope_per_hour([(parse_time(r["utc"]), mb(int(r["heapBytes"]))) for r in gc]):+.1f} MB/h); '
                  f'committed MB mean {sum(committed) / len(committed):.1f} max {max(committed):.1f}')
            print(f'    collections gen0 +{int(b["gen0"]) - int(a["gen0"])} gen1 +{int(b["gen1"]) - int(a["gen1"])} '
                  f'gen2 +{int(b["gen2"]) - int(a["gen2"])}; pause {b["pauseTimePercent"]}%; server GC {b["isServerGc"]}, '
                  f'{b["latencyMode"]}')
        seed = list(csv.DictReader(open(gc_path, encoding='utf-8-sig')))
        if seed:
            print(f'  Host allocated in total: {mb(int(seed[-1]["totalAllocatedBytes"])):.0f} MB')


def main(argv):
    skip = 3
    runs = []
    i = 0
    while i < len(argv):
        if argv[i] == '--skip-minutes':
            skip = int(argv[i + 1])
            i += 2
            continue
        runs.append(argv[i])
        i += 1
    for run in runs:
        summarise(run, skip)


if __name__ == '__main__':
    main(sys.argv[1:])
