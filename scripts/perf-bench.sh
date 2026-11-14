#!/usr/bin/env bash
# PERF.8 baseline: engine throughput, store saves and start times (tools/Bench; read tools/Bench/README.md first).
#
#   scripts/perf-bench.sh --out artifacts/perf-bench/linux-pc            # the full baseline, about 15 to 30 minutes
#   scripts/perf-bench.sh --out artifacts/perf-bench/smoke --quick       # smoke test, a minute or two
#
# Options of its own (anything else is passed to the tool, for example --size 8G --repeat 5 --disk-dir /mnt/ssd/bench):
#   --out DIR         where the CSV files and summary.md go (required)
#   --quick           small files, few runs; not a baseline
#   --no-publish      reuse the published programs in --bin-dir instead of publishing again
#   --bin-dir DIR     where the self-contained Release builds go (default artifacts/bench-bin)
#   --rid RID         runtime identifier to publish for (default linux-x64, or linux-arm64 on aarch64)
#
# Run it on a quiet machine, from a normal terminal, with the real Inbound app closed or not: the Host it starts uses its own
# instance name and data folder and never touches the real one.
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH"

OUT=""; QUICK=0; PUBLISH=1; BIN="$ROOT/artifacts/bench-bin"; RID=""; EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT=$2; shift 2 ;;
    --quick) QUICK=1; shift ;;
    --no-publish) PUBLISH=0; shift ;;
    --bin-dir) BIN=$2; shift 2 ;;
    --rid) RID=$2; shift 2 ;;
    *) EXTRA+=("$1"); shift ;;
  esac
done
[ -n "$OUT" ] || { echo "--out DIR is required" >&2; exit 2; }
if [ -z "$RID" ]; then
  case "$(uname -m)" in aarch64|arm64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac
fi

cd "$ROOT"
dotnet build tools/Bench -c Release -nologo -v q
if [ "$PUBLISH" = 1 ]; then
  rm -rf "$BIN"
  for project in Host.Linux Cli NativeHost; do
    dotnet publish "src/DownloadManager.$project" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false \
      -p:DebugType=none -o "$BIN/$project" -nologo -v q
  done
fi

ARGS=(all --out "$OUT" --cli "$BIN/Cli/DownloadManager.Cli" --native-host "$BIN/NativeHost/DownloadManager.NativeHost" --host "$BIN/Host.Linux/inbound-host")
[ "$QUICK" = 1 ] && ARGS+=(--quick)
case " ${EXTRA[*]:-} " in *" --disk-dir "*) ;; *) ARGS+=(--disk-dir "$ROOT/artifacts/bench-work") ;; esac
exec dotnet tools/Bench/bin/Release/net10.0/Bench.dll "${ARGS[@]}" ${EXTRA[@]+"${EXTRA[@]}"}
