#!/usr/bin/env bash
# Nightly backup of the score database (issues #3, #6): a consistent copy taken while the service runs, thirty days
# kept on the box, the newest copied off it to a drive the Pi mounts, and every other day an encrypted copy off the
# site, because the disk dying is the failure a backup on the same disk does not survive, and the house burning the one
# a card in the Pi does not. Restore is copying a backup to /var/lib/bs3d-api/scores.db with the service stopped.
#
# backup.env is root's, read by systemd before it drops to bs3d-api; deploy/backup-card.sh writes it for a card, and
# deploy/backup-offsite.sh for the copy off the site:
#   BACKUP_OFFBOX_TARGET       where the copy goes: a folder on a drive the Pi mounts (/mnt/bs3d-backup/bs3d-api/), or
#                              an rsync destination over SSH (pi-backup@host:/backups/bs3d-api/)
#   BACKUP_OFFBOX_MOUNT        the drive's mount point, required with a folder: a drive that is not mounted fails the run,
#                              where rsync would otherwise write into the empty mount point on the Pi's own disk and succeed
#   BACKUP_OFFBOX_KEEP_DAYS    copies in such a folder older than this are deleted (365 if unset)
#   BACKUP_OFFSITE_REPOSITORY  a restic repository, over SFTP (sftp://account@host:port//folder): the copy is encrypted
#                              on the Pi, so the host holds nothing it can read
#   BACKUP_OFFSITE_EVERY_DAYS  days between copies off the site (2 if unset, the owner's choice on 2026-10-07): a copy is
#                              due when the last good one is that old, less half a day for the timer's jitter, so a
#                              failed one is tried again the next night
# The copy off the site needs three credentials, root's files in /etc/credstore that bs3d-api-backup.service imports
# while it runs ($CREDENTIALS_DIRECTORY), so the public service, which runs as bs3d-api too, never sees them:
# bs3d-api-offsite.password (the repository's), and for SFTP bs3d-api-offsite.key and bs3d-api-offsite.known_hosts.
# The repository keeps a copy a day for 30 days and one a month for 12 months.
#
# Every run records each copy, in backups/last-offbox (the drive) and backups/last-offsite: the attempt, its result
# (ok, failed or none) and why, the last good copy and its time, which a failed run keeps, and the days between copies,
# and adds the run as a line to the record's log (last-offbox.log, last-offsite.log), the last 1000 runs. The admin
# page shows the records and charts the copies' ages from the logs, because the journal is gone after a reboot and a unit that never ran still reads as a
# success. Each copy is tried whether the other worked or not, after the copy on the box is made, and either failing
# fails the unit. BACKUP_DIR and BS3D_API exist for tests/deploy/backup.test.sh.
set -euo pipefail

BACKUPS=${BACKUP_DIR:-/var/lib/bs3d-api/backups}
API=${BS3D_API:-/opt/bs3d-api/current/BS3D.Api}
KEEP_DAYS=${BACKUP_KEEP_DAYS:-30}
OFFBOX_KEEP_DAYS=${BACKUP_OFFBOX_KEEP_DAYS:-365}
OFFSITE_EVERY_DAYS=${BACKUP_OFFSITE_EVERY_DAYS:-2}
name="scores-$(date -u +%Y%m%d-%H%M%S).db"
target="$BACKUPS/$name"

mkdir -p "$BACKUPS"
"$API" admin backup "$target"
"$API" admin count

find "$BACKUPS" -name 'scores-*.db' -type f -mtime +"$KEEP_DAYS" -print -delete

# previous <record> <key>: the value the record holds
previous() {
    [[ -f $1 ]] || return 0
    sed -n "s/^$2=//p" "$1" | head -1
}

# record <record> <days between copies> <result> <detail> [<good copy's time> <good copy>]: written whole and renamed,
# so a reader never sees half. Its log, <record>.log, gets the run as one line, without the detail, and keeps the last
# 1000: what the admin page charts the copy's age from (#9), since the record holds only the last run
record() {
    local ok copy attempt
    ok=${5:-$(previous "$1" ok)}
    copy=${6:-$(previous "$1" copy)}
    attempt=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    printf 'attempt=%s\nresult=%s\ndetail=%s\nok=%s\ncopy=%s\nevery=%s\n' "$attempt" "$3" "$4" "$ok" "$copy" "$2" > "$1.tmp"
    mv -f "$1.tmp" "$1"
    { tail -n 999 "$1.log" 2> /dev/null || true
      printf 'attempt=%s result=%s ok=%s every=%s\n' "$attempt" "$3" "$ok" "$2"; } > "$1.log.tmp"
    mv -f "$1.log.tmp" "$1.log"
}

# The copy to a drive the Pi mounts, or over rsync: every night
offbox() {
    local status="$BACKUPS/last-offbox"
    local offbox=${BACKUP_OFFBOX_TARGET:-} mount=${BACKUP_OFFBOX_MOUNT:-} local_folder=false real_mount real_offbox
    fail() {
        record "$status" 1 failed "$1"
        echo "off-box copy FAILED: $1" >&2
        exit 1
    }

    if [[ -z $offbox ]]; then
        record "$status" 1 none "no BACKUP_OFFBOX_TARGET in backup.env: the backups live on this disk only"
        echo "no BACKUP_OFFBOX_TARGET in /etc/bs3d-api/backup.env: this backup lives on the box only"
        return 0
    fi

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
    record "$status" 1 ok "copied to $offbox" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$name"
    echo "copied off the box to $offbox"
}

# The encrypted copy off the site, by restic: every OFFSITE_EVERY_DAYS days
offsite() {
    local status="$BACKUPS/last-offsite"
    local repo=${BACKUP_OFFSITE_REPOSITORY:-} creds=${CREDENTIALS_DIRECTORY:-} every=$OFFSITE_EVERY_DAYS
    local last since snapshot code
    fail() {
        record "$status" "$every" failed "$1"
        echo "off-site copy FAILED: $1" >&2
        exit 1
    }

    [[ $every =~ ^[1-9][0-9]{0,2}$ ]] || fail "BACKUP_OFFSITE_EVERY_DAYS is '$every', not a number of days"
    if [[ -z $repo ]]; then
        record "$status" "$every" none "no BACKUP_OFFSITE_REPOSITORY in backup.env: every copy is in this house"
        echo "no BACKUP_OFFSITE_REPOSITORY in /etc/bs3d-api/backup.env: no copy leaves the house"
        return 0
    fi

    last=$(previous "$status" ok)
    if [[ -n $last ]] && since=$(( $(date +%s) - $(date -d "$last" +%s 2> /dev/null || echo 0) )) \
        && (( since < every * 86400 - 43200 )); then
        echo "the copy off the site is not due: the last good one is from $last, and one goes every $every days"
        return 0
    fi

    [[ -n $creds ]] || fail "the unit imported no credentials: is ImportCredential=bs3d-api-offsite.* in bs3d-api-backup.service?"
    local args=(--repo "$repo" --password-file "$creds/bs3d-api-offsite.password" --cache-dir "$BACKUPS/.restic-cache")
    local needed=(password)
    if [[ $repo == sftp:* ]]; then
        needed+=(key known_hosts)
        # Its own key and the host's keys recorded at setup, never a prompt: a host that changed its key fails the run
        args+=(-o "sftp.args=-i $creds/bs3d-api-offsite.key -o IdentitiesOnly=yes -o UserKnownHostsFile=$creds/bs3d-api-offsite.known_hosts -o StrictHostKeyChecking=yes -o BatchMode=yes -o ConnectTimeout=30 -o ServerAliveInterval=15 -o ServerAliveCountMax=4")
    fi
    for credential in "${needed[@]}"; do
        [[ -r $creds/bs3d-api-offsite.$credential ]] \
            || fail "no bs3d-api-offsite.$credential among the unit's credentials: deploy/backup-offsite.sh puts them in /etc/credstore"
    done

    # Not local: the trap that deletes them runs after the function has returned
    out=$(mktemp)
    errors=$(mktemp)
    trap 'rm -f "$out" "$errors"' EXIT
    # restic's last line on stderr, which says why
    why() { grep -v '^[[:space:]]*$' "$errors" | tail -1 | tr -d '\r' | cut -c 1-300; }

    # One host and one tag for every copy, so the policy below sees one series although each copy's file has its own name
    code=0
    timeout 30m restic "${args[@]}" backup --host bs3d-api --tag nightly "$target" > "$out" 2> "$errors" || code=$?
    cat "$out"
    cat "$errors" >&2
    (( code == 0 )) || fail "restic backup failed with exit code $code: $(why)"
    snapshot=$(sed -n 's/^snapshot \([0-9a-f]*\) saved$/\1/p' "$out")

    code=0
    timeout 30m restic "${args[@]}" forget --host bs3d-api --group-by host --keep-daily 30 --keep-monthly 12 --prune 2> "$errors" || code=$?
    cat "$errors" >&2
    (( code == 0 )) || fail "the copy is off the site (snapshot ${snapshot:-?}), but restic forget failed with exit code $code: $(why)"

    record "$status" "$every" ok "snapshot ${snapshot:-?} in the repository off the site" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$name"
    echo "copied off the site: snapshot ${snapshot:-?}"
}

# Each in a subshell of its own under errexit, so one failing neither stops the other nor goes unnoticed. Not in an
# || list, where errexit would be off inside the subshell too
set +e
(set -e; offbox)
offbox_code=$?
(set -e; offsite)
offsite_code=$?
set -e
(( offbox_code == 0 && offsite_code == 0 ))
