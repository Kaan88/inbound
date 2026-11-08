#!/usr/bin/env bash
# Soak driver for PERF.2/PERF.3: a test instance with many jobs and a steady stream of local TestServer
# downloads (retries, truncated responses, slow and failing servers), sampled once a minute.
#
# Run from Git Bash (never from PowerShell: this script starts executables). PowerShell is used only for
# the read-only sampler scripts\soak-sample.ps1.
#
#   scripts/soak.sh --name soak --minutes 480 --jobs 5000 --port 5231 [--ui]
#   scripts/soak.sh --stop soak          # asks a running soak to stop; it shuts its processes down
#
# GC settings to compare go in the environment, for example
#   DOTNET_GCConserveMemory=5 scripts/soak.sh --name gc-conserve --minutes 20 ...
#
# Everything lives under artifacts/soak/<name>/ (git-ignored):
#   bin/          a private copy of the Host, CLI, window and TestServer, so rebuilding the worktree
#                 never touches a running soak and the sampler can tell its processes apart
#   data/         the instance's state (DOWNLOADMANAGER_DATA)   downloads/  the files
#   samples.csv   utc,label,name,pid,privateBytes,workingSet,handles,threads,cpuMs, once a minute
#   gc.csv        the Host's own GC heap figures once a minute (DOWNLOADMANAGER_GC_LOG)
#   status.log    job counts every 10 minutes        driver.log  what the driver did
#   stop          create this file to stop the soak
set -u

NAME=soak; MINUTES=480; JOBS=5000; PORT=5231; UI=0; ADD_EVERY=20; KEEP_STREAM=40; STOP=""
while [ $# -gt 0 ]; do
  case "$1" in
    --name) NAME=$2; shift 2 ;;
    --minutes) MINUTES=$2; shift 2 ;;
    --jobs) JOBS=$2; shift 2 ;;
    --port) PORT=$2; shift 2 ;;
    --ui) UI=1; shift ;;
    --add-every) ADD_EVERY=$2; shift 2 ;;
    --stop) STOP=$2; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -n "$STOP" ]; then
  touch "$ROOT/artifacts/soak/$STOP/stop" && echo "asked soak '$STOP' to stop (it checks every few seconds)"
  exit 0
fi

OUT="$ROOT/artifacts/soak/$NAME"
if [ -e "$OUT/data" ]; then echo "$OUT already holds a run; pick another --name" >&2; exit 2; fi
mkdir -p "$OUT/bin" "$OUT/data" "$OUT/downloads"
LOG="$OUT/driver.log"
log() { echo "$(date -u +%FT%TZ) $*" >> "$LOG"; }

# Private copies of the build outputs.
cp -r "$ROOT/src/DownloadManager.Host/bin/Release/net10.0-windows" "$OUT/bin/host"
cp -r "$ROOT/src/DownloadManager.Desktop.WinUI/bin/x64/Release/net10.0-windows10.0.19041.0" "$OUT/bin/host/desktop"
cp -r "$ROOT/src/DownloadManager.Cli/bin/Release/net10.0" "$OUT/bin/cli"
cp -r "$ROOT/tools/TestServer/bin/Release/net10.0" "$OUT/bin/testserver"
H="$OUT/bin/host/DownloadManager.Host.exe"
C="$OUT/bin/cli/DownloadManager.Cli.exe"
T="$OUT/bin/testserver/TestServer.exe"
WIN_BIN=$(cygpath -w "$OUT/bin")
SAMPLER=$(cygpath -w "$ROOT/scripts/soak-sample.ps1")

export DOWNLOADMANAGER_INSTANCE="perf-$NAME-$$"
export DOWNLOADMANAGER_DATA=$(cygpath -w "$OUT/data")
export DOWNLOADMANAGER_GC_LOG=$(cygpath -w "$OUT/gc.csv")
DEST=$(cygpath -w "$OUT/downloads")
BASE="http://127.0.0.1:$PORT"
log "start name=$NAME minutes=$MINUTES jobs=$JOBS port=$PORT ui=$UI instance=$DOWNLOADMANAGER_INSTANCE"
log "gc env: $(env | grep -E '^DOTNET_(GC|gc)' | tr '\n' ' ')"

cli() { timeout 120 "$C" "$@" 2>>"$OUT/cli-errors.log"; }
sample() { powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$SAMPLER" -Root "$WIN_BIN" -Label "$1" 2>/dev/null | tr -d '\r' >> "$OUT/samples.csv"; }
pids() { powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$SAMPLER" -Root "$WIN_BIN" 2>/dev/null | tr -d '\r' | cut -d, -f4; }

cleanup() {
  log "stopping"
  cli host shutdown >/dev/null 2>&1
  sleep 5
  for p in $(pids); do taskkill //PID "$p" //F >/dev/null 2>&1; done
  log "stopped"
}
trap cleanup EXIT

echo "utc,label,name,pid,privateBytes,workingSet,handles,threads,cpuMs" > "$OUT/samples.csv"
"$T" --port "$PORT" >/dev/null 2>&1 &
"$H" --background >/dev/null 2>&1 &
for _ in $(seq 1 30); do sleep 1; cli host status >/dev/null 2>&1 && break; done
cli settings set defaultdir "$DEST" >/dev/null
cli settings set maxconcurrent 4 >/dev/null
# No completion windows: nobody is there to close them, and each one is a window process with a pipe
# connection. (The CLI has no key for this setting, so it goes through a settings export and import.)
cli settings export "$(cygpath -w "$OUT/settings-in.json")" >/dev/null
sed -i 's/"showCompletionDialog": *true/"showCompletionDialog": false/' "$OUT/settings-in.json"
cli settings import "$(cygpath -w "$OUT/settings-in.json")" >/dev/null
log "showCompletionDialog: $(grep -o '"showCompletionDialog": *[a-z]*' "$OUT/settings-in.json")"
sample seed-start

# Seed: about 40% finished small files, the rest held for later (queued), in batches of up to 1,000.
FINISHED=$((JOBS * 2 / 5)); HELD=$((JOBS - FINISHED))
seed() { # $1 prefix, $2 count, $3 start mode, $4 size
  local done=0 n
  while [ "$done" -lt "$2" ]; do
    n=$(( $2 - done )); [ "$n" -gt 1000 ] && n=1000
    printf -v a '%05d' $((done + 1)); printf -v b '%05d' $((done + n))
    cli add-batch "$BASE/file/$1-[$a-$b].bin?size=$4" --dest "$DEST" --start "$3" --yes >/dev/null
    log "seeded $1 $a-$b ($3) exit $?"
    done=$((done + n))
  done
}
seed fin "$FINISHED" now 4096
seed held "$HELD" later 1048576
sample seed-done
cli host status >> "$OUT/status.log" 2>&1

if [ "$UI" = 1 ]; then
  cli host open >/dev/null
  sleep 30
  # Closed to the tray (the option to end the window process is off by default): the hidden window keeps
  # its process, which is the state most users leave it in.
  log "window opened through host open; $(powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/scripts/soak-hide-window.ps1")" -Root "$WIN_BIN" | tr -d '\r' | tr '\n' ' ')"
fi

START=$(date +%s); END=$((START + MINUTES * 60)); NEXT_SAMPLE=$START; NEXT_STATUS=$((START + 600)); NEXT_ADD=$START
I=0; STREAM=()
while [ "$(date +%s)" -lt "$END" ] && [ ! -e "$OUT/stop" ]; do
  NOW=$(date +%s)
  if [ "$NOW" -ge "$NEXT_SAMPLE" ]; then sample run; NEXT_SAMPLE=$((NEXT_SAMPLE + 60)); fi
  if [ "$NOW" -ge "$NEXT_STATUS" ]; then
    { date -u +%FT%TZ; cli host status; } >> "$OUT/status.log" 2>&1; NEXT_STATUS=$((NEXT_STATUS + 600))
  fi
  if [ "$NOW" -ge "$NEXT_ADD" ]; then
    I=$((I + 1)); NEXT_ADD=$((NEXT_ADD + ADD_EVERY))
    case $((I % 6)) in
      0) URL="$BASE/file/st-$I.bin?size=16777216&bps=4194304&seed=$I" ;;          # plain, segmented
      1) URL="$BASE/throttled/st-$I.bin?failures=2&status=503&after=2&size=4194304" ;; # retried after 503s
      2) URL="$BASE/truncate/st-$I.bin?size=3145728&cut=1048576" ;;                  # cut off, resumed
      3) URL="$BASE/slow/st-$I.bin?size=1048576&bps=262144" ;;                        # slow server
      4) URL="$BASE/norange/st-$I.bin?size=2097152" ;;                               # no resume
      5) URL="$BASE/throttled/st-$I.bin?failures=1000&status=500&size=65536" ;;     # never succeeds
    esac
    ID=$(cli add "$URL" --dest "$DEST" --start now | sed -n 's/^\(Added\|Already queued\): \([0-9a-f]\{32\}\).*/\2/p')
    if [ -n "$ID" ]; then STREAM+=("$ID"); else log "add failed: $URL"; fi
    # Keep the job count steady: the oldest stream job goes, with its file, once enough are newer.
    while [ "${#STREAM[@]}" -gt "$KEEP_STREAM" ]; do
      cli remove "${STREAM[0]}" >/dev/null || log "remove failed ${STREAM[0]}"
      STREAM=("${STREAM[@]:1}")
    done
  fi
  sleep 2
done
sample end
log "loop finished after $(( ($(date +%s) - START) / 60 )) minutes, $I stream downloads added"
