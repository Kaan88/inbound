"""Medians of the start-up marks written by run-start.sh. Usage: summarize-start.py <outdir> <variant> <apkBytes>"""
import glob, json, os, re, statistics as st, sys

out, variant, apk = sys.argv[1], sys.argv[2], int(sys.argv[3])
# (label, from-mark, to-mark)
STEPS = [
    ("process fork -> Activity.OnCreate", None, "activity.OnCreate.enter"),
    ("OnCreate (view setup)", "activity.OnCreate.enter", "activity.contentSet"),
    ("-> Task.Run reaches HostScenario", "activity.contentSet", "scenario.begin"),
    ("-> AndroidHost.StartAsync entered", "scenario.begin", "host.begin"),
    ("StoragePaths + EnsureCreated", "host.begin", "host.paths"),
    ("secret protector (AES key file)", "host.paths", "host.protector"),
    ("construct 3 stores", "host.protector", "host.stores"),
    ("construct HttpDownloadEngine", "host.stores", "host.engine"),
    ("construct DownloadManagerService", "host.engine", "host.service.constructed"),
    ("service.InitializeAsync (total)", "host.service.constructed", "host.service.initialized"),
    ("construct HostCommandDispatcher", "host.service.initialized", "host.dispatcher.constructed"),
    ("dispatcher.Start", "host.dispatcher.constructed", "host.dispatcher.started"),
    ("-> session.open begins", "host.dispatcher.started", "session.open.begin"),
    ("hello built + enqueued", "session.open.begin", "session.hello.frame"),
    ("hello parsed (client side)", "session.hello.frame", "session.hello.parsed"),
    ("snapshot built + serialized", "session.hello.parsed", "session.snapshot.frame"),
    ("snapshot parsed (client side)", "session.snapshot.frame", "session.snapshot.parsed"),
]
INIT_SUB = ["jobStore.Initialize", "settings.Load", "jobStore.LoadAll", "jobStore.Save", "checkpoint.Load", "checkpoint.Save", "settings.Save"]


def med(v):
    return st.median(v) if v else float("nan")


def load(label):
    runs = []
    for f in sorted(glob.glob(os.path.join(out, label + "[0-9]*.json"))):
        try:
            runs.append((os.path.basename(f), json.load(open(f))))
        except Exception:
            pass
    return runs


def amstart(name):
    t = {}
    try:
        for line in open(os.path.join(out, name.replace(".json", "-amstart.txt"))):
            m = re.match(r"(TotalTime|WaitTime|ThisTime): (\d+)", line)
            if m:
                t[m.group(1)] = int(m.group(2))
    except Exception:
        pass
    return t


for label in ("empty", "jobs"):
    runs = load(label)
    if not runs:
        continue
    print("\n### variant=%s state=%s (%d cold starts, medians; ms)\n" % (variant, label, len(runs)))
    print("| step | median ms | min | max |\n|---|---:|---:|---:|")

    def col(fn):
        return [x for x in (fn(j) for _, j in runs) if x is not None]

    for name, a, b in STEPS:
        def d(j, a=a, b=b):
            m = j.get("trace", {}).get("marks", {})
            if b not in m or (a and a not in m):
                return None
            return m[b] - (m[a] if a else 0)
        v = col(d)
        if v:
            print("| %s | %.0f | %.0f | %.0f |" % (name, med(v), min(v), max(v)))
    for s in INIT_SUB:
        v = col(lambda j, s=s: j.get("trace", {}).get("totals", {}).get(s, {}).get("totalMs"))
        c = col(lambda j, s=s: j.get("trace", {}).get("totals", {}).get(s, {}).get("count"))
        if v:
            print("|   InitializeAsync part: %s (x%.0f) | %.0f | %.0f | %.0f |" % (s, med(c), med(v), min(v), max(v)))

    def rest(j):
        m = j.get("trace", {}).get("marks", {})
        t = j.get("trace", {}).get("totals", {})
        if "host.service.initialized" not in m:
            return None
        return m["host.service.initialized"] - m["host.service.constructed"] - sum(t.get(s, {}).get("totalMs", 0) for s in INIT_SUB)
    v = col(rest)
    if v:
        print("|   InitializeAsync part: everything else in it (reconcile, scan, scheduler...) | %.0f | %.0f | %.0f |" % (med(v), min(v), max(v)))
    for key, name in (("hostStartEndMs", "**host started** (hostStartEndMs)"), ("snapshotMs", "**hello + snapshot** (snapshotMs)")):
        v = col(lambda j, key=key: j.get(key))
        if v:
            print("| %s | %.0f | %.0f | %.0f |" % (name, med(v), min(v), max(v)))
    for mark in ("host.begin", "host.service.initialized", "session.snapshot.parsed"):
        m = col(lambda j, mark=mark: j.get("trace", {}).get("jit", {}).get(mark, {}).get("methods"))
        t = col(lambda j, mark=mark: j.get("trace", {}).get("jit", {}).get(mark, {}).get("ms"))
        if m:
            print("|   runtime-compiled methods by %s: %.0f (JitInfo time %.0f ms) | | | |" % (mark, med(m), med(t)))
    am = [amstart(n) for n, _ in runs]
    for k in ("TotalTime", "WaitTime"):
        v = [a[k] for a in am if k in a]
        if v:
            print("| am start -W %s | %.0f | %.0f | %.0f |" % (k, med(v), min(v), max(v)))
    jobs = col(lambda j: j.get("snapshotStatus", {}).get("total"))
    print("\njobs in snapshot: %.0f; hostVersion: %s; runtime: %s; APK %.1f MB" % (med(jobs), runs[0][1].get("hello", {}).get("hostVersion"), runs[0][1].get("runtime"), apk / 1048576))
