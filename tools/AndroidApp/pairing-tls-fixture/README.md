# Disposable HTTPS pairing relay

Test-only TLS termination for an isolated Inbound HTTP server. It forwards bytes
unchanged to **127.0.0.1** on the supplied upstream port; HTTP and WebSocket
upgrades use the same stream. It does not log requests, headers, credentials,
tokens, or payloads. It is not a production reverse proxy.

Requires Node.js and a disposable, unencrypted PEM private key and matching
self-signed certificate. The certificate's **IP subjectAltName** must include
the IP address the client uses in the HTTPS URL. For example, with OpenSSL:

```powershell
$testDir = Join-Path $env:TEMP ("pairing-test-" + [guid]::NewGuid())
New-Item -ItemType Directory $testDir | Out-Null
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 `
  -keyout "$testDir\test-key.pem" -out "$testDir\test-cert.pem" -subj "/CN=pairing-test" `
  -addext "subjectAltName=IP:127.0.0.1"
node relay.js --key "$testDir\test-key.pem" --cert "$testDir\test-cert.pem" --upstream-port 5000
```

Run from this directory; replace `5000` with the isolated real Inbound HTTP
server's actual loopback port. The default listener is `127.0.0.1:8443`;
override the port with `--listen-port`. Neither the certificate nor key is
generated or installed by the relay. Keep the key outside the repository,
private to the test, and delete both files afterward. Do not use production
keys, change system trust stores, or commit test private keys. A self-signed
certificate is intentionally untrusted by default; use the Android app's
pairing-time certificate approval/pinning flow, not a device-wide trust change.

For a phone test only when explicitly authorized, generate a *new* disposable
certificate with the selected machine's LAN IP as its IP SAN, then explicitly
specify that IP using `--listen-host <LAN-IP>`. The HTTPS URL must use that
same IP and the selected listen port. Binding `0.0.0.0` or `::` exposes the
listener on all interfaces and is discouraged. The relay provides no access
control of its own: anyone able to reach it can reach the upstream server.
Use an isolated server/data directory, a trusted test network, and stop the
relay with Ctrl+C immediately after the test. No firewall rules are changed.

## Loopback smoke test

From this directory, run `node smoke-test.js`. It starts only an owned loopback
HTTP/upgrade server and an owned relay process, generates a one-day certificate
in a temporary directory, and cleans them up. It tests certificate validation,
HTTP forwarding, and a WebSocket upgrade and frame round trip. It does not
exercise the real Inbound server or an Android device. OpenSSL must be on PATH;
alternatively set `OPENSSL` to its executable path. No trust store is modified.
