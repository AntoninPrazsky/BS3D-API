#!/usr/bin/env bash
# Installs one release of the service and switches to it (issue #3): download the linux-arm64 archive and its
# checksum from the GitHub Release, verify, extract to /opt/bs3d-api/<version>, flip the `current` symlink, restart,
# and ask GET /v1/health — flipping back to the previous version if it does not answer. The service applies its own
# schema at start, so an update is exactly this. Rows are counted before and after: an update must lose none.
#
#   sudo /opt/bs3d-api/current/deploy/update.sh v0.2.0      a given release
#   sudo /opt/bs3d-api/current/deploy/update.sh latest      the newest
set -euo pipefail

REPO=${API_REPO:-AntoninPrazsky/BS3D-API}
ROOT=/opt/bs3d-api
version=${1:?usage: update.sh <vX.Y.Z | latest>}

if [[ "$version" == latest ]]; then
    version=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | python3 -c 'import json,sys; print(json.load(sys.stdin)["tag_name"])')
fi

name="BS3D.Api-$version-linux-arm64"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

curl -fsSL "https://github.com/$REPO/releases/download/$version/$name.tar.gz" -o "$work/$name.tar.gz"
curl -fsSL "https://github.com/$REPO/releases/download/$version/$name.tar.gz.sha256" -o "$work/$name.tar.gz.sha256"
(cd "$work" && sha256sum -c "$name.tar.gz.sha256")

rm -rf "${ROOT:?}/$version"
mkdir -p "$ROOT/$version"
tar -xzf "$work/$name.tar.gz" -C "$ROOT/$version"
chmod +x "$ROOT/$version/BS3D.Api" "$ROOT/$version"/deploy/*.sh

count() {
    if [[ -x "$ROOT/current/BS3D.Api" && -f /var/lib/bs3d-api/scores.db ]]; then
        sudo -u bs3d-api env ASPNETCORE_ENVIRONMENT=Production Scores__Database=/var/lib/bs3d-api/scores.db \
            Scores__AddressSalt=count "$ROOT/current/BS3D.Api" admin count
    else
        echo "no database yet"
    fi
}

before=$(count)
previous=$(readlink -f "$ROOT/current" 2>/dev/null || true)

ln -sfn "$ROOT/$version" "$ROOT/current"
systemctl restart bs3d-api

healthy=false
for _ in $(seq 1 30); do
    if curl -fsS http://127.0.0.1:5000/v1/health > /dev/null 2>&1; then healthy=true; break; fi
    sleep 1
done

if ! $healthy; then
    echo "bs3d-api $version did not answer /v1/health within 30 s" >&2
    if [[ -n "$previous" && -d "$previous" ]]; then
        ln -sfn "$previous" "$ROOT/current"
        systemctl restart bs3d-api
        echo "rolled back to $(basename "$previous")" >&2
    fi
    exit 1
fi

after=$(count)
echo "bs3d-api $version is up: $(curl -fsS http://127.0.0.1:5000/v1/health)"
echo "rows before: $before"
echo "rows after:  $after"
[[ "$before" == "$after" || "$before" == "no database yet" ]] || { echo "ROW COUNT CHANGED ACROSS THE UPDATE" >&2; exit 2; }
