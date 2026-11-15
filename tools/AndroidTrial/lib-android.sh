#!/usr/bin/env bash
# Shared helpers for run-host.sh / run-service.sh / run-saf.sh (expects ROOT, SDK, ADB, PKG, PORT, OUT set).
build_install() {
  if [ -z "${SKIP_BUILD:-}" ]; then
    dotnet build "$ROOT/tools/AndroidTrial/AndroidTrial.csproj" -c Release -p:AndroidSdkDirectory="$SDK" -p:RuntimeIdentifier=android-x64 ${TRIAL_VARIANT:+-p:TrialVariant=$TRIAL_VARIANT} ${TRIAL_MSBUILD:-} 2>&1 | grep -E "error|Error\(s\)"
    dotnet build "$ROOT/tools/AndroidTrialServer/AndroidTrialServer.csproj" -c Release 2>&1 | tail -1
  fi
  APK="$(find "$ROOT/tools/AndroidTrial/bin/Release" -name "$PKG-Signed.apk" -printf '%T@ %p\n' | sort -rn | head -1 | cut -d' ' -f2-)"
  echo "APK: $APK ($(stat -c %s "$APK") bytes)"
  "$ADB" install -r "$APK" | tail -1
  "$ADB" shell pm grant $PKG android.permission.POST_NOTIFICATIONS >/dev/null 2>&1
}
start_server() {
  dotnet "$ROOT/tools/AndroidTrialServer/bin/Release/net10.0/AndroidTrialServer.dll" --port $PORT > "$OUT/server.log" 2>&1 &
  SERVER_PID=$!
  trap 'kill $SERVER_PID 2>/dev/null; "$ADB" reverse --remove tcp:$PORT >/dev/null 2>&1' EXIT
  for _ in $(seq 1 30); do grep -q LISTENING "$OUT/server.log" && break; sleep 1; done
  "$ADB" reverse tcp:$PORT tcp:$PORT >/dev/null; BASE="http://127.0.0.1:$PORT"
}
wait_file() { # devicePath timeoutSeconds localCopy
  local i
  for i in $(seq 1 $(( $2 / 2 ))); do
    sleep 2
    "$ADB" shell cat "$1" 2>/dev/null | tr -d '\r' > "$3"
    [ -s "$3" ] && return 0
  done
  return 1
}
