#!/usr/bin/env bash
# Installs or rotates the Cloudflare Tunnel token on the Pi (issue #4) without the token reaching a log, a command
# line or a shell history. `sudo cloudflared service install <token>` puts the token on sudo's command line, and sudo
# logs every command line to the journal; here it is typed at a prompt that does not echo it, so sudo logs only this
# script. It also puts this release's cloudflared.service in place (a throwaway user, the token as a systemd
# credential) and retires the daily root `cloudflared update` timer that `service install` leaves behind.
#
#   sudo /opt/bs3d-api/current/deploy/tunnel-token.sh
#
# First, in the Cloudflare dashboard: Networking > Tunnels > the tunnel > Overview > Rotate token, in the right-hand
# column under the heading "Refresh token" (a new tunnel has a fresh one already), then Add a replica, and copy the
# token: the long string starting with eyJ. Never run the install command the dashboard shows. The whole
# "sudo cloudflared service install eyJ..." line is accepted too. cloudflared itself comes from Cloudflare's apt
# repository (README, "The tunnel").
#
# TOKEN_FILE, UNIT_DIR and WAIT_SECONDS exist for tests/deploy/tunnel-token.test.sh.
set -euo pipefail

TOKEN_FILE=${TOKEN_FILE:-/etc/cloudflared/token}
UNIT_DIR=${UNIT_DIR:-/etc/systemd/system}
WAIT_SECONDS=${WAIT_SECONDS:-30}
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

[[ $EUID -eq 0 ]] || { echo "run it with sudo: sudo $0" >&2; exit 1; }
command -v cloudflared > /dev/null || { echo "cloudflared is not installed (README, \"The tunnel\")" >&2; exit 1; }
[[ -f "$here/cloudflared.service" ]] || { echo "no cloudflared.service beside $0" >&2; exit 1; }

# A token pasted without a final Enter (or piped with no newline) still counts
input=""
IFS= read -rs -p "Paste the tunnel token (it is not shown) and press Enter: " input || [[ -n "$input" ]] \
    || { echo; echo "No token given. Nothing changed." >&2; exit 1; }
echo
# A paste that wrapped onto more lines leaves the rest waiting at the terminal, where the shell would run it as a
# command and keep it in its history: it is read here and thrown away, and the cut-short token is refused below
if [[ -t 0 ]]; then while IFS= read -rs -t 0.2 _; do :; done; fi
token=$(printf '%s' "$input" | tr -d '[:space:]')
unset input
if [[ "$token" == *eyJ* ]]; then token="eyJ${token#*eyJ}"; fi
[[ "$token" =~ ^eyJ[A-Za-z0-9+/=]{40,}$ ]] \
    || { echo "That is not a tunnel token (it starts with eyJ). Nothing changed." >&2; exit 1; }
# What cloudflared itself requires: standard base64 of a JSON object with the account (a), the tunnel (t) and the
# secret (s). A token pasted twice or cut short has the right letters and fails here, before the working one is
# replaced and the tunnel restarted onto something it refuses.
printf '%s' "$token" | python3 -c '
import base64, binascii, json, sys
try:
    token = json.loads(base64.b64decode(sys.stdin.read(), validate=True))
except (binascii.Error, ValueError):
    sys.exit(1)
sys.exit(0 if isinstance(token, dict) and {"a", "t", "s"} <= token.keys() else 1)' \
    || { echo "That is not a whole tunnel token (pasted twice, or cut short?). Nothing changed." >&2; exit 1; }

if [[ -f "$TOKEN_FILE" && "$(tr -d '[:space:]' < "$TOKEN_FILE")" == "$token" ]]; then
    echo "This is the token already in place, so Rotate token (under \"Refresh token\" on the tunnel's Overview) was" >&2
    echo "not pressed. Press it, copy the new token and run this again. Nothing changed." >&2
    exit 1
fi

# Written beside the old one and moved over it, so the file is never half-written and never readable by anyone
install -d -m 0755 -o root -g root "$(dirname "$TOKEN_FILE")"
umask 077
printf '%s\n' "$token" > "$TOKEN_FILE.new"
unset token
chown root:root "$TOKEN_FILE.new"
chmod 0600 "$TOKEN_FILE.new"
mv -f "$TOKEN_FILE.new" "$TOKEN_FILE"
echo "token written to $TOKEN_FILE"

if ! cmp -s "$here/cloudflared.service" "$UNIT_DIR/cloudflared.service"; then
    install -m 0644 -o root -g root "$here/cloudflared.service" "$UNIT_DIR/cloudflared.service"
    echo "installed $UNIT_DIR/cloudflared.service"
fi
for unit in cloudflared-update.timer cloudflared-update.service; do
    if [[ -f "$UNIT_DIR/$unit" ]]; then
        systemctl disable --now "$unit" > /dev/null 2>&1 || true
        rm -f "$UNIT_DIR/$unit"
        echo "removed $unit (cloudflared is updated by apt: README, Updating)"
    fi
done
systemctl daemon-reload
systemctl enable cloudflared > /dev/null 2>&1

# Type=notify: the restart returns once cloudflared says it is ready, or fails; either way the journal says why
start=$(date '+%Y-%m-%d %H:%M:%S')
systemctl restart cloudflared || true
for _ in $(seq 1 "$WAIT_SECONDS"); do
    connections=$(journalctl -u cloudflared --since "$start" -o cat | grep -c "Registered tunnel connection" || true)
    if (( connections >= 1 )); then
        echo "cloudflared is connected with the new token ($connections connection(s) so far)."
        exit 0
    fi
    sleep 1
done
echo "cloudflared did not connect within $WAIT_SECONDS s. The token is in place; look at: journalctl -u cloudflared -n 30" >&2
exit 1
