#!/usr/bin/env bash
# PERF.15 web UI baseline on Linux: builds what is missing (scripts/perf-seed.sh build), then runs perf-webui.mjs.
# All options go to the Node script (--jobs, --active, --duration, --warmup, --out, --results, --chrome, --keep).
#   scripts/perf-webui.sh                                  the full matrix: 500 and 5,000 jobs x 1, 8, 20 active, 60 s each
#   scripts/perf-webui.sh --jobs 500 --active 1 --duration 10 --warmup 4     a smoke run
# Needs: google-chrome-stable (headless; no window opens on your desktop), Node 22+ (built-in WebSocket), the .NET SDK
# in ~/.dotnet, and python3, curl. Nothing is installed; results go to $PERF_OUT/results (default artifacts/perf/results).
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
export PERF_OUT=${PERF_OUT:-$root/artifacts/perf}
export PATH="$HOME/.dotnet:$PATH"
[ -d "$HOME/.dotnet" ] && export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
if [ ! -x "$PERF_OUT/bin/lib/DownloadManager.Server" ] || [ ! -f "$PERF_OUT/bin/lib/wwwroot/signin/index.html" ] || [ ! -x "$PERF_OUT/bin/testserver/TestServer" ]; then
  "$root/scripts/perf-seed.sh" build --skip-window
fi
exec node "$root/scripts/perf-webui.mjs" --out "$PERF_OUT" "$@"
