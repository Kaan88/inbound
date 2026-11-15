#!/usr/bin/env bash
# Runs run-toolkits.sh for each toolkit, with a cold emulator reboot before each one (the guest slows down after many installs and runs:
# on 2026-11-16 jank went from 0.2% to 60% between two runs of the same app until it was rebooted). The first toolkit is measured again at
# the end as a control. Needs the apps installed (run-toolkits.sh with the "build size" phases does that). Emulator only: ANDROID_SERIAL
# defaults to emulator-5554 and no other attached device is ever addressed.
#   bash tools/AndroidToolkits/run-all.sh [names...]      default: native maui avalonia uno native
export MSYS_NO_PATHCONV=1
export ANDROID_SERIAL="${ANDROID_SERIAL:-emulator-5554}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
LOGDIR="$ROOT/artifacts/android-trial/toolkits"
NAMES=("$@"); [ ${#NAMES[@]} -gt 0 ] || NAMES=(native maui avalonia uno native)
PHASES_ALL="${PHASES_ALL:-empty seeded idle smooth sf shots noanim dump}"

reboot_emulator() {
  timeout 30 "$ADB" -s emulator-5554 emu kill >/dev/null 2>&1
  for _ in $(seq 1 30); do tasklist | grep -qi qemu-system || break; sleep 2; done
  tasklist | grep -qi qemu-system && taskkill //F //IM qemu-system-x86_64.exe >/dev/null 2>&1   # our emulator only; the phone is not a qemu process
  sleep 3
  ("$SDK/emulator/emulator.exe" -avd kweb35 -no-snapshot-save -no-boot-anim -port 5554 > "$LOGDIR/emulator-run.log" 2>&1 &)
  for _ in $(seq 1 120); do [ "$(timeout 10 "$ADB" -s emulator-5554 shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = 1 ] && break; sleep 3; done
  sleep 45   # let the boot-time work settle
}

i=0
for n in "${NAMES[@]}"; do
  i=$((i + 1))
  echo "=== $n (run $i): rebooting the emulator"; reboot_emulator
  PHASES="$PHASES_ALL" SKIP_BUILD=1 bash "$HERE/run-toolkits.sh" "$n" > "$LOGDIR/$n-all-$i.log" 2>&1
  echo "=== $n done; log $LOGDIR/$n-all-$i.log"
done
echo ALLDONE > "$LOGDIR/alldone2.txt"
