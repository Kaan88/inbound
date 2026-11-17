#!/usr/bin/env bash
# Android build plan, step 1 gate, on one device: install a TEST build of src/DownloadManager.Android, seed 200 jobs, cold starts
# to a drawn list, memory, one real download with its hash checked, and a kill during a download that must resume from its
# checkpoint. Ends with PASS or FAIL (exit 0 or 1). Everything raw goes to $OUT (git-ignored artifacts/).
#   WIPE=1 ANDROID_SERIAL=<serial> APK=<test apk> bash tools/AndroidApp/run-gate.sh
# WIPE=1 is required: the run uninstalls the app first, which deletes its job list and key on that phone.
# Env: RUNS (cold starts, default 5), LIMIT_MS (cold-start mark, default 2000), PORT (test server, default 8765),
#      SIZE (download bytes, default 256 MiB), KILL_BPS (per-connection throttle for the kill run, default 500000, so 256 MiB
#      takes about a minute on 8 connections), KILL_AFTER (seconds before the kill, default 20), HTTPS_PROBE_URL (an https
#      address the app's own HTTP stack must reach, default https://example.com/; empty skips it).
# Needs: a TEST build (-TestBuild: the adb hooks), the phone unlocked (checked before each timing; the run fails otherwise).
# The screen is kept on without changing any setting: a Shift key press every 15 s counts as user activity (owner: the
# screen turns off; a locked phone made a cold start look 3x slower in the trial).
# Resume is proven from the test server's own count (--log): the bytes it sends after the app is reopened must be fewer than
# the file (the real guard), some request must start past byte 0, and with the bytes sent before the kill they must cover it.
# On the phone it installs and removes only our app, deletes only the files this run added (by their names), reads only our
# log tags and does not clear the phone's log. It changes no setting. Two things reach beyond our app: the wake and Shift key
# presses go to whatever is on screen, and the adb reverse on PORT is removed at the end unless it was there before the run.
set -u
export MSYS_NO_PATHCONV=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
: "${ANDROID_SERIAL:?pin ANDROID_SERIAL}"; : "${APK:?APK=<test build apk>}"
[ "${WIPE:-}" = 1 ] || { echo "WIPE=1 is required: the app is uninstalled first (its jobs and key on the phone are deleted)."; exit 2; }
export ANDROID_SERIAL
RUNS="${RUNS:-5}"; LIMIT_MS="${LIMIT_MS:-2000}"; PORT="${PORT:-8765}"; SIZE="${SIZE:-268435456}"
KILL_BPS="${KILL_BPS:-500000}"; KILL_AFTER="${KILL_AFTER:-20}"
PKG=app.inbound.android; DL=/sdcard/Download
TAG_SAFE="$(echo "$ANDROID_SERIAL" | cut -c1-12 | tr -c 'A-Za-z0-9\n' '_')"
OUT="${OUT_DIR:-$ROOT/artifacts/android-app/step1/gate-$TAG_SAFE-$(date +%Y%m%d-%H%M%S)}"; mkdir -p "$OUT"
FAILS=0
say() { echo "$*" | tee -a "$OUT/summary.txt"; }
fail() { say "FAIL: $*"; FAILS=$((FAILS + 1)); }
adbt() { timeout "${T:-60}" "$ADB" "$@"; }            # no adb call may hang the run (an am start -W once never returned)
# Fails closed: only a seen "isKeyguardShowing=false" counts as unlocked (an adb timeout or a renamed field is "locked").
locked() { ! adbt shell dumpsys window | tr -d '\r' | grep -q "isKeyguardShowing=false"; }
# A phone with no lock screen can doze with no keyguard showing; a cold start timed then was ~50% slower (Mi 8 Lite,
# 2026-11-17: 588-657 ms dozing, 391-424 ms awake). KEYCODE_WAKEUP wakes it and does nothing when it is awake.
wake() { adbt shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1; }
awake() { adbt shell dumpsys power | tr -d '\r' | grep -q "mWakefulness=Awake"; }
# adb shell joins its arguments with spaces for the phone's shell, so a format with a space is quoted once more inside
# (adb logcat quotes its own arguments, so -T "$1" needs nothing extra).
now() { adbt shell "date +'%m-%d %H:%M:%S.000'" | tr -d '\r'; }   # device time, for logcat -T
ourlog() { adbt logcat -d -T "$1" -s Inbound InboundTest | tr -d '\r'; }
OURFILES=()
# A name we may delete by: not empty, no slash, no quote, not starting with a dot (an empty one would make the cleanup
# pattern match other apps' part files).
safe_name() { case "$1" in ""|.*|*/*|*\'*) return 1;; esac; return 0; }

say "device: $(adbt shell getprop ro.product.model | tr -d '\r') sdk $(adbt shell getprop ro.build.version.sdk | tr -d '\r') serial $ANDROID_SERIAL"
say "apk: $APK ($(stat -c %s "$APK") bytes)"
# What ran: a NativeAOT build carries the app as one native library and no managed assemblies; and the commit it came from.
if unzip -l "$APK" | grep -q "lib/arm64-v8a/libDownloadManager.Android.so" && ! unzip -l "$APK" | grep -qE "assemblies/|\.dll$"; then
  say "build: NativeAOT (lib/arm64-v8a/libDownloadManager.Android.so, no managed assemblies)"
else fail "the APK is not a NativeAOT arm64 build"; fi
say "source: $(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet HEAD -- src tests tools scripts || echo ' (with uncommitted changes)')"
wake
locked && { say "FAIL: the phone is locked; unlock it and run again"; exit 1; }

dotnet build "$ROOT/tools/AndroidTrialServer/AndroidTrialServer.csproj" -c Release 2>&1 | tail -1 > "$OUT/server-build.txt"
dotnet "$ROOT/tools/AndroidTrialServer/bin/Release/net10.0/AndroidTrialServer.dll" --port $PORT --log > "$OUT/server.log" 2>&1 &
SERVER_PID=$!
REVERSE_WAS_SET=0
adbt reverse --list | tr -d '\r' | grep -q "tcp:$PORT " && REVERSE_WAS_SET=1
# Paused while $OUT/.quiet exists: each press starts a process on the phone, which on the slow phone landed inside the
# cold-start timing (574-622 ms against 407-413 without it). The cold starts press once before each start instead.
( while true; do [ -f "$OUT/.quiet" ] || { timeout 20 "$ADB" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1
    timeout 20 "$ADB" shell input keyevent KEYCODE_SHIFT_LEFT >/dev/null 2>&1; }; sleep 15; done ) &
AWAKE_PID=$!
cleanup() {
  kill $SERVER_PID $AWAKE_PID 2>/dev/null
  [ "$REVERSE_WAS_SET" = 1 ] || adbt reverse --remove tcp:$PORT >/dev/null 2>&1
  local f
  for f in "${OURFILES[@]}"; do safe_name "$f" && adbt shell "rm -f '$DL/$f' '$DL/$f'.*.part" >/dev/null 2>&1; done
}
trap cleanup EXIT
for _ in $(seq 1 30); do grep -q LISTENING "$OUT/server.log" && break; sleep 1; done
grep -q LISTENING "$OUT/server.log" || { say "FAIL: the test server did not start (see server.log)"; exit 1; }
BASE="http://127.0.0.1:$PORT"
# The forward was seen to vanish on the Mi 8 Lite after the reinstall: set it after install and check it before each download.
ensure_reverse() {
  adbt reverse --list | tr -d '\r' | grep -q "tcp:$PORT tcp:$PORT" || adbt reverse tcp:$PORT tcp:$PORT >/dev/null
  adbt reverse --list | tr -d '\r' | grep -q "tcp:$PORT tcp:$PORT" || { say "FAIL: adb reverse tcp:$PORT is not set"; exit 1; }
}
bytes_sent() { sed -n "$(( $1 + 1 )),$2p" "$OUT/server.log" | grep -oE 'bytes=[0-9]+' | cut -d= -f2 | awk '{s+=$1} END {print s+0}'; }

T=180 adbt uninstall $PKG >/dev/null 2>&1
T=180 adbt install -r "$APK" | tr -d '\r' | tail -1 | tee -a "$OUT/summary.txt"
sleep 10   # install-time background work
ensure_reverse

wait_log() { # since pattern timeoutSeconds -> prints the line
  local i line
  for i in $(seq 1 $(( $3 * 2 ))); do
    line="$(ourlog "$1" | grep -E "$2" | tail -1)"
    [ -n "$line" ] && { echo "$line"; return 0; }
    sleep 0.5
  done
  return 1
}

# Adds one download through the test hook, waits until the host accepted it, sets ADDED_FILE.
add_download() { # url
  local since line
  since="$(now)"
  adbt shell am start -n $PKG/.MainActivity --es add_url "'$1'" >/dev/null
  line="$(wait_log "$since" 'added accepted=' 30)" || { fail "no answer to the add"; return 1; }
  say "add: ${line#*InboundTest: }"
  case "$line" in *accepted=True*) ;; *) fail "the host refused the download"; return 1;; esac
  # The name may hold spaces ("bench (1).bin" when a bench.bin is there): everything between " file=" and " error=".
  ADDED_FILE="$(echo "$line" | sed -E 's/.* file=(.*) error=.*/\1/')"
  safe_name "$ADDED_FILE" || { fail "the hook named the file '$ADDED_FILE'; nothing is deleted by that name"; return 1; }
  OURFILES+=("$ADDED_FILE")
}
# Waits until ADDED_FILE is complete (its size, no part file of its own). Prints the seconds taken.
wait_done() { # timeoutSeconds
  local t0=$(date +%s) size parts
  for _ in $(seq 1 "$1"); do
    size="$(adbt shell "stat -c %s '$DL/$ADDED_FILE' 2>/dev/null" | tr -d '\r')"
    parts="$(adbt shell "ls '$DL' 2>/dev/null | grep -cF '$ADDED_FILE.'" | tr -d '\r')"
    [ "$size" = "$SIZE" ] && [ "$parts" = "0" ] && { echo $(( $(date +%s) - t0 )); return 0; }
    sleep 1
  done
  return 1
}
check_hash() { # label
  local got
  got="$(T=120 adbt shell "sha256sum '$DL/$ADDED_FILE'" | tr -d '\r' | cut -d' ' -f1)"
  if [ ${#EXPECT} -ne 64 ]; then fail "$1: the server gave no sha256 ('$EXPECT')"
  elif [ ${#got} -ne 64 ]; then fail "$1: the phone gave no sha256 ('$got')"
  elif [ "$got" = "$EXPECT" ]; then say "$1: sha256 equal ($got)"; else fail "$1: sha256 $got, server $EXPECT"; fi
}

# 1. First start (creates key and settings) and 200 seeded jobs, all idle ("later": they never run, so transfers do not
#    disturb the timing).
since="$(now)"
adbt shell am start -n $PKG/.MainActivity --ei seed 200 --ei real 0 >/dev/null
seeded="$(wait_log "$since" 'seeded=' 240)" || { fail "seeding did not finish"; ourlog "$since" > "$OUT/log-seed.txt"; exit 1; }
say "seed: ${seeded#*InboundTest: }"
ourlog "$since" > "$OUT/log-seed.txt"

# 1b. HTTPS in this build (Fable review, finding 2): one GET with SocketsHttpHandler, the engine's handler, must be answered.
#     A NativeAOT build that lost its TLS callback export fails every handshake and fails here. Needs the phone online;
#     HTTPS_PROBE_URL= (empty) skips it.
HTTPS_PROBE_URL="${HTTPS_PROBE_URL-https://example.com/}"
if [ -n "$HTTPS_PROBE_URL" ]; then
  since="$(now)"
  adbt shell am start -n $PKG/.MainActivity --es probe_url "'$HTTPS_PROBE_URL'" >/dev/null
  if line="$(wait_log "$since" 'probe sockets: ' 60)"; then
    case "$line" in
      *"probe sockets: "[23][0-9][0-9]*) say "https probe: ${line#*InboundTest: }" ;;
      *) fail "https probe: ${line#*InboundTest: }" ;;
    esac
  else fail "https probe: no answer in 60 s"; fi
  ourlog "$since" > "$OUT/log-https-probe.txt"
fi
sleep 3
adbt exec-out screencap -p > "$OUT/list-200.png"

# 2. Cold starts to a drawn list ("fullyDrawn" = process start to two frames after the snapshot's rows are in the list).
touch "$OUT/.quiet"
for i in $(seq 1 "$RUNS"); do
  locked && { fail "the phone locked during the cold starts"; break; }
  wake; adbt shell input keyevent KEYCODE_SHIFT_LEFT >/dev/null 2>&1   # screen on and kept on, before the timing starts
  adbt shell am force-stop $PKG; sleep 3
  awake || { fail "cold $i: the screen is not awake; not timed"; continue; }
  since="$(now)"
  adbt shell am start -W -n $PKG/.MainActivity | tr -d '\r' > "$OUT/cold$i-amstart.txt"
  drawn="$(wait_log "$since" 'fullyDrawn=' 30)" || { fail "cold $i: no fullyDrawn line"; continue; }
  ms="$(echo "$drawn" | grep -oE 'fullyDrawn=[0-9]+' | cut -d= -f2)"
  rows="$(echo "$drawn" | grep -oE 'rows=[0-9]+' | cut -d= -f2)"
  say "cold $i: $(grep TotalTime "$OUT/cold$i-amstart.txt" | tr -d ' ') fullyDrawn=${ms}ms rows=$rows"
  [ "$rows" -ge 200 ] || fail "cold $i: $rows rows, not 200"
  [ "$ms" -le "$LIMIT_MS" ] || fail "cold $i: ${ms} ms is over the ${LIMIT_MS} ms mark"
  ourlog "$since" > "$OUT/log-cold$i.txt"
done
rm -f "$OUT/.quiet"

# 3. Memory, idle, 10 s after the last cold start.
sleep 10
adbt shell dumpsys meminfo $PKG | tr -d '\r' > "$OUT/meminfo.txt"
say "memory: $(grep -E 'TOTAL PSS|TOTAL RSS' "$OUT/meminfo.txt" | head -1 | tr -s ' ')"

EXPECT="$(curl -s "$BASE/sha256?size=$SIZE" | tr -d '\r\n"')"

# 4. One real download, unthrottled, hash checked. The server's own count must show the whole file sent, so a complete
#    file left in Download by an earlier run cannot pass for this download.
ensure_reverse
since="$(now)"
MARKD=$(wc -l < "$OUT/server.log")
if add_download "$BASE/bench.bin?size=$SIZE&n=dl$RANDOM"; then
  if secs="$(wait_done 600)"; then say "download: $SIZE bytes in about ${secs}s"; check_hash download
    sleep 2
    SENT=$(bytes_sent $MARKD $(wc -l < "$OUT/server.log"))
    if [ "$SENT" -ge "$SIZE" ]; then say "download: the server sent $SENT bytes for it"; else fail "download: the server sent only $SENT bytes, the file was not downloaded by this run"; fi
    adbt shell "rm -f '$DL/$ADDED_FILE'" >/dev/null   # so the kill run gets the plain name again
  else fail "download: not complete after 600 s"; fi
fi
ourlog "$since" > "$OUT/log-download.txt"

# 5. Kill during a throttled download, then open the app again: it must resume from its checkpoint and finish with the hash.
ensure_reverse
since="$(now)"
MARK0=$(wc -l < "$OUT/server.log")
if add_download "$BASE/bench.bin?size=$SIZE&bps=$KILL_BPS&n=kill$RANDOM"; then
  sleep "$KILL_AFTER"
  adbt shell "ls -l '$DL'" | tr -d '\r' | grep -F "$ADDED_FILE" > "$OUT/kill-before.txt"
  adbt shell am force-stop $PKG
  sleep 3   # the cut connections end and are counted before the mark
  MARK1=$(wc -l < "$OUT/server.log")
  BEFORE=$(bytes_sent $MARK0 $MARK1)
  say "killed after ${KILL_AFTER}s: $(awk '{print $NF}' "$OUT/kill-before.txt" | tr '\n' ' ')"
  ensure_reverse
  adbt shell am start -n $PKG/.MainActivity >/dev/null
  if secs="$(wait_done 900)"; then
    sleep 2   # the last requests' SENT lines are written when they end
    MARK2=$(wc -l < "$OUT/server.log")
    AFTER=$(bytes_sent $MARK1 $MARK2)
    LATE_START=$(sed -n "$(( MARK1 + 1 )),${MARK2}p" "$OUT/server.log" | grep -oE 'start=[0-9]+' | cut -d= -f2 | awk '$1 > 0' | wc -l)
    say "resume: done ${secs}s after reopening; server sent $BEFORE bytes before the kill, $AFTER after (file $SIZE), $LATE_START requests after reopening started past byte 0"
    # The real guard is AFTER < SIZE: a restart from zero sends the whole file again. LATE_START only shows that some
    # request after reopening began past byte 0 (a fresh segmented download has those too). --log counts bytes written to
    # the socket, not received, which can only raise BEFORE; the hash check catches a bad file.
    if [ "$AFTER" -lt "$SIZE" ] && [ $(( BEFORE + AFTER )) -ge "$SIZE" ] && [ "$LATE_START" -gt 0 ]; then say "resume: RESUMED from the checkpoint"
    else fail "resume: not proven (a restart from zero would send the whole file again)"; fi
    check_hash resume
  else fail "resume: not complete 900 s after reopening"; fi
fi
ourlog "$since" > "$OUT/log-kill-resume.txt"

say "raw: $OUT"
if [ "$FAILS" -eq 0 ]; then say "GATE: PASS"; exit 0; else say "GATE: FAIL ($FAILS)"; exit 1; fi
