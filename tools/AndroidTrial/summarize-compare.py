"""One table over the variants of a run-start-compare.sh folder. Usage: summarize-compare.py <dir> <variant>..."""
import glob, json, os, re, statistics as st, sys

out, variants = sys.argv[1], sys.argv[2:]


def runs(v, state):
    res = []
    for f in sorted(glob.glob(os.path.join(out, v, state + "[0-9]*.json"))):
        try:
            j = json.load(open(f))
        except Exception:
            continue
        am = {}
        try:
            for line in open(f.replace(".json", "-amstart.txt")):
                m = re.match(r"(TotalTime|WaitTime): (\d+)", line)
                if m:
                    am[m.group(1)] = int(m.group(2))
        except Exception:
            pass
        res.append((j, am))
    return res


def cell(vals):
    vals = [x for x in vals if x is not None]
    if not vals:
        return "-"
    return "%.0f (%.0f-%.0f)" % (st.median(vals), min(vals), max(vals))


print("| variant | state | n | host started ms | hello+snapshot ms | am start -W TotalTime ms | methods compiled at run time |")
print("|---|---|---:|---:|---:|---:|---:|")
for v in variants:
    for state in ("empty", "jobs"):
        r = runs(v, state)
        if not r:
            continue
        jit = [j.get("trace", {}).get("jit", {}).get("session.snapshot.parsed", {}).get("methods") for j, _ in r]
        print("| %s | %s | %d | %s | %s | %s | %s |" % (
            v, state, len(r), cell([j.get("hostStartEndMs") for j, _ in r]), cell([j.get("snapshotMs") for j, _ in r]),
            cell([a.get("TotalTime") for _, a in r]), cell(jit)))
