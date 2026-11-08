#!/usr/bin/env bash
# PERF.5 measurements for the option "Closing the window ends it" (CloseWindowCompletely), on test instances.
# Run from Git Bash: it starts executables. PowerShell is used only for read-only helpers
# (perf-window-wait.ps1, perf-idle-sample.ps1) and for the UI Automation close in soak-hide-window.ps1.
#
#   scripts/perf-window.sh coldopen OUTDIR [REPEATS]     window open time with 0, 500 and 5,000 jobs,
#                                                        option on (a cold start) and off (showing the hidden one)
#   scripts/perf-window.sh idle OUTDIR on|off [MINUTES] [JOBS]
#                                                        open the window, close it, then sample Host and window
#
# Each instance gets its own copy of the binaries under OUTDIR, so the sampler finds its processes by path.
set -u
MODE=$1; OUTDIR=$2; PORT=${PERF_PORT:-5251}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$OUTDIR"; OUTDIR=$(cd "$OUTDIR" && pwd)
ps1() { local f=$1; shift; powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/scripts/$f")" "$@" | tr -d '\r'; }
now_ms() { date +%s%3N; }

setup() { # $1 name: copies binaries and starts a Host for a fresh instance
  local name=$1
  INST="$OUTDIR/$name"; rm -rf "$INST"; mkdir -p "$INST/bin" "$INST/data" "$INST/downloads"
  cp -r "$ROOT/src/DownloadManager.Host/bin/Release/net10.0-windows" "$INST/bin/host"
  cp -r "$ROOT/src/DownloadManager.Desktop.WinUI/bin/x64/Release/net10.0-windows10.0.19041.0" "$INST/bin/host/desktop"
  cp -r "$ROOT/src/DownloadManager.Cli/bin/Release/net10.0" "$INST/bin/cli"
  cp -r "$ROOT/tools/TestServer/bin/Release/net10.0" "$INST/bin/testserver"
  export DOWNLOADMANAGER_INSTANCE="perf-window-$name-$$"
  export DOWNLOADMANAGER_DATA=$(cygpath -w "$INST/data")
  C="$INST/bin/cli/DownloadManager.Cli.exe"; WIN_BIN=$(cygpath -w "$INST/bin"); TRACE="$INST/data/logs/ui-trace.log"
  # Adding a download asks its server for the file's name, so the jobs point at a local TestServer that answers.
  "$INST/bin/testserver/TestServer.exe" --port "$PORT" >/dev/null 2>&1 &
  "$INST/bin/host/DownloadManager.Host.exe" --background >/dev/null 2>&1 &
  for _ in $(seq 1 30); do sleep 1; timeout 20 "$C" host status >/dev/null 2>&1 && break; done
  timeout 20 "$C" settings set defaultdir "$(cygpath -w "$INST/downloads")" >/dev/null
}

add_jobs() { # $1 count: held for later, so nothing downloads
  local done=0 n a b
  while [ "$done" -lt "$1" ]; do
    n=$(( $1 - done )); [ "$n" -gt 1000 ] && n=1000
    printf -v a '%05d' $((done + 1)); printf -v b '%05d' $((done + n))
    timeout 300 "$C" add-batch "http://127.0.0.1:$PORT/file/job-[$a-$b].bin?size=1048576" --start later --yes >/dev/null 2>&1
    done=$((done + n))
  done
}

teardown() {
  timeout 20 "$C" host shutdown >/dev/null 2>&1; sleep 3
  for p in $(ps1 soak-sample.ps1 -Root "$WIN_BIN" | cut -d, -f4); do taskkill //PID "$p" //F >/dev/null 2>&1; done
}

trace_ms() { # $1 line text, $2 occurrence: Unix ms of that ui-trace line
  python - "$TRACE" "$1" "$2" <<'EOF'
import sys, datetime
path, text, nth = sys.argv[1], sys.argv[2], int(sys.argv[3])
hits = [l for l in open(path, encoding='utf-8') if text in l]
if len(hits) < nth: print(''); sys.exit()
stamp = hits[nth - 1].split(' ', 1)[0]
print(int(datetime.datetime.fromisoformat(stamp).timestamp() * 1000))
EOF
}

open_and_time() { # $1 label, $2 repeat, $3 option (CSV only), $4 jobs,
  # $5 occurrence of the trace line to wait for: set for a cold start, whatever the option; empty when only shown
  local ready="$INST/ready.$2" t0 tw tf ts
  rm -f "$ready"
  ps1 perf-window-wait.ps1 -Root "$WIN_BIN" -Ready "$(cygpath -w "$ready")" > "$INST/seen.$2" &
  local waiter=$!
  for _ in $(seq 1 100); do [ -e "$ready" ] && break; sleep 0.1; done
  t0=$(now_ms)
  timeout 20 "$C" host open >/dev/null
  wait $waiter; tw=$(tail -1 "$INST/seen.$2")
  ts=""; tf=""
  if [ -n "${5:-}" ]; then
    for _ in $(seq 1 600); do tf=$(trace_ms "First frame with the list" "$5"); [ -n "$tf" ] && break; sleep 0.1; done
    ts=$(trace_ms "First snapshot applied" "$5")
  fi
  echo "$1,$4,$3,$2,$((tw - t0)),${ts:+$((ts - t0))},${tf:+$((tf - t0))}" >> "$OUTDIR/coldopen.csv"
}

close_window() { # posts WM_CLOSE, as the close button does, until the window is off screen (option off) or its
  # process has ended (option on). A posted WM_CLOSE is ignored the first time in these runs, so it is repeated.
  local try check
  [ "$1" = on ] && check=-Gone || check=-Hidden
  sleep 3
  for try in 1 2 3 4; do
    echo "close $1 try $try: $(ps1 soak-hide-window.ps1 -Root "$WIN_BIN" | paste -sd' ')" >> "$OUTDIR/close.log"
    ps1 perf-window-wait.ps1 -Root "$WIN_BIN" -Ready "$(cygpath -w "$INST/closed")" $check -TimeoutSeconds 8 | grep -q timeout || break
  done
  sleep 2
}

case "$MODE" in
  coldopen)
    REPEATS=${3:-3}
    echo "label,jobs,option,repeat,windowShownMs,snapshotAppliedMs,firstFrameWithListMs" > "$OUTDIR/coldopen.csv"
    for jobs in 0 500 5000; do
      setup "jobs-$jobs"; add_jobs "$jobs"
      timeout 20 "$C" settings set closewindowcompletely true >/dev/null
      for r in $(seq 1 "$REPEATS"); do open_and_time cold "$r" on "$jobs" "$r"; close_window on; done
      # Option off: the first open is a cold start too; after closing, the process stays and only shows again.
      timeout 20 "$C" settings set closewindowcompletely false >/dev/null
      open_and_time first-start-off 0 off "$jobs" $((REPEATS + 1)); close_window off
      for r in $(seq 1 "$REPEATS"); do open_and_time show-hidden "$r" off "$jobs" ""; close_window off; done
      teardown
    done
    ;;
  idle)
    OPTION=$3; MINUTES=${4:-30}; JOBS=${5:-500}
    setup "idle-$OPTION"; add_jobs "$JOBS"
    [ "$OPTION" = on ] && v=true || v=false
    timeout 20 "$C" settings set closewindowcompletely "$v" >/dev/null
    timeout 20 "$C" host open >/dev/null; sleep 20
    close_window "$OPTION"
    sleep 30
    ps1 perf-idle-sample.ps1 -Root "$WIN_BIN" -Out "$(cygpath -w "$OUTDIR/idle-$OPTION.csv")" -Minutes "$MINUTES" >/dev/null
    teardown
    ;;
  *) echo "usage: $0 coldopen|idle OUTDIR ..." >&2; exit 2 ;;
esac
