"""Summarises scripts/perf-window.sh idle runs: python scripts/perf-idle-summary.py <idle-on.csv> [<idle-off.csv> ...]

Per process: CPU time over the run (and per hour), mean and max context switches per second (summed over the
process's threads), and private memory first, last and mean.
"""
import collections
import csv
import datetime
import sys

for path in sys.argv[1:]:
    rows = list(csv.DictReader(open(path, encoding='utf-8-sig')))
    print(f'== {path}')
    by = collections.defaultdict(list)
    for r in rows:
        by[(r['name'], r['pid'])].append(r)
    if not by:
        print('  no processes from this instance were running')
    for (name, pid), rs in sorted(by.items()):
        t = lambda r: datetime.datetime.fromisoformat(r['utc'])
        span = (t(rs[-1]) - t(rs[0])).total_seconds()
        cpu = (int(rs[-1]['cpuMs']) - int(rs[0]['cpuMs'])) / 1000
        sw = [float(r['switchesPerSec']) for r in rs]
        mb = [int(r['privateBytes']) / 1048576 for r in rs]
        per_hour = cpu / span * 3600 if span > 0 else float('nan')
        print(f'  {name} pid {pid}: {len(rs)} samples over {span / 60:.1f} min')
        print(f'    CPU {cpu:.2f} s ({per_hour:.1f} s per hour); context switches/s mean {sum(sw) / len(sw):.1f} max {max(sw):.1f}')
        print(f'    private MB first {mb[0]:.1f} last {mb[-1]:.1f} mean {sum(mb) / len(mb):.1f}; '
              f'handles {rs[0]["handles"]} -> {rs[-1]["handles"]}, threads {rs[0]["threads"]} -> {rs[-1]["threads"]}')
