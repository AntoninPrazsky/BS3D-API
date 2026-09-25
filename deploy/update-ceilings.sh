#!/usr/bin/env bash
# Fetches every ceiling table the game's releases carry (BS3D-<version>-ceilings.json, BS3D#549) into the service's
# ceilings folder — all of them, because old game versions stay in the wild and their boards stay valid — and
# restarts the service when anything new arrived. Run after a game release (issue #3), by hand or from install.sh.
set -euo pipefail

CEILINGS=/var/lib/bs3d-api/ceilings
GAME_REPO=${GAME_REPO:-AntoninPrazsky/BS3D}

mkdir -p "$CEILINGS"
urls=$(curl -fsSL "https://api.github.com/repos/$GAME_REPO/releases?per_page=100" | python3 -c '
import json, sys
for release in json.load(sys.stdin):
    for asset in release.get("assets", []):
        if asset["name"].endswith("-ceilings.json"):
            print(asset["name"], asset["browser_download_url"])
')

added=0
while read -r name url; do
    [[ -z "$name" ]] && continue
    if [[ ! -f "$CEILINGS/$name" ]]; then
        curl -fsSL "$url" -o "$CEILINGS/$name.part"
        mv "$CEILINGS/$name.part" "$CEILINGS/$name"
        echo "added $name"
        added=$((added + 1))
    fi
done <<< "$urls"

chown -R bs3d-api:bs3d-api "$CEILINGS" 2>/dev/null || true
echo "$added new table(s); $(find "$CEILINGS" -maxdepth 1 -name "*.json" | wc -l) in $CEILINGS"

if (( added > 0 )) && systemctl is-active --quiet bs3d-api; then
    systemctl restart bs3d-api
    echo "restarted bs3d-api to load them"
fi
