#!/usr/bin/env bash
# ANDROID.2 start-up trial: where the time goes between process start and "hello + snapshot", cold starts, empty state and ~200 jobs.
#   VARIANT=default|noaot|fullaot|fullaot-llvm|trimfull|fullaot-trimfull|marshal|coreclr|nativeaot|customprofile  bash tools/AndroidTrial/run-start.sh
# Env: RUNS (cold starts per state, default 5), JOBS (seeded jobs for the second state, default 200; 0 skips it),
#      SKIP_BUILD=1 (install the newest APK as is), APK_FILE=<prebuilt apk> (install that one, no build), TRIAL_PROFILE=<file.aprof> (VARIANT=customprofile),
#      PASS_CHECK=1 (also run run-host.sh with the same variant afterwards)
# Build: dotnet build -c Release -p:RuntimeIdentifier=${RID:-android-x64} ${MINSDK:+-p:SupportedOSPlatformVersion=$MINSDK} -p:TrialVariant=$VARIANT (nativeaot: dotnet publish). The default
# build is the one with no TrialVariant. Needs a running emulator (start it from Bash). Results: artifacts/android-trial/start-<variant>-<time>/
set -u
export MSYS_NO_PATHCONV=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
VARIANT="${VARIANT:-default}"; RUNS="${RUNS:-5}"; JOBS="${JOBS:-200}"
PKG=app.inbound.trial; EXT=/sdcard/Android/data/$PKG/files
OUT="${OUT_DIR:-$ROOT/artifacts/android-trial/start-$VARIANT-${WARM:+warm-${WARM//,/+}-}$(date +%Y%m%d-%H%M%S)}"; mkdir -p "$OUT"
"$ADB" get-state >/dev/null 2>&1 || { echo "No device: start the emulator first."; exit 1; }
PRJ="$ROOT/tools/AndroidTrial/AndroidTrial.csproj"

if [ -n "${APK_FILE:-}" ]; then SKIP_BUILD=1; fi   # APK_FILE=<a prebuilt apk>: no build, install this one
if [ -z "${SKIP_BUILD:-}" ]; then
  rm -rf "$ROOT/tools/AndroidTrial/bin/Release" "$ROOT/tools/AndroidTrial/obj/Release" "$ROOT/tools/AndroidTrial/obj/project.assets.json"
  VARG=""; [ "$VARIANT" != default ] && VARG="-p:TrialVariant=$VARIANT"
  [ -n "${TRIAL_PROFILE:-}" ] && VARG="$VARG -p:TrialProfile=$TRIAL_PROFILE"
  if [ "$VARIANT" = nativeaot ]; then VERB=publish; else VERB=build; fi
  echo "== dotnet $VERB $VARG"
  dotnet $VERB "$PRJ" -c Release -p:AndroidSdkDirectory="$SDK" -p:RuntimeIdentifier=${RID:-android-x64} ${MINSDK:+-p:SupportedOSPlatformVersion=$MINSDK} $VARG -bl:"$OUT/build.binlog" > "$OUT/build.log" 2>&1
  echo "build exit $?; $(grep -cE 'warning' "$OUT/build.log") warning lines (distinct: $(grep -oE 'warning [A-Z]+[0-9]+' "$OUT/build.log" | sort -u | tr '\n' ' '))" | tee "$OUT/build-summary.txt"
  grep -E "error " "$OUT/build.log" | sort -u | head -10
fi
APK="${APK_FILE:+$(cygpath -m "$APK_FILE")}"; [ -n "$APK" ] || APK="$(find "$ROOT/tools/AndroidTrial/bin/Release" -name "$PKG-Signed.apk" -printf '%T@ %p\n' | sort -rn | head -1 | cut -d' ' -f2-)"
[ -n "$APK" ] || { echo "no APK built"; exit 2; }
cp "$APK" "$OUT/app.apk"; APKBYTES=$(stat -c %s "$APK"); echo "APK: $APK $APKBYTES bytes" | tee -a "$OUT/build-summary.txt"
"$ADB" uninstall $PKG >/dev/null 2>&1
"$ADB" install -r "$APK" | tail -1; sleep 20   # let the install-time background work settle
CODE=$("$ADB" shell pm path $PKG | tr -d '\r' | head -1 | sed 's/package://; s#/[^/]*$##')
echo "installed code dir: $CODE; du -sk: $("$ADB" shell du -sk "$CODE" | tr -d '\r' | cut -f1) KB (after install, before first run)" | tee -a "$OUT/build-summary.txt"

cold() { # label index -> result json + am start -W output
  local label=$1 i=$2 id="$1$2"
  "$ADB" shell am force-stop $PKG; sleep 1
  "$ADB" shell rm -f "$EXT/host-result-$id-add.json" >/dev/null
  # WARM=<flags>: the phone-trial warm-up (WarmUp.cs) from Application.OnCreate: all|prot|lazyprot|settings|jobs|ipc|host, comma list. Empty = off.
  if [ -n "${WARM:-}" ]; then "$ADB" shell "mkdir -p $EXT; echo $WARM > $EXT/warm.flag" >/dev/null; else "$ADB" shell "rm -f $EXT/warm.flag" >/dev/null; fi
  "$ADB" shell am start -W -n $PKG/.MainActivity --es mode host --es phase add --es runId "$id" --ez idle true | tr -d '\r' > "$OUT/$id-amstart.txt"
  for _ in $(seq 1 120); do
    "$ADB" shell cat "$EXT/host-result-$id-add.json" 2>/dev/null | tr -d '\r' > "$OUT/$id.json"
    [ -s "$OUT/$id.json" ] && break; sleep 0.5
  done
  [ -s "$OUT/$id.json" ] || echo "no result for $id"
}
state_runs() { # label
  cold "$1-warmup" 0   # first start on this state (creates key/settings, settles the job files); not counted
  for i in $(seq 1 "$RUNS"); do cold "$1" "$i"; done
}

"$ADB" shell pm clear $PKG >/dev/null
echo "== empty state"; state_runs empty
if [ "$JOBS" -gt 0 ]; then
  echo "== seeding $JOBS jobs"
  "$ADB" shell pm clear $PKG >/dev/null; "$ADB" shell am force-stop $PKG
  "$ADB" shell am start -W -n $PKG/.MainActivity --es mode host --es phase seed --es runId seed --ei count "$JOBS" >/dev/null
  for _ in $(seq 1 120); do "$ADB" shell cat "$EXT/host-result-seed-seed.json" 2>/dev/null | tr -d '\r' > "$OUT/seed.json"; [ -s "$OUT/seed.json" ] && break; sleep 0.5; done
  echo "== $JOBS-job state"; state_runs jobs
fi
python "$ROOT/tools/AndroidTrial/summarize-start.py" "$OUT" "$VARIANT" "$APKBYTES" | tee "$OUT/summary.txt"
if [ -n "${PASS_CHECK:-}" ]; then
  echo "== run-host.sh with the same variant"
  TRIAL_VARIANT=$([ "$VARIANT" = default ] && echo "" || echo "$VARIANT") bash "$ROOT/tools/AndroidTrial/run-host.sh" 2>&1 | tee "$OUT/run-host.log" | tail -4
  python "$ROOT/tools/AndroidTrial/check-host-result.py" "$(ls -dt "$ROOT"/artifacts/android-trial/host-2* | head -1)" | tee "$OUT/run-host-verdict.txt"
fi
