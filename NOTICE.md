# Notice

This launcher is based on **okuzpe/Lineage2Launcher** ("L2Titan Launcher"), https://github.com/okuzpe/Lineage2Launcher,
MIT License, Copyright (c) 2025-2026 L2Titan (upstream commit 3910f52, 2026-06-25). The original license text is kept in `LICENSE`.

Taken from upstream and adapted: the signed-manifest approach (RSA PKCS#1 v1.5 / SHA-256, `manifest.json.sig`),
path-traversal protection, `.part` download + hash check + atomic replace, the self-update flow (PowerShell swap
script, anti-rollback, same-host rule), SHA-256 file integrity, `app.manifest`.

New in this fork: v1 manifest object format (path/size/sha256 + userConfig + launcher release in one signed
file), EN/RU UI, EN/RU game language via `system\Option.ini [Game] IsNative`, protected user-config files,
hash cache, pre-launch gate hook, WebView2 removed, new UI, Python `tools/keygen.py` and `tools/build_manifest.py`,
xUnit tests including an end-to-end install/repair test. No upstream images, fonts or branding are included.
