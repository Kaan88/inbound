import json, sys
def row(path, label, t0, t1, sysbusy, win, dlticks, mediaticks):
    try:
        j = json.load(open(path))
    except Exception as e:
        return f"{label}\tNO RESULT ({e})"
    if "fatal" in j:
        return f"{label}\tFATAL {j['fatal'][:300]!r}"
    g = lambda k, d="": j.get(k, d)
    bytes_ = g("bytes", 0) or 0
    gb = bytes_ / 1e9 if bytes_ else 0
    sysc = (int(sysbusy) / 100.0) / gb if gb else 0
    if gb and "cpuUserPerGB" not in j and "cpuUserSeconds" in j:   # the engine path (dest=app) reports seconds, not per GB
        j["cpuUserPerGB"] = round(j["cpuUserSeconds"] / gb, 2); j["cpuKernelPerGB"] = round(j["cpuKernelSeconds"] / gb, 2)
    h = g("readSizeHistogram", {})
    th = ";".join(f"{t['name']}:{t['user']}u/{t['kernel']}k" for t in g("topThreads", [])[:4])
    extra = ""
    if g("impl") == "sysdm" or "ownProcessCpuSeconds" in j:
        extra = f" dlproc={int(dlticks)/100:.1f}s mediaproc={int(mediaticks)/100:.1f}s own={g('ownProcessCpuSeconds')}"
    return "\t".join(str(x) for x in [
        label, g("dest"), g("impl"), g("segments"), f"mbps={g('mbPerSecond')}", f"user/GB={g('cpuUserPerGB')}", f"kern/GB={g('cpuKernelPerGB')}",
        f"cpu/GB={g('cpuSecondsPerGB')}", f"sys/GB={sysc:.1f}", f"reads={g('reads')}", f"B/read={g('bytesPerRead')}", f"vcsw={g('voluntaryCtxSwitches')}",
        f"temp={t0}->{t1}", f"ok={g('success')}", f"hist={json.dumps(h)}", f"threads={th}" + extra])
if sys.argv[1] == "row":
    print(row(*sys.argv[2:]))
