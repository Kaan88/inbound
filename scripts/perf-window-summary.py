#!/usr/bin/env python3
"""Turns the per-combination JSON lines of perf-window-linux.sh into a CSV and a Markdown summary.

  perf-window-summary.py RESULTS.jsonl OUT_PREFIX [--meta FILE]

Writes OUT_PREFIX.csv and OUT_PREFIX.md. One line of the input: {jobs, active, renderer, opens: [{...ms}], sample: {...}, activeSeen}.
"""
import csv, json, statistics, sys


def med(values):
    values = [v for v in values if v is not None]
    return round(statistics.median(values)) if values else None


def main():
    src, prefix = sys.argv[1], sys.argv[2]
    meta = open(sys.argv[4]).read() if len(sys.argv) > 4 and sys.argv[3] == "--meta" else ""
    rows = [json.loads(l) for l in open(src) if l.strip()]
    out = []
    for r in rows:
        if "error" in r:
            out.append({"jobs": r["jobs"], "active": r["active"], "renderer": r["renderer"], "error": r["error"]})
            continue
        s, o = r["sample"], r["opens"]
        row = {"jobs": r["jobs"], "active": r["active"], "renderer": r["renderer"], "opens": len(o)}
        for key in ("mainBeginMs", "activatedMs", "snapshotMs", "firstFrameMs", "mappedMs"):
            row[key + "Median"] = med([x.get(key) for x in o])
        row["firstFrameMsAll"] = " ".join(str(x.get("firstFrameMs")) for x in o)
        row.update({
            "cpuPct": s["cpuPct"], "cpuMsPerMin": s["cpuMsPerMin"], "uiPct": s["uiPct"], "uiMsPerMin": s["uiMsPerMin"],
            "renderPct": s["renderPct"], "renderMsPerMin": s["renderMsPerMin"], "mainThreadPct": s["mainThreadPct"],
            "rssMBStart": s["rssMBStart"], "rssMBEnd": s["rssMBEnd"], "rssMBMax": s["rssMBMax"], "threads": s["threads"],
            "hostCpuPct": s["hostCpuPct"], "hostRssMB": s["hostRssMB"], "activeSeen": r.get("activeSeen"),
            "seconds": s["seconds"], "windowEnded": s["ended"],
            "topThreads": "; ".join(f'{t["name"]} {t["pct"]}%' for t in s["topThreads"]),
        })
        out.append(row)
    cols = []
    for row in out:
        for k in row:
            if k not in cols:
                cols.append(k)
    with open(prefix + ".csv", "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=cols)
        w.writeheader()
        w.writerows(out)

    ok = [r for r in out if "error" not in r]
    md = ["# Linux (Uno) window baseline", "", meta.strip(), "",
          "CPU figures are percent of ONE core over the sampling window; ms/min is CPU milliseconds per minute. "
          "UI = the thread named 'Uno Event Loop' (XAML work); render = 'X11RenderThread' (Skia drawing). "
          "Read the numbers only from a quiet machine.", "",
          "## Time to open (ms from the process start; median of the opens)", "",
          "| Jobs | Active | Renderer | Opens | Runtime up (Main) | Window activated | Snapshot applied | First frame with list | X window mapped | First frame, each open |",
          "| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |"]
    for r in ok:
        md.append(f'| {r["jobs"]} | {r["active"]} | {r["renderer"]} | {r["opens"]} | {r["mainBeginMsMedian"]} | {r["activatedMsMedian"]} | {r["snapshotMsMedian"]} | {r["firstFrameMsMedian"]} | {r["mappedMsMedian"]} | {r["firstFrameMsAll"]} |')
    md += ["", "## CPU and memory of the window process while open", "",
           "| Jobs | Active | Renderer | CPU % | CPU ms/min | UI thread % | UI ms/min | Render thread % | RSS MB start / end / max | Threads | Host CPU % | Host RSS MB | Downloading |",
           "| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |"]
    for r in ok:
        md.append(f'| {r["jobs"]} | {r["active"]} | {r["renderer"]} | {r["cpuPct"]} | {r["cpuMsPerMin"]} | {r["uiPct"]} | {r["uiMsPerMin"]} | {r["renderPct"]} | {r["rssMBStart"]} / {r["rssMBEnd"]} / {r["rssMBMax"]} | {r["threads"]} | {r["hostCpuPct"]} | {r["hostRssMB"]} | {r["activeSeen"]} |')
    md += ["", "## Busiest threads (percent of one core)", ""]
    md += [f'- {r["jobs"]} jobs, {r["active"]} active, {r["renderer"]}: {r["topThreads"]}' for r in ok]
    failed = [r for r in out if "error" in r]
    if failed:
        md += ["", "## Failed", ""] + [f'- {r["jobs"]} jobs, {r["active"]} active, {r["renderer"]}: {r["error"]}' for r in failed]
    open(prefix + ".md", "w").write("\n".join(md) + "\n")


if __name__ == "__main__":
    main()
