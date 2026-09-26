# L2Launcher: Lineage II Interlude launcher (prototype)

Windows launcher for Gleb's L2jMobius Interlude server:
* UI in **English / Russian** (toggle top-right; the same toggle picks the **game language**)
* **Integrity check + repair** against a **signed** `manifest.json` (path, size, sha256); only missing or changed files are downloaded, over **https** only
* **EN/RU game language**: before starting `system\l2.exe` it writes `system\Option.ini` → `[Game] IsNative=True` (Russian) / `False` (English)
* **Self-update** of the launcher exe (signed via the manifest)
* **Pre-launch hook** for a future server-side login gate (`PreLaunchUrl`, disabled by default)

Based on okuzpe/Lineage2Launcher (MIT). See `LICENSE` and `NOTICE.md`.

## Layout
```
src/L2Launcher.Core/   platform-neutral logic (manifest, verify, download, Option.ini, self-update, gate)
src/L2Launcher/        WPF UI (net10.0-windows)
tests/L2Launcher.Tests xUnit tests (run on Linux; include an end-to-end install/repair test)
tools/keygen.py        create signing key pair + embed public key (src/L2Launcher.Core/PublicKey.cs)
tools/build_manifest.py build + sign the update tree from a reference client
publish.sh             test + publish dist/L2Launcher.exe (win-x64, self-contained, single file)
keys/                  manifest_private.pem (SECRET, gitignored) / manifest_public.pem
```

## 1. Keys (once)
```
python3 tools/keygen.py          # RSA-3072; writes keys/ and PublicKey.cs
./publish.sh                     # rebuild so the exe embeds the public key
```
A key pair was already generated on the box (`keys/`), and `dist/L2Launcher.exe` embeds it. **Move
`keys/manifest_private.pem` to an offline place** (not the web server, not git). If it leaks, anyone who
can serve files to your players can push code. If you lose it, ship a new launcher with a new key.

## 2. Configure the update URL
Either edit `BuildDefaults.ManifestUrl` in `src/L2Launcher.Core/Settings.cs` and rebuild, or ship a
`launcher.json` next to `L2Launcher.exe` (see `launcher.example.json`):
```json
{ "ManifestUrl": "https://updates.example.com/l2/manifest.json", "ServerName": "L2 Interlude x100" }
```
The URL must be https (exception for local tests: plain `http://127.0.0.1` / `localhost` / `[::1]` is accepted; the signature is still required). Files are fetched from `<folder of manifest.json>/files/<path>`, unless the manifest has `baseUrl`.
`GamePath` empty means the launcher's own folder, so put `L2Launcher.exe` into the client root (next to `system\`).
Player preferences (language) live in `%LOCALAPPDATA%\L2Launcher\prefs.json`, together with the hash cache, crash.log and update.log.

## 3. Build + publish an update
Prepare a **reference client** exactly as players should have it (English client + patched `system\nwindow.dll`
+ any server patch files). Then:
```
python3 tools/build_manifest.py \
   --client /path/to/reference_client \
   --overlay /workspace/l2ru/enru_switch/pkg --enru \
   --launcher-exe dist/L2Launcher.exe --launcher-version 1.0.0 \
   --version 20260926.1 --product "L2 Interlude x100" \
   --out out/update
```
* `--overlay .../enru_switch/pkg` adds `system\localization.ini` (Korea slot + Rus4la font, needed for both languages),
  the Russian `*-k.dat`, `SmallFont-r.gly` and `systextures\L2Font-r.utx`. The `enru_switch\` helper folder is skipped.
* `--enru` also creates `<name>-k.dat` copies of English files that have no Russian version, like `install_finish.bat` does.
* Excluded, and never overwritten by the launcher either: `system\Option.ini`, `User.ini`, `WindowsInfo.ini`, `*.log`, `*.part`,
  screenshots, replays, the launcher itself and `launcher.json`. Add more with `--exclude "glob"`.
* To keep the private key on another machine: run with `--unsigned` where the client is, copy `manifest.json` to the key machine,
  run `python3 tools/sign_manifest.py manifest.json`, copy `manifest.json.sig` back.
* Upload `out/update/` so that `manifest.json`, `manifest.json.sig` and `files/` sit at the ManifestUrl folder.
  Any static https host works (nginx on the VPS, a CDN). Serve `.sig` as text and don't gzip-transform it.
* Self-update: bump `<Version>` in `src/L2Launcher/L2Launcher.csproj`, run `./publish.sh`, then rebuild the manifest with
  `--launcher-exe dist/L2Launcher.exe --launcher-version <new>`. Clients update when the manifest version is newer
  (same host, sha256 checked, no downgrade).

## Language logic (from enru_switch/README.txt)
With the enru_switch `localization.ini`, the client always runs in the "Korea" slot:
`IsNative=True` → `*-k.dat` = **Russian**; `IsNative=False` → `*-e.dat` = **English**. The launcher changes only that key
(the file keeps its encoding, CRLF and all other lines) and creates `[Game]` if needed. Before a Russian launch it checks
that the RU files exist (`localization.ini`, `itemname/skillname/systemmsg/sysstring-k.dat`, `SmallFont-r.gly`,
`L2Font-r.utx`). The player can still switch in game (Options → Game → Korean/English).

## Pre-launch gate (future)
If `PreLaunchUrl` (https) is set, Play does `POST {launcherVersion, manifestVersion, language, clientVerified, machineId}`
and starts the game only on 2xx with `{"allow":true}`. `message` is shown to the player, and optional `gameArguments` are appended.
See `docs/SERVER_NOTES.md`.

## Tests
`dotnet test tests/L2Launcher.Tests` runs the tests: INI editing on a real Option.ini, path traversal, protected
files, self-update rules, http rejection, and an end-to-end run. The end-to-end test builds a fake client + real enru overlay,
signs it with keys/, serves it on 127.0.0.1, installs it into an empty folder, tampers/deletes files, repairs them,
checks that Option.ini is untouched and that a tampered manifest is rejected.
