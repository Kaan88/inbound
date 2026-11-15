#!/usr/bin/env bash
# PERF.15 on Windows: the WinUI window's CPU (process and UI thread) and memory with N held jobs and A active downloads,
# item 3 of "What is left for Windows" in docs/plans/perf-baseline-how.md. Run from Git Bash: it starts executables.
# PowerShell runs only the read-only samplers (perf-thread-sample.ps1, soak-sample.ps1) and the window close.
#
#   scripts/perf-window-active.sh OUTDIR [JOBS...] [-- ACTIVE...]      default: 500 5000 -- 0 1 8 20
#
# Build first: `dotnet build DownloadManager.slnx -c Release` (the WinUI project builds as x64), and tools/TestServer.
# Per combination: a fresh instance (own name, own data folder under OUTDIR, own copy of the binaries), N held jobs,
# A downloads of 256 MiB at 256 KiB/s each (about 17 minutes, so they stay active), the window opened, WARMUP seconds,
# then SECONDS of sampling. "20 active" runs 16: the product's cap (maxconcurrent is clamped to 16).
# Writes OUTDIR/window-active.csv and keeps each instance's sample under OUTDIR/<name>/.
set -u
OUTDIR=$1; shift
JOBS_LIST=(); ACTIVE_LIST=()
while [ $# -gt 0 ] && [ "$1" != -- ]; do JOBS_LIST+=("$1"); shift; done
[ "${1:-}" = -- ] && shift
while [ $# -gt 0 ]; do ACTIVE_LIST+=("$1"); shift; done
[ ${#JOBS_LIST[@]} -eq 0 ] && JOBS_LIST=(500 5000)
[ ${#ACTIVE_LIST[@]} -eq 0 ] && ACTIVE_LIST=(0 1 8 20)
WARMUP=${WARMUP:-10}; SECONDS_SAMPLED=${SAMPLE_SECONDS:-60}
ACTIVE_SIZE=${ACTIVE_SIZE:-268435456}; ACTIVE_BPS=${ACTIVE_BPS:-262144}
PORT=${PERF_PORT:-5252}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$OUTDIR"; OUTDIR=$(cd "$OUTDIR" && pwd)
ps1() { local f=$1; shift; powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/scripts/$f")" "$@" | tr -d '\r'; }
log() { echo "[$(date +%H:%M:%S)] $*" >&2; }

setup() { # $1 name
  local name=$1
  INST="$OUTDIR/$name"; rm -rf "$INST"; mkdir -p "$INST/bin" "$INST/data" "$INST/downloads"
  cp -r "$ROOT/src/DownloadManager.Host/bin/Release/net10.0-windows" "$INST/bin/host"
  cp -r "$ROOT/src/DownloadManager.Desktop.WinUI/bin/x64/Release/net10.0-windows10.0.19041.0" "$INST/bin/host/desktop"
  cp -r "$ROOT/src/DownloadManager.Cli/bin/Release/net10.0" "$INST/bin/cli"
  cp -r "$ROOT/tools/TestServer/bin/Release/net10.0" "$INST/bin/testserver"
  export DOWNLOADMANAGER_INSTANCE="perf-active-$name-$$"
  export DOWNLOADMANAGER_DATA=$(cygpath -w "$INST/data")
  C="$INST/bin/cli/DownloadManager.Cli.exe"; WIN_BIN=$(cygpath -w "$INST/bin"); TRACE="$INST/data/logs/ui-trace.log"
  "$INST/bin/testserver/TestServer.exe" --port "$PORT" >/dev/null 2>&1 &
  "$INST/bin/host/DownloadManager.Host.exe" --background >/dev/null 2>&1 &
  for _ in $(seq 1 30); do sleep 1; timeout 20 "$C" host status >/dev/null 2>&1 && break; done
  timeout 20 "$C" settings set defaultdir "$(cygpath -w "$INST/downloads")" >/dev/null
}

configure() { # $1 active: room for all of them, no completion windows (as perf-seed.sh does)
  local active=$1 file="$INST/settings-in.json"
  timeout 20 "$C" settings set maxconcurrent $((active > 4 ? active + 2 : 4)) >/dev/null
  timeout 20 "$C" settings export "$(cygpath -w "$file")" >/dev/null
  sed -i 's/"showCompletionDialog": *true/"showCompletionDialog": false/' "$file"
  if [ "$active" -gt 8 ]; then
    sed -i "s/\"maxConnectionsPerHost\": *[0-9]*/\"maxConnectionsPerHost\": $((active + 2))/" "$file"
    grep -q "\"maxConnectionsPerHost\": $((active + 2))" "$file" || { log "could not raise maxConnectionsPerHost"; return 1; }
  fi
  timeout 20 "$C" settings import "$(cygpath -w "$file")" >/dev/null
}

add_jobs() { # $1 count, held for later
  local done=0 n a b
  while [ "$done" -lt "$1" ]; do
    n=$(( $1 - done )); [ "$n" -gt 1000 ] && n=1000
    printf -v a '%05d' $((done + 1)); printf -v b '%05d' $((done + n))
    timeout 300 "$C" add-batch "http://127.0.0.1:$PORT/file/job-[$a-$b].bin?size=1048576" --start later --yes >/dev/null 2>&1
    done=$((done + n))
  done
}

add_active() { # $1 count
  local i
  for i in $(seq 1 "$1"); do
    timeout 30 "$C" add "http://127.0.0.1:$PORT/slow/active-$i.bin?size=$ACTIVE_SIZE&bps=$ACTIVE_BPS&seed=$i" \
      --start now --connections 1 >/dev/null || log "add of active $i failed"
  done
}

active_seen() { timeout 30 "$C" list 2>/dev/null | grep -c '^Downloading'; }

trace_seen() { [ -f "$TRACE" ] && grep -q "$1" "$TRACE"; }

teardown() {
  timeout 20 "$C" host shutdown >/dev/null 2>&1; sleep 3
  for p in $(ps1 soak-sample.ps1 -Root "$WIN_BIN" | cut -d, -f4); do taskkill //PID "$p" //F >/dev/null 2>&1; done
}

CSV="$OUTDIR/window-active.csv"
[ -f "$CSV" ] || echo "jobs,active,activeSeen,openMs,seconds,windowCpuPct,uiThreadPct,windowPrivateMB,windowWorkingSetMB,hostCpuPct,hostPrivateMB,windowTop5" > "$CSV"
for jobs in "${JOBS_LIST[@]}"; do
  for active in "${ACTIVE_LIST[@]}"; do
    name="j$jobs-a$active"
    log "$name: setting up"
    setup "$name"
    configure "$active" || { teardown; continue; }
    add_jobs "$jobs"
    add_active "$active"
    t0=$(date +%s%3N)
    timeout 20 "$C" host open >/dev/null
    for _ in $(seq 1 600); do trace_seen "First frame with the list" && break; sleep 0.1; done
    open_ms=$(( $(date +%s%3N) - t0 ))
    sleep "$WARMUP"
    log "$name: sampling $SECONDS_SAMPLED s"
    ps1 perf-thread-sample.ps1 -Root "$WIN_BIN" -Out "$(cygpath -w "$INST/threads.csv")" -Seconds "$SECONDS_SAMPLED" >/dev/null
    seen=$(active_seen)
    python - "$INST/threads.csv" "$jobs" "$active" "$seen" "$open_ms" >> "$CSV" <<'EOF'
import csv, sys
path, jobs, active, seen, open_ms = sys.argv[1:]
rows = list(csv.DictReader(open(path, encoding='utf-8-sig')))
win = next((r for r in rows if r['name'] == 'DownloadManager.Desktop.WinUI'), None)
host = next((r for r in rows if r['name'] == 'DownloadManager.Host'), None)
g = lambda r, k: r[k] if r else ''
print(','.join([jobs, active, seen, open_ms, g(win, 'seconds'), g(win, 'cpuPct'), g(win, 'mainThreadPct'), g(win, 'privateMBEnd'),
                g(win, 'workingSetMBEnd'), g(host, 'cpuPct'), g(host, 'privateMBEnd'), '"' + g(win, 'top5') + '"']))
EOF
    tail -1 "$CSV" >&2
    teardown
  done
done
