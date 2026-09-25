#!/usr/bin/env bash
# Nightly backup of the score database (issue #3): a consistent copy taken while the service runs, thirty days kept
# on the box, and — when /etc/bs3d-api/backup.env sets BACKUP_OFFBOX_TARGET (an rsync destination such as
# pi-backup@desktop:/backups/bs3d-api/) — the newest copied off it, because the SD card dying is the failure a
# backup on the same card does not survive. Restore is copying a backup to /var/lib/bs3d-api/scores.db with the
# service stopped.
set -euo pipefail

BACKUPS=/var/lib/bs3d-api/backups
KEEP_DAYS=${BACKUP_KEEP_DAYS:-30}
target="$BACKUPS/scores-$(date -u +%Y%m%d-%H%M%S).db"

mkdir -p "$BACKUPS"
/opt/bs3d-api/current/BS3D.Api admin backup "$target"
/opt/bs3d-api/current/BS3D.Api admin count

find "$BACKUPS" -name 'scores-*.db' -type f -mtime +"$KEEP_DAYS" -print -delete

if [[ -n "${BACKUP_OFFBOX_TARGET:-}" ]]; then
    rsync --timeout=60 "$target" "$BACKUP_OFFBOX_TARGET"
    echo "copied off the box to $BACKUP_OFFBOX_TARGET"
else
    echo "no BACKUP_OFFBOX_TARGET in /etc/bs3d-api/backup.env: this backup lives on the box only"
fi
