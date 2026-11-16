#!/usr/bin/env bash
# ANDROID.2 phone trial: CPU per GiB of the read patterns in NetProbe.cs (dest=net|write|sysdm), on a real phone over Wi-Fi.
#   ANDROID_SERIAL=<serial> BASE=http://<pc-ip>:5601 OUT=<dir> REPEAT=2 COOL=30 bash tools/AndroidTrial/run-net.sh < spec.txt
# spec.txt: one experiment per line:  <label> <dest> <connections> [key=value ...]
#   dest=net   url = $BASE/bench.bin?size=$SIZE; keys: impl buf rcvbuf lowat delayms env=K=V;K=V (anything else is passed as an extra)
#   dest=write url = target app|internal|downloads; keys: chunk wstyle size
#   dest=sysdm url = as net (Android DownloadManager; also samples the system downloads/media provider processes)
# Needs the APK installed (SKIP install here), the test server up. One JSON per run in $OUT, one row per run in $OUT/rows.tsv.
set -u
export MSYS_NO_PATHCONV=1
: "${ANDROID_SERIAL:?set ANDROID_SERIAL (an emulator may be attached too)}"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"; ADB="$SDK/platform-tools/adb.exe"
PKG=app.inbound.trial; EXT=/sdcard/Android/data/$PKG/files
BASE="${BASE:-http://192.168.1.115:5601}"; SIZE="${SIZE:-1073741824}"; REPEAT="${REPEAT:-2}"; COOL="${COOL:-30}"
OUT="${OUT:?set OUT}"; mkdir -p "$OUT"; ROWS="$OUT/rows.tsv"
temp() { "$ADB" shell dumpsys battery | tr -d '\r' | awk '/temperature/{print $2/10}'; }
busy() { "$ADB" shell head -1 /proc/stat | tr -d '\r' | awk '{b=$2+$3+$4+$7+$8+$9; print b}'; }   # ticks (10 ms), user+nice+system+irq+softirq+steal
pidstat() { "$ADB" shell "for p in \$(pidof $1); do awk '{print \$14+\$15}' /proc/\$p/stat; done" | tr -d '\r' | awk '{s+=$1}END{print s+0}'; }
cool() { sleep "$COOL"; while awk -v t="$(temp)" 'BEGIN{exit !(t>=42)}'; do echo "  hot ($(temp) C), pausing 30 s"; sleep 30; done; }

run_one() { # label dest conns kvs...
  local label=$1 dest=$2 conns=$3; shift 3
  if [ "$dest" = curl ]; then   # the phone's own curl, same URL, one connection; the time builtin gives real/user/system
    local b0 b1 t; b0=$(busy)
    t=$("$ADB" shell "time curl -s -o ${CURLOUT:-/dev/null} '$BASE/bench.bin?size=$SIZE'" 2>&1 | tr -d '
' | tr '
' ' ')
    b1=$(busy)
    [ -n "${CURLOUT:-}" ] && "$ADB" shell rm -f "$CURLOUT"   # CURLOUT=<phone path>: curl writes the file there, deleted after the run
    echo "$label	curl	$t	sys-busy=$(( b1 - b0 )) ticks temp=$(temp)" | tee -a "$ROWS"; return
  fi
  local extras=() url
  if [ "$dest" = write ]; then url=${WTARGET:-app}; else url="$BASE/bench.bin?size=$SIZE"; fi
  for kv in "$@"; do
    case "$kv" in target=*) url=${kv#target=};; env=*) extras+=(--es env "'${kv#env=}'");; *=*) extras+=(--es "${kv%%=*}" "'${kv#*=}'");; esac
  done
  "$ADB" shell am force-stop $PKG; sleep 1
  "$ADB" shell input keyevent KEYCODE_WAKEUP; "$ADB" shell wm dismiss-keyguard; sleep 1   # the phone may be dozing (Doze blocks a background app's network)
  "$ADB" shell "rm -f $EXT/result-$label.json /sdcard/Download/inbound-trial-* $EXT/inbound-trial-*" >/dev/null
  local t0 b0 dl0 md0 b1 dl1 md1
  t0=$(temp); b0=$(busy); dl0=$(pidstat com.android.providers.downloads); md0=$(pidstat com.android.providers.media.module)
  local ts=$(date +%s)
  "$ADB" shell am start -n $PKG/.MainActivity --es url "'$url'" --ei segments "$conns" --es dest "$dest" --es runId "$label" "${extras[@]}" >/dev/null
  local i
  for i in $(seq 1 400); do
    sleep 1; "$ADB" shell cat "$EXT/result-$label.json" 2>/dev/null | tr -d '\r' > "$OUT/$label.json"; [ -s "$OUT/$label.json" ] && break
  done
  b1=$(busy); dl1=$(pidstat com.android.providers.downloads); md1=$(pidstat com.android.providers.media.module)
  local win=$(( $(date +%s) - ts ))
  python "$(dirname "$0")/summarize-net.py" row "$OUT/$label.json" "$label" "$t0" "$(temp)" "$(( b1 - b0 ))" "$win" "$(( dl1 - dl0 ))" "$(( md1 - md0 ))" | tee -a "$ROWS"
  "$ADB" shell "rm -f /sdcard/Download/inbound-trial-* $EXT/inbound-trial-*" >/dev/null
}

exec 3<&0 </dev/null   # adb shell would eat the spec otherwise
while read -u 3 -r label dest conns rest; do
  [ -z "${label:-}" ] && continue; case "$label" in \#*) continue;; esac
  for r in $(seq 1 "$REPEAT"); do run_one "$label-r$r" "$dest" "$conns" $rest; cool; done
done
