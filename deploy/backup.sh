#!/usr/bin/env bash
# Nightly backup of the score database (issues #3, #6): a consistent copy taken while the service runs, thirty days
# kept on the box, and the newest copied off it when /etc/bs3d-api/backup.env says where, because the disk dying is
# the failure a backup on the same disk does not survive. Restore is copying a backup to /var/lib/bs3d-api/scores.db
# with the service stopped.
#
# backup.env is root's, read by systemd before it drops to bs3d-api; deploy/backup-card.sh writes it for a card:
#   BACKUP_OFFBOX_TARGET     where the copy goes: a folder on a drive the Pi mounts (/mnt/bs3d-backup/bs3d-api/), or
#                            an rsync destination over SSH (pi-backup@host:/backups/bs3d-api/)
#   BACKUP_OFFBOX_MOUNT      the drive's mount point, required with a folder: a drive that is not mounted fails the run,
#                            where rsync would otherwise write into the empty mount point on the Pi's own disk and succeed
#   BACKUP_OFFBOX_KEEP_DAYS  copies in such a folder older than this are deleted (365 if unset)
#
# Every run records the off-box copy in backups/last-offbox: the attempt, its result (ok, failed or none) and why,
# and the last good copy and its time, which a failed run keeps. The admin page shows it, because the journal is gone
# after a reboot and a unit that never ran still reads as a success. A failed off-box copy fails the unit, after the
# copy on the box is made. BACKUP_DIR and BS3D_API exist for tests/deploy/backup.test.sh.
set -euo pipefail

BACKUPS=${BACKUP_DIR:-/var/lib/bs3d-api/backups}
API=${BS3D_API:-/opt/bs3d-api/current/BS3D.Api}
KEEP_DAYS=${BACKUP_KEEP_DAYS:-30}
OFFBOX_KEEP_DAYS=${BACKUP_OFFBOX_KEEP_DAYS:-365}
name="scores-$(date -u +%Y%m%d-%H%M%S).db"
target="$BACKUPS/$name"
status="$BACKUPS/last-offbox"

mkdir -p "$BACKUPS"
"$API" admin backup "$target"
"$API" admin count

find "$BACKUPS" -name 'scores-*.db' -type f -mtime +"$KEEP_DAYS" -print -delete

previous() {
    [[ -f $status ]] || return 0
    sed -n "s/^$1=//p" "$status" | head -1
}

# record <result> <detail> [<good copy's time> <good copy>]: written whole and renamed, so a reader never sees half
record() {
    local ok copy
    ok=${3:-$(previous ok)}
    copy=${4:-$(previous copy)}
    printf 'attempt=%s\nresult=%s\ndetail=%s\nok=%s\ncopy=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" "$2" "$ok" "$copy" > "$status.tmp"
    mv -f "$status.tmp" "$status"
}

fail() {
    record failed "$1"
    echo "off-box copy FAILED: $1" >&2
    exit 1
}

offbox=${BACKUP_OFFBOX_TARGET:-}
mount=${BACKUP_OFFBOX_MOUNT:-}

if [[ -z $offbox ]]; then
    record none "no BACKUP_OFFBOX_TARGET in backup.env: the backups live on this disk only"
    echo "no BACKUP_OFFBOX_TARGET in /etc/bs3d-api/backup.env: this backup lives on the box only"
    exit 0
fi

local_folder=false
if [[ $offbox == /* ]]; then
    local_folder=true
    [[ -n $mount ]] || fail "$offbox is a folder on this machine, and BACKUP_OFFBOX_MOUNT does not name the drive it must be on"
    real_mount=$(realpath -m "$mount")
    real_offbox=$(realpath -m "$offbox")
    [[ $real_offbox == "$real_mount" || $real_offbox == "$real_mount"/* ]] || fail "$offbox is not on the drive at $mount"
    mountpoint -q "$real_mount" || fail "no drive is mounted at $mount"
    [[ -d $real_offbox ]] || fail "$offbox is not a folder on the drive at $mount"
fi

rsync --timeout=60 "$target" "$offbox" || fail "rsync to $offbox failed with exit code $?"

if $local_folder; then
    # On the card before the run says so, and its old copies pruned as the box's are
    sync -f "$real_offbox" || fail "the copy did not reach the drive at $mount (sync failed)"
    find "$real_offbox" -maxdepth 1 -name 'scores-*.db' -type f -mtime +"$OFFBOX_KEEP_DAYS" -print -delete \
        || fail "the copy is on the drive, but its old copies could not be deleted"
fi
record ok "copied to $offbox" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$name"
echo "copied off the box to $offbox"
