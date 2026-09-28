#!/bin/bash
# Adds /updates/ and /downloads/ static locations to the l2start 443 server block. Rolls back if nginx -t fails.
set -euo pipefail
CONF=/etc/nginx/sites-available/l2start          # sites-enabled/l2start is a symlink to this file
TS=$(date +%Y%m%d-%H%M%S)
BAK=/etc/nginx/sites-available/l2start.bak-$TS    # NOT in sites-enabled/ (nginx includes sites-enabled/*)
SNIP=/root/nginx_updates.snippet
[ -L /etc/nginx/sites-enabled/l2start ] || { echo "sites-enabled/l2start is not a symlink - abort"; exit 1; }
grep -q "location ^~ /updates/" "$CONF" && { echo "already applied"; exit 0; }
cp -a "$CONF" "$BAK"
python3 - "$CONF" "$SNIP" <<'PY'
import sys
conf, snip = sys.argv[1], sys.argv[2]
s = open(conf).read()
anchor = "    location / {\n        proxy_pass http://l2start_frontend;"
assert s.count(anchor) == 1, "anchor not unique"
s = s.replace(anchor, open(snip).read().rstrip("\n") + "\n\n" + anchor)
open(conf, "w").write(s)
PY
if nginx -t; then
  systemctl reload nginx
  echo "APPLIED backup=$BAK"
  diff -u "$BAK" "$CONF" || true
else
  cp -a "$BAK" "$CONF"
  echo "nginx -t FAILED - restored $BAK"; nginx -t; exit 1
fi
