#!/bin/sh
# Inbound server container entrypoint (D3).
#
# Started as root (the default), it gives the data folder to PUID:PGID when its owner differs, checks that the download
# folders can be written, and then runs the server as PUID:PGID with no root rights left (setpriv, from the image's
# util-linux). Started as another user (docker run --user), it changes nothing and runs the server as that user.
set -eu

say() { printf '%s\n' "entrypoint: $*" >&2; }

if [ "$(id -u)" != "0" ]; then
    exec "$@"
fi

uid="${PUID:-1654}"
gid="${PGID:-1654}"
case "$uid$gid" in
    *[!0-9]*|"") say "PUID and PGID must be numbers (got PUID=$uid, PGID=$gid)."; exit 2 ;;
esac
if [ "$uid" = "0" ] || [ "$gid" = "0" ]; then
    say "PUID=0 or PGID=0 would run the server as root. Choose the user that owns your download folders."
    exit 2
fi

data="${DOWNLOADMANAGER_DATA:-/data}"
mkdir -p "$data"
# A new data folder is for its owner only: it will hold the secret key file and the password hash.
if [ -z "$(ls -A "$data")" ]; then
    chmod 700 "$data"
fi

# chown only when something in the data folder has another owner, so a restart doesn't rewrite every file's metadata.
if [ -n "$(find "$data" \( ! -user "$uid" -o ! -group "$gid" \) -print -quit)" ]; then
    say "giving $data to $uid:$gid"
    chown -R "$uid:$gid" "$data"
fi

as_user() { setpriv --reuid="$uid" --regid="$gid" --clear-groups "$@"; }

# The download folders are never changed recursively (they may be a large share). An empty one the user can't write
# (the image's own folder when nothing is mounted there) is given to the user; anything else only gets a warning.
roots="${DOWNLOADMANAGER_ROOTS:-/downloads}"
old_ifs="$IFS"
set -f
IFS=':;'
for root in $roots; do
    IFS="$old_ifs"
    [ -n "$root" ] || continue
    if [ ! -d "$root" ]; then
        say "warning: $root does not exist; mount a volume there or change DOWNLOADMANAGER_ROOTS."
        continue
    fi
    if ! as_user test -w "$root"; then
        if [ -z "$(ls -A "$root")" ]; then
            chown "$uid:$gid" "$root"
        else
            say "warning: user $uid:$gid can't write to $root. Set PUID/PGID to its owner, or change its owner."
        fi
    fi
done
IFS="$old_ifs"
set +f

export HOME=/tmp
exec setpriv --reuid="$uid" --regid="$gid" --clear-groups --no-new-privs "$@"
