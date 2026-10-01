#!/usr/bin/env bash
# Installs one release of the service and switches to it (issue #3): download the linux-arm64 archive and its
# checksum from the GitHub Release, verify, extract to /opt/bs3d-api/<version>, flip the `current` symlink, restart,
# and ask GET /v1/health — flipping back to the previous version if it does not answer. The service applies its own
# schema at start, so an update is exactly this. Rows are counted before and after: an update must lose none.
#
# The release's systemd units are put in place with it (since v0.1.9, #5), and the ones they replace are put back on a
# rollback. The update that brings this runs the old script, which leaves the units alone: run it once more for the
# same version.
#
#   sudo /opt/bs3d-api/current/deploy/update.sh v0.2.0      a given release
#   sudo /opt/bs3d-api/current/deploy/update.sh latest      the newest
#
# API_ROOT, API_DATABASE, UNIT_DIR and HEALTH_SECONDS exist for tests/deploy/update.test.sh.
set -euo pipefail

# The row counts run the service's binary as bs3d-api, the one before the update included. v0.1.0 took the working
# directory as its content root and failed in the caller's home, which bs3d-api cannot read; / it can.
cd /

REPO=${API_REPO:-AntoninPrazsky/BS3D-API}
ROOT=${API_ROOT:-/opt/bs3d-api}
DATABASE=${API_DATABASE:-/var/lib/bs3d-api/scores.db}
UNIT_DIR=${UNIT_DIR:-/etc/systemd/system}
HEALTH_SECONDS=${HEALTH_SECONDS:-30}
UNITS=(bs3d-api.service bs3d-api-backup.service bs3d-api-backup.timer)
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
# Everything here is root's, because root runs it: the binary, this script's successor and update-ceilings.sh. tar run
# as root keeps an archive's owners, and v0.1.0 and v0.1.1 carry the GitHub runner's uid and gid 1001 (issue #3).
tar --no-same-owner -xzf "$work/$name.tar.gz" -C "$ROOT/$version"
# Root's tar also keeps an archive's modes, and a file anyone can write is as good as anyone's (issue #4)
foreign=$(find "$ROOT/$version" \( ! -user root -o ! -group root -o \( ! -type l -perm /022 \) \) -print -quit)
[[ -z "$foreign" ]] || { echo "refusing $version: $foreign is not root:root, or is writable by group or others" >&2; exit 1; }
chmod +x "$ROOT/$version/BS3D.Api" "$ROOT/$version"/deploy/*.sh

count() {
    if [[ -x "$ROOT/current/BS3D.Api" && -f "$DATABASE" ]]; then
        sudo -u bs3d-api env ASPNETCORE_ENVIRONMENT=Production Scores__Database="$DATABASE" \
            Scores__AddressSalt=count "$ROOT/current/BS3D.Api" admin count
    else
        echo "no database yet"
    fi
}

# A unit that changed is reloaded before the restart that should use it; the timer is re-armed only if it runs
reload_units() {
    (( $# > 0 )) || return 0
    systemctl daemon-reload
    if [[ " $* " == *" bs3d-api-backup.timer "* ]]; then systemctl try-restart bs3d-api-backup.timer; fi
}

before=$(count)
previous=$(readlink -f "$ROOT/current" 2>/dev/null || true)

# The release's units, each kept aside first for a rollback. One whose mode is not 0644 is put back too: install.sh up to
# v0.1.8 left them 0600, which systemd reads, but no one else can
mkdir -p "$work/units"
changed=()
for unit in "${UNITS[@]}"; do
    if [[ -f "$UNIT_DIR/$unit" ]]; then cp -p "$UNIT_DIR/$unit" "$work/units/$unit"; fi
    if ! cmp -s "$ROOT/$version/deploy/$unit" "$UNIT_DIR/$unit" || [[ "$(stat -c %a "$UNIT_DIR/$unit")" != 644 ]]; then
        install -m 0644 -o root -g root "$ROOT/$version/deploy/$unit" "$UNIT_DIR/$unit"
        changed+=("$unit")
    fi
done
reload_units "${changed[@]}"
if (( ${#changed[@]} > 0 )); then echo "installed ${changed[*]} from $version"; fi

ln -sfn "$ROOT/$version" "$ROOT/current"
systemctl restart bs3d-api

healthy=false
for _ in $(seq 1 "$HEALTH_SECONDS"); do
    if curl -fsS http://127.0.0.1:5000/v1/health > /dev/null 2>&1; then healthy=true; break; fi
    sleep 1
done

if ! $healthy; then
    echo "bs3d-api $version did not answer /v1/health within $HEALTH_SECONDS s" >&2
    if [[ -n "$previous" && -d "$previous" ]]; then
        ln -sfn "$previous" "$ROOT/current"
        for unit in "${changed[@]}"; do
            if [[ -f "$work/units/$unit" ]]; then cp -p "$work/units/$unit" "$UNIT_DIR/$unit"; else rm -f "$UNIT_DIR/$unit"; fi
        done
        reload_units "${changed[@]}"
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

# The admin page's launcher is the owner's to install (install-admin.sh, #5), never an update's: this only says so
for pair in "bs3d-admin-run.sh /usr/local/libexec/bs3d-admin-run" "bs3d-admin.sh /usr/local/bin/bs3d-admin"; do
    read -r shipped installed <<< "$pair"
    if [[ -e "$installed" ]] && ! cmp -s "$ROOT/$version/deploy/$shipped" "$installed"; then
        echo "The admin launcher in $version differs from the installed one: sudo $ROOT/current/deploy/install-admin.sh"
        break
    fi
done
