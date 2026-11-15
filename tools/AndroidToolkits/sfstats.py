#!/usr/bin/env python3
"""Frame statistics from repeated `dumpsys SurfaceFlinger --latency <layer>` samples (run-toolkits.sh sf_sample).
Used for toolkits that draw on their own surface, where `dumpsys gfxinfo` reports no frames (Avalonia, Uno with Skia).
Input: a file with '#L <layer>' header lines each followed by one latency dump (refresh period, then 'desired actual ready' rows).
Output per layer: unique frames presented, frame intervals p50/p90/p95/p99 in ms, share of intervals above 1.5 vsync periods (a late frame)."""
import sys

PENDING = 9223372036854775807
layers = {}
cur = None
for line in open(sys.argv[1], errors="ignore"):
    line = line.strip()
    if line.startswith("#L "):
        cur = line[3:]
        layers.setdefault(cur, {"period": None, "t": set()})
        continue
    if cur is None or not line:
        continue
    parts = line.split()
    if len(parts) == 1 and parts[0].isdigit():
        layers[cur]["period"] = int(parts[0])
    elif len(parts) == 3 and all(p.lstrip("-").isdigit() for p in parts):
        actual = int(parts[1])
        if 0 < actual < PENDING:
            layers[cur]["t"].add(actual)

for name, d in layers.items():
    ts = sorted(d["t"])
    period = (d["period"] or 16666666) / 1e6
    if len(ts) < 2:
        print(f"{name}: frames presented={len(ts)}")
        continue
    iv = [(b - a) / 1e6 for a, b in zip(ts, ts[1:])]
    s = sorted(iv)
    pct = lambda p: s[min(len(s) - 1, int(p / 100 * len(s)))]
    late = sum(1 for x in iv if x > 1.5 * period)
    span = (ts[-1] - ts[0]) / 1e9
    print(f"{name}: frames presented={len(ts)} over {span:.1f}s, intervals ms p50={pct(50):.1f} p90={pct(90):.1f} p95={pct(95):.1f} p99={pct(99):.1f}, late(>1.5 vsync)={late} ({100*late/len(iv):.1f}%)")
