#!/usr/bin/env bash
# Tests deploy/firewall.sh (issue #4) as root against a scratch folder, with stand-ins for ip (a home network to read),
# systemctl and systemd-run, and for nft: it records what it is asked, except a check (-c), which the real nft makes,
# so the rules the script writes are the ones nft is shown, and nothing is ever loaded into the machine running this.
# Then its behaviour, end to end: firewall-lab.sh runs it with the real ip and nft in a network namespace of its own.
# CI runs it with sudo; on the Pi, where root is not needed for anything it touches: unshare -rn tests/deploy/firewall.test.sh
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -rn): the script under test refuses anyone else" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
real_nft=$(PATH="$PATH:/usr/sbin:/sbin" command -v nft || true)
[[ -n "$real_nft" ]] || { echo "nft is not installed here: apt install nftables" >&2; exit 1; }
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/etc"
export STUB_LOG="$work/calls" REAL_NFT="$real_nft"

cat > "$work/bin/ip" << 'EOF'
#!/bin/sh
case "$*" in
    "-4 route show default") [ -n "$STUB_NO_ROUTE" ] || echo "default via 192.168.0.1 dev wlan0 proto dhcp src 192.168.0.239 metric 600" ;;
    "-4 -o addr show dev wlan0 scope global") echo "3: wlan0    inet 192.168.0.239/24 brd 192.168.0.255 scope global dynamic noprefixroute wlan0\       valid_lft 80000sec" ;;
    "-6 -o addr show dev wlan0 scope global")
        [ -n "$STUB_NO_V6" ] && exit 0
        echo "3: wlan0    inet6 2001:db8:1:2::5/64 scope global dynamic noprefixroute \       valid_lft 86000sec"
        echo "3: wlan0    inet6 2001:db8:1:2:aaaa::9/64 scope global temporary dynamic \       valid_lft 86000sec"
        echo "3: wlan0    inet6 fd00:1:2:3::5/64 scope global noprefixroute \       valid_lft forever" ;;
    *) echo "ip stand-in: unexpected $*" >&2; exit 1 ;;
esac
EOF
cat > "$work/bin/nft" << 'EOF'
#!/bin/sh
if [ "$1" = "-c" ]; then
    [ -n "$STUB_CHECK_FAIL" ] && { echo "nft check refused" >> "$STUB_LOG"; exit 1; }
    cp "$3" "$STUB_CHECKED"
    exec unshare -n "$REAL_NFT" "$@"
fi
echo "nft $*" >> "$STUB_LOG"
# Listed the way the real nft writes it, in pieces: a reader that stops at "policy drop" leaves it writing into a closed pipe
if [ "$*" = "list chain inet filter input" ] && [ -n "$STUB_LOADED" ]; then
    echo "table inet filter {"; echo "	chain input {"; echo "		type filter hook input priority filter; policy drop;"
    sleep 0.2; echo "		iif \"lo\" accept"; echo "	}"; echo "}"
fi
EOF
# shellcheck disable=SC2016  # the stand-ins expand $* and $STUB_LOG when they run, not here
for tool in systemctl systemd-run; do printf '#!/bin/sh\necho "%s $*" >> "$STUB_LOG"\n' "$tool" > "$work/bin/$tool"; done
chmod +x "$work/bin/"*

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# Runs firewall.sh with $1 as its argument and any VAR=value after it; output to $work/out, exit code to $status
run() {
    local args=()
    [[ -n "$1" ]] && args=("$1")
    shift
    : > "$STUB_LOG"
    rm -f "$work/checked"
    set +e
    env PATH="$work/bin:$PATH" NFT_CONF="$work/etc/nftables.conf" STUB_CHECKED="$work/checked" WARMUP_SECONDS=0 "$@" \
        bash "$repo/deploy/firewall.sh" "${args[@]}" > "$work/out" 2>&1
    status=$?
    set -e
}
at() { grep -n -m1 -- "$1" "$STUB_LOG" | cut -d: -f1; }
original="# the distribution's own file"
printf '%s\n' "$original" > "$work/etc/nftables.conf"
conf=$work/etc/nftables.conf

run "" STUB_NO_ROUTE=1
if [[ $status -ne 0 && "$(cat "$conf")" == "$original" ]] && grep -q "no default route" "$work/out"; then pass "without a default route it changes nothing"; else fail "without a default route ($status): $(cat "$work/out")"; fi

run "" STUB_CHECK_FAIL=1
if [[ $status -ne 0 && "$(cat "$conf")" == "$original" ]] && ! grep -q "systemd-run\|nft -f" "$STUB_LOG"; then pass "rules nft refuses are neither written nor loaded"; else fail "rules nft refuses ($status)"; fi

run ""
if [[ $status -eq 0 ]]; then pass "loads the rules"; else fail "loads the rules ($status): $(cat "$work/out")"; fi
if grep -q "ip saddr 192.168.0.0/24 tcp dport { 22, 5900 } accept" "$conf" \
    && grep -q "ip6 saddr { fe80::/10, 2001:db8:1:2::/64, fd00:1:2:3::/64 } tcp dport { 22, 5900 } accept" "$conf" \
    && ! grep -q "@LAN" "$conf"; then pass "the home network's prefixes, not its addresses, each once"; else fail "the prefixes: $(grep saddr "$conf")"; fi
if cmp -s "$conf" "$work/checked" && "$real_nft" --version > /dev/null; then pass "the real nft checked the very rules written (no stand-in for -c)"; else fail "the rules checked are not the rules written"; fi
if grep -q "policy drop" "$conf" && [[ "$(stat -c '%a %U' "$conf")" == "644 root" ]]; then pass "/etc/nftables.conf drops by default, root's, 0644"; else fail "the file: $(stat -c '%a %U' "$conf")"; fi
if [[ "$(cat "$conf.before-bs3d")" == "$original" ]]; then pass "the distribution's file is kept aside"; else fail "the old file is not kept aside"; fi
if [[ -n "$(at "systemd-run")" && "$(at "systemd-run")" -lt "$(at "nft -f $conf")" ]] && grep -q "systemd-run --quiet --on-active=180s --unit=bs3d-firewall-undo .*nft flush ruleset" "$STUB_LOG"; then pass "the undo is armed before the rules load"; else fail "the undo: $(cat "$STUB_LOG")"; fi
if [[ -n "$(at "warmup.nft")" && "$(at "systemd-run")" -lt "$(at "warmup.nft")" && "$(at "warmup.nft")" -lt "$(at "nft -f $conf")" ]]; then pass "conntrack watches with nothing dropped before the rules load"; else fail "the warm-up: $(cat "$STUB_LOG")"; fi
if grep -q -- "--confirm" "$work/out"; then pass "it says how to confirm"; else fail "it says how to confirm"; fi

run ""
if [[ $status -eq 0 && "$(cat "$conf.before-bs3d")" == "$original" ]] && [[ "$(at "systemctl stop bs3d-firewall-undo")" -lt "$(at "systemd-run")" ]]; then pass "a second run keeps the first backup and replaces a pending undo"; else fail "a second run ($status)"; fi

run "" STUB_NO_V6=1
if [[ $status -eq 0 ]] && grep -q "ip6 saddr { fe80::/10 } tcp dport" "$conf"; then pass "without an IPv6 prefix, link-local only"; else fail "without an IPv6 prefix: $(grep "ip6 saddr" "$conf")"; fi

run --confirm
if [[ $status -ne 0 ]] && ! grep -q "systemctl enable" "$STUB_LOG"; then pass "a confirm with no rules loaded enables nothing"; else fail "a confirm with no rules loaded ($status)"; fi

run --confirm STUB_LOADED=1
if [[ $status -eq 0 ]] && grep -q "systemctl stop bs3d-firewall-undo.timer" "$STUB_LOG" && grep -q "systemctl enable --now nftables" "$STUB_LOG"; then pass "a confirm disarms the undo and loads the rules at boot"; else fail "a confirm ($status): $(cat "$STUB_LOG")"; fi

run --remove
if [[ $status -eq 0 && "$(cat "$conf")" == "$original" && ! -e "$conf.before-bs3d" ]] && grep -q "nft flush ruleset" "$STUB_LOG" && grep -q "systemctl disable --now nftables" "$STUB_LOG"; then pass "--remove flushes, stops loading at boot and puts the old file back"; else fail "--remove ($status)"; fi

run --force
if [[ $status -eq 2 ]]; then pass "refuses an unknown argument"; else fail "refuses an unknown argument ($status)"; fi

# --- Behaviour, end to end: the script with the real ip and nft, in a network namespace of its own
mkdir -p "$work/labbin"
cp "$work/bin/systemctl" "$work/bin/systemd-run" "$work/labbin/"
while IFS= read -r line; do
    echo "$line"
    if [[ $line == FAIL* ]]; then failures=$((failures + 1)); fi
done < <(STUB_LOG="$work/lab-calls" unshare -n bash "$repo/tests/deploy/firewall-lab.sh" "$repo" "$work/labbin" 2>&1)

echo
if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
