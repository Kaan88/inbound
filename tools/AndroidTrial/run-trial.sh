#!/usr/bin/env bash
# ANDROID.2 first trial: builds the trial app, installs it on a running emulator/phone, serves a file from this PC and
# runs the downloads and the kill/resume test. Prints a markdown table. Results (JSON per run, server log) go to
# artifacts/android-trial/<timestamp>/ (artifacts/ is git-ignored).
#
#   bash tools/AndroidTrial/run-trial.sh                      # 1 GB files
#   SIZE_MB=256 KILL_MB=512 bash tools/AndroidTrial/run-trial.sh
#
# Start the emulator first (from Bash): "$LOCALAPPDATA/Android/Sdk/emulator/emulator.exe" -avd kweb35 -no-snapshot-save &
# Environment:
#   SIZE_MB      size of the throughput files (default 1024)
#   KILL_MB      size of the kill-test file (default 1024), served at KILL_BPS bytes/s per connection (default 4000000)
#   NET          reverse (default: adb reverse, the emulator sees the PC as 127.0.0.1, fast) or nat (10.0.2.2, the emulator's
#                slow user-mode network: ~15 MB/s ceiling, it measures the emulator, not the engine)
#   SKIP_BUILD=1 reuse the APK that is built already
#   RID          android-x64 (default; emulator) or empty to build every RID in the project (x64 + arm64)
#   CONFIG       Release (default) or Debug
set -u
export MSYS_NO_PATHCONV=1   # Git Bash must not turn /sdcard into C:/Program Files/Git/sdcard
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"   # C:/... form: native tools get no path conversion (MSYS_NO_PATHCONV=1)
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
SIZE_MB="${SIZE_MB:-1024}"
KILL_MB="${KILL_MB:-1024}"
KILL_BPS="${KILL_BPS:-4000000}"
NET="${NET:-reverse}"
RID="${RID-android-x64}"
CONFIG="${CONFIG:-Release}"
PORT=5599
PKG=app.inbound.trial
EXT=/sdcard/Android/data/$PKG/files
OUT="$ROOT/artifacts/android-trial/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"
SIZE=$((SIZE_MB * 1024 * 1024))
KILL_SIZE=$((KILL_MB * 1024 * 1024))
ROWS="$OUT/rows.txt"; : > "$ROWS"

"$ADB" get-state >/dev/null 2>&1 || { echo "No device: start the emulator first (see the header of this script)."; exit 1; }

# ---- build and install ----
if [ -z "${SKIP_BUILD:-}" ]; then
  echo "== build ($CONFIG, ${RID:-all RIDs}) =="
  rid_arg=(); [ -n "$RID" ] && rid_arg=(-p:RuntimeIdentifier="$RID")
  dotnet build "$ROOT/tools/AndroidTrial/AndroidTrial.csproj" -c "$CONFIG" -p:AndroidSdkDirectory="$SDK" "${rid_arg[@]}" 2>&1 | tail -3
  dotnet build "$ROOT/tools/AndroidTrialServer/AndroidTrialServer.csproj" -c Release 2>&1 | tail -1
fi
APK="$(find "$ROOT/tools/AndroidTrial/bin/$CONFIG" -name "$PKG-Signed.apk" -printf '%T@ %p\n' | sort -rn | head -1 | cut -d' ' -f2-)"
[ -f "$APK" ] || { echo "No signed APK under tools/AndroidTrial/bin/$CONFIG"; exit 1; }
echo "APK: $APK ($(stat -c %s "$APK") bytes)"
"$ADB" install -r "$APK" | tail -1
"$ADB" shell am force-stop $PKG
"$ADB" shell rm -rf $EXT/result-*.json
APK_BYTES=$(stat -c %s "$APK")
INSTALLED_KB=$("$ADB" shell du -sk "$("$ADB" shell pm path $PKG | head -1 | sed 's/package://;s/base.apk//;s/\r//')" 2>/dev/null | cut -f1)

# ---- server on this PC ----
dotnet "$ROOT/tools/AndroidTrialServer/bin/Release/net10.0/AndroidTrialServer.dll" --port $PORT > "$OUT/server.log" 2>&1 &
SERVER_PID=$!
cleanup() { kill $SERVER_PID 2>/dev/null; "$ADB" reverse --remove tcp:$PORT >/dev/null 2>&1; }
trap cleanup EXIT
for _ in $(seq 1 30); do grep -q LISTENING "$OUT/server.log" && break; sleep 1; done
grep -q LISTENING "$OUT/server.log" || { echo "server did not start"; cat "$OUT/server.log"; exit 1; }
if [ "$NET" = reverse ]; then
  "$ADB" reverse tcp:$PORT tcp:$PORT >/dev/null; BASE="http://127.0.0.1:$PORT"
else
  BASE="http://10.0.2.2:$PORT"
fi
echo "server: $BASE (from the device); NET=$NET"

jget() { grep -oE "\"$2\":(\"[^\"]*\"|[^,}]*)" "$1" | head -1 | cut -d: -f2- | tr -d '"'; }

start_run() { # runId url segments dest [sha]
  "$ADB" shell am force-stop $PKG
  "$ADB" logcat -c
  local sha=(); [ -n "${5:-}" ] && sha=(--es sha256 "$5")
  "$ADB" shell am start -n $PKG/.MainActivity --es url "'$2'" --ei segments "$3" --es dest "$4" --es runId "$1" "${sha[@]}" > /dev/null
}

wait_result() { # runId timeoutSeconds -> file $OUT/<runId>.json
  local i
  for i in $(seq 1 $(( $2 / 2 ))); do
    sleep 2
    "$ADB" shell cat "$EXT/result-$1.json" 2>/dev/null | tr -d '\r' > "$OUT/$1.json"
    [ -s "$OUT/$1.json" ] && return 0
  done
  return 1
}

clean_payload() { "$ADB" shell "rm -f /sdcard/Download/inbound-trial-* /sdcard/Download/probe-* $EXT/inbound-trial-* $EXT/probe-*" >/dev/null 2>&1; }

one() { # label dest segments sizeBytes perm
  local label="$1" dest="$2" segs="$3" size="$4" perm="$5"
  local sha; sha=$(curl -s "http://127.0.0.1:$PORT/sha256?size=$size")
  clean_payload
  # a fresh state for every run id: the id is unique, so it never resumes
  start_run "$label" "$BASE/bench.bin?size=$size" "$segs" "$dest" "$sha"
  if ! wait_result "$label" $(( 120 + size / 5000000 )); then echo "$label: no result"; echo "$label|$dest|$perm|$segs|no result|||||||" >> "$ROWS"; return; fi
  local f="$OUT/$label.json"
  local ok; ok=$(jget "$f" success)
  local fatal; fatal=$(jget "$f" fatal)
  local mem; mem=$(awk -v b="$(jget "$f" peakWorkingSetKernelHwm)" 'BEGIN{printf "%.0f", b/1048576}')
  echo "$label|$dest|$perm|$segs|$(jget "$f" bytes)|$(awk -v x="$(jget "$f" wallSeconds)" 'BEGIN{printf "%.2f", x}')|$(awk -v x="$(jget "$f" mbPerSecond)" 'BEGIN{printf "%.1f", x}')|$(awk -v x="$(jget "$f" cpuSeconds)" 'BEGIN{printf "%.1f", x}')|$(awk -v x="$(jget "$f" cpuSecondsPerGB)" 'BEGIN{printf "%.1f", x}')|$mem|$(jget "$f" hashMatched)|$ok $(jget "$f" engineState) $(jget "$f" failure)${fatal:+ FATAL}" >> "$ROWS"
  clean_payload
}

if [ -z "${ONLY_KILL:-}" ]; then
echo "== throughput: loopback ceiling (no storage), then app / internal / downloads =="
one "raw1" raw 1 $SIZE none
one "raw8" raw 8 $SIZE none
for segs in 1 8; do
  one "app-$segs" app $segs $SIZE none
  one "internal-$segs" internal $segs $SIZE none
done

echo "== shared Downloads: no storage permission =="
"$ADB" shell appops set $PKG MANAGE_EXTERNAL_STORAGE deny >/dev/null 2>&1
for segs in 1 8; do one "dl-noperm-$segs" downloads $segs $SIZE none; done
echo "== shared Downloads: MANAGE_EXTERNAL_STORAGE granted (appops) =="
"$ADB" shell appops set $PKG MANAGE_EXTERNAL_STORAGE allow >/dev/null 2>&1
for segs in 1 8; do one "dl-allfiles-$segs" downloads $segs $SIZE all-files; done
"$ADB" shell appops set $PKG MANAGE_EXTERNAL_STORAGE deny >/dev/null 2>&1

fi

# ---- kill test ----
echo "== kill test: $KILL_MB MB, 8 connections at $KILL_BPS B/s each, force-stop at 25%, restart with the same runId =="
KDEST="${KDEST:-app}"
clean_payload
KSHA=$(curl -s "http://127.0.0.1:$PORT/sha256?size=$KILL_SIZE")
start_run kill1 "$BASE/bench.bin?size=$KILL_SIZE&bps=$KILL_BPS" 8 "$KDEST" "$KSHA"
KILLED_AT=0
for _ in $(seq 1 300); do
  sleep 1
  last=$("$ADB" logcat -d -s InboundTrial:I | grep -oE 'progress [0-9]+ /' | tail -1 | grep -oE '[0-9]+')
  [ -n "$last" ] && [ "$last" -ge $((KILL_SIZE / 4)) ] && { KILLED_AT=$last; break; }
done
sleep 6   # let at least one 5 s checkpoint land after the threshold
"$ADB" shell am force-stop $PKG
echo "killed after ~$KILLED_AT bytes were reported"
"$ADB" shell "ls -l $EXT" | tr -d '\r' | grep -E 'part|inbound-trial' > "$OUT/kill-files-after-kill.txt"
cat "$OUT/kill-files-after-kill.txt"
start_run kill1 "$BASE/bench.bin?size=$KILL_SIZE&bps=$KILL_BPS" 8 "$KDEST" "$KSHA"
if wait_result kill1 $(( 120 + KILL_SIZE / 2000000 )); then
  f="$OUT/kill1.json"
  RESUME_ROW="kill1-resume|$KDEST|none|8|$(jget "$f" bytes)|$(awk -v x="$(jget "$f" wallSeconds)" 'BEGIN{printf "%.2f", x}')|$(awk -v x="$(jget "$f" mbPerSecond)" 'BEGIN{printf "%.1f", x}')|$(awk -v x="$(jget "$f" cpuSeconds)" 'BEGIN{printf "%.1f", x}')||$(awk -v b="$(jget "$f" peakWorkingSetKernelHwm)" 'BEGIN{printf "%.0f", b/1048576}')|$(jget "$f" hashMatched)|resumed=$(jget "$f" resumed) bytesAtResume=$(jget "$f" bytesAtResume) partSizeAtResume=$(jget "$f" partFileSizeAtResume) final=$(jget "$f" finalExistsAtFinalName) partGone=$(jget "$f" partFileGone)"
else
  RESUME_ROW="kill1-resume|$KDEST|none|8|no result|||||||"
fi
echo "$RESUME_ROW" >> "$ROWS"
clean_payload

# ---- the table ----
{
  echo
  echo "Emulator run (not a phone). Config: $CONFIG, RID: ${RID:-all}, NET: $NET, file $SIZE_MB MB. APK $APK_BYTES bytes, installed ${INSTALLED_KB:-?} KB."
  echo
  echo "| run | dest | storage permission | segs | bytes | seconds | MB/s | CPU s | CPU s/GB | peak MB | hash ok | note |"
  echo "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"
  sed 's/|/ | /g; s/^/| /; s/$/ |/' "$ROWS"
} | tee "$OUT/table.md"
echo "Raw JSON and logs: $OUT"
