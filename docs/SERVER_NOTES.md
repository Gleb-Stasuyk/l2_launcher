# Server-side work (not done yet)

## Update hosting (needed for the launcher to work)
Static https, e.g. nginx on the Linux VPS:
```
server {
  listen 443 ssl; server_name updates.example.com;
  ssl_certificate ...; ssl_certificate_key ...;      # Let's Encrypt
  root /var/www/l2update;                           # contents of out/update/
  location = /manifest.json      { add_header Cache-Control "no-cache"; }
  location = /manifest.json.sig  { add_header Cache-Control "no-cache"; default_type text/plain; }
  location /files/ { add_header Cache-Control "public, max-age=3600"; }
}
```
Upload in this order: first the `files/`, then `manifest.json.sig` + `manifest.json` (rsync --delay-updates), so clients
never see a new manifest before its files exist. Old files can stay.

## Login gate (design, to implement in Mobius later)
1. A small HTTPS endpoint (e.g. `POST /api/launch`, any language, behind nginx) receives the launcher request and
   stores `(client IP, expiresAt = now + 5 min, launcherVersion, manifestVersion)` in a table, e.g. `launcher_gate`
   in the Mobius DB. It answers `{"allow":true}`, or `{"allow":false,"message":"Please update the launcher"}` when
   `launcherVersion`/`manifestVersion` is too old.
2. Mobius LoginServer: in the login request handler (after password check), reject when there is no fresh
   `launcher_gate` row for the connection IP (config switch `LauncherGateEnabled`, allowlist for GMs/local IPs).
   Behind Docker/NAT, make sure the LoginServer sees the real client IP (host networking or the proxy protocol).
3. Later (optional): return a one-time token in `gameArguments` / via a proxy DLL. Without a client module the
   stock Interlude client cannot carry a token, so the IP binding is the practical variant.
Limits: this forces players through the launcher (and so through updates). It does NOT stop a cheater who
replays the HTTP request. Real anti-bot needs a guard (see /workspace/launcher_research/REPORT.md).

## Things to check on Gleb's servers (Docker at E:\L2J_Interlude_x100\docker and the VPS)
* Which client files the server pack expects (system patch, `L2.bin`/`clmods.dll` from the Mobius client patch?),
  so the reference client matches the server protocol (AllowedProtocolRevisions = 746).
* Where to host `/l2update` (the VPS nginx) and the domain/TLS certificate.
* Real client IP visibility for the future gate (Docker port publishing hides it with userland-proxy).
