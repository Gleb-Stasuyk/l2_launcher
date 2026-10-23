# L2Start Local — implementation plan (launcher 1.4 + downloadable server package)

Status: draft for approval. Nothing below is implemented yet (only the 1.3.0 sources are in `main`).

## 0. Goal
A player downloads two things from GitHub Releases (VPS is off, l2start.ru/updates untouched):
1. **Server package** `l2start-server-<ver>.zip`: docker-compose, prebuilt image (no build on the player PC), geodata, clean DB, `install.bat`.
2. **Launcher** `L2Launcher.exe` + small client patch (`system\l2.ini` -> 127.0.0.1, our system files such as the return-skill icon).

The player drops the launcher into the Interlude client folder, picks the server folder, starts the local server from the launcher and plays.
Server selector keeps two entries: **Local** (always visible) and **L2Start** (visible, greyed "unavailable" while the VPS is off).

## 1. Constraints / principles
- Settings are applied **on server restart** (launcher writes files, then `docker compose restart game` / `up -d`).
- Launcher never edits files inside the image: all player settings go to the server folder (`overrides/local/...` + `modules.env` + `.env`), which compose already mounts read-only into `/opt/l2/overrides`.
- Docker Desktop is installed by the player; launcher only detects and links to https://www.docker.com/products/docker-desktop/.
- Prod signing key: never in git (`.gitignore` covers `keys/*private*`, `*.pfx`, `*.key`, `.env`). It lives offline with Gleb (handed over from the box on v1.3.0, see commit a3fa690); only `keys/manifest_public.pem` is in the repo.
- Local mode must work fully offline (no manifest download needed to play locally).

## 2. Settings -> server mapping
Base files: `core/dist/game/config/*.ini` (L2J Mobius CT_0 Interlude) overridden via `docker/overrides/<profile>/game/config/...`; platform modules via `config/modules/<id>.ini` or env `L2_MOD_<ID>__<KEY>=value` in `modules.env` (see l2start-server docs/MODULES.md). New profile: **`local`** (copy of `solo`) generated/owned by the launcher.

| UI setting | Target | Key(s) | Exists? |
|---|---|---|---|
| XP / SP | Rates.ini | `RateXp`, `RateSp` | yes |
| Adena | Rates.ini | `DropAmountMultiplierByItemId` (57,x), `DropChanceMultiplierByItemId` (57,x) | yes (launcher must rewrite list entry, keep others) |
| Drop | Rates.ini | `DeathDropAmountMultiplier` (+ `DeathDropChanceMultiplier` if present) | yes |
| Spoil | Rates.ini | `SpoilDropAmountMultiplier`, `SpoilDropChanceMultiplier` | yes |
| RB drop | Rates.ini | `RaidDropAmountMultiplier`, `RaidDropChanceMultiplier` | yes |
| Quest | Rates.ini | `RateQuestRewardXP/SP/Adena`, `RateQuestReward*` | yes |
| Manor | Rates.ini | `RateDropManor` | yes |
| (conflict) | module `rates-floating` | must be off or respect fixed rates in local profile | check |
| Auto-learn skills up to level N | Player.ini `AutoLearnSkills=True` (+`AutoLearnSkillsWithoutItems`, `AutoLearnForgottenScrollSkills`); level cap via bots/player module key `AutoLearnMaxLevel` | partly: Mobius has no level cap for players -> **new** small hook in a module (e.g. `starter-kit`) |
| Auto class change 1/2/3 | **new module `class-master`**: on level 20/40/76 offer/perform change (dialog with class choice, no quest/items); keys `Enabled`, `Change1`, `Change2`, `Change3`, `Free=true` | **new** (Mobius Interlude ClassMaster config not present in this tree) |
| Subclass without quest | Player.ini `AltSubClassWithoutQuests` | yes |
| Catacomb/necropolis entry without Seven Signs | Feature.ini `AltOpenNecropolis` (verify it also covers catacombs in CT_0) ; `StrictSevenSigns=False` | partly; if catacombs not covered -> **new** hook in `lw-fixes` |
| Admin rights | DB `characters.accesslevel` (0 / 100 GM, levels from `config/AccessLevels.xml`) via `docker exec l2s-db mariadb ...`; character list from `SELECT char_name, level, accesslevel FROM characters` | yes (DB), launcher **new** |
| Bots on/off, max | modules.env `L2_MODULE_BOTS_CORE=on/off`, `L2_MOD_BOTS_CORE__MAXBOTS` | yes |
| Town / spot density | `bots-core` keys (see docs/BOTS_DENSITY.md), `DirectorPoolReserve` | yes, names to confirm |
| PK bots, RB parties, traders | `bots-*` keys (`RbParty`, PvP/PK flag, trader count; economy-market module on/off) | yes, names to confirm |
| Buffer | module `town-buffer` on/off; SchemeBuffer.ini | yes |
| GM shop | community board / GM shop module (`CommunityBoard.ini`) | check, maybe **new** multisell |
| Enchant max / chance | `data/EnchantItemData.xml` (max) + Character/Player enchant rates; override via data override | check: data override path may be needed in entrypoint (**new**) |
| Start level / adena | Player.ini `StartingLevel`, `StartingAdena` | yes |
| Return skill | module `return-skill` on/off | yes |
| Backup / restore DB | existing backup-db.bat / restore.bat logic, reimplemented as `docker exec l2s-db mariadb-dump` into `<server>\backups\*.sql.gz` | yes |
| Reset world | `docker compose down -v` for `l2s-db-data` only + `up` (db-init recreates clean DB), with confirmation + auto-backup | yes |
| Logs | `docker compose logs --tail 500 -f game/login` | yes |
| Autostart server with launcher | launcher setting | new (launcher) |

Launcher stores its state in `<server>\launcher-local.json` (chosen values) and generates the ini overrides from it — the ini files are output, not the source of truth, so upgrades of the server package can regenerate them.

## 3. Work items and checkpoints

### Step 1 — server: `local` profile + release image (l2start-server repo)
- `docker/profiles/local.env`: `L2S_PROFILE=local`, `L2S_BIND=127.0.0.1`, random DB password generated by install.bat into `.env`.
- compose for release: `image: ghcr.io/gleb-stasuyk/l2start-server:<ver>` without `build:` (separate `docker-compose.release.yml`).
- Image delivery: `docker save | zstd` tar attached to the Release (works without registry login) — GHCR optional later.
- Geodata in the package (`geodata/`), `L2S_GEODATA_DIR=./geodata`.
- Clean DB: db-init on empty volume only; confirm no Gleb accounts/characters in SQL seed; bots are generated at runtime.
- `install.bat`: check `docker version` / Docker Desktop running -> else open download page; `docker load`; `docker compose up -d`; wait for health.
- **Checkpoint 1:** on a clean Windows VM/user (no repo, no JDK) the zip installs and a client connects to 127.0.0.1 and creates a character.

### Step 2 — server: new features
- `class-master` module (1/2/3 profession, optional), auto-learn level cap, catacomb entry check, enchant/GM-shop overrides if needed. Unit tests in platform test suite.
- **Checkpoint 2:** every setting from §2 can be changed via override file / modules.env and takes effect after `restart game` (test script per key).

### Step 3 — launcher core (L2Launcher.Core, no UI)
- `DockerService` (process runner for `docker`/`docker compose`, parse `docker compose ps --format json`, detect: not installed / daemon not running / containers states / health).
- `LocalServerConfig` model + `IniOverrideWriter` (preserve comments, change only owned keys) + `ModulesEnvWriter`.
- `DbService` via `docker exec l2s-db mariadb` (characters list, set accesslevel, backup/restore, reset).
- `ClientPatchService`: verify/apply `l2.ini` (127.0.0.1) + system files from embedded/zip patch, with backup of originals.
- xUnit tests with a fake process runner, golden ini files, patch apply/rollback.
- **Checkpoint 3:** tests green; CLI smoke (`L2Launcher.exe --local-status`) against Gleb's Docker.

### Step 4 — launcher UI
- Server selector: Local / L2Start (disabled, tooltip "сервер выключен").
- Tab "Локальный сервер": folder picker (remembered), status, Start/Stop/Restart, Logs window, settings sections (Рейты, Персонаж/профессии, Боты, Геймплей, Админ, Сервис), "Применить и перезапустить".
- Play button for Local: ensure server healthy -> patch client -> start `system\l2.exe`.
- EN/RU strings in Loc.cs.
- **Checkpoint 4 (Gleb tests in game):** full flow on his PC, one rebuild at the end.

### Step 5 — release pipeline
- GitHub Actions (launcher repo): build + test on windows-latest, publish single-file exe, attach `L2Launcher.exe` + `client-patch.zip` + SHA256 to Release on tag `v1.4.0`.
- l2start-server: workflow (or local script on Gleb PC, since build needs his toolchain/geodata) producing `l2start-server-<ver>.zip` with image tar; uploaded with `gh release upload` from Gleb's PC.
- Release only after Gleb's approval.
- **Checkpoint 5:** download both assets from the Release page on a clean machine, play locally.

## 4. Test approach
- Unit: launcher Core (xUnit, existing 1.3.0 suite kept green), server modules (platform tests).
- Integration on Gleb's PC: scripted `apply setting -> restart -> query` (e.g. rates via `//` admin command or DB check).
- Manual: Gleb's single in-game pass after checkpoint 4.

## 5. Open questions
1. Image distribution: Release asset tar (~1–2 GB, GitHub limit 2 GB per file) vs public GHCR image? Proposal: tar now.
2. Geodata licence/size ok to redistribute in the Release?
3. Admin: is GM level 100 enough, or also lower "moderator" levels in the UI?
4. Auto-profession: automatic or via dialog with class choice (proposal: dialog on 20/40/76)?
5. GM shop: which items/prices (existing multisell or new)?
6. Keep updating the client patch through the signed manifest later when the VPS returns?
