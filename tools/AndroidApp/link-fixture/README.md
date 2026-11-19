# Android link-intake fixture

Run manually with `python -I server.py`. The server binds only 127.0.0.1:47612;
the Android emulator reaches the computer at 10.0.2.2:47612. It is not started
by the app or this documentation.

`/page` links to `/files/a.zip`, `/files/b.pdf`, `/files/c.jpg`, the same-origin
cookie-protected `/auth.zip`, a cross-origin 127.0.0.1 auth sentinel, an expired
link and `/files/slow.zip`. `/signin` sets the invented `inbound_test=yes`
HttpOnly cookie and redirects to `/page`; `/auth.zip` returns 403 without it.
`/expired.zip?token=old` returns 403; `token=gone` returns 410 on GET and
HEAD for explicit expiry; `token=new` serves the fixture payload, and
`&slow=1` throttles its body. `/files/slow.zip` delays HEAD by six seconds.
The page also submits a POST to a hidden frame and offers a blob download; neither
should cause a Host GET replay merely because a browser download was observed.

Successful file routes serve deterministic 1 MiB bytes (0 through 255 repeated),
attachment names and MIME, HEAD, single byte ranges, stable ETag and Accept-Ranges.
The payload SHA-256 is
`fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83`.
Invalid ranges return 416. Logs show only method, route category, invented-cookie
presence and validated Range or NONE/INVALID; no raw headers or query strings.
