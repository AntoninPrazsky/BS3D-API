#!/usr/bin/env bash
# Tests deploy/update.sh (issues #3, #5) as root against a scratch folder: releases packed here as release.yml packs
# them, a stand-in for curl that serves them and answers /v1/health unless the running release carries a file named
# "unhealthy", and one for systemctl that records which release and which unit each restart of the service met. CI
# runs it with sudo; on the Pi, where root is not needed for anything it touches: unshare -r tests/deploy/update.test.sh
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -r): the script under test installs root's files" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/releases" "$work/opt" "$work/units"
umask 022
units=(bs3d-api.service bs3d-api-backup.service bs3d-api-backup.timer)

cat > "$work/bin/curl" << 'EOF'
#!/bin/sh
out=""
url=""
while [ $# -gt 0 ]; do
    case "$1" in
        -o) out=$2; shift ;;
        http*) url=$1 ;;
    esac
    shift
done
case "$url" in
    */v1/health)
        if [ -e "$API_ROOT/current/unhealthy" ]; then exit 7; fi
        echo '{"status":"ok"}' ;;
    */releases/download/*)
        cp "$STUB_RELEASES/$(basename "$(dirname "$url")")/$(basename "$url")" "$out" ;;
    *) exit 22 ;;
esac
EOF
cat > "$work/bin/systemctl" << 'EOF'
#!/bin/sh
echo "systemctl $*" >> "$STUB_LOG"
if [ "$*" = "restart bs3d-api" ]; then
    echo "running $(basename "$(readlink "$API_ROOT/current")") under $(head -1 "$UNIT_DIR/bs3d-api.service")" >> "$STUB_LOG"
fi
EOF
chmod +x "$work/bin/"*

# A release: a stand-in service, this repository's update.sh, and units whose first line names the set they belong to
release() {
    local version=$1 set=$2 health=$3 dir="$work/build/$1" name="BS3D.Api-$1-linux-arm64"
    mkdir -p "$dir/deploy" "$work/releases/$version"
    printf '#!/bin/sh\n' > "$dir/BS3D.Api"
    cp "$repo/deploy/update.sh" "$dir/deploy/"
    for unit in "${units[@]}"; do printf '# %s of %s\n' "$unit" "$set" > "$dir/deploy/$unit"; done
    if [[ $health == unhealthy ]]; then touch "$dir/unhealthy"; fi
    tar -czf "$work/releases/$version/$name.tar.gz" --owner=0 --group=0 --numeric-owner -C "$dir" .
    (cd "$work/releases/$version" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
}
release v1 A healthy
release v2 B unhealthy
release v3 A healthy

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# Runs update.sh for $1; its output goes to $work/out, the stand-ins' record to $work/calls, its exit code to $status
run() {
    : > "$work/calls"
    set +e
    env PATH="$work/bin:$PATH" API_ROOT="$work/opt" API_DATABASE="$work/no.db" UNIT_DIR="$work/units" HEALTH_SECONDS=2 \
        STUB_LOG="$work/calls" STUB_RELEASES="$work/releases" bash "$repo/deploy/update.sh" "$1" > "$work/out" 2>&1
    status=$?
    set -e
}
units_are() { for unit in "${units[@]}"; do [[ "$(head -1 "$work/units/$unit")" == "# $unit of $1" ]] || return 1; done; }
modes() { stat -c '%a %U %G' "${units[@]/#/$work/units/}" | sort -u; }
running() { basename "$(readlink "$work/opt/current")"; }
called() { grep -c "^systemctl $1\$" "$work/calls" || true; }
# The line number of the first record matching $1, so that order can be compared
at() { grep -n -m1 -- "$1" "$work/calls" | cut -d: -f1; }

# What install.sh up to v0.1.8 left: units from main, 0600
for unit in "${units[@]}"; do printf '# %s of main\n' "$unit" > "$work/units/$unit"; chmod 600 "$work/units/$unit"; done

run v1
if [[ $status -eq 0 && "$(running)" == v1 ]]; then pass "installs a first release"; else fail "installs a first release ($status): $(cat "$work/out")"; fi
if units_are A && [[ "$(modes)" == "644 root root" ]]; then pass "puts the release's units in place, root's, 0644"; else fail "the units: $(head -qn1 "$work/units/"*) $(modes)"; fi
if [[ -n "$(at 'daemon-reload')" && "$(at 'daemon-reload')" -lt "$(at 'restart bs3d-api')" ]] && grep -qx "running v1 under # bs3d-api.service of A" "$work/calls"; then pass "reloads them before the restart, which meets the release's own unit"; else fail "reloads before the restart: $(cat "$work/calls")"; fi
if [[ "$(called 'try-restart bs3d-api-backup.timer')" == 1 ]]; then pass "re-arms the backup timer it changed"; else fail "re-arms the backup timer it changed"; fi

run v3
if [[ $status -eq 0 && "$(running)" == v3 ]] && units_are A && [[ "$(called daemon-reload)" == 0 ]]; then pass "leaves units that did not change alone"; else fail "leaves units that did not change alone ($status): $(cat "$work/calls")"; fi

run v2
if [[ $status -eq 1 && "$(running)" == v3 ]] && grep -q "rolled back to v3" "$work/out"; then pass "rolls back a release that does not come up"; else fail "rolls back ($status): $(cat "$work/out")"; fi
if grep -qx "running v2 under # bs3d-api.service of B" "$work/calls"; then pass "the failed release ran under its own unit"; else fail "the failed release ran under: $(grep running "$work/calls")"; fi
if units_are A && [[ "$(modes)" == "644 root root" ]] && grep -qx "running v3 under # bs3d-api.service of A" "$work/calls"; then pass "and the rollback puts the units it replaced back"; else fail "the units after the rollback: $(head -qn1 "$work/units/"*) $(modes)"; fi
if [[ "$(called daemon-reload)" == 2 && "$(called 'try-restart bs3d-api-backup.timer')" == 2 ]]; then pass "reloading them both times"; else fail "daemon-reload $(called daemon-reload) times, the timer $(called 'try-restart bs3d-api-backup.timer')"; fi

chmod 600 "$work/units/bs3d-api-backup.service"
run v1
if [[ $status -eq 0 && "$(modes)" == "644 root root" && "$(called daemon-reload)" == 1 && "$(called 'try-restart bs3d-api-backup.timer')" == 0 ]] \
    && grep -qx "installed bs3d-api-backup.service from v1" "$work/out"; then pass "puts back a unit whose mode is not 0644, and leaves the timer be"; else fail "a unit's mode ($status): $(cat "$work/out")"; fi

echo
if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
