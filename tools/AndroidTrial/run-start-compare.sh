#!/usr/bin/env bash
# ANDROID.2 start-up trial, fair comparison: the cold starts of several already-built variants are interleaved (round 1 of every
# variant, then round 2, ...) so that drift of the emulator or the PC hits all of them alike. Before each counted start the variant's
# APK is installed over the previous one (same state folder; the job and settings files are runtime-independent) and started once,
# not counted.
#   APK_DIR=artifacts/android-trial/apks VARIANTS="default fullaot coreclr nativeaot" ROUNDS=7 bash tools/AndroidTrial/run-start-compare.sh
# Each <variant>.apk in APK_DIR comes from: dotnet build|publish tools/AndroidTrial/AndroidTrial.csproj -c Release
#   -p:RuntimeIdentifier=android-x64 -p:TrialVariant=<variant>   (default: no TrialVariant; nativeaot: publish). See run-start.sh.
# Results: artifacts/android-trial/compare-<time>/<variant>/{empty,jobs}N.json and a summary per variant.
set -u
export MSYS_NO_PATHCONV=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
APK_DIR="${APK_DIR:-$ROOT/artifacts/android-trial/apks}"
VARIANTS="${VARIANTS:-default fullaot coreclr nativeaot}"; ROUNDS="${ROUNDS:-7}"; JOBS="${JOBS:-200}"
PKG=app.inbound.trial; EXT=/sdcard/Android/data/$PKG/files
OUT="$ROOT/artifacts/android-trial/compare-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$OUT"
"$ADB" get-state >/dev/null 2>&1 || { echo "No device: start the emulator first."; exit 1; }
for v in $VARIANTS; do mkdir -p "$OUT/$v"; done

install() { "$ADB" install -r -d "$APK_DIR/$1.apk" | tail -1; }
cold() { # variant label index
  local v=$1 label=$2 i=$3 id="$2$3" dir="$OUT/$1"
  "$ADB" shell am force-stop $PKG; sleep 1
  "$ADB" shell rm -f "$EXT/host-result-$id-add.json" >/dev/null
  "$ADB" shell am start -W -n $PKG/.MainActivity --es mode host --es phase add --es runId "$id" --ez idle true | tr -d '\r' > "$dir/$id-amstart.txt"
  for _ in $(seq 1 120); do
    "$ADB" shell cat "$EXT/host-result-$id-add.json" 2>/dev/null | tr -d '\r' > "$dir/$id.json"
    [ -s "$dir/$id.json" ] && break; sleep 0.5
  done
  [ -s "$dir/$id.json" ] || echo "no result for $v $id"
}
round_all() { # state
  for r in $(seq 1 "$ROUNDS"); do
    for v in $VARIANTS; do
      install "$v" >/dev/null
      cold "$v" "$1-warmup$r-" 0 # not counted (the name has no digit right after the label)
      cold "$v" "$1" "$r"
    done
    echo "round $r of $1 done"
  done
}

first=${VARIANTS%% *}
install "$first" >/dev/null; "$ADB" shell pm clear $PKG >/dev/null
echo "== empty state"; round_all empty
if [ "$JOBS" -gt 0 ]; then
  echo "== seeding $JOBS jobs"
  install "$first" >/dev/null; "$ADB" shell pm clear $PKG >/dev/null; "$ADB" shell am force-stop $PKG
  "$ADB" shell am start -W -n $PKG/.MainActivity --es mode host --es phase seed --es runId seed --ei count "$JOBS" >/dev/null
  for _ in $(seq 1 120); do "$ADB" shell cat "$EXT/host-result-seed-seed.json" 2>/dev/null | tr -d '\r' > "$OUT/seed.json"; [ -s "$OUT/seed.json" ] && break; sleep 0.5; done
  echo "== $JOBS-job state"; round_all jobs
fi
for v in $VARIANTS; do
  python "$ROOT/tools/AndroidTrial/summarize-start.py" "$OUT/$v" "$v" "$(stat -c %s "$APK_DIR/$v.apk")" > "$OUT/$v/summary.txt"
done
python "$ROOT/tools/AndroidTrial/summarize-compare.py" "$OUT" $VARIANTS | tee "$OUT/compare.md"
