#!/usr/bin/env bash
# Step 2 (ANDROID.3) check on an API 34+ phone, in two parts because the phone must be unlocked after the reboot:
#   PART=a: install the test APK, add one throttled download, swipe Inbound away in Recents, check the download goes on,
#           then reboot the phone (owner's OK needed).
#   PART=b: after the owner unlocks the phone: check the download resumed on its own (the app is not opened), then
#           Pause all and Resume from the notification (tapped through the UI), and let it finish (hash checked).
#   ANDROID_SERIAL=<serial> APK=<test apk> PART=a|b bash tools/AndroidApp/run-swipe-reboot.sh
# Changes no phone setting. Our file and the adb reverse are removed at the end of part b.
# LAN=<this PC's address>: the phone reaches the test server over Wi-Fi (adb reverse does not survive the reboot).
set -u
export MSYS_NO_PATHCONV=1   # Git Bash must not rewrite /sdcard paths in adb arguments
ROOT="$(cd "$(dirname "$0")/../.." && pwd -W)"   # Windows form: MSYS_NO_PATHCONV stops the rewrite for git and dotnet too
ADB="${ANDROID_SDK_ROOT:-$LOCALAPPDATA/Android/Sdk}/platform-tools/adb.exe"
: "${ANDROID_SERIAL:?pin ANDROID_SERIAL}"; : "${PART:?PART=a or b}"; export ANDROID_SERIAL
SIZE="${SIZE:-314572800}"; BPS="${BPS:-60000}"; PORT=8765; PKG=app.inbound.android; DL=/sdcard/Download
STATE="$ROOT/artifacts/android-app/step2/swipe-reboot-state.txt"
OUT="$ROOT/artifacts/android-app/step2/swipe-reboot-$(date +%H%M%S)-$PART"; mkdir -p "$OUT"; FAILS=0
say() { echo "$*" | tee -a "$OUT/summary.txt"; }
fail() { say "FAIL: $*"; FAILS=$((FAILS + 1)); }
adbt() { timeout "${T:-60}" "$ADB" "$@"; }
now() { adbt shell "date +'%m-%d %H:%M:%S.000'" | tr -d '\r'; }
ourlog() { adbt logcat -d -T "$1" -s Inbound InboundTest | tr -d '\r'; }
safe_name() { case "$1" in ""|.*|*/*|*\'*) return 1;; esac; return 0; }
wait_log() { local i line; for i in $(seq 1 $(( $3 * 2 ))); do line="$(ourlog "$1" | grep -E "$2" | tail -1)"; [ -n "$line" ] && { echo "$line"; return 0; }; sleep 0.5; done; return 1; }
part_size() { adbt shell "ls -l '$DL/' 2>/dev/null | grep -F '$FILE.' | awk '{print \$5}' | head -1" | tr -d '\r'; }
final_size() { adbt shell "stat -c %s '$DL/$FILE' 2>/dev/null" | tr -d '\r'; }
# Progress without opening the app: the part file is full size from the start, so read the ongoing notification's progress.
progress() { adbt shell dumpsys notification --noredact | tr -d '\r' | awk '/pkg=app.inbound.android.*id=1001 /{f=1} f&&/android.progress=/{print; exit}' | grep -oE "[0-9]+" | tail -1; }
grows() { local a b; a="$(progress)"; sleep "$1"; b="$(progress)"; echo "progress $a -> $b"; [ -n "$b" ] && [ "${b:-0}" -gt "${a:-0}" ]; }
notifs() { adbt shell cmd notification list 2>/dev/null | tr -d '\r' | grep -F "|$PKG|" | cut -d'|' -f3 | sort | tr '\n' ' '; }
# Tap a notification action by its visible text: open the shade, find the button in a UI dump, tap its centre.
tap_action() {
  adbt shell cmd statusbar expand-notifications >/dev/null; sleep 2
  local i b; for i in 1 2 3; do
    adbt shell uiautomator dump /sdcard/inbound-ui.xml >/dev/null 2>&1
    b="$(adbt shell cat /sdcard/inbound-ui.xml | tr '>' '\n' | grep -iE "text=\"$1\"" | grep -oE 'bounds="\[[0-9]+,[0-9]+\]\[[0-9]+,[0-9]+\]"' | head -1)"
    [ -n "$b" ] && break
    adbt shell input swipe 540 600 540 1600 300 >/dev/null; sleep 1   # expand the group
  done
  adbt shell rm -f /sdcard/inbound-ui.xml >/dev/null
  [ -z "$b" ] && { adbt shell cmd statusbar collapse >/dev/null; return 1; }
  local n; n=($(echo "$b" | grep -oE '[0-9]+')); adbt shell input tap $(( (n[0]+n[2])/2 )) $(( (n[1]+n[3])/2 )) >/dev/null
  sleep 1; adbt shell cmd statusbar collapse >/dev/null; return 0
}
HOSTADDR="${LAN:-127.0.0.1}"; LANARG=""; [ -n "${LAN:-}" ] && LANARG="--lan"
start_server() {
  curl -s -m 3 "http://127.0.0.1:$PORT/sha256?size=1" >/dev/null 2>&1 && { SERVER_PID=""; adbt reverse tcp:$PORT tcp:$PORT >/dev/null; return; }
  dotnet "$ROOT/tools/AndroidTrialServer/bin/Release/net10.0/AndroidTrialServer.dll" --port $PORT --log $LANARG >> "$ROOT/artifacts/android-app/step2/swipe-reboot-server.log" 2>&1 &
  SERVER_PID=$!; cat /proc/$SERVER_PID/winpid > "$STATE.server" 2>/dev/null; sleep 4; adbt reverse tcp:$PORT tcp:$PORT >/dev/null
}

say "device: $(adbt shell getprop ro.product.model | tr -d '\r') sdk $(adbt shell getprop ro.build.version.sdk | tr -d '\r') part $PART"
say "source: $(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet HEAD -- src tests tools || echo ' (with uncommitted changes)')"
adbt shell input keyevent KEYCODE_WAKEUP >/dev/null
adbt shell dumpsys window | tr -d '\r' | grep -q "isKeyguardShowing=false" || { say "FAIL: the phone is locked"; exit 1; }

if [ "$PART" = a ]; then
  : > "$ROOT/artifacts/android-app/step2/swipe-reboot-server.log"
  dotnet build "$ROOT/tools/AndroidTrialServer/AndroidTrialServer.csproj" -c Release 2>&1 | tail -1 > "$OUT/server-build.txt"
  start_server   # left running across the reboot; part b stops it
  T=180 adbt install -r "${APK:?APK=<test apk>}" | tr -d '\r' | tail -1 | tee -a "$OUT/summary.txt"
  adbt shell pm grant $PKG android.permission.POST_NOTIFICATIONS >/dev/null 2>&1
  adbt reverse tcp:$PORT tcp:$PORT >/dev/null
  since="$(now)"
  adbt shell am start -n $PKG/.MainActivity --es add_url "'http://$HOSTADDR:$PORT/bench.bin?size=$SIZE&bps=$BPS&n=sr$RANDOM'" >/dev/null
  line="$(wait_log "$since" 'added accepted=' 60)" || { fail "no answer to the add"; exit 1; }
  FILE="$(echo "$line" | sed -E 's/.* file=(.*) error=.*/\1/')"; safe_name "$FILE" || { fail "unsafe name"; exit 1; }
  echo "$FILE" > "$STATE"
  wait_log "$since" 'session job start' 30 >/dev/null && say "session job started" || fail "no session job"
  sleep 15
  # Swipe Inbound's own card up (found by its label in a UI dump; other apps' cards are not touched).
  adbt shell input keyevent KEYCODE_APP_SWITCH >/dev/null; sleep 3
  adbt shell uiautomator dump /sdcard/inbound-ui.xml >/dev/null 2>&1
  card="$(adbt shell cat /sdcard/inbound-ui.xml | tr '>' '\n' | grep 'content-desc="Inbound"' | grep -oE 'bounds="\[[0-9]+,[0-9]+\]\[[0-9]+,[0-9]+\]"' | head -1)"
  adbt shell rm -f /sdcard/inbound-ui.xml >/dev/null
  if [ -n "$card" ]; then n=($(echo "$card" | grep -oE '[0-9]+')); cx=$(( (n[0]+n[2])/2 )); cy=$(( (n[1]+n[3])/2 ))
    adbt shell input swipe $cx $cy $cx $(( n[1] / 2 )) 200 >/dev/null; sleep 2
  else say "no Inbound card found in Recents"; fi
  adbt shell input keyevent KEYCODE_HOME >/dev/null
  adbt shell dumpsys activity recents | tr -d '\r' | grep -q "A=.*:$PKG" && fail "Inbound is still in Recents (the swipe missed)" || say "swiped away in Recents"
  say "process after the swipe: pid '$(adbt shell pidof $PKG | tr -d '\r')'"
  g="$(grows 20)" && say "goes on after the swipe ($g)" || fail "stopped after the swipe ($g)"
  say "notifications: $(notifs)"
  ourlog "$since" > "$OUT/log.txt"
  say "rebooting at $(date +%H:%M:%S) with part $(part_size)"
  adbt reboot >/dev/null 2>&1
  say "PART a done: unlock the phone, then run PART=b"; [ "$FAILS" = 0 ] && say "PASS (part a)" || say "FAILED: $FAILS"
  exit 0
fi

FILE="$(cat "$STATE")"; safe_name "$FILE" || { fail "no state from part a"; exit 1; }
start_server
cleanup() { [ -s "$STATE.server" ] && taskkill /F /PID "$(cat "$STATE.server")" >/dev/null 2>&1; rm -f "$STATE.server"; kill $SERVER_PID 2>/dev/null; adbt reverse --remove tcp:$PORT >/dev/null 2>&1; adbt shell "rm -f '$DL/$FILE' '$DL/$FILE'.*.part" >/dev/null 2>&1; }
trap cleanup EXIT
since_boot="$(adbt shell "date -d @\$(( \$(date +%s) - \$(cut -d. -f1 /proc/uptime) )) +'%m-%d %H:%M:%S.000'" | tr -d '\r')"
say "uptime $(adbt shell cut -d' ' -f1 /proc/uptime | tr -d '\r') s; app opened by us: no"
ourlog "$since_boot" | grep -E "session job|last run stopped|crash guard|boot:|start option|resum" | head -10 | sed 's/^/  /' | tee -a "$OUT/summary.txt"
ok=""; for _ in 1 2 3 4 5 6; do g="$(grows 20)" && { ok=1; break; }; done   # the reverse was lost in the reboot: allow for retry backoff
[ -n "$ok" ] && say "resumed on its own after the reboot ($g)" || fail "not running after the reboot ($g)"
say "notifications: $(notifs)"
since="$(now)"
tap_action "Pause all" && say "tapped Pause all" || fail "no Pause all button found"
sleep 5; g="$(grows 15)" && fail "still growing after Pause all ($g)" || say "stopped after Pause all ($g)"
say "notifications after Pause all: $(notifs) (1002 = paused)"
tap_action "Resume" && say "tapped Resume" || fail "no Resume button found"
sleep 8; g="$(grows 15)" && say "runs again after Resume ($g)" || fail "not running after Resume ($g)"
EXPECT="$(curl -s "http://127.0.0.1:$PORT/sha256?size=$SIZE" | tr -d '\r\n"')"
t0=$(date +%s); ok=""
for _ in $(seq 1 200); do [ "$(final_size)" = "$SIZE" ] && [ -z "$(part_size)" ] && { ok=1; break; }; sleep 5; done
[ -n "$ok" ] && say "finished $(( $(date +%s) - t0 ))s after Resume" || fail "did not finish"
got="$(T=180 adbt shell "sha256sum '$DL/$FILE'" | tr -d '\r' | cut -d' ' -f1)"
[ ${#got} -eq 64 ] && [ "$got" = "$EXPECT" ] && say "sha256 equal" || fail "sha256 $got, server $EXPECT"
ourlog "$since_boot" > "$OUT/log.txt"
say "raw: $OUT"; [ "$FAILS" = 0 ] && say "PASS (part b)" || say "FAILED: $FAILS"
