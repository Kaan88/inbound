#!/usr/bin/env bash
# ANDROID.2 part 2: the Host in a foreground service (dataSync). Emulator figures only.
#   bash tools/AndroidTrial/run-service.sh            # idle (IDLE_SECONDS, default 300) then the survival checks
#   ONLY=idle | ONLY=survive  to run one part
# Raw output: artifacts/android-trial/service-<timestamp>/
set -u
export MSYS_NO_PATHCONV=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
PORT=5599; PKG=app.inbound.trial; EXT=/sdcard/Android/data/$PKG/files
IDLE_SECONDS="${IDLE_SECONDS:-300}"
OUT="$ROOT/artifacts/android-trial/service-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$OUT"
"$ADB" get-state >/dev/null 2>&1 || { echo "No device: start the emulator first."; exit 1; }
source "$(dirname "${BASH_SOURCE[0]}")/lib-android.sh"
build_install
start_server
HZ=$("$ADB" shell getconf CLK_TCK | tr -d '\r')
pid_of() { "$ADB" shell pidof $PKG | tr -d '\r' | awk '{print $1}'; }
ticks() { "$ADB" shell "cat /proc/$1/stat" | tr -d '\r' | awk '{ s=$0; sub(/.*\) /,"",s); n=split(s,a," "); print a[12]+a[13] }'; }   # utime+stime (fields 14,15)
threads() { "$ADB" shell "ls /proc/$1/task | wc -l" | tr -d '\r'; }
# every thread: tid, name, voluntary+involuntary context switches, utime+stime ticks
thread_table() { "$ADB" shell "for t in /proc/$1/task/*; do n=\$(cat \$t/comm); v=\$(grep -E 'ctxt' \$t/status | tr -s '[:space:]' ' ' | tr '\n' ' '); s=\$(cat \$t/stat | sed 's/.*) //' | cut -d' ' -f12,13); echo \"\$(basename \$t)|\$n|\$v|\$s\"; done" | tr -d '\r'; }
meminfo() { "$ADB" shell dumpsys meminfo $PKG | tr -d '\r'; }
last_progress() { "$ADB" logcat -d -s InboundTrial:I | grep -oE 'progress [0-9]+ /' | tail -1 | grep -oE '[0-9]+'; }

if [ "${ONLY:-}" != survive ]; then
  echo "== idle: host in the foreground service, nothing to do, Activity closed =="
  "$ADB" shell pm clear $PKG >/dev/null; "$ADB" shell pm grant $PKG android.permission.POST_NOTIFICATIONS; "$ADB" logcat -c
  "$ADB" shell am start -n $PKG/.MainActivity --es mode service --es runId idle --ez idle true --ez finish true >/dev/null
  sleep 25
  PID=$(pid_of); echo "pid $PID"
  "$ADB" shell dumpsys activity services $PKG | tr -d '\r' > "$OUT/idle-dumpsys-services.txt"
  "$ADB" shell dumpsys activity top 2>/dev/null | grep -c "$PKG" > /dev/null
  meminfo > "$OUT/idle-meminfo-after25s.txt"
  echo "settling 60 s before the CPU window"; sleep 60
  meminfo > "$OUT/idle-meminfo-after85s.txt"
  T0=$(ticks $PID); thread_table $PID > "$OUT/idle-threads-start.txt"; TH0=$(threads $PID); D0=$(date +%s)
  "$ADB" shell cat /proc/$PID/schedstat | tr -d '\r' > "$OUT/idle-schedstat-start.txt"
  sleep "$IDLE_SECONDS"
  T1=$(ticks $PID); thread_table $PID > "$OUT/idle-threads-end.txt"; TH1=$(threads $PID); D1=$(date +%s)
  "$ADB" shell cat /proc/$PID/schedstat | tr -d '\r' > "$OUT/idle-schedstat-end.txt"
  "$ADB" shell dumpsys cpuinfo | grep -E "$PKG|Load|TOTAL" | tr -d '\r' > "$OUT/idle-dumpsys-cpuinfo.txt"
  meminfo > "$OUT/idle-meminfo-end.txt"
  "$ADB" logcat -d -s InboundTrial:I > "$OUT/idle-logcat.txt"
  {
    echo "idle window: $((D1-D0)) s, pid $PID, CLK_TCK $HZ"
    echo "utime+stime ticks: $T0 -> $T1 = $((T1-T0)) ticks = $(awk -v d=$((T1-T0)) -v h=$HZ 'BEGIN{printf "%.2f", d/h}') CPU s"
    echo "threads: $TH0 -> $TH1"
  } | tee "$OUT/idle-summary.txt"
fi

if [ "${ONLY:-}" != idle ]; then
  echo "== survive: a download while the app goes Home, is swiped away and the device is put in Doze =="
  SIZE=$((1024*1024*1024)); BPS=1000000   # 8 connections x 1 MB/s = ~8 MB/s: ~2 min
  SHA=$(curl -s "http://127.0.0.1:$PORT/sha256?size=$SIZE")
  "$ADB" shell am force-stop $PKG; "$ADB" shell pm clear $PKG >/dev/null; "$ADB" shell pm grant $PKG android.permission.POST_NOTIFICATIONS; "$ADB" logcat -c
  "$ADB" shell am start -n $PKG/.MainActivity --es mode service --es runId dl --es url "'$BASE/bench.bin?size=$SIZE&bps=$BPS'" --es sha256 "$SHA" --ei segments 8 >/dev/null
  sleep 20
  PID=$(pid_of); echo "pid $PID"
  meminfo > "$OUT/download-meminfo-during.txt"
  thread_table $PID > "$OUT/download-threads-during.txt"
  echo "during download: threads $(threads $PID)" | tee "$OUT/survive-steps.txt"
  note() { echo "$1: progress=$(last_progress) pid=$(pid_of)" | tee -a "$OUT/survive-steps.txt"; }
  note "A before Home"
  "$ADB" shell input keyevent KEYCODE_HOME; sleep 15; note "B 15 s after Home"
  "$ADB" shell am start -n $PKG/.MainActivity >/dev/null 2>&1; sleep 3
  "$ADB" shell input keyevent KEYCODE_APP_SWITCH; sleep 2
  "$ADB" shell uiautomator dump /sdcard/ui-recents.xml >/dev/null 2>&1; "$ADB" shell cat /sdcard/ui-recents.xml > "$OUT/recents-ui.xml"
  SIZE_WH=$("$ADB" shell wm size | tr -d '\r' | grep -oE '[0-9]+x[0-9]+' | tail -1); W=${SIZE_WH%x*}; H=${SIZE_WH#*x}
  "$ADB" shell input swipe $((W/2)) $((H*55/100)) $((W/2)) $((H/50)) 250; sleep 3
  "$ADB" shell input keyevent KEYCODE_HOME; sleep 12; note "C 12 s after swiping the task away"
  "$ADB" shell dumpsys activity recents | grep -c "$PKG" > "$OUT/recents-count-after-swipe.txt"
  "$ADB" shell dumpsys battery unplug >/dev/null; "$ADB" shell dumpsys deviceidle enable deep >/dev/null 2>&1
  "$ADB" shell input keyevent KEYCODE_SLEEP; sleep 3
  "$ADB" shell dumpsys deviceidle force-idle | tr -d '\r' | tee -a "$OUT/survive-steps.txt"
  note "D at Doze start"; sleep 30; note "E 30 s into forced Doze"; sleep 30; note "F 60 s into forced Doze"
  "$ADB" shell dumpsys deviceidle get deep | tr -d '\r' | tee -a "$OUT/survive-steps.txt"
  "$ADB" shell dumpsys deviceidle unforce >/dev/null; "$ADB" shell dumpsys battery reset >/dev/null; "$ADB" shell input keyevent KEYCODE_WAKEUP
  sleep 10; note "G 10 s after leaving Doze"
  "$ADB" logcat -d -s InboundTrial:I > "$OUT/survive-logcat.txt"
  "$ADB" shell dumpsys deviceidle whitelist > /dev/null 2>&1
  echo "waiting for the job to end (up to 4 min)"
  wait_file "$EXT/host-result-dl-add.json" 240 "$OUT/dl-result.json" && grep -E '"(finalState|hashMatched|receivedBytes)"' "$OUT/dl-result.json" | tee -a "$OUT/survive-steps.txt"
  "$ADB" logcat -d -s InboundTrial:I > "$OUT/survive-logcat.txt"
  "$ADB" shell "rm -f $EXT/host-* $EXT/service-*"
fi
echo "Raw output: $OUT"
