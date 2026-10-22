#!/usr/bin/env bash
# Installs the status snapshot for the server's front panel (#13, BS3D-LCD#1) from this release: the bs3d-status group,
# the account that reads the snapshot added to it, and bs3d-api-status.service, enabled and (re)started. Run once with
# sudo; update.sh keeps the unit current after that and restarts the feed with every release.
#
#   sudo /opt/bs3d-api/current/deploy/install-status.sh bs3d-lcd    the panel's account reads it
#   sudo /opt/bs3d-api/current/deploy/install-status.sh             the feed alone, read by no one yet
#   sudo /opt/bs3d-api/current/deploy/install-status.sh --remove    stops it and takes the unit away; the group stays
#
# The snapshot holds counts and the nicknames the public boards show, never a hash, a user agent or a hidden player
# (#13). Who may read it is the owner's call, so nothing here runs at update. A process of the reader that is running
# keeps the groups it started with: restart it (BS3D-LCD's deploy/install.sh does).
#
# UNIT_DIR exists for tests/deploy/install-status.test.sh.
set -euo pipefail

UNIT_DIR=${UNIT_DIR:-/etc/systemd/system}
UNIT=bs3d-api-status.service
GROUP=bs3d-status
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

[[ $EUID -eq 0 ]] || { echo "run it with sudo: sudo $0" >&2; exit 1; }

usage() { echo "usage: sudo $0 [<account that reads it> | --remove]" >&2; exit 2; }
case "${1:-}" in
    --remove)
        (( $# == 1 )) || usage
        systemctl disable --now "$UNIT" 2> /dev/null || true
        rm -f "$UNIT_DIR/$UNIT"
        systemctl daemon-reload
        echo "stopped $UNIT and removed it; the group $GROUP stays"
        exit 0 ;;
    -*) usage ;;
esac
(( $# <= 1 )) || usage
reader=${1:-}

# A name is checked before anything is changed
if [[ -n "$reader" ]] && { ! [[ "$reader" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || ! id -u "$reader" > /dev/null 2>&1; }; then
    echo "refusing the account '$reader': no such user. Nothing changed." >&2
    exit 1
fi
[[ -f "$here/$UNIT" ]] || { echo "no $UNIT beside $0. Nothing changed." >&2; exit 1; }

if ! getent group "$GROUP" > /dev/null; then
    groupadd --system "$GROUP"
    echo "added the group $GROUP"
fi
if [[ -n "$reader" ]]; then
    usermod -a -G "$GROUP" "$reader"
    echo "$reader reads the snapshot, from its next start"
fi
install -m 0644 -o root -g root "$here/$UNIT" "$UNIT_DIR/$UNIT"
systemctl daemon-reload
systemctl enable "$UNIT"
systemctl restart "$UNIT"
echo "$UNIT runs: the snapshot is /run/bs3d-status/status.json, rewritten every 15 s"
