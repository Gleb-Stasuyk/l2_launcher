#!/usr/bin/env python3
"""Sign an existing manifest.json (exact bytes) -> manifest.json.sig, on the machine that holds the private key.

  python3 tools/sign_manifest.py path/to/manifest.json [--key keys/manifest_private.pem]

Also sanity-checks the manifest structure (format 1, relative paths, sha256 hex, no duplicates).
"""
import argparse, json, re, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_manifest import sign_file, ROOT

def check(m):
    assert m.get("format") == 1, "format must be 1"
    seen = set()
    for f in m["files"]:
        p = f["path"]
        assert not p.startswith("/") and ":" not in p and ".." not in p.split("/"), f"bad path {p}"
        assert re.fullmatch(r"[0-9a-f]{64}", f["sha256"]), f"bad sha256 {p}"
        assert p.lower() not in seen, f"duplicate {p}"
        seen.add(p.lower())
    lang = m.get("languages")
    if lang:
        for t in lang["files"]:
            assert t.lower() not in seen, f"language target also a normal file: {t}"
            for c in lang["sets"]:
                assert f"lang/{c}/{t}".lower() in seen, f"set {c} missing {t}"
    srv = m.get("servers")
    if srv:
        ids = [x["id"] for x in srv["list"]]
        assert ids and len(ids) == len(set(ids)), "server ids must be unique and non-empty"
        for i in ids:
            assert re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,31}", i), f"bad server id {i}"
        d = [x for x in srv["list"] if x["id"] == srv["default"]]
        assert d and not d[0].get("hidden"), "default server missing or hidden"
        ltargets = {t.lower() for t in (lang or {}).get("files", [])}
        for t in srv["files"]:
            assert t.lower() not in seen, f"server target also a normal file: {t}"
            assert t.lower() not in ltargets, f"server target also a language file: {t}"
            for i in ids:
                assert f"server/{i}/{t}".lower() in seen, f"server {i} missing {t}"
    return len(seen)

ap = argparse.ArgumentParser()
ap.add_argument("manifest")
ap.add_argument("--key", default=str(ROOT / "keys" / "manifest_private.pem"))
a = ap.parse_args()
mp = Path(a.manifest)
n = check(json.loads(mp.read_bytes().decode("utf-8")))
print(f"manifest OK: {n} files")
sign_file(mp, Path(a.key))
