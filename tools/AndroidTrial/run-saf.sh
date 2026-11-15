#!/usr/bin/env bash
# ANDROID.2 part 3: a folder picked with ACTION_OPEN_DOCUMENT_TREE (mode=saf). The picker is driven with uiautomator dump +
# adb input tap. If a step cannot be found the script stops and leaves the picker open for a person to finish.
#   bash tools/AndroidTrial/run-saf.sh
# Env: FOLDER (new folder name to create under Documents, default inbound-saf-trial), SKIP_BUILD=1
# Raw output: artifacts/android-trial/saf-<timestamp>/
set -u
export MSYS_NO_PATHCONV=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -W)"
SDK="$(cygpath -m "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ADB="$SDK/platform-tools/adb.exe"
PORT=5599; PKG=app.inbound.trial; EXT=/sdcard/Android/data/$PKG/files
FOLDER="${FOLDER:-inbound-saf-trial}"
OUT="$ROOT/artifacts/android-trial/saf-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$OUT"
"$ADB" get-state >/dev/null 2>&1 || { echo "No device: start the emulator first."; exit 1; }
source "$(dirname "${BASH_SOURCE[0]}")/lib-android.sh"
build_install

dump_ui() { "$ADB" shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1; "$ADB" shell cat /sdcard/ui.xml | tr -d '\r' > "$OUT/ui-$1.xml"; }
# tap the first node whose text, content-desc or resource-id matches the regex ($1); prints what it tapped
tap() {
  dump_ui "$2"
  local xy
  xy=$(python - "$OUT/ui-$2.xml" "$1" <<'PY'
import re, sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1]); rx = re.compile(sys.argv[2], re.I)
for n in tree.iter('node'):
    hay = ' '.join([n.get('text',''), n.get('content-desc',''), n.get('resource-id','')])
    if rx.search(hay) and n.get('enabled') == 'true':
        m = re.match(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', n.get('bounds'))
        a = list(map(int, m.groups())); print((a[0]+a[2])//2, (a[1]+a[3])//2, hay.strip()[:60]); break
PY
)
  [ -z "$xy" ] && { echo "tap '$1': not found"; return 1; }
  echo "tap '$1' -> $xy"
  "$ADB" shell input tap $(echo "$xy" | cut -d' ' -f1,2); sleep 2
}

"$ADB" shell pm clear $PKG >/dev/null; "$ADB" logcat -c
"$ADB" shell "mkdir -p /sdcard/Documents && rm -rf /sdcard/Documents/$FOLDER"
"$ADB" shell am start -n $PKG/.MainActivity --es mode saf --es runId saf1 --es phase pick >/dev/null
sleep 5
"$ADB" shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1; "$ADB" shell cat /sdcard/ui.xml | tr -d '\r' > "$OUT/ui-picker0.xml"
echo "picker window: $("$ADB" shell dumpsys window | grep mCurrentFocus | tr -d '\r')"
# The picker opens on its last/ default root. Go to Documents (a fresh volume lists Documents) and create a folder.
tap 'Documents' picker1 || true
tap 'New folder|Create new folder|create_dir|action_menu_create_dir' picker2 || tap 'More options|Overflow' picker2b
dump_ui picker3
if grep -q 'android.widget.EditText' "$OUT/ui-picker3.xml"; then
  "$ADB" shell input text "$FOLDER"; sleep 1
  tap '^OK$|^Create$|button1' picker4
fi
sleep 2
tap 'Use this folder|select_button|button_select|Allow access|^Select$' picker5 || true
sleep 2
tap '^Allow$|button1|permission_allow' picker6 || true
sleep 20
echo "== result =="
wait_file "$EXT/saf-result-saf1-pick.json" 120 "$OUT/saf1-pick.json" && cat "$OUT/saf1-pick.json" | head -80 || { echo "no result: the picker probably needs a person (see $OUT/ui-*.xml, screenshot below)"; "$ADB" exec-out screencap -p > "$OUT/picker-stuck.png"; exit 2; }

echo "== force-stop, then verify the grant survives =="
"$ADB" shell am force-stop $PKG
"$ADB" shell dumpsys package $PKG | grep -iE "uriGrant|persisted" | head -5 > "$OUT/package-grants.txt"
"$ADB" shell am start -n $PKG/.MainActivity --es mode saf --es runId saf1 --es phase verify >/dev/null
wait_file "$EXT/saf-result-saf1-verify.json" 90 "$OUT/saf1-verify.json" && head -60 "$OUT/saf1-verify.json"
"$ADB" shell "ls -la /sdcard/Documents/$FOLDER" > "$OUT/folder-listing.txt" 2>&1
echo "Raw output: $OUT"
