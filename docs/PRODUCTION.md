# Production update distribution (l2start.ru)

- Manifest URL (built-in default since v1.3.0): https://l2start.ru/updates/manifest.json
  (a `launcher.json` next to the exe can still override `ManifestUrl`, e.g. http://127.0.0.1:8765/... for the local test host)
- VPS layout: `/var/www/l2start/updates/{manifest.json, manifest.json.sig, files/<client paths>, files/launcher/L2Launcher.exe}`
- nginx (443 block of /etc/nginx/sites-available/l2start, see `nginx_updates.snippet`):
  manifest + sig and `files/launcher/` = `Cache-Control: no-cache`; other files `public, max-age=86400`; Range (206) supported.
  If a CDN/Cloudflare proxy is ever put in front: purge `/updates/files/*` after every update (paths are not versioned).
- Both servers (`main` = L2Start, `local` = 127.0.0.1) are visible; do NOT pass `--hide-server local`.

## Publishing an update
1. Update the reference client, then build (no key needed):
   `python build_manifest.py --client <ref client> --overlay <chat-fix localization.ini dir> --lang-ru <ru dir> --obsolete-enru
    --server main=<dir with system/l2.ini for 80.78.246.62> --server local=<dir with system/l2.ini for 127.0.0.1>
    --launcher-exe L2Launcher.exe --launcher-version X.Y.Z --version prod.YYYYMMDD.N --product L2Start --out <out> --no-link --unsigned`
2. Sign on the offline machine holding `manifest_private.pem`: `python sign_manifest.py <out>/manifest.json --key manifest_private.pem`
   (needs `pip install cryptography`).
3. Upload `files/` first (scp -r per folder), verify on the VPS with a `sha256sum -c` list generated from the manifest,
   then upload `manifest.json.sig` and `manifest.json` last.
