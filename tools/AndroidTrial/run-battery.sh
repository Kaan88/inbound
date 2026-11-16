#!/usr/bin/env bash
# ANDROID.2 phone trial: battery cost of a 12 GiB download, trial app engine vs the phone's browser (Jelly), over Wi-Fi, unplugged.
#   ANDROID_SERIAL=<serial> OUT=<dir> bash tools/AndroidTrial/run-battery.sh smoke | full [extra labels] | one <label>...
# Window = from one Charge-counter step edge to a later one (>= 480 s after the start and >= 20 s after the download is done).
# One 3 s poll loop, the same command in every case.
# Samples: <label>.samples.tsv  (epoch_ms, charge_uAh, mV, temp_dC, level, size of download files [Jelly's DownloadManager preallocates the
#          full size, so this is NOT progress for the browser], result_json_exists, wlan0 rx bytes)
# Events:  <label>.events.tsv   (name, epoch_ms): window_start, case_start, first_bytes, done, window_end
set -u
export MSYS_NO_PATHCONV=1
: "${ANDROID_SERIAL:?set ANDROID_SERIAL}"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"; ADB="$SDK/platform-tools/adb.exe"
PKG=app.inbound.trial; EXT=/sdcard/Android/data/$PKG/files; JELLY=${BROWSER:-org.lineageos.jelly}
BASE="${BASE:-http://192.168.1.115:5601}"; HERE="$(cd "$(dirname "$0")" && pwd -W)"
MODE="${1:-}"; shift || true
if [ "$MODE" = smoke ]; then SIZE=${SIZE:-268435456}; OUT="${OUT:?set OUT}/smoke"; MINWIN=0; MINDONE=5; GUARD=300
else SIZE=${SIZE:-12884901888}; OUT="${OUT:?set OUT}"; MINWIN=${MINWIN:-480}; MINDONE=20; GUARD=${GUARD:-900}; fi
mkdir -p "$OUT"; ROWS="$OUT/rows.tsv"
ENVX='DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1'
now() { python -c 'import time;print(int(time.time()*1000))'; }
PROBE="dumpsys battery | grep -E 'Charge counter|voltage|temperature|level'; echo SZ \$(find /sdcard/Download $EXT -maxdepth 1 \\( -name 'bench*' -o -name '.*bench*' -o -name 'inbound-trial-*' \\) -exec stat -c %s {} + 2>/dev/null | tr '\\n' ' '); set -- \$(grep wlan0 /proc/net/dev | tr : ' '); echo RX \$2; [ -s $EXT/result-\$RUNID.json ] && echo RES"

# poll <samples-file> <runid>: sets CC V T LV SZ RES and TS (PC epoch ms), appends one line
poll() {
  local out l; TS=$(now)
  out=$("$ADB" shell "RUNID=$2; $PROBE" | tr -d '\r')
  CC=; V=; T=; LV=; SZ=0; RES=0; RX=0
  while read -r l; do case "$l" in
    "Charge counter:"*) CC=${l#*: };; "voltage:"*) V=${l#*: };; "temperature:"*) T=${l#*: };; "level:"*) LV=${l#*: };;
    SZ*) SZ=0; for n in ${l#SZ}; do SZ=$((SZ + n)); done;; RX*) RX=${l#RX };; RES) RES=1;; esac
  done <<< "$out"
  [ -n "$CC" ] && printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$TS" "$CC" "$V" "$T" "$LV" "$SZ" "$RES" "$RX" >> "$1"
}
cleanup_phone() { "$ADB" shell "rm -f /sdcard/Download/bench* /sdcard/Download/.*bench* /sdcard/Download/inbound-trial-* $EXT/inbound-trial-* $EXT/result-*.json" >/dev/null; }
stop_apps() { "$ADB" shell "am force-stop $PKG; am force-stop $JELLY" >/dev/null; }
ev() { printf '%s\t%s\n' "$1" "$2" >> "$EV"; echo "[$label] $1 $2"; }

# tap_ui <regex>: find a clickable node whose text/desc/id matches, tap its centre, echo what was tapped
tap_ui() {
  local line
  line=$("$ADB" exec-out uiautomator dump /dev/tty 2>/dev/null | tr -d '\r' | python "$HERE/uitap.py" "$1")
  if [ -n "$line" ]; then set -- $line; "$ADB" shell input tap "$1" "$2"; echo "tapped $line"; fi
}

run_window() { # label kind
  label=$1; local kind=$2 S="$OUT/$1.samples.tsv"; local runid=$1; EV="$OUT/$1.events.tsv"; : > "$S"; : > "$EV"; : > "$OUT/$1.notes.txt"
  echo "=== $label ($kind) size=$SIZE $(date +%T)"
  "$ADB" shell "input keyevent KEYCODE_WAKEUP; wm dismiss-keyguard" >/dev/null
  cleanup_phone; stop_apps; "$ADB" shell input keyevent KEYCODE_HOME >/dev/null; sleep 2
  poll "$S" "$runid"
  while [ "${T:-0}" -ge 380 ]; do echo "waited: temperature ${T} dC, +30 s" | tee -a "$OUT/$label.notes.txt"; sleep 30; poll "$S" "$runid"; done
  "$ADB" shell dumpsys batterystats --reset >/dev/null
  : > "$S"   # the window's samples start after the reset
  poll "$S" "$runid"; local cc0=$CC start_ms t_loop=$(date +%s)
  if [ "$MODE" = smoke ]; then start_ms=$TS
  else
    while :; do
      sleep 3; poll "$S" "$runid"; [ "$CC" != "$cc0" ] && break
      [ $(( $(date +%s) - t_loop )) -gt 600 ] && { echo "no start edge within 600 s, started anyway" | tee -a "$OUT/$label.notes.txt"; break; }
    done
    start_ms=$TS
  fi
  ev window_start "$start_ms"; local rx0=$RX prx=$RX quiet=0
  case "$kind" in
    idle) "$ADB" shell input keyevent KEYCODE_HOME >/dev/null; ev case_start "$(now)";;
    app-c1|app-c8)
      ev case_start "$(now)"
      "$ADB" shell am start -n $PKG/.MainActivity --es url "'$BASE/bench.bin?size=$SIZE'" --ei segments "${kind#app-c}" --es dest app --es runId "$runid" --es env "'$ENVX'" >/dev/null;;
    browser)
      ev case_start "$(now)"
      "$ADB" shell am start -a android.intent.action.VIEW -d "'$BASE/bench.bin?size=$SIZE'" $JELLY >/dev/null;;
  esac
  local tapped=0 stable=0 end_ms= doneflag=0 done_ms= first_growth= i=0 armed= r el
  while :; do
    sleep 3; poll "$S" "$runid"; i=$((i+1)); el=$(( (TS - start_ms) / 1000 ))
    case "$kind" in
      browser)
        if [ $doneflag = 0 ]; then
          local got=$((RX - rx0)) d=$((RX - prx))
          if [ $tapped -gt 0 ] && [ $got -gt 2000000 ] && [ -z "$first_growth" ]; then first_growth=$TS; ev first_bytes "$TS"; fi
          if [ -z "$first_growth" ] && [ $tapped -lt 6 ]; then   # a dialog is probably waiting
            r=$(tap_ui '^(Download|DOWNLOAD|Allow|ALLOW|OK|Yes|Continue)$|permission_allow_button|button1')
            if [ -n "$r" ]; then tapped=$((tapped+1)); echo "$r" | tee -a "$OUT/$label.notes.txt"; ev tap "$(now)"; fi
          fi
          # done = wlan0 rx since the start reached the file size and the link went quiet for two samples
          if [ $got -ge $((SIZE / 100 * 99)) ] && [ $d -lt 1000000 ]; then quiet=$((quiet+1)); [ $quiet = 1 ] && qts=$TS; else quiet=0; fi
          [ $quiet -ge 2 ] && { doneflag=1; done_ms=$qts; ev done "$done_ms"; ev rx_total "$got"; }
          prx=$RX
        fi;;
      app-c1|app-c8)
        if [ $doneflag = 0 ] && [ "$RES" = 1 ]; then
          doneflag=1; done_ms=$TS; ev done "$done_ms"; "$ADB" shell cat "$EXT/result-$runid.json" | tr -d '\r' > "$OUT/$label.json"
        fi;;
      idle) doneflag=1; done_ms=$start_ms;;
    esac
    if [ "$MODE" = smoke ]; then [ $doneflag = 1 ] && [ $(( (TS - done_ms)/1000 )) -ge $MINDONE ] && break
    elif [ $doneflag = 1 ] && [ $el -ge $MINWIN ] && [ $(( (TS - done_ms)/1000 )) -ge $MINDONE ]; then
      if [ -z "$armed" ]; then armed=$CC; elif [ "$CC" != "$armed" ]; then end_ms=$TS; break; fi   # the next counter change is the end edge
    fi
    [ $el -gt $GUARD ] && { echo "GUARD ($GUARD s) hit; window ends at the last sample, not an edge" | tee -a "$OUT/$label.notes.txt"; break; }
  done
  [ -z "$end_ms" ] && end_ms=$TS && echo "window did not end on a counter edge (smoke/guard)" >> "$OUT/$label.notes.txt"
  ev window_end "$end_ms"
  "$ADB" shell dumpsys batterystats --charged | tr -d '\r' > "$OUT/$label.batterystats.txt"
  "$ADB" shell "ls -l /sdcard/Download $EXT" | tr -d '\r' > "$OUT/$label.files-after.txt"
  cleanup_phone; stop_apps; "$ADB" shell input keyevent KEYCODE_HOME >/dev/null
  python "$HERE/summarize-battery.py" row "$(cygpath -m "$OUT")" "$label" "$kind" "$SIZE" | tee -a "$ROWS"
}

case "$MODE" in
  smoke) for c in ${@:-idle app-c1 browser}; do run_window "smoke-$c" "$c"; done;;
  full) for l in idle-1 app-c1-1 browser-1 app-c8-1 app-c8-2 browser-2 app-c1-2 idle-2 "$@"; do
          lv=$("$ADB" shell dumpsys battery | tr -d '\r' | awk '/level/{print $2}'); [ "${lv:-100}" -lt 30 ] && { echo "level $lv < 30, stop"; break; }
          run_window "$l" "${l%-*}"; done;;
  one) for l in "$@"; do run_window "$l" "${l%-*}"; done;;
esac
