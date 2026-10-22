#!/usr/bin/env bash
# Tests deploy/install-status.sh (#13) as root against a scratch folder, with stand-ins for getent, groupadd, usermod and
# systemctl that record what they are asked. CI runs it with sudo; on the Pi, where root is not needed for anything it
# touches: unshare -r tests/deploy/install-status.test.sh
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -r): the installer refuses anyone else" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/units"

# The group exists when $work/group-exists does; every call is recorded in $work/calls
cat > "$work/bin/getent" << 'EOF'
#!/bin/sh
echo "getent $*" >> "$STUB_LOG"
[ "$1 $2" = "group bs3d-status" ] && [ -e "$STUB_DIR/group-exists" ]
EOF
for tool in groupadd usermod systemctl; do
    # shellcheck disable=SC2016 # $* and $STUB_LOG are the stand-in's own, expanded when it runs
    printf '#!/bin/sh\necho "%s $*" >> "$STUB_LOG"\n' "$tool" > "$work/bin/$tool"
done
chmod +x "$work/bin/"*

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# Runs install-status.sh with its arguments; its output goes to $work/out, the stand-ins' record to $work/calls
install_status() {
    : > "$work/calls"
    set +e
    env PATH="$work/bin:$PATH" UNIT_DIR="$work/units" STUB_LOG="$work/calls" STUB_DIR="$work" \
        bash "$repo/deploy/install-status.sh" "$@" > "$work/out" 2>&1
    status=$?
    set -e
}
called() { grep -c -x -- "$1" "$work/calls" || true; }
changed_nothing() { [[ -z "$(find "$work/units" -mindepth 1)" ]] && ! grep -qE "^(groupadd|usermod|systemctl)" "$work/calls"; }
reset() { find "$work/units" -mindepth 1 -delete; rm -f "$work/group-exists"; }

reset
install_status --force
if [[ $status -eq 2 ]] && changed_nothing; then pass "refuses an unknown option, changes nothing"; else fail "refuses an unknown option ($status)"; fi

reset
install_status nobody extra
if [[ $status -eq 2 ]] && changed_nothing; then pass "refuses a second account, changes nothing"; else fail "refuses a second account ($status)"; fi

reset
install_status "nobody,root"
if [[ $status -eq 1 ]] && changed_nothing && grep -q "refusing the account" "$work/out"; then pass "refuses a name that is not one account"; else fail "refuses a name that is not one account ($status)"; fi

reset
install_status no-such-user-here
if [[ $status -eq 1 ]] && changed_nothing; then pass "refuses an account that does not exist"; else fail "refuses an account that does not exist ($status)"; fi

reset
install_status nobody
if [[ $status -eq 0 && "$(called 'groupadd --system bs3d-status')" == 1 && "$(called 'usermod -a -G bs3d-status nobody')" == 1 ]]; then
    pass "adds the group and the account that reads it"; else fail "the group and the reader ($status): $(cat "$work/calls")"; fi
if cmp -s "$repo/deploy/bs3d-api-status.service" "$work/units/bs3d-api-status.service" \
    && [[ "$(stat -c '%a %U %G' "$work/units/bs3d-api-status.service")" == "644 root root" ]]; then
    pass "installs the release's unit, root's, 0644"; else fail "the unit: $(stat -c '%a %U %G' "$work/units/bs3d-api-status.service" 2>&1)"; fi
if [[ "$(called 'systemctl daemon-reload')" == 1 && "$(called 'systemctl enable bs3d-api-status.service')" == 1 \
    && "$(called 'systemctl restart bs3d-api-status.service')" == 1 ]]; then
    pass "enables it and (re)starts it, so a changed unit takes effect"; else fail "starting it: $(cat "$work/calls")"; fi

touch "$work/group-exists"
install_status
if [[ $status -eq 0 && "$(called 'groupadd --system bs3d-status')" == 0 && "$(called 'usermod -a -G bs3d-status nobody')" == 0 \
    && "$(called 'systemctl restart bs3d-api-status.service')" == 1 ]]; then
    pass "leaves a group that exists alone, and adds no reader unless named"; else fail "a second run ($status): $(cat "$work/calls")"; fi

install_status --remove
if [[ $status -eq 0 && ! -e "$work/units/bs3d-api-status.service" && "$(called 'systemctl disable --now bs3d-api-status.service')" == 1 ]] \
    && ! grep -q "^groupdel" "$work/calls"; then
    pass "--remove stops it and takes the unit away, the group stays"; else fail "--remove ($status): $(cat "$work/calls")"; fi

echo
if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
