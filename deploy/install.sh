#!/usr/bin/env bash
# First install on a fresh Raspberry Pi OS 64-bit (issue #3). Idempotent: running it again changes nothing that is
# already in place and never replaces the salt. Run as root:
#
#   curl -fsSL https://raw.githubusercontent.com/AntoninPrazsky/BS3D-API/main/deploy/install.sh | sudo bash
#
# It installs the service and its nightly backup. It does NOT install cloudflared: that needs the tunnel token from
# the owner's Cloudflare account, and the README says how (issue #2).
set -euo pipefail

REPO=${API_REPO:-AntoninPrazsky/BS3D-API}
[[ $EUID -eq 0 ]] || { echo "run as root (sudo)" >&2; exit 1; }
[[ $(uname -m) == aarch64 ]] || echo "warning: this is $(uname -m), and the release is built for linux-arm64" >&2

apt-get update -qq
apt-get install -y -qq curl python3 rsync > /dev/null

id bs3d-api > /dev/null 2>&1 || useradd --system --home-dir /var/lib/bs3d-api --shell /usr/sbin/nologin bs3d-api
install -d -o bs3d-api -g bs3d-api -m 0750 /var/lib/bs3d-api /var/lib/bs3d-api/ceilings /var/lib/bs3d-api/backups
install -d -m 0755 /opt/bs3d-api
install -d -m 0700 /etc/bs3d-api

# The salt for hashed addresses: made once, on the box, never shown and never committed
if [[ ! -f /etc/bs3d-api/env ]]; then
    umask 077
    echo "Scores__AddressSalt=$(head -c 32 /dev/urandom | base64 | tr -d '=+/\n')" > /etc/bs3d-api/env
    echo "made /etc/bs3d-api/env with a fresh salt"
fi

# The newest release, which carries these scripts and the unit files beside the service
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "https://raw.githubusercontent.com/$REPO/main/deploy/update.sh" -o "$tmp/update.sh"
for unit in bs3d-api.service bs3d-api-backup.service bs3d-api-backup.timer; do
    curl -fsSL "https://raw.githubusercontent.com/$REPO/main/deploy/$unit" -o "/etc/systemd/system/$unit"
done
systemctl daemon-reload
systemctl enable bs3d-api.service bs3d-api-backup.timer > /dev/null

bash "$tmp/update.sh" latest
/opt/bs3d-api/current/deploy/update-ceilings.sh
systemctl start bs3d-api-backup.timer

echo
echo "Installed. Next, once: cloudflared with the tunnel token (README, 'The tunnel')."
