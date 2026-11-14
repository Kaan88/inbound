#!/usr/bin/env python3
"""PERF.15: samples one Linux process (and optionally a second, the Host) from /proc for a number of seconds.

  perf-proc-sample.py PID SECONDS [--host HOSTPID] [--ui "Uno Event Loop"] [--render X11RenderThread]
                                  [--every 1.0] [--series FILE.csv]

Prints one JSON object on stdout:
  cpuPct / cpuMsPerMin     the whole process, in percent of ONE core and in CPU milliseconds per minute
  ui*                      the threads whose name (/proc/PID/task/TID/comm) is --ui, summed. In Uno's X11 host the XAML work
                           runs on the thread named "Uno Event Loop" (the main thread, tid == pid, sleeps in the runtime's
                           start-up code); render* is the same for --render ("X11RenderThread", where Skia draws)
  main*                    the thread with tid == pid, for the record
  topThreads               the five busiest threads, so a wrong name shows at once
  rssMB*, threads, hostCpuPct, hostRssMB
Read-only: it only reads /proc. A process that ends before SECONDS is reported with "ended": true.
"""
import json, os, sys, time

TICK = os.sysconf("SC_CLK_TCK")


def stat_ticks(path):
    """utime + stime of a /proc/.../stat file (the comm field can hold spaces and parentheses)."""
    with open(path) as f:
        rest = f.read().rsplit(")", 1)[1].split()
    return int(rest[11]) + int(rest[12])  # fields 14 and 15 of the file; rest[0] is field 3


def rss_kb(pid):
    with open(f"/proc/{pid}/status") as f:
        for line in f:
            if line.startswith("VmRSS:"):
                return int(line.split()[1])
    return 0


def threads(pid):
    """{tid: (name, ticks)}"""
    out = {}
    try:
        for tid in os.listdir(f"/proc/{pid}/task"):
            try:
                with open(f"/proc/{pid}/task/{tid}/comm") as f:
                    name = f.read().strip()
                out[int(tid)] = (name, stat_ticks(f"/proc/{pid}/task/{tid}/stat"))
            except OSError:
                pass  # the thread ended between the listing and the read
    except OSError:
        pass
    return out


def by_name(table, name):
    return sum(t for n, t in table.values() if n == name)


def main():
    argv = sys.argv[1:]
    pid, seconds = int(argv[0]), float(argv[1])
    opts = {argv[i]: argv[i + 1] for i in range(2, len(argv) - 1, 2)}
    host = int(opts["--host"]) if "--host" in opts else None
    ui_name = opts.get("--ui", "Uno Event Loop")
    render_name = opts.get("--render", "X11RenderThread")
    every = float(opts.get("--every", "1.0"))
    series = open(opts["--series"], "w") if "--series" in opts else None
    if series:
        series.write("t_s,cpu_ticks,ui_ticks,render_ticks,rss_kb,threads\n")

    t0 = time.monotonic()
    first = threads(pid)
    cpu0 = stat_ticks(f"/proc/{pid}/stat")
    host0 = stat_ticks(f"/proc/{host}/stat") if host else 0
    rss = []
    ended = False
    cpu1, last = cpu0, first
    while time.monotonic() - t0 < seconds:
        time.sleep(max(0.0, min(every, seconds - (time.monotonic() - t0))))
        try:
            cpu1 = stat_ticks(f"/proc/{pid}/stat")
            last = threads(pid)
            rss.append(rss_kb(pid))
        except OSError:
            ended = True
            break
        if series:
            series.write(f"{time.monotonic() - t0:.1f},{cpu1},{by_name(last, ui_name)},{by_name(last, render_name)},{rss[-1]},{len(last)}\n")
    span = time.monotonic() - t0
    host_cpu = host_rss = 0
    if host:
        try:
            host_cpu = stat_ticks(f"/proc/{host}/stat") - host0
            host_rss = rss_kb(host)
        except OSError:
            pass

    def pct(ticks):
        return round(ticks / TICK / span * 100, 2)

    def ms_per_min(ticks):
        return round(ticks / TICK * 1000 / span * 60)

    per_thread = []
    for tid, (name, ticks) in last.items():
        base = first.get(tid, (name, 0))[1]  # a thread that started during the window counts from zero
        per_thread.append((ticks - base, tid, name))
    per_thread.sort(reverse=True)
    ui = by_name(last, ui_name) - by_name(first, ui_name)
    render = by_name(last, render_name) - by_name(first, render_name)
    main_t = last.get(pid, ("?", 0))[1] - first.get(pid, ("?", 0))[1]
    print(json.dumps({
        "seconds": round(span, 1),
        "ended": ended,
        "cpuPct": pct(cpu1 - cpu0),
        "cpuMsPerMin": ms_per_min(cpu1 - cpu0),
        "uiThread": ui_name,
        "uiPct": pct(ui),
        "uiMsPerMin": ms_per_min(ui),
        "renderThread": render_name,
        "renderPct": pct(render),
        "renderMsPerMin": ms_per_min(render),
        "mainThreadPct": pct(main_t),
        "topThreads": [{"name": n, "tid": t, "pct": pct(d)} for d, t, n in per_thread[:5]],
        "threads": len(last),
        "rssMBStart": round(rss[0] / 1024, 1) if rss else None,
        "rssMBEnd": round(rss[-1] / 1024, 1) if rss else None,
        "rssMBMax": round(max(rss) / 1024, 1) if rss else None,
        "hostCpuPct": pct(host_cpu) if host else None,
        "hostRssMB": round(host_rss / 1024, 1) if host else None,
    }))


if __name__ == "__main__":
    main()
