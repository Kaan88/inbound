"""Mean of the repeats per experiment from run-net.sh's rows.tsv. Usage: aggregate-net.py <rows.tsv>"""
import re, sys, collections
rows = collections.OrderedDict()
cur = None
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.rstrip("\n")
    if line.startswith(" ") and cur: line = cur + line.strip()
    f = line.split("\t")
    cur = line
    m = re.match(r"(.+)-r(\d+)$", f[0])
    if not m or "FATAL" in line or "NO RESULT" in line:
        if m: rows.setdefault(m.group(1), []).append({"bad": line[:120]})
        continue
    d = {}
    for x in f[1:]:
        k, _, v = x.partition("=")
        if _: d[k] = v
    if f[1] == "curl":
        t = " ".join(f[2:])
        mm = re.findall(r"(\d+)m(\d+\.\d+)s (real|user|system)", t)
        s = {k: int(a) * 60 + float(b) for a, b, k in mm}
        d = {"mbps": f"{1073.741824 / s['real']:.1f}", "user/GB": f"{s['user'] / 1.073741824:.1f}", "kern/GB": f"{s['system'] / 1.073741824:.1f}", "cpu/GB": f"{(s['user'] + s['system']) / 1.073741824:.1f}"}
    rows.setdefault(m.group(1), []).append(d)
def avg(v, k):
    xs = []
    for r in v:
        try: xs.append(float(r[k]))
        except Exception: pass
    return "%.1f" % (sum(xs) / len(xs)) if xs else "-"
print("%-26s %6s %6s %6s %7s %9s %9s %5s" % ("experiment", "MB/s", "user", "kern", "cpu/GB", "reads", "B/read", "n"))
for k, v in rows.items():
    if v and "bad" in v[0]: print("%-26s %s" % (k, v[0]["bad"])); continue
    print("%-26s %6s %6s %6s %7s %9s %9s %5d  [%s]" % (k, avg(v, "mbps"), avg(v, "user/GB"), avg(v, "kern/GB"), avg(v, "cpu/GB"), avg(v, "reads"), avg(v, "B/read"), len(v), ",".join(r.get("cpu/GB", "?") for r in v)))
