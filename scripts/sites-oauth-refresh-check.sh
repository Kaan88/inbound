#!/usr/bin/env bash
# SITES.0 point 9: does rclone write a refreshed OAuth token back into a NAMED in-memory remote, readable with
# config/get, when it runs with --config "" (no config file)? remote-sites.md plans on exactly that.
#
#   scripts/sites-oauth-refresh-check.sh <rclone> drive|dropbox
#
# What it does, in order:
#   1. `rclone authorize <kind>`: prints a sign-in address; you open it, sign in to YOUR account and allow rclone's app
#      (rclone's shared app ID; it asks for full access to Drive or Dropbox). The token comes back on stdout.
#   2. Starts `rclone rcd` on loopback with a random rc password in the environment and --config "".
#   3. config/create a named remote `probe` with that token, but with its "expiry" moved into the past, so the next call
#      must refresh it.
#   4. operations/list of the top folder (read only; the names are counted, never printed).
#   5. config/get probe: compares the token with the one it was given and prints only yes/no answers and the new expiry.
#   6. Stops rclone. Nothing is written to disk: the token lives in shell variables and rclone's memory only.
# Afterwards you may remove rclone's access from your account (Google: myaccount.google.com > Security > Third-party
# access; Dropbox: Settings > Connected apps). No token, address or file name is printed.
set -euo pipefail
rclone=${1:?usage: $0 <rclone> drive|dropbox}
kind=${2:?usage: $0 <rclone> drive|dropbox}
case $kind in drive|dropbox) ;; *) echo "kind must be drive or dropbox" >&2; exit 2 ;; esac
command -v python3 >/dev/null || { echo "needs python3" >&2; exit 2; }
command -v curl >/dev/null || { echo "needs curl" >&2; exit 2; }

env -u RCLONE_CONFIG "$rclone" version | head -1
echo "Step 1: sign in. rclone prints an address; open it in your browser and allow access."
# authorize prints the token between ---> and <---End paste on stdout; the sign-in chatter goes to stderr.
auth_out=$(env -u RCLONE_CONFIG "$rclone" authorize "$kind" --auth-no-open-browser --config "")
token=$(printf '%s\n' "$auth_out" | python3 -c '
import sys, re
text = sys.stdin.read()
m = re.search(r"Paste the following into your remote machine --->\s*(\{.*?\})\s*<---End paste", text, re.S)
print(m.group(1) if m else "")')
[ -n "$token" ] || { echo "No token came back from rclone authorize." >&2; exit 1; }
unset auth_out

stale=$(TOKEN="$token" python3 -c '
import json, os
t = json.loads(os.environ["TOKEN"])
print("given expiry:", t.get("expiry"), "| has refresh_token:", bool(t.get("refresh_token")), file=__import__("sys").stderr)
t["expiry"] = "2001-01-01T00:00:00Z"
print(json.dumps(t))')

pass=$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')
port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
# Exported in a subshell, not `env VAR=... cmd`, whose own command line would show the password.
( unset RCLONE_CONFIG; export RCLONE_RC_USER=probe RCLONE_RC_PASS="$pass"
  exec "$rclone" rcd --rc-addr "127.0.0.1:$port" --config "" --log-level NOTICE >/dev/null 2>&1 ) &
rcd=$!
trap 'kill $rcd 2>/dev/null || true' EXIT
# The password and the body never go on curl's command line (/proc/<pid>/cmdline is readable by every user):
# the password through a file descriptor, the body (which can hold the token) on stdin.
rc() { printf '%s' "$2" | curl -s -K <(printf 'user = "probe:%s"\n' "$pass") -H 'Content-Type: application/json' \
  --data-binary @- "http://127.0.0.1:$port/$1"; }
for _ in $(seq 50); do rc rc/noop '{}' >/dev/null 2>&1 && break; sleep 0.1; done

create=$(STALE="$stale" KIND="$kind" python3 -c '
import json, os
print(json.dumps({"name": "probe", "type": os.environ["KIND"], "parameters": {"token": os.environ["STALE"]},
                  "opt": {"nonInteractive": True, "noObscure": True}}))')
rc config/create "$create" >/dev/null
echo "Step 3: remote created with an expired token."

listing=$(rc operations/list '{"fs":"probe:","remote":""}')
LISTING="$listing" python3 -c '
import json, os
r = json.loads(os.environ["LISTING"])
print("Step 4: list", "failed (status %s); the error is not shown because it may hold the token" % r.get("status") if "error" in r else "ok, %d entries" % len(r.get("list", [])))'

after=$(rc config/get '{"name":"probe"}')
AFTER="$after" STALE="$stale" python3 -c '
import json, os
before = json.loads(os.environ["STALE"])
remote = json.loads(os.environ["AFTER"])
raw = remote.get("token")
if not raw:
    print("Step 5: config/get has no token for the remote")
else:
    t = json.loads(raw)
    print("Step 5: access token changed:", t.get("access_token") != before.get("access_token"))
    print("        refresh token rotated:", t.get("refresh_token") != before.get("refresh_token"))
    print("        expiry now:", t.get("expiry"))'
rc core/quit '{}' >/dev/null 2>&1 || true
wait $rcd 2>/dev/null || true
trap - EXIT
unset token stale pass
echo "Done. rclone has stopped; nothing was saved."
