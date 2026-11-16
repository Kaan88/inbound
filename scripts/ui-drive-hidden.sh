#!/usr/bin/env bash
# Runs scripts/ui-drive.ps1 -NoInput on a hidden desktop (see run-on-hidden-desktop.ps1) and prints its output.
# Usage: scripts/ui-drive-hidden.sh <desktop> <path-part> <out-dir> '<step>|<step>|...'
# Set DOWNLOADMANAGER_INSTANCE and DOWNLOADMANAGER_DATA first (the launcher refuses otherwise).
# -NoInput makes ui-drive refuse click/right/key/front: real input goes to the owner's active desktop, not this one.
# Exit code: ui-drive's, or the launcher's (124 on a timeout).
set -uo pipefail
desk=$1 part=$2 out=$3 steps=$4
root=$(cd "$(dirname "$0")/.." && pwd)
log=$(mktemp -u "${TEMP:-/tmp}/ui-drive-XXXX").txt
mkdir -p "$out"
q() { printf "'%s'" "${1//\'/\'\'}"; }   # a PowerShell single-quoted literal
cmd="& $(q "$(cygpath -w "$root/scripts/ui-drive.ps1")") -NoInput -PathPart $(q "$part") -OutDir $(q "$(cygpath -w "$out")") -Do $(q "$steps") *> $(q "$(cygpath -w "$log")"); exit \$LASTEXITCODE"
# -EncodedCommand (UTF-16LE base64) carries any character, quotes and the ellipsis included.
enc=$(printf '%s' "$cmd" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)
MSYS_NO_PATHCONV=1 powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$root/scripts/run-on-hidden-desktop.ps1")" \
  -Desktop "$desk" -Exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' \
  -Arguments "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $enc" -WorkingDirectory "$(cygpath -w "$root")" -Wait -TimeoutSeconds 300 >/dev/null
code=$?
# PowerShell 5 writes UTF-16.
if [ -f "$log" ]; then iconv -f UTF-16 -t UTF-8 "$log" 2>/dev/null || cat "$log"; rm -f -- "$log"; else echo "no output from ui-drive" >&2; fi
exit $code
