#!/usr/bin/env bash
# ANDROID.2 part 1: the whole Host core (Runtime + HostCommandDispatcher) in process on Android, driven only through the
# dispatcher's JSON envelopes (mode=host). Plain run, then kill (am force-stop) mid-download and start again.
#   bash tools/AndroidTrial/run-host.sh
# Env: SIZE_MB (plain run, default 256), KILL_MB (default 512), KILL_BPS per connection (default 4000000), SKIP_BUILD=1,
#      RESUME_IF_PAUSED=1 (send job.resume if the Runtime left the job paused; default 1)
# Needs a running emulator (start it from Bash). Results: artifacts/android-trial/host-<timestamp>/
set -u
export MSYS_NO_PATHCONV=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
SIZE_MB="${SIZE_MB:-256}"; KILL_MB="${KILL_MB:-512}"; KILL_BPS="${KILL_BPS:-4000000}"
PORT=5599; PKG=app.inbound.trial; EXT=/sdcard/Android/data/$PKG/files
OUT="$ROOT/artifacts/android-trial/host-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$OUT"
"$ADB" get-state >/dev/null 2>&1 || { echo "No device: start the emulator first."; exit 1; }
source "$(dirname "${BASH_SOURCE[0]}")/lib-android.sh"
build_install
start_server

# ---- 1. plain run ----
SIZE=$((SIZE_MB*1024*1024)); SHA=$(curl -s "http://127.0.0.1:$PORT/sha256?size=$SIZE")
"$ADB" shell pm clear $PKG >/dev/null; "$ADB" logcat -c
"$ADB" shell am start -n $PKG/.MainActivity --es mode host --es phase add --es runId plain --es url "'$BASE/bench.bin?size=$SIZE'" --es sha256 "$SHA" --ei segments 8 >/dev/null
wait_file "$EXT/host-result-plain-add.json" 180 "$OUT/plain-add.json" || echo "plain: no result"
"$ADB" logcat -d -s InboundTrial:I > "$OUT/plain-logcat.txt"

# ---- 2. kill and restart ----
KSIZE=$((KILL_MB*1024*1024)); KSHA=$(curl -s "http://127.0.0.1:$PORT/sha256?size=$KSIZE")
"$ADB" shell pm clear $PKG >/dev/null; "$ADB" logcat -c
KURL="$BASE/bench.bin?size=$KSIZE&bps=$KILL_BPS"
"$ADB" shell am start -n $PKG/.MainActivity --es mode host --es phase add --es runId kill --es url "'$KURL'" --es sha256 "$KSHA" --ei segments 8 >/dev/null
for _ in $(seq 1 300); do
  sleep 1
  last=$("$ADB" logcat -d -s InboundTrial:I | grep -oE 'progress [0-9]+ /' | tail -1 | grep -oE '[0-9]+')
  [ -n "$last" ] && [ "$last" -ge $((KSIZE/4)) ] && break
done
sleep 6   # a checkpoint lands every few seconds
"$ADB" shell am force-stop $PKG
echo "killed with $last bytes reported"
"$ADB" logcat -d -s InboundTrial:I > "$OUT/kill-phase1-logcat.txt"
"$ADB" shell "ls -l $EXT" | tr -d '\r' > "$OUT/kill-files-after-kill.txt"
"$ADB" logcat -c
"$ADB" shell am start -n $PKG/.MainActivity --es mode host --es phase resume --es runId kill --es sha256 "$KSHA" --ez resumeIfPaused "${RESUME_IF_PAUSED:-true}" >/dev/null
wait_file "$EXT/host-result-kill-resume.json" 300 "$OUT/kill-resume.json" || echo "resume: no result"
"$ADB" logcat -d -s InboundTrial:I > "$OUT/kill-phase2-logcat.txt"
"$ADB" shell "rm -f $EXT/host-* $EXT/*.part $EXT/host-*.bin"
echo "Raw output: $OUT"
