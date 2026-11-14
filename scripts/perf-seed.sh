#!/usr/bin/env bash
# PERF.15 seeding on Linux: builds the binaries once, then starts an ISOLATED instance filled with N jobs in the list
# and A active downloads against a local throttled TestServer. perf-webui.sh and perf-window-linux.sh use it; it also
# runs on its own to leave an instance up for looking at by hand.
#
#   scripts/perf-seed.sh build [--out DIR] [--skip-webui] [--skip-window]
#   scripts/perf-seed.sh start --name NAME --jobs N --active A [--target host|server] [--out DIR] [--password PW]
#   scripts/perf-seed.sh stop  --name NAME [--out DIR]
#   scripts/perf-seed.sh status --name NAME [--out DIR]
#
# Source it (". scripts/perf-seed.sh") to call perf_build / perf_start / perf_stop from another script.
#
# Everything lives under DIR (default artifacts/perf, git-ignored; set PERF_OUT to change it):
#   bin/{lib,desktop,testserver}   the publish output (lib = Host.Linux, CLI, Server; desktop = the Uno window), laid out
#                                  as package-appimage.sh lays out an AppDir, so the Host finds the window
#   inst/NAME/{data,downloads}     the instance's state (DOWNLOADMANAGER_DATA) and download folder
#   inst/NAME/instance.env         what a caller needs: DOWNLOADMANAGER_INSTANCE/DATA, ports, pids, PERF_URL
#
# Isolation (CLAUDE.md "Known traps"): DOWNLOADMANAGER_INSTANCE gives the pipe/socket names of this instance and
# DOWNLOADMANAGER_DATA its data folder, so nothing here touches a running Inbound of the user's. Ports are random loopback ports.
# Only the processes this script started are ever stopped (their pids are in instance.env).
#
# --target host   (default) the Linux Host holds the jobs: for the Uno window.
# --target server the headless DownloadManager.Server holds them (web UI), signed in with --password (default a random
#                 one, written to instance.env). The held jobs are seeded through the Host first, which then stops; the
#                 server starts on the same data folder and the active downloads are added to it over HTTP.
#
# The jobs: N held jobs ("later", 1 MiB, never started) plus A active downloads of 256 MiB from the TestServer's
# /slow endpoint, one connection each, throttled to ACTIVE_BPS bytes per second (default 262144), so they stay active
# for about 17 minutes. N counts the held jobs only; the list has N + A rows.

_perf_root() { cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd; }
PERF_ROOT=$(_perf_root)
PERF_OUT=${PERF_OUT:-$PERF_ROOT/artifacts/perf}
ACTIVE_BPS=${ACTIVE_BPS:-262144}
ACTIVE_SIZE=${ACTIVE_SIZE:-268435456}
export PATH="$HOME/.dotnet:$PATH"
# The TestServer is framework-dependent (the others are self-contained): it must find a runtime.
[ -d "$HOME/.dotnet" ] && export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

perf_log() { echo "[perf-seed] $*" >&2; }

perf_free_port() { python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])'; }

# ---------------------------------------------------------------- build
perf_build() { # [--skip-webui] [--skip-window]
  local webui=1 window=1
  for a in "$@"; do case "$a" in --skip-webui) webui=0 ;; --skip-window) window=0 ;; esac; done
  local out=$PERF_OUT/bin lib=$PERF_OUT/bin/lib
  mkdir -p "$lib" "$out/testserver"
  local config=Release
  pub() { dotnet publish "$PERF_ROOT/$1" -c "$config" -r linux-x64 --self-contained true -p:PublishSingleFile=false \
            -p:DebugType=none -o "$2" -nologo -v q "${@:3}" || { perf_log "publish of $1 failed"; return 1; }; }
  if [ "$webui" = 1 ]; then
    # Built in a copy under OUT, so the checkout gets no node_modules and no dist.
    local wb=$PERF_OUT/webui-build
    rm -rf "$wb"; mkdir -p "$wb"
    ( cd "$PERF_ROOT/webui" && tar cf - --exclude=node_modules --exclude=dist . ) | ( cd "$wb" && tar xf - )
    ( cd "$wb" && npm ci --no-audit --no-fund --loglevel=error && npm run build >/dev/null ) || { perf_log "web UI build failed"; return 1; }
    pub src/DownloadManager.Server "$lib" "-p:WebUiDist=$wb/dist/" || return 1
    [ -f "$lib/wwwroot/assets/$(ls "$wb/dist/assets" | head -1)" ] || { perf_log "the server did not get the built web UI"; return 1; }
  else
    pub src/DownloadManager.Server "$lib" || return 1
  fi
  pub src/DownloadManager.Host.Linux "$lib" || return 1
  pub src/DownloadManager.Cli "$lib" || return 1
  if [ "$window" = 1 ]; then pub src/DownloadManager.Desktop.Uno "$PERF_OUT/bin/desktop" || return 1; fi
  dotnet publish "$PERF_ROOT/tools/TestServer" -c "$config" -o "$out/testserver" -nologo -v q || return 1
  perf_log "built into $PERF_OUT/bin"
}

# ---------------------------------------------------------------- instance
perf_inst() { echo "$PERF_OUT/inst/$1"; }

perf_cli() { # runs the CLI against the instance whose env is loaded
  timeout 300 "$PERF_OUT/bin/lib/DownloadManager.Cli" "$@"
}

perf_start_host() { # env of the instance must be exported
  "$PERF_OUT/bin/lib/inbound-host" --background >"$PERF_INST/host.out" 2>&1 &
  PERF_HOST_PID=$!
  local i
  for i in $(seq 1 60); do
    perf_cli host status >/dev/null 2>&1 && return 0
    kill -0 "$PERF_HOST_PID" 2>/dev/null || { perf_log "the Host exited: $(tail -3 "$PERF_INST/host.out")"; return 1; }
    sleep 0.5
  done
  perf_log "the Host did not answer in 30 s"; return 1
}

perf_stop_host() {
  [ -n "${PERF_HOST_PID:-}" ] || return 0
  perf_cli host shutdown >/dev/null 2>&1
  local i
  for i in $(seq 1 30); do kill -0 "$PERF_HOST_PID" 2>/dev/null || break; sleep 0.5; done
  perf_kill "$PERF_HOST_PID"
  wait "$PERF_HOST_PID" 2>/dev/null
  PERF_HOST_PID=""
}

perf_add_held() { # $1 count: batches of up to 1000, held for later
  local done=0 n a b
  while [ "$done" -lt "$1" ]; do
    n=$(( $1 - done )); [ "$n" -gt 1000 ] && n=1000
    printf -v a '%05d' $((done + 1)); printf -v b '%05d' $((done + n))
    perf_cli add-batch "http://127.0.0.1:$PERF_TS_PORT/file/job-[$a-$b].bin?size=1048576" --dest "$PERF_INST/downloads" --start later --yes >/dev/null \
      || { perf_log "add-batch $a-$b failed"; return 1; }
    done=$((done + n))
  done
}

perf_add_active() { # $1 count
  local i
  for i in $(seq 1 "$1"); do
    perf_cli add "http://127.0.0.1:$PERF_TS_PORT/slow/active-$i.bin?size=$ACTIVE_SIZE&bps=$ACTIVE_BPS&seed=$i" \
      --dest "$PERF_INST/downloads" --start now --connections 1 >/dev/null || { perf_log "add of active $i failed"; return 1; }
  done
}

# perf_start NAME JOBS ACTIVE TARGET [PASSWORD]
perf_start() {
  local name=$1 jobs=$2 active=$3 target=${4:-host} password=${5:-}
  PERF_INST=$(perf_inst "$name")
  [ -e "$PERF_INST/instance.env" ] && { perf_log "$name already exists; stop it or pick another name"; return 2; }
  [ -x "$PERF_OUT/bin/lib/inbound-host" ] || { perf_log "not built: run scripts/perf-seed.sh build"; return 2; }
  mkdir -p "$PERF_INST/data" "$PERF_INST/downloads"
  PERF_INST=$(cd "$PERF_INST" && pwd)
  export DOWNLOADMANAGER_INSTANCE="perf-$name-$$-$RANDOM"
  export DOWNLOADMANAGER_DATA="$PERF_INST/data"
  # Not inherited from the caller's shell: a variable of the user's own session must not point us at their app.
  unset DOWNLOADMANAGER_HOST DOWNLOADMANAGER_UI DOWNLOADMANAGER_URLS DOWNLOADMANAGER_ROOTS DOWNLOADMANAGER_AUTH DOWNLOADMANAGER_PASSWORD DOWNLOADMANAGER_PASSWORD_FILE
  PERF_TS_PORT=$(perf_free_port)
  "$PERF_OUT/bin/testserver/TestServer" --port "$PERF_TS_PORT" >"$PERF_INST/testserver.out" 2>&1 &
  PERF_TS_PID=$!
  local i ok=0
  for i in $(seq 1 40); do curl -fs "http://127.0.0.1:$PERF_TS_PORT/health" >/dev/null && { ok=1; break; }; sleep 0.25; done
  [ "$ok" = 1 ] || { perf_log "the TestServer did not answer: $(head -c 300 "$PERF_INST/testserver.out")"; perf_kill_started; return 1; }
  perf_start_host || { perf_kill_started; return 1; }
  # No completion windows or notifications: nobody is there, and a window is a process of its own. The CLI has no key for
  # this setting, so it goes through a settings export and import (as soak.sh does).
  perf_cli settings set defaultdir "$PERF_INST/downloads" >/dev/null
  perf_cli settings set maxconcurrent $((active > 4 ? active + 2 : 4)) >/dev/null
  perf_cli settings export "$PERF_INST/settings-in.json" >/dev/null
  sed -i 's/"showCompletionDialog": *true/"showCompletionDialog": false/' "$PERF_INST/settings-in.json"
  # All active downloads come from one host (127.0.0.1) and the per-host limit is 8 by default, so more than 8 would
  # leave the rest waiting. Raised only then, so runs with 8 or fewer keep the default.
  if [ "$active" -gt 8 ]; then
    sed -i "s/\"maxConnectionsPerHost\": *[0-9]*/\"maxConnectionsPerHost\": $((active + 2))/" "$PERF_INST/settings-in.json"
    grep -q "\"maxConnectionsPerHost\": $((active + 2))" "$PERF_INST/settings-in.json" || { perf_log "could not raise maxConnectionsPerHost"; perf_kill_started; return 1; }
  fi
  perf_cli settings import "$PERF_INST/settings-in.json" >/dev/null
  perf_add_held "$jobs" || { perf_kill_started; return 1; }

  local server_pid="" server_port="" url=""
  if [ "$target" = server ]; then
    # The held jobs are in the data folder now. The Host stops and the server takes the folder over; the server has no
    # CLI, so the active downloads are added once it is up, over HTTP (perf-seed-active.mjs).
    perf_stop_host
    server_port=$(perf_free_port)
    [ -n "$password" ] || password=$(python3 -c 'import secrets; print(secrets.token_urlsafe(18))')
    DOWNLOADMANAGER_URLS="http://127.0.0.1:$server_port" DOWNLOADMANAGER_ROOTS="$PERF_INST/downloads" \
      DOWNLOADMANAGER_PASSWORD="$password" DOWNLOADMANAGER_TORRENTS=off \
      "$PERF_OUT/bin/lib/DownloadManager.Server" >"$PERF_INST/server.out" 2>&1 &
    server_pid=$!
    url="http://127.0.0.1:$server_port"
    for i in $(seq 1 80); do curl -fs "$url/health" >/dev/null && break; kill -0 "$server_pid" 2>/dev/null || { perf_log "the server exited: $(tail -3 "$PERF_INST/server.out")"; perf_kill_started; return 1; }; sleep 0.25; done
    PERF_SERVER_PID=$server_pid
    perf_server_add_active "$url" "$password" "$active" || { perf_kill_started; return 1; }
  else
    perf_add_active "$active" || { perf_kill_started; return 1; }
  fi

  {
    echo "export DOWNLOADMANAGER_INSTANCE='$DOWNLOADMANAGER_INSTANCE'"
    echo "export DOWNLOADMANAGER_DATA='$DOWNLOADMANAGER_DATA'"
    echo "PERF_NAME='$name'; PERF_JOBS=$jobs; PERF_ACTIVE=$active; PERF_TARGET=$target"
    echo "PERF_TS_PORT=$PERF_TS_PORT; PERF_TS_PID=$PERF_TS_PID; PERF_HOST_PID='${PERF_HOST_PID:-}'"
    echo "PERF_SERVER_PID='$server_pid'; PERF_URL='$url'; PERF_PASSWORD='$password'"
  } > "$PERF_INST/instance.env"
  chmod 600 "$PERF_INST/instance.env"
  perf_log "$name up: $jobs held jobs + $active active, target $target${url:+, $url}"
}

# Signs in over HTTP and sends one job.add per active download through POST /api/ipc, as the web UI does.
perf_server_add_active() { # url password count
  [ "$3" -gt 0 ] || return 0
  node "$PERF_ROOT/scripts/perf-seed-active.mjs" --url "$1" --password "$2" --count "$3" \
    --base "http://127.0.0.1:$PERF_TS_PORT" --size "$ACTIVE_SIZE" --bps "$ACTIVE_BPS" --dest "$PERF_INST/downloads" \
    || { perf_log "adding the active downloads through the server failed"; return 1; }
}

# Stops one process of ours: only if its command line is still one of the binaries built under PERF_OUT (a recycled pid
# is never touched), SIGTERM first, SIGKILL after 3 s (the TestServer waits for its open slow downloads otherwise).
perf_kill() {
  local pid=$1
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null || return 0
  tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -qF "$PERF_OUT/bin/" || return 0
  kill "$pid" 2>/dev/null
  local i; for i in $(seq 1 12); do kill -0 "$pid" 2>/dev/null || return 0; sleep 0.25; done
  kill -9 "$pid" 2>/dev/null
}

perf_kill_started() { # only what this call started
  perf_kill "${PERF_SERVER_PID:-}"
  perf_stop_host
  perf_kill "${PERF_TS_PID:-}"
}

perf_stop() { # NAME: stops what instance.env lists, then leaves the data (delete inst/NAME yourself, or use --purge)
  local inst; inst=$(perf_inst "$1")
  [ -f "$inst/instance.env" ] || { perf_log "no instance $1"; return 0; }
  ( . "$inst/instance.env"
    PERF_INST=$inst
    perf_kill "$PERF_SERVER_PID"
    if [ -n "$PERF_HOST_PID" ] && kill -0 "$PERF_HOST_PID" 2>/dev/null; then
      perf_cli host shutdown >/dev/null 2>&1
      for _ in $(seq 1 30); do kill -0 "$PERF_HOST_PID" 2>/dev/null || break; sleep 0.5; done
      perf_kill "$PERF_HOST_PID"
    fi
    perf_kill "$PERF_TS_PID"
  )
  rm -f "$inst/instance.env"
  perf_log "stopped $1"
}

perf_purge() { rm -rf "$(perf_inst "$1")"; }

# ---------------------------------------------------------------- command line
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  set -u
  CMD=${1:-}; shift || true
  NAME=perf; JOBS=500; ACTIVE=1; TARGET=host; PW=""; EXTRA=()
  while [ $# -gt 0 ]; do
    case "$1" in
      --name) NAME=$2; shift 2 ;;
      --jobs) JOBS=$2; shift 2 ;;
      --active) ACTIVE=$2; shift 2 ;;
      --target) TARGET=$2; shift 2 ;;
      --out) PERF_OUT=$(mkdir -p "$2" && cd "$2" && pwd); shift 2 ;;
      --password) PW=$2; shift 2 ;;
      --purge) EXTRA+=(--purge); shift ;;
      --skip-webui|--skip-window) EXTRA+=("$1"); shift ;;
      *) echo "unknown option $1" >&2; exit 2 ;;
    esac
  done
  case "$CMD" in
    build) perf_build "${EXTRA[@]}" ;;
    start) perf_start "$NAME" "$JOBS" "$ACTIVE" "$TARGET" "$PW" ;;
    stop) perf_stop "$NAME"; [[ " ${EXTRA[*]:-} " == *" --purge "* ]] && perf_purge "$NAME" ;;
    status) cat "$(perf_inst "$NAME")/instance.env" | grep -v PASSWORD ;;
    *) sed -n 2,12p "$0"; exit 2 ;;
  esac
fi
