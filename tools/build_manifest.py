#!/usr/bin/env python3
"""Build + sign the launcher update tree from a reference ("golden") client folder.

Example (client + EN/RU switch overlay + launcher self-update):
  python3 tools/build_manifest.py \
      --client /data/golden_client \
      --overlay /workspace/l2ru/enru_switch/pkg --enru \
      --launcher-exe dist/L2Launcher.exe --launcher-version 1.0.0 \
      --version 20260926.1 --product "L2 Interlude x100" \
      --out out/update

Output (upload the whole folder to your https server so manifest.json is at ManifestUrl):
  out/update/manifest.json       signed file list (path, size, sha256)
  out/update/manifest.json.sig   base64 RSA-SHA256 signature
  out/update/files/<path>        the files themselves (hard-linked when possible)

Rules:
  * user-config files (Option.ini, User.ini, WindowsInfo.ini, logs, ...) are never included
  * --overlay DIR adds/replaces files (later overlays win); the "enru_switch/" helper folder inside
    the EN/RU package is skipped automatically
  * --enru reproduces enru_switch\\install_finish.bat: creates <name>-k.dat copies of English
    <name>-e.dat files that have no Russian version (the engine refuses the Korea slot without them)
"""
import argparse, base64, fnmatch, hashlib, json, os, shutil, sys, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

DEFAULT_EXCLUDES = [
    "system/option.ini", "system/user.ini", "system/windowsinfo.ini",
    "*.log", "*.part", "*.launcher.tmp", "*.bak", "*.tmp",
    "screenshot/*", "screenshots/*", "replay/*",
    "enru_switch/*", "lang/*", "_backup_*/*", "_launcher_test_backup_*/*",
    "l2launcher*.exe", "launcher.json", "*.exe.new",
    "thumbs.db", "desktop.ini",
]
USER_CONFIG = ["system/Option.ini", "system/User.ini", "system/WindowsInfo.ini"]

# From enru_switch/install_finish.bat
ENRU_K_NAMES = ["castlename", "classinfo", "commandname", "creditgrp", "hennagrp", "huntingzone", "obscene",
                "raiddata", "servername", "staticobject", "symbolname", "variationeffectgrp", "zonename",
                "warningnotice", "idcname", "actionname", "eula", "gametip", "itemname", "npcname",
                "optiondata_client", "questname", "skillname", "sysstring", "systemmsg"]
ENRU_EXTRA = [("Sounds/SystemMsg_e.uax", "Sounds/SystemMsg_k.uax"), ("Notice/Notice-e.txt", "Notice/Notice-k.txt")]
ENRU_REQUIRED = ["system/localization.ini", "system/itemname-k.dat", "system/skillname-k.dat",
                 "system/systemmsg-k.dat", "system/sysstring-k.dat", "system/SmallFont-r.gly",
                 "systextures/L2Font-r.utx"]


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def excluded(rel: str, patterns) -> bool:
    r = rel.lower()
    return any(fnmatch.fnmatchcase(r, p.lower()) or fnmatch.fnmatchcase(r.rsplit("/", 1)[-1], p.lower())
               for p in patterns)


def scan(folder: Path, patterns, files: dict, label: str):
    n = 0
    for p in sorted(folder.rglob("*")):
        if not p.is_file() or p.is_symlink():
            continue
        rel = p.relative_to(folder).as_posix()
        if excluded(rel, patterns):
            continue
        key = rel.lower()
        # keep the casing already present in the client when an overlay replaces a file
        disp = files[key][0] if key in files else rel
        files[key] = (disp, p)
        n += 1
    print(f"  {label}: {n} files from {folder}")


def sign_file(manifest_path: Path, key_path: Path):
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding
    data = manifest_path.read_bytes()
    key = serialization.load_pem_private_key(key_path.read_bytes(), password=None)
    sig = key.sign(data, padding.PKCS1v15(), hashes.SHA256())
    key.public_key().verify(sig, data, padding.PKCS1v15(), hashes.SHA256())  # self-check
    Path(str(manifest_path) + ".sig").write_text(base64.b64encode(sig).decode())
    print(f"signed:   {manifest_path}.sig")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--client", required=True, help="reference client folder (contains system/)")
    ap.add_argument("--overlay", action="append", default=[], help="extra folder copied over the client (repeatable)")
    ap.add_argument("--enru", action="store_true", help="LEGACY Korea-slot layout: add -k.dat copies like enru_switch/install_finish.bat")
    ap.add_argument("--lang-ru", default=None,
                    help="folder with Russian versions under English names (e.g. system/itemname-e.dat). Files that differ from "
                         "the client become lang/en/<path> + lang/ru/<path>; the launcher copies the chosen set into place")
    ap.add_argument("--obsolete-enru", action="store_true",
                    help="list the Korea-slot files (system/*-k.dat etc.) as obsolete so launchers delete them")
    ap.add_argument("--exclude", action="append", default=[], help="extra glob to exclude (relative, case-insensitive)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--key", default=str(ROOT / "keys" / "manifest_private.pem"))
    ap.add_argument("--version", default=datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d.%H%M"))
    ap.add_argument("--product", default="Lineage II Interlude")
    ap.add_argument("--base-url", default=None, help="absolute https base for files (default: <manifest dir>/files/)")
    ap.add_argument("--launcher-exe", default=None, help="launcher exe to offer as self-update")
    ap.add_argument("--launcher-version", default=None)
    ap.add_argument("--no-copy", action="store_true", help="only write manifest (files/ already uploaded)")
    ap.add_argument("--no-link", action="store_true", help="always copy into files/ (no hard links; use when the reference folder will change)")
    ap.add_argument("--unsigned", action="store_true",
                    help="write manifest.json only; sign it elsewhere with tools/sign_manifest.py (keeps the private key off this machine)")
    a = ap.parse_args()

    client = Path(a.client)
    if not (client / "system").is_dir():
        sys.exit(f"{client} has no system/ folder")
    patterns = DEFAULT_EXCLUDES + a.exclude
    files = {}
    print("scanning:")
    scan(client, patterns, files, "client")
    for ov in a.overlay:
        scan(Path(ov), patterns, files, "overlay")

    if a.enru:
        added = 0
        for name in ENRU_K_NAMES:
            k, e = f"system/{name}-k.dat", f"system/{name}-e.dat"
            if k not in files and e in files:
                files[k] = (f"system/{name}-k.dat", files[e][1]); added += 1
        for e_rel, k_rel in ENRU_EXTRA:
            if k_rel.lower() not in files and e_rel.lower() in files:
                files[k_rel.lower()] = (k_rel, files[e_rel.lower()][1]); added += 1
        print(f"  enru: {added} -k copies of English files added")
        missing = [r for r in ENRU_REQUIRED if r.lower() not in files]
        if missing:
            sys.exit(f"--enru given but required RU files are missing: {missing}")

    languages = None
    if a.lang_ru:
        ru_dir = Path(a.lang_ru)
        targets = []
        for p in sorted(ru_dir.rglob("*")):
            if not p.is_file():
                continue
            rel = p.relative_to(ru_dir).as_posix()
            key = rel.lower()
            if key not in files:
                sys.exit(f"--lang-ru: {rel} has no English counterpart in the client")
            disp, en_src = files.pop(key)
            if sha256(en_src) == sha256(p):
                files[key] = (disp, en_src)  # identical: not language-dependent
                continue
            files[f"lang/en/{key}"] = (f"lang/en/{disp}", en_src)
            files[f"lang/ru/{key}"] = (f"lang/ru/{disp}", p)
            targets.append(disp)
        languages = {"default": "en", "sets": ["en", "ru"], "files": targets}
        print(f"  languages: {len(targets)} language-dependent files -> lang/en + lang/ru")

    obsolete = []
    if a.obsolete_enru:
        obsolete = [f"system/{n}-k.dat" for n in ENRU_K_NAMES] + [k for _, k in ENRU_EXTRA]
        dropped = [files.pop(o.lower())[0] for o in obsolete if o.lower() in files]
        if dropped:
            print(f"  obsolete: {len(dropped)} Korea-slot files found in the reference were left out: {dropped}")

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    fdir = out / "files"
    entries = []
    total = 0
    for key in sorted(files):
        disp, src = files[key]
        size = src.stat().st_size
        entries.append({"path": disp, "size": size, "sha256": sha256(src)})
        total += size
        if not a.no_copy:
            dst = fdir / disp
            dst.parent.mkdir(parents=True, exist_ok=True)
            if dst.exists():
                dst.unlink()
            if a.no_link:
                shutil.copy2(src, dst)
            else:
                try:
                    os.link(src, dst)
                except OSError:
                    shutil.copy2(src, dst)

    manifest = {
        "format": 1,
        "product": a.product,
        "version": a.version,
        "generatedAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "files": entries,
        "userConfig": USER_CONFIG,
    }
    if languages:
        manifest["languages"] = languages
    if obsolete:
        manifest["obsolete"] = obsolete
    if a.base_url:
        if not a.base_url.startswith("https://"):
            sys.exit("--base-url must be https://")
        manifest["baseUrl"] = a.base_url
    if a.launcher_exe:
        if not a.launcher_version:
            sys.exit("--launcher-version is required with --launcher-exe")
        le = Path(a.launcher_exe)
        rel = f"launcher/{le.name}"
        manifest["launcher"] = {"version": a.launcher_version, "path": rel,
                                "size": le.stat().st_size, "sha256": sha256(le)}
        if not a.no_copy:
            (fdir / "launcher").mkdir(parents=True, exist_ok=True)
            shutil.copy2(le, fdir / rel)

    data = json.dumps(manifest, ensure_ascii=False, indent=1).encode("utf-8")
    (out / "manifest.json").write_bytes(data)
    if a.unsigned:
        stale = out / "manifest.json.sig"
        if stale.exists():
            stale.unlink()
        print("UNSIGNED: sign manifest.json with tools/sign_manifest.py and put manifest.json.sig next to it")
    else:
        sign_file(out / "manifest.json", Path(a.key))
    print(f"manifest: {len(entries)} files, {total / 1048576:.1f} MiB, version {a.version}")
    print(f"written:  {out / 'manifest.json'}{'' if a.unsigned else ' (+ .sig)'}{'' if a.no_copy else ' and files/'}")


if __name__ == "__main__":
    main()
