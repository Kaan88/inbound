#!/usr/bin/env bash
# Step 2 (ANDROID.3) background check on one phone: a throttled download goes on after Home and with the screen off.
#   ANDROID_SERIAL=<serial> APK=<test apk> [SIZE=bytes] [BPS=bytes/s] [OFF=seconds] bash tools/AndroidApp/run-background.sh
# The phone must be unlocked at the start (the keep-alive is only started while the app is visible). The script installs
# the test APK (our app only), grants our app the notification permission, adds one download through the test hook,
# presses Home and turns the screen off, samples the part file every 30 s, and checks the hash and that the keep-alive
# ended. It changes no phone setting. Our file and the adb reverse it set are removed at the end.
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SDK="${ANDROID_SDK_ROOT:-$LOCALAPPDATA/Android/Sdk}"
ADB="$SDK/platform-tools/adb.exe"
: "${ANDROID_SERIAL:?pin ANDROID_SERIAL}"; : "${APK:?APK=<test build apk>}"
export ANDROID_SERIAL
SIZE="${SIZE:-314572800}"; BPS="${BPS:-1500000}"; OFF="${OFF:-1800}"; PORT=8765
PKG=app.inbound.android; DL=/sdcard/Download
OUT="$ROOT/artifacts/android-app/step2/bg-$(echo "$ANDROID_SERIAL" | tr -c 'A-Za-z0-9' '_' | cut -c1-20)-$(date +%H%M%S)"
mkdir -p "$OUT"; FAILS=0; OURFILES=()
say() { echo "$*" | tee -a "$OUT/summary.txt"; }
fail() { say "FAIL: $*"; FAILS=$((FAILS + 1)); }
adbt() { timeout "${T:-60}" "$ADB" "$@"; }
locked() { ! adbt shell dumpsys window | tr -d '\r' | grep -q "isKeyguardShowing=false"; }
now() { adbt shell "date +'%m-%d %H:%M:%S.000'" | tr -d '\r'; }
ourlog() { adbt logcat -d -T "$1" -s Inbound InboundTest | tr -d '\r'; }
safe_name() { case "$1" in ""|.*|*/*|*\'*) return 1;; esac; return 0; }
wait_log() { local i line; for i in $(seq 1 $(( $3 * 2 ))); do line="$(ourlog "$1" | grep -E "$2" | tail -1)"; [ -n "$line" ] && { echo "$line"; return 0; }; sleep 0.5; done; return 1; }

say "device: $(adbt shell getprop ro.product.model | tr -d '\r') sdk $(adbt shell getprop ro.build.version.sdk | tr -d '\r')"
say "source: $(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet HEAD -- src tests tools || echo ' (with uncommitted changes)')"
say "size $SIZE at $BPS B/s (about $(( SIZE / BPS )) s); screen off for up to $OFF s"
adbt shell input keyevent KEYCODE_WAKEUP >/dev/null
locked && { say "FAIL: the phone is locked; unlock it and run again"; exit 1; }
say "charging: $(adbt shell dumpsys battery | tr -d '\r' | grep -E 'AC powered|USB powered|Wireless powered' | tr -s ' ' | tr '\n' ' ')"

dotnet build "$ROOT/tools/AndroidTrialServer/AndroidTrialServer.csproj" -c Release 2>&1 | tail -1 > "$OUT/server-build.txt"
dotnet "$ROOT/tools/AndroidTrialServer/bin/Release/net10.0/AndroidTrialServer.dll" --port $PORT --log > "$OUT/server.log" 2>&1 &
SERVER_PID=$!
REVERSE_WAS_SET=0; adbt reverse --list | tr -d '\r' | grep -q "tcp:$PORT " && REVERSE_WAS_SET=1
AWAKE_PID=""
cleanup() {
  kill $SERVER_PID $AWAKE_PID 2>/dev/null
  [ "$REVERSE_WAS_SET" = 1 ] || adbt reverse --remove tcp:$PORT >/dev/null 2>&1
  local f; for f in "${OURFILES[@]}"; do safe_name "$f" && adbt shell "rm -f '$DL/$f' '$DL/$f'.*.part" >/dev/null 2>&1; done
}
trap cleanup EXIT
for _ in $(seq 1 30); do grep -q LISTENING "$OUT/server.log" && break; sleep 1; done
grep -q LISTENING "$OUT/server.log" || { say "FAIL: the test server did not start"; exit 1; }

T=180 adbt install -r "$APK" | tr -d '\r' | tail -1 | tee -a "$OUT/summary.txt"
adbt shell pm grant $PKG android.permission.POST_NOTIFICATIONS >/dev/null 2>&1
adbt reverse tcp:$PORT tcp:$PORT >/dev/null
# Keep the screen on while the app is visible (paused once the screen is turned off on purpose).
( while true; do timeout 20 "$ADB" shell input keyevent KEYCODE_SHIFT_LEFT >/dev/null 2>&1; sleep 15; done ) & AWAKE_PID=$!

BASE="http://127.0.0.1:$PORT"
EXPECT="$(curl -s "$BASE/sha256?size=$SIZE" | tr -d '\r\n"')"
since="$(now)"
adbt shell am start -n $PKG/.MainActivity --es add_url "'$BASE/bench.bin?size=$SIZE&bps=$BPS&n=bg$RANDOM'" >/dev/null
line="$(wait_log "$since" 'added accepted=' 60)" || { fail "no answer to the add"; ourlog "$since" > "$OUT/log.txt"; exit 1; }
say "add: ${line#*InboundTest: }"
FILE="$(echo "$line" | sed -E 's/.* file=(.*) error=.*/\1/')"
safe_name "$FILE" || { fail "unsafe file name '$FILE'"; exit 1; }
OURFILES+=("$FILE")
wait_log "$since" 'keepalive=true' 30 >/dev/null && say "keep-alive: true" || fail "the keep-alive did not turn on"
started="$(wait_log "$since" 'session job start|foreground service requested' 30)" && say "mechanism: ${started#*Inbound: }" || fail "no session job or service started"

sleep 15
adbt shell input keyevent KEYCODE_HOME >/dev/null
sleep 10
kill $AWAKE_PID 2>/dev/null; AWAKE_PID=""
adbt shell input keyevent KEYCODE_SLEEP >/dev/null
say "home and screen off at $(date +%H:%M:%S)"
t0=$(date +%s); done_at=""
while [ $(( $(date +%s) - t0 )) -lt "$OFF" ]; do
  size="$(adbt shell "stat -c %s '$DL/$FILE' 2>/dev/null" | tr -d '\r')"
  part="$(adbt shell "ls -l '$DL/' 2>/dev/null | grep -F '$FILE.' | awk '{print \$5}' | head -1" | tr -d '\r')"
  wake="$(adbt shell dumpsys power | tr -d '\r' | grep -oE 'mWakefulness=[A-Za-z]+')"
  echo "$(( $(date +%s) - t0 ))s part=${part:-none} final=${size:-none} $wake" >> "$OUT/samples.txt"
  [ "$size" = "$SIZE" ] && [ -z "$part" ] && { done_at=$(( $(date +%s) - t0 )); break; }
  sleep 30
done
tail -3 "$OUT/samples.txt" | sed 's/^/  /' | tee -a "$OUT/summary.txt"
if [ -n "$done_at" ]; then
  say "finished with the screen off after ${done_at}s"
  got="$(T=180 adbt shell "sha256sum '$DL/$FILE'" | tr -d '\r' | cut -d' ' -f1)"
  if [ ${#got} -eq 64 ] && [ "$got" = "$EXPECT" ]; then say "sha256 equal ($got)"; else fail "sha256 $got, server $EXPECT"; fi
else
  fail "not finished after $OFF s with the screen off"
fi
sleep 5
ourlog "$since" > "$OUT/log.txt"
grep -qE 'keepalive=false' "$OUT/log.txt" && say "keep-alive: false at the end" || fail "the keep-alive did not turn off"
grep -E 'session job (stop|finished)|foreground service' "$OUT/log.txt" | sed 's/^/  /' | tee -a "$OUT/summary.txt"
adbt shell dumpsys notification --noredact | tr -d '\r' | grep -E "pkg=$PKG" | sed 's/^ *//' | sort | uniq -c | sed 's/^/  notification: /' | tee -a "$OUT/summary.txt"
adbt shell input keyevent KEYCODE_WAKEUP >/dev/null
say "raw: $OUT"
[ "$FAILS" = 0 ] && say "PASS" || say "FAILED: $FAILS"
