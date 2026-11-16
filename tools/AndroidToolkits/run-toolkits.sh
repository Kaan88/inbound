#!/usr/bin/env bash
# ANDROID.2 toolkit trial: one app per run, same measurements for each. Emulator only; needs a running emulator (start it from Bash).
#   bash tools/AndroidToolkits/run-toolkits.sh <native|maui|avalonia|uno>
# Env: RUNS (cold starts per state, default 5), JOBS (seeded jobs, default 200), SKIP_BUILD=1 (use the newest APK), PHASES (default
#      "build size empty seeded idle smooth shots noanim dump"; pick some), PORT (test server, default 8765)
# Build: default Release of each project (dotnet build -c Release -p:RuntimeIdentifier=android-x64); the runtime/AOT mode is whatever that is
# for the project (printed in the summary from the APK's contents). Results: artifacts/android-trial/toolkits/<name>/run-<time>/
set -u
export MSYS_NO_PATHCONV=1
export ANDROID_SERIAL="${ANDROID_SERIAL:-emulator-5554}"   # never touch any other attached device
NAME="${1:?name: native|maui|avalonia|uno}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
RUNS="${RUNS:-5}"; JOBS="${JOBS:-200}"; PORT="${PORT:-8765}"
PHASES="${PHASES:-build size empty seeded idle smooth sf shots noanim dump}"
case "$NAME" in
  native)   PRJ=Native/Native.csproj;        PKG=app.inbound.tk.nativeui ;;
  maui)     PRJ=Maui/Maui.csproj;            PKG=app.inbound.tk.maui ;;
  avalonia) PRJ=Avalonia/AvaloniaTk.csproj;  PKG=app.inbound.tk.avalonia ;;
  uno)      PRJ=Uno/UnoTk.csproj;            PKG=app.inbound.tk.uno ;;
  *) echo "unknown toolkit"; exit 1 ;;
esac
ACT=$PKG/$PKG.MainActivity
DIR="$ROOT/tools/AndroidToolkits/$(dirname $PRJ)"
OUT="$ROOT/artifacts/android-trial/toolkits/$NAME/run-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$OUT"
SHOTS="$ROOT/artifacts/android-trial/toolkits/$NAME"
adb() { timeout 120 "$ADB" "$@"; }
"$ADB" get-state >/dev/null 2>&1 || { echo "No device: start the emulator first."; exit 1; }
has() { [[ " $PHASES " == *" $1 "* ]]; }
median() { sort -n | awk '{a[NR]=$1} END{ if(NR==0){print "-"} else print a[int((NR+1)/2)] }'; }

# test server (throttled files) + reverse port
if ! curl -s -o /dev/null "http://127.0.0.1:$PORT/sha256?size=1"; then
  (dotnet "$ROOT/tools/AndroidTrialServer/bin/Release/net10.0/AndroidTrialServer.dll" --port $PORT > "$OUT/server.log" 2>&1 &)
  sleep 4
fi
adb reverse tcp:$PORT tcp:$PORT >/dev/null
ORIG_NIGHT=$(adb shell cmd uimode night | tr -d '\r' | sed 's/Night mode: //')
restore() { adb shell cmd uimode night "${ORIG_NIGHT:-auto}" >/dev/null 2>&1; for k in animator_duration_scale transition_animation_scale window_animation_scale; do adb shell settings put global $k 1.0 >/dev/null 2>&1; done; }
trap restore EXIT

apk_path() { find "$DIR/bin/Release" -name "$PKG-Signed.apk" -printf '%T@ %p\n' | sort -rn | head -1 | cut -d' ' -f2-; }
pid_of() { adb shell pidof $PKG | tr -d '\r' | awk '{print $1}'; }
settle() { sleep "${1:-20}"; }
to_top() { for _ in 1 2 3; do adb shell input swipe 540 700 540 2100 150; sleep 0.4; done; sleep 1; }   # new rows are inserted above the first visible one; bring the list to its top

start_cold() { # extras...  -> fills AM_TOTAL, FULL_MS, ROWS
  adb shell am force-stop $PKG; sleep 1
  adb logcat -c
  AM_TOTAL=$(adb shell am start -W -n $ACT "$@" | tr -d '\r' | grep TotalTime | awk '{print $2}')
  FULL_MS=""; ROWS=""
  for _ in $(seq 1 90); do
    L=$(adb logcat -d -s InboundTk | grep fullyDrawn | tail -1)
    if [ -n "$L" ]; then FULL_MS=$(echo "$L" | sed 's/.*fullyDrawn=\([0-9]*\)ms.*/\1/'); ROWS=$(echo "$L" | sed 's/.*rows=\([0-9]*\).*/\1/'); break; fi
    sleep 0.5
  done
}
cold_series() { # label extras...
  local label=$1; shift
  start_cold "$@"; sleep 3   # warm-up start, not counted
  : > "$OUT/$label.first"; : > "$OUT/$label.full"
  for i in $(seq 1 $RUNS); do
    start_cold "$@"; echo "$AM_TOTAL" >> "$OUT/$label.first"; echo "$FULL_MS" >> "$OUT/$label.full"
    echo "  $label run $i: TotalTime=$AM_TOTAL fullyDrawn=$FULL_MS rows=$ROWS"; sleep 2
  done
  echo "$label: first-frame median $(median < $OUT/$label.first) ms; fully drawn median $(median < $OUT/$label.full) ms" | tee -a "$OUT/summary.txt"
}
gfx_summary() { # file
  grep -E "Total frames rendered|Janky frames|50th percentile|90th percentile|95th percentile|99th percentile" "$1" | tr -s ' ' | tr '\n' ';'; echo
}
sf_sample() { # seconds outfile: repeated SurfaceFlinger --latency dumps of every layer of this app (gfxinfo can't see surfaces an app draws itself)
  local end=$((SECONDS + $1)) L; : > "$2"
  mapfile -t LAYERS < <(adb shell dumpsys SurfaceFlinger --list | tr -d '' | grep -o "SurfaceView\[$PKG[^]]*\](BLAST)" | sort -u)
  while [ $SECONDS -lt $end ]; do
    for L in "${LAYERS[@]}"; do echo "#L $L" >> "$2"; adb shell "dumpsys SurfaceFlinger --latency '$L'" | tr -d '' >> "$2"; done
    sleep 0.8
  done
}
sf_report() { python "$ROOT/tools/AndroidToolkits/sfstats.py" "$1" | sed 's/^/    sf: /'; }
ui_dump() { # outfile: uiautomator dump with retries; the file is deleted first so a failed dump can't leave an older app's tree behind
  adb shell rm -f /sdcard/uidump.xml >/dev/null 2>&1
  for _ in 1 2 3; do adb shell uiautomator dump /sdcard/uidump.xml >/dev/null 2>&1; adb shell cat /sdcard/uidump.xml 2>/dev/null | tr -d '' > "$1"; [ -s "$1" ] && grep -q "$PKG" "$1" && return 0; sleep 2; done
  echo "UI DUMP FAILED" > "$1"; return 1
}
ticks() { local p; p=$(pid_of); [ -n "$p" ] && adb shell cat /proc/$p/stat | tr -d '\r' | awk '{print $14+$15}'; }

if has build; then
  if [ -z "${SKIP_BUILD:-}" ]; then
    rm -rf "$DIR/bin/Release" "$DIR/obj/Release"
    dotnet build "$ROOT/tools/AndroidToolkits/$PRJ" -c Release -p:AndroidSdkDirectory="$SDK" -p:RuntimeIdentifier=${RID:-android-x64} -p:AndroidTrialRid=${RID:-android-x64} > "$OUT/build.log" 2>&1
    echo "build exit $?; $(grep -E 'Warn.*\(s\)|Error\(s\)' "$OUT/build.log" | tr -s ' ' | tr '\n' ' ')" | tee -a "$OUT/summary.txt"
  fi
fi
APK="$(apk_path)"; [ -n "$APK" ] || { echo "no APK"; exit 2; }
cp "$APK" "$OUT/app.apk"
if has size; then
  echo "apk bytes: $(stat -c %s "$APK")  (x86_64 only)" | tee -a "$OUT/summary.txt"
  adb uninstall $PKG >/dev/null 2>&1
  adb install -r "$APK" | tail -1; settle 20
  CODE=$(adb shell pm path $PKG | tr -d '\r' | head -1 | sed 's/package://; s#/[^/]*$##')
  echo "installed code dir $CODE: $(adb shell du -sk "$CODE" | tr -d '\r' | cut -f1) KB (after install, before first run)" | tee -a "$OUT/summary.txt"
  unzip -l "$APK" | grep -E "libmonosgen|libcoreclr|libmonodroid|assemblies|\.so$" | awk '{print $4}' | head -20 > "$OUT/apk-native-libs.txt"
  unzip -l "$APK" | tail -1 | tee -a "$OUT/summary.txt"
else
  adb install -r "$APK" >/dev/null; settle 5
fi

if has empty; then
  echo "== empty state"
  adb shell pm clear $PKG >/dev/null
  cold_series empty
fi

if has seeded; then
  echo "== seeding $JOBS jobs"
  adb shell pm clear $PKG >/dev/null; adb shell am force-stop $PKG; adb logcat -c
  adb shell am start -W -n $ACT --ei seed $JOBS --ei real 0 >/dev/null
  for _ in $(seq 1 240); do adb logcat -d -s InboundTk | grep -q "seeded=" && break; sleep 1; done
  adb logcat -d -s InboundTk | grep "seeded=" | tail -1 | tee -a "$OUT/summary.txt"; sleep 3
  cold_series seeded
fi

reseed() { # clean state: JOBS idle jobs, no real downloads (a restarted run would otherwise leave old downloads holding the engine's slots)
  adb shell pm clear $PKG >/dev/null; adb shell am force-stop $PKG; adb logcat -c
  adb shell am start -W -n $ACT --ei seed $JOBS --ei real 0 >/dev/null
  for _ in $(seq 1 240); do adb logcat -d -s InboundTk | grep -q "seeded=" && break; sleep 1; done
  sleep 3
}

if has idle; then
  echo "== idle 60 s, list shown, no downloads (state: the seeded jobs, all idle)"
  reseed
  start_cold; settle 15
  adb shell dumpsys gfxinfo $PKG reset >/dev/null
  T0=$(ticks); sleep 60; T1=$(ticks)
  adb shell dumpsys gfxinfo $PKG > "$OUT/idle-gfxinfo.txt"
  echo "idle: CPU ticks (utime+stime, 1 tick = 10 ms) in 60 s: $((T1 - T0)); $(gfx_summary "$OUT/idle-gfxinfo.txt")" | tee -a "$OUT/summary.txt"
fi

if has smooth; then
  echo "== three downloads + one scroll (30 s)"
  reseed
  start_cold --ei downloads 3; settle 20; to_top
  adb shell dumpsys meminfo $PKG > "$OUT/meminfo.txt"
  echo "meminfo: $(grep -E 'TOTAL PSS|TOTAL:' "$OUT/meminfo.txt" | head -2 | tr -s ' ' | tr '\n' ';')" | tee -a "$OUT/summary.txt"
  adb shell dumpsys gfxinfo $PKG reset >/dev/null
  sleep 8
  adb shell input swipe 540 1800 540 600 600; sleep 1; adb shell input swipe 540 600 540 1800 600
  sleep 8
  adb shell input swipe 540 1800 540 600 300; sleep 1; adb shell input swipe 540 600 540 1800 300
  sleep 10
  adb shell dumpsys gfxinfo $PKG > "$OUT/smooth-gfxinfo.txt"
  adb shell dumpsys gfxinfo $PKG framestats > "$OUT/smooth-framestats.txt"
  echo "smooth: $(gfx_summary "$OUT/smooth-gfxinfo.txt")" | tee -a "$OUT/summary.txt"
  adb exec-out screencap -p > "$SHOTS/mid-download.png"
fi

if has sf; then
  echo "== SurfaceFlinger frame statistics (same scenarios; the sampler itself loads the guest, so these are NOT comparable with the gfxinfo numbers)"
  reseed; start_cold; settle 15
  sf_sample 30 "$OUT/sf-idle.txt"; echo "sf idle (30 s, list shown, nothing running):" | tee -a "$OUT/summary.txt"; sf_report "$OUT/sf-idle.txt" | tee -a "$OUT/summary.txt"
  reseed; start_cold --ei downloads 3; settle 20; to_top
  sf_sample 40 "$OUT/sf-smooth-bg.txt" & SFPID=$!
  sleep 8
  adb shell input swipe 540 1800 540 600 600; sleep 1; adb shell input swipe 540 600 540 1800 600
  sleep 8
  adb shell input swipe 540 1800 540 600 300; sleep 1; adb shell input swipe 540 600 540 1800 300
  sleep 10; wait $SFPID
  echo "sf smooth (three downloads, two scrolls):" | tee -a "$OUT/summary.txt"; sf_report "$OUT/sf-smooth-bg.txt" | tee -a "$OUT/summary.txt"
fi

if has shots; then
  echo "== screenshots"
  for mode in no yes; do
    adb shell cmd uimode night $mode >/dev/null; sleep 2
    reseed
    start_cold --ei downloads 3; settle 14; to_top
    adb exec-out screencap -p > "$SHOTS/$([ $mode = yes ] && echo dark || echo light).png"
  done
  for mode in no yes; do
    adb shell cmd uimode night $mode >/dev/null; sleep 2
    reseed
    start_cold --ei downloads 3 --ez dynamic true; settle 14; to_top
    adb exec-out screencap -p > "$SHOTS/dynamic-$([ $mode = yes ] && echo dark || echo light).png"
  done
  adb shell cmd uimode night "${ORIG_NIGHT:-auto}" >/dev/null
fi

if has noanim; then
  echo "== Remove animations on (animator/transition/window scale 0): does progress still move?"
  for k in animator_duration_scale transition_animation_scale window_animation_scale; do adb shell settings put global $k 0 >/dev/null; done
  reseed
  start_cold --ei downloads 3; settle 25; to_top
  ui_dump "$OUT/noanim-ui1.xml"
  adb exec-out screencap -p > "$SHOTS/no-animations-1.png"
  sleep 8
  adb exec-out screencap -p > "$SHOTS/no-animations-2.png"
  ui_dump "$OUT/noanim-ui2.xml"
  A=$(grep -o 'text="[0-9.]* [MG]B of [^"]*"' "$OUT/noanim-ui1.xml" | head -1); B=$(grep -o 'text="[0-9.]* [MG]B of [^"]*"' "$OUT/noanim-ui2.xml" | head -1)
  echo "noanim: first [$A] ; 8 s later [$B]; screenshots 8 s apart identical: $([ "$(md5sum < "$SHOTS/no-animations-1.png")" = "$(md5sum < "$SHOTS/no-animations-2.png")" ] && echo yes || echo no) (the status-bar clock can differ)" | tee -a "$OUT/summary.txt"
  adb exec-out screencap -p > "$SHOTS/no-animations.png"
  for k in animator_duration_scale transition_animation_scale window_animation_scale; do adb shell settings put global $k 1.0 >/dev/null; done
fi

if has dump; then
  echo "== accessibility tree (uiautomator dump)"
  reseed
  start_cold --ei downloads 3; settle 25; to_top
  ui_dump "$OUT/uiautomator.xml"
  echo "uiautomator: nodes=$(grep -o '<node ' "$OUT/uiautomator.xml" | wc -l) with text=$(grep -oE 'text="[^"]+"' "$OUT/uiautomator.xml" | wc -l) content-desc=$(grep -oE 'content-desc="[^"]+"' "$OUT/uiautomator.xml" | wc -l) clickable=$(grep -o 'clickable="true"' "$OUT/uiautomator.xml" | wc -l); 'seed-' rows exposed: $(grep -o 'text="seed-' "$OUT/uiautomator.xml" | wc -l)" | tee -a "$OUT/summary.txt"
  adb exec-out screencap -p > "$OUT/final.png"
fi
adb shell am force-stop $PKG
echo "done: $OUT"
