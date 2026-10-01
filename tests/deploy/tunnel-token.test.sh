#!/usr/bin/env bash
# Tests deploy/tunnel-token.sh (issue #4) as root against a scratch folder, with stand-ins for systemctl, journalctl
# and cloudflared on PATH. CI runs it with sudo; on the Pi: sudo tests/deploy/tunnel-token.test.sh
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo): the script under test refuses anyone else" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
script="$repo/deploy/tunnel-token.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/units"
export STUB_LOG="$work/calls" STUB_CONNECT=1
touch "$STUB_LOG"

# Every stand-in records its arguments, so a token on any of their command lines would show
cat > "$work/bin/systemctl" << 'EOF'
#!/bin/sh
echo "systemctl $*" >> "$STUB_LOG"
EOF
cat > "$work/bin/cloudflared" << 'EOF'
#!/bin/sh
echo "cloudflared $*" >> "$STUB_LOG"
EOF
# The journal always holds the old connector's registrations; only the --since the script passes keeps them out,
# so without it the "did not connect" case below would wrongly see a connection
cat > "$work/bin/journalctl" << 'EOF'
#!/bin/sh
echo "journalctl $*" >> "$STUB_LOG"
case " $* " in
    *" --since "*) if [ "$STUB_CONNECT" = 1 ]; then echo "INF Registered tunnel connection connIndex=0"; fi ;;
    *) echo "INF Registered tunnel connection connIndex=0" ;;
esac
EOF
chmod +x "$work/bin/"*

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# Runs the script with $1 on its standard input; its output goes to $work/out, its exit code to $status
run() {
    : > "$STUB_LOG"
    set +e
    printf '%s' "$1" | env PATH="$work/bin:$PATH" TOKEN_FILE="$work/etc/token" UNIT_DIR="$work/units" WAIT_SECONDS=2 \
        STUB_LOG="$STUB_LOG" STUB_CONNECT="$STUB_CONNECT" bash "$script" > "$work/out" 2>&1
    status=$?
    set -e
}

# Shaped as cloudflared requires: standard base64 of a JSON object with a, t and s
fake_token() {
    python3 -c 'import base64, json, secrets, uuid
print(base64.b64encode(json.dumps({"a": secrets.token_hex(16), "t": str(uuid.uuid4()),
    "s": base64.b64encode(secrets.token_bytes(32)).decode()}, separators=(",", ":")).encode()).decode())'
}
token_a=$(fake_token)
token_b=$(fake_token)
unchanged() { [[ "$(tr -d '[:space:]' < "$work/etc/token")" == "$1" ]] && ! grep -q "restart" "$STUB_LOG"; }

run "not a token"
if [[ $status -ne 0 && ! -e "$work/etc/token" ]] && grep -q "not a tunnel token" "$work/out"; then pass "refuses what is not a token, writes nothing"; else fail "refuses what is not a token ($status)"; fi

run ""
if [[ $status -ne 0 && ! -e "$work/etc/token" ]] && grep -q "No token given" "$work/out"; then pass "refuses an empty input"; else fail "refuses an empty input ($status)"; fi

touch "$work/units/cloudflared-update.timer" "$work/units/cloudflared-update.service"
run "$token_a"
if [[ $status -eq 0 ]]; then pass "installs a first token"; else fail "installs a first token ($status): $(cat "$work/out")"; fi
if [[ "$(tr -d '[:space:]' < "$work/etc/token")" == "$token_a" ]]; then pass "the file holds the token"; else fail "the file holds the token"; fi
if [[ "$(stat -c '%a %U %G' "$work/etc/token")" == "600 root root" ]]; then pass "the file is root's, 0600"; else fail "the file is $(stat -c '%a %U %G' "$work/etc/token")"; fi
if cmp -s "$repo/deploy/cloudflared.service" "$work/units/cloudflared.service"; then pass "installs the release's unit"; else fail "installs the release's unit"; fi
if [[ ! -e "$work/units/cloudflared-update.timer" && ! -e "$work/units/cloudflared-update.service" ]] \
    && grep -q "systemctl disable --now cloudflared-update.timer" "$STUB_LOG"; then pass "retires service install's updater"; else fail "retires service install's updater"; fi
if grep -q "systemctl daemon-reload" "$STUB_LOG" && grep -q "systemctl enable cloudflared" "$STUB_LOG" \
    && grep -q "systemctl restart cloudflared" "$STUB_LOG"; then pass "reloads, enables and restarts"; else fail "reloads, enables and restarts"; fi
if ! grep -qF "$token_a" "$work/out" "$STUB_LOG"; then pass "the token is in no output and in no stand-in's arguments"; else fail "the token leaked"; fi

run "$token_a"
if [[ $status -ne 0 ]] && grep -q "already in place" "$work/out" && unchanged "$token_a"; then pass "refuses the token already in place, restarts nothing"; else fail "refuses the token already in place ($status)"; fi

run "$token_b$token_b"
if [[ $status -ne 0 ]] && grep -q "not a whole tunnel token" "$work/out" && unchanged "$token_a"; then pass "refuses a token pasted twice, keeps the working one"; else fail "refuses a token pasted twice ($status)"; fi

run "${token_b:0:60}"
if [[ $status -ne 0 ]] && grep -q "not a whole tunnel token" "$work/out" && unchanged "$token_a"; then pass "refuses a token cut short, keeps the working one"; else fail "refuses a token cut short ($status)"; fi

run "sudo cloudflared service install $token_b"
if [[ $status -eq 0 && "$(tr -d '[:space:]' < "$work/etc/token")" == "$token_b" ]]; then pass "takes the token out of a whole copied line"; else fail "takes the token out of a whole copied line ($status)"; fi
if ! grep -q "installed" "$work/out"; then pass "leaves an unchanged unit alone"; else fail "reinstalled an unchanged unit"; fi

STUB_CONNECT=0
run "$(fake_token)"
if [[ $status -ne 0 ]] && grep -q "did not connect" "$work/out"; then pass "says so when the tunnel does not connect, old registrations notwithstanding"; else fail "says so when the tunnel does not connect ($status)"; fi

echo
if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
