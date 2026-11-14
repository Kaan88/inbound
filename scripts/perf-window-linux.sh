#!/usr/bin/env bash
# PERF.15, Linux window baseline: the Uno window (the same sources as the WinUI window) with an isolated Host, seeded with
# N jobs and A active downloads, on the default renderer (OpenGL on X11) and on software rendering
# (DOWNLOADMANAGER_UNO_SOFTWARE=1, src/DownloadManager.Desktop.Uno/Platforms/Desktop/Program.cs).
#
#   scripts/perf-window-linux.sh [options]
#     --jobs 500,5000        held jobs in the list                         (default 500,5000)
#     --active 0,1,8,20      active downloads; 0 is the idle figure        (default 0,1,8,20)
#     --renderer default,software                                          (default both)
#     --duration 60          seconds of CPU/memory sampling per combination (default 60)
#     --warmup 10            seconds between the first frame and sampling   (default 10)
#     --opens 3              windows opened per combination for the open time; the last one is the one sampled (default 3)
#     --display :N           use this X display instead of starting a private one. THE WINDOW OPENS THERE: on your own
#                            session that is your desktop. WAYLAND_DISPLAY is unset for the window (Uno uses X11).
#     --out DIR              build output and instances (default artifacts/perf, or $PERF_OUT)
#     --results DIR          default OUT/results
#     --no-build             do not build when something is missing
#
# Without --display it starts its own display and touches nothing of yours: KWin's virtual backend
# (`kwin_wayland --virtual --xwayland`) in a PRIVATE runtime dir, session bus and config dir, with a rootless Xwayland whose
# display number is a free one. The window is a real window of that KWin, drawn with the machine's GPU through the render
# node when Mesa/NVIDIA allow it (the header of the results says which GL renderer it was). Everything is torn down at the end.
# Needs: kwin_wayland, Xwayland, dbus-run-session, xprop, glxinfo (header only), python3, the .NET SDK in ~/.dotnet.
#
# Each combination: starts an isolated instance (perf-seed.sh), opens the window --opens times (process start to: runtime
# up, window activated, snapshot applied, first frame with the list [ui-trace.log lines], X window mapped), then samples the
# last window for --duration seconds from /proc (perf-proc-sample.py): CPU of the process, of the UI thread ("Uno Event
# Loop") and of the render thread, RSS, the Host's CPU and RSS. Dropped frames while scrolling are NOT measured (see
# docs/plans/perf-baseline-how.md).
set -u
ROOT=$(cd "$(dirname "$0")/.." && pwd)
SELF="$ROOT/scripts/perf-window-linux.sh"

# ------------------------------------------------------------------ outer part: parse, build, give a display
if [ -z "${PERF_WL_ARGS_FILE:-}" ]; then
  JOBS=500,5000; ACTIVE=0,1,8,20; RENDERER=default,software; DURATION=60; WARMUP=10; OPENS=3; DISPLAY_GIVEN=""; NOBUILD=0
  export PERF_OUT=${PERF_OUT:-$ROOT/artifacts/perf}; RESULTS=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --jobs) JOBS=$2; shift 2 ;;       --active) ACTIVE=$2; shift 2 ;;     --renderer) RENDERER=$2; shift 2 ;;
      --duration) DURATION=$2; shift 2 ;; --warmup) WARMUP=$2; shift 2 ;;   --opens) OPENS=$2; shift 2 ;;
      --display) DISPLAY_GIVEN=$2; shift 2 ;; --out) PERF_OUT=$(mkdir -p "$2" && cd "$2" && pwd); export PERF_OUT; shift 2 ;;
      --results) RESULTS=$2; shift 2 ;;  --no-build) NOBUILD=1; shift ;;
      -h|--help) sed -n 2,32p "$0"; exit 0 ;;
      *) echo "unknown option $1" >&2; exit 2 ;;
    esac
  done
  RESULTS=${RESULTS:-$PERF_OUT/results}; mkdir -p "$RESULTS"; RESULTS=$(cd "$RESULTS" && pwd)
  export PATH="$HOME/.dotnet:$PATH"
  [ -d "$HOME/.dotnet" ] && export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
  if [ ! -x "$PERF_OUT/bin/desktop/DownloadManager.Desktop.Uno" ] || [ ! -x "$PERF_OUT/bin/lib/inbound-host" ] || [ ! -x "$PERF_OUT/bin/testserver/TestServer" ]; then
    [ "$NOBUILD" = 1 ] && { echo "Nothing built under $PERF_OUT/bin; run scripts/perf-seed.sh build --skip-webui" >&2; exit 2; }
    "$ROOT/scripts/perf-seed.sh" build --skip-webui || exit 1
  fi
  STAMP=$(date +%Y-%m-%dT%H-%M-%S)
  ARGS=$(mktemp "${TMPDIR:-/tmp}/perf-wl-args-XXXXXX")
  printf '%s\n' "$JOBS" "$ACTIVE" "$RENDERER" "$DURATION" "$WARMUP" "$OPENS" "$RESULTS" "$STAMP" > "$ARGS"
  export PERF_WL_ARGS_FILE=$ARGS
  trap 'rm -f "$ARGS"' EXIT
  if [ -n "$DISPLAY_GIVEN" ]; then
    DISPLAY=$DISPLAY_GIVEN WAYLAND_DISPLAY="" "$SELF"
    exit $?
  fi
  for tool in kwin_wayland Xwayland dbus-run-session xprop; do
    command -v "$tool" >/dev/null || { echo "$tool not found. Without it, start a display yourself and pass --display :N" >&2; exit 2; }
  done
  RT=$(mktemp -d "${TMPDIR:-/tmp}/perf-kwin-XXXXXX"); chmod 700 "$RT"; mkdir -p "$RT/cfg" "$RT/data"
  # Only variables of this private session: the user's own session bus, runtime dir, Wayland socket and X display are not
  # inherited, so nothing here can reach their KWin (which owns org.kde.KWin on their bus).
  env -u WAYLAND_DISPLAY -u DISPLAY -u DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR="$RT" XDG_CONFIG_HOME="$RT/cfg" XDG_DATA_HOME="$RT/data" \
      XDG_CACHE_HOME="$RT/cfg" XDG_SESSION_TYPE=wayland PERF_WL_ARGS_FILE="$ARGS" \
      dbus-run-session -- kwin_wayland --virtual --width 1600 --height 1000 --socket "perf-wl-$$" --xwayland \
      --no-lockscreen --no-global-shortcuts --exit-with-session "$SELF" >"$RT/kwin.out" 2>"$RT/kwin.err"
  rc=$?
  # Whatever of the private session is left (kwin, Xwayland, the bus): found by its environment, never by name.
  for p in /proc/[0-9]*; do
    if { tr '\0' '\n' < "$p/environ" | grep -qxF "XDG_RUNTIME_DIR=$RT"; } 2>/dev/null; then kill "${p#/proc/}" 2>/dev/null; fi
  done
  sleep 1
  rm -rf "$RT"
  exit $rc
fi

# ------------------------------------------------------------------ inner part: runs with DISPLAY set
mapfile -t A < "$PERF_WL_ARGS_FILE"
JOBS=${A[0]}; ACTIVE=${A[1]}; RENDERER=${A[2]}; DURATION=${A[3]}; WARMUP=${A[4]}; OPENS=${A[5]}; RESULTS=${A[6]}; STAMP=${A[7]}
. "$ROOT/scripts/perf-seed.sh"        # perf_start, perf_stop, perf_kill ... (PERF_OUT is exported by the outer part)
WINDOW="$PERF_OUT/bin/desktop/DownloadManager.Desktop.Uno"
JSONL="$RESULTS/window-$STAMP.jsonl"; : > "$JSONL"
log() { echo "[perf-window] $*" >&2; }

trace_ms() { # file, byte offset, text: epoch ms of the first matching line after the offset; nothing if none yet
  python3 - "$1" "$2" "$3" <<'PY'
import os, sys, datetime
path, off, text = sys.argv[1], int(sys.argv[2]), sys.argv[3]
try:
    if os.path.getsize(path) < off: off = 0      # the trace file restarts when it passes its cap
    with open(path, 'rb') as f:
        f.seek(off); data = f.read().decode('utf-8', 'replace')
except OSError:
    sys.exit()
for line in data.splitlines():
    if text in line:
        print(int(datetime.datetime.fromisoformat(line.split(' ', 1)[0]).timestamp() * 1000)); break
PY
}

mapped_list() { xprop -root _NET_CLIENT_LIST 2>/dev/null; }

# Opens the window once and records the open times in $INST/opens.jsonl; sets WIN_PID.
open_window() { # $1 renderer
  local trace="$DOWNLOADMANAGER_DATA/logs/ui-trace.log" off t0 base now frame="" mapped="" i
  off=$(stat -c %s "$trace" 2>/dev/null || echo 0)
  base=$(mapped_list)
  t0=$(date +%s%3N)
  if [ "$1" = software ]; then
    DOWNLOADMANAGER_UNO_SOFTWARE=1 "$WINDOW" >"$PERF_INST/window.out" 2>&1 &
  else
    env -u DOWNLOADMANAGER_UNO_SOFTWARE "$WINDOW" >"$PERF_INST/window.out" 2>&1 &
  fi
  WIN_PID=$!
  for i in $(seq 1 600); do
    if [ -z "$mapped" ]; then now=$(mapped_list); [ "$now" != "$base" ] && mapped=$(( $(date +%s%3N) - t0 )); fi
    frame=$(trace_ms "$trace" "$off" "First frame with the list")
    [ -n "$frame" ] && break
    kill -0 "$WIN_PID" 2>/dev/null || { log "the window exited: $(head -c 300 "$PERF_INST/window.out")"; return 1; }
    sleep 0.1
  done
  [ -n "$frame" ] || { log "no first frame within 60 s"; return 1; }
  local main act snap
  main=$(trace_ms "$trace" "$off" "Uno Program.Main begin"); act=$(trace_ms "$trace" "$off" "MainWindow activated")
  snap=$(trace_ms "$trace" "$off" "First snapshot applied")
  printf '{"mainBeginMs": %s, "activatedMs": %s, "snapshotMs": %s, "firstFrameMs": %s, "mappedMs": %s}\n' \
    "$((main - t0))" "$((act - t0))" "$((snap - t0))" "$((frame - t0))" "${mapped:-null}" >> "$PERF_INST/opens.jsonl"
}

run_combo() { # jobs active renderer
  local jobs=$1 active=$2 renderer=$3 name sample seen k
  name="win-$jobs-$active-$renderer"
  perf_start "$name" "$jobs" "$active" host || { echo "{\"jobs\": $jobs, \"active\": $active, \"renderer\": \"$renderer\", \"error\": \"seeding failed\"}" >> "$JSONL"; return; }
  : > "$PERF_INST/opens.jsonl"
  for k in $(seq 1 "$OPENS"); do
    if ! open_window "$renderer"; then
      echo "{\"jobs\": $jobs, \"active\": $active, \"renderer\": \"$renderer\", \"error\": \"the window did not open\"}" >> "$JSONL"
      perf_kill "$WIN_PID"; perf_stop "$name"; perf_purge "$name"; return
    fi
    [ "$k" -lt "$OPENS" ] && { perf_kill "$WIN_PID"; sleep 2; }
  done
  sleep "$WARMUP"
  sample=$(python3 "$ROOT/scripts/perf-proc-sample.py" "$WIN_PID" "$DURATION" --host "$PERF_HOST_PID" --series "$RESULTS/series-$STAMP-$name.csv")
  seen=$(perf_cli list 2>/dev/null | grep -c '^Downloading')
  python3 - "$jobs" "$active" "$renderer" "$seen" "$PERF_INST/opens.jsonl" "$sample" >> "$JSONL" <<'PY'
import json, sys
jobs, active, renderer, seen, opens, sample = sys.argv[1:7]
print(json.dumps({"jobs": int(jobs), "active": int(active), "renderer": renderer, "activeSeen": int(seen),
                  "opens": [json.loads(l) for l in open(opens) if l.strip()], "sample": json.loads(sample)}))
PY
  perf_kill "$WIN_PID"
  perf_stop "$name"; perf_purge "$name"
}

META="$RESULTS/window-$STAMP.meta.txt"
{
  echo "Date: $(date -Is)    Display: DISPLAY=$DISPLAY    Sampling: $DURATION s after $WARMUP s warm-up, $OPENS opens per combination"
  echo "KWin: $(kwin_wayland --version 2>/dev/null)    Xwayland: $(Xwayland -version 2>&1 | head -1)"
  echo "GL: $(glxinfo -B 2>/dev/null | grep -E 'OpenGL (vendor|renderer) string' | sed 's/.*: //' | paste -sd' ')"
  echo "CPU cores: $(nproc)    Load at start: $(cut -d' ' -f1-3 /proc/loadavg)"
} > "$META"
cat "$META" >&2

trap 'perf_kill "${WIN_PID:-}"; [ -n "${PERF_NAME:-}" ] && { perf_stop "$PERF_NAME"; perf_purge "$PERF_NAME"; }' EXIT
for renderer in ${RENDERER//,/ }; do
  for jobs in ${JOBS//,/ }; do
    for active in ${ACTIVE//,/ }; do
      log "jobs $jobs, active $active, renderer $renderer"
      PERF_NAME="win-$jobs-$active-$renderer"
      run_combo "$jobs" "$active" "$renderer"
      PERF_NAME=""
    done
  done
done
python3 "$ROOT/scripts/perf-window-summary.py" "$JSONL" "$RESULTS/window-$STAMP" --meta "$META"
log "wrote $RESULTS/window-$STAMP.csv and .md"
