#!/usr/bin/env bash
# Step 2 (ANDROID.3) Wi-Fi only check on one phone, without touching any phone setting: the test build's hooks turn the
# setting on and make the app treat the network as metered. A download added then must wait ("Waiting for Wi-Fi"), not
# fail; when the network is reported not metered it must run and finish with an equal hash.
#   ANDROID_SERIAL=<serial> APK=<test apk> bash tools/AndroidApp/run-wifi-only.sh
# The phone must be unlocked. The hooks are put back at the end (setting off, override removed by a restart of the app).
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ADB="${ANDROID_SDK_ROOT:-$LOCALAPPDATA/Android/Sdk}/platform-tools/adb.exe"
: "${ANDROID_SERIAL:?pin ANDROID_SERIAL}"; : "${APK:?APK=<test build apk>}"; export ANDROID_SERIAL
SIZE="${SIZE:-52428800}"; BPS="${BPS:-200000}"; PORT=8765; PKG=app.inbound.android; DL=/sdcard/Download
OUT="$ROOT/artifacts/android-app/step2/wifi-$(echo "$ANDROID_SERIAL" | tr -c 'A-Za-z0-9' '_' | cut -c1-20)-$(date +%H%M%S)"
mkdir -p "$OUT"; FAILS=0; OURFILES=()
say() { echo "$*" | tee -a "$OUT/summary.txt"; }
fail() { say "FAIL: $*"; FAILS=$((FAILS + 1)); }
adbt() { timeout "${T:-60}" "$ADB" "$@"; }
now() { adbt shell "date +'%m-%d %H:%M:%S.000'" | tr -d '\r'; }
ourlog() { adbt logcat -d -T "$1" -s Inbound InboundTest | tr -d '\r'; }
safe_name() { case "$1" in ""|.*|*/*|*\'*) return 1;; esac; return 0; }
wait_log() { local i line; for i in $(seq 1 $(( $3 * 2 ))); do line="$(ourlog "$1" | grep -E "$2" | tail -1)"; [ -n "$line" ] && { echo "$line"; return 0; }; sleep 0.5; done; return 1; }
hook() { adbt shell am start -n $PKG/.MainActivity "$@" >/dev/null; }
notifs() { adbt shell cmd notification list 2>/dev/null | tr -d '\r' | grep -F "|$PKG|" | cut -d'|' -f3 | sort | tr '\n' ' '; }

say "device: $(adbt shell getprop ro.product.model | tr -d '\r') sdk $(adbt shell getprop ro.build.version.sdk | tr -d '\r')"
say "source: $(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet HEAD -- src tests tools || echo ' (with uncommitted changes)')"
adbt shell input keyevent KEYCODE_WAKEUP >/dev/null
adbt shell dumpsys window | tr -d '\r' | grep -q "isKeyguardShowing=false" || { say "FAIL: the phone is locked"; exit 1; }
dotnet build "$ROOT/tools/AndroidTrialServer/AndroidTrialServer.csproj" -c Release 2>&1 | tail -1 > "$OUT/server-build.txt"
dotnet "$ROOT/tools/AndroidTrialServer/bin/Release/net10.0/AndroidTrialServer.dll" --port $PORT --log > "$OUT/server.log" 2>&1 &
SERVER_PID=$!
REVERSE_WAS_SET=0; adbt reverse --list | tr -d '\r' | grep -q "tcp:$PORT " && REVERSE_WAS_SET=1
( while true; do timeout 20 "$ADB" shell input keyevent KEYCODE_SHIFT_LEFT >/dev/null 2>&1; sleep 15; done ) & AWAKE_PID=$!
cleanup() {
  hook --ez wifi_only false; sleep 2; adbt shell am force-stop $PKG
  kill $SERVER_PID $AWAKE_PID 2>/dev/null
  [ "$REVERSE_WAS_SET" = 1 ] || adbt reverse --remove tcp:$PORT >/dev/null 2>&1
  local f; for f in "${OURFILES[@]}"; do safe_name "$f" && adbt shell "rm -f '$DL/$f' '$DL/$f'.*.part" >/dev/null 2>&1; done
}
trap cleanup EXIT
for _ in $(seq 1 30); do grep -q LISTENING "$OUT/server.log" && break; sleep 1; done
T=180 adbt install -r "$APK" | tr -d '\r' | tail -1 | tee -a "$OUT/summary.txt"
adbt shell pm grant $PKG android.permission.POST_NOTIFICATIONS >/dev/null 2>&1
adbt reverse tcp:$PORT tcp:$PORT >/dev/null
BASE="http://127.0.0.1:$PORT"; EXPECT="$(curl -s "$BASE/sha256?size=$SIZE" | tr -d '\r\n"')"

since="$(now)"
hook --ez wifi_only true; wait_log "$since" 'wifi_only=True ok=True' 30 >/dev/null && say "setting on" || fail "the setting did not turn on"
hook --ez metered_override true; sleep 2
since="$(now)"
hook --es add_url "'$BASE/bench.bin?size=$SIZE&bps=$BPS&n=wf$RANDOM'"
line="$(wait_log "$since" 'added accepted=' 60)" || { fail "no answer to the add"; exit 1; }
FILE="$(echo "$line" | sed -E 's/.* file=(.*) error=.*/\1/')"; safe_name "$FILE" || { fail "unsafe name"; exit 1; }; OURFILES+=("$FILE")
sleep 8
hook --ez log_state true; st="$(wait_log "$since" 'why=hook' 15)"; say "held: ${st#*InboundTest: }"
case "$st" in *keepalive=false*waiting=1*) say "waits, keep-alive off";; *) fail "expected keepalive=false and waiting=1";; esac
sent0="$(grep -c 'bytes=' "$OUT/server.log")"
sleep "${HOLD:-10}"   # HOLD=45 checks that a hold job the system started anyway ends after the 30 s grace (API 34+)
[ "$(grep -c 'bytes=' "$OUT/server.log")" = "$sent0" ] && say "no bytes sent while held" || fail "bytes were sent while held"
say "notifications while held: $(notifs) (1003 = Waiting for Wi-Fi)"
notifs | grep -qw 1003 || fail "no Waiting for Wi-Fi notification"
if [ "${HOLD:-10}" -gt 35 ]; then
  ourlog "$since" | grep -q "hold job started but the network still counts as metered" && say "stray hold job ended after the grace"     || say "note: no stray hold job to end (the system did not start it, or it is a service path)"
  notifs | grep -qw 1001 && fail "the ongoing notification is still up while held" || say "no ongoing notification while held"
fi
adbt shell input keyevent KEYCODE_HOME >/dev/null

since="$(now)"
hook --ez metered_override false
t0=$(date +%s); ok=""
for _ in $(seq 1 600); do
  size="$(adbt shell "stat -c %s '$DL/$FILE' 2>/dev/null" | tr -d '\r')"
  parts="$(adbt shell "ls '$DL' 2>/dev/null | grep -cF '$FILE.'" | tr -d '\r')"
  [ "$size" = "$SIZE" ] && [ "$parts" = "0" ] && { ok=1; break; }; sleep 1
done
[ -n "$ok" ] && say "ran on the not-metered network, finished after $(( $(date +%s) - t0 ))s" || fail "did not finish after the network turned not metered"
got="$(T=120 adbt shell "sha256sum '$DL/$FILE'" | tr -d '\r' | cut -d' ' -f1)"
[ ${#got} -eq 64 ] && [ "$got" = "$EXPECT" ] && say "sha256 equal" || fail "sha256 $got, server $EXPECT"
ourlog "$since" > "$OUT/log.txt"
say "notifications after: $(notifs)"
say "raw: $OUT"; [ "$FAILS" = 0 ] && say "PASS" || say "FAILED: $FAILS"
