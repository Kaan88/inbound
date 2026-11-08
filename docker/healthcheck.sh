#!/bin/bash
# Container health check (D3): GET /health on this container, with bash's /dev/tcp so the image needs no curl.
# Where to ask comes from the first address in DOWNLOADMANAGER_URLS (default http://0.0.0.0:8080): a wildcard or
# localhost means 127.0.0.1, an IPv4 address is used as it is. For an https:// address this can only check that the
# port accepts a connection; give compose a health check of your own for more.
set -u
url="${DOWNLOADMANAGER_URLS:-http://0.0.0.0:8080}"
url="${url%%;*}"
scheme="${url%%://*}"
hostport="${url#*://}"
hostport="${hostport%%/*}"
port="${hostport##*:}"
host="${hostport%:*}"
case "$port" in ''|*[!0-9]*) if [ "$scheme" = "https" ]; then port=443; else port=80; fi; host="$hostport" ;; esac
case "$host" in
    ''|'*'|'+'|0.0.0.0|'[::]'|localhost|'[::1]') host=127.0.0.1 ;;
    *[!0-9.]*) host=127.0.0.1 ;;
esac

exec 3<>"/dev/tcp/$host/$port" || exit 1
[ "$scheme" = "http" ] || exit 0
printf 'GET /health HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
IFS= read -r -t 5 status <&3 || exit 1
case "$status" in
    "HTTP/"*" 200 "*) exit 0 ;;
esac
exit 1
