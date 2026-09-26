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
    return len(seen)

ap = argparse.ArgumentParser()
ap.add_argument("manifest")
ap.add_argument("--key", default=str(ROOT / "keys" / "manifest_private.pem"))
a = ap.parse_args()
mp = Path(a.manifest)
n = check(json.loads(mp.read_bytes().decode("utf-8")))
print(f"manifest OK: {n} files")
sign_file(mp, Path(a.key))
