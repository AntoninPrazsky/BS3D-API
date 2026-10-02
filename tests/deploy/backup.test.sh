#!/usr/bin/env bash
# Tests deploy/backup.sh (issues #3, #6) against a scratch folder: a stand-in for BS3D.Api that writes the copy, and a
# tmpfs as the backup card, mounted in a mount namespace of the test's own, so mountpoint, rsync and find are the real
# ones and nothing is mounted on the machine running it. Root is needed for the mount only: CI runs it with sudo, and
# on the Pi it runs as unshare -r tests/deploy/backup.test.sh
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -r): it mounts a tmpfs in a namespace of its own" >&2; exit 1; }
if [[ ${BACKUP_TEST_NAMESPACE:-} != 1 ]]; then
    BACKUP_TEST_NAMESPACE=1 exec unshare -m --propagation private "$0" "$@"
fi

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'umount "$work/card" 2> /dev/null; rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/card"

cat > "$work/bin/BS3D.Api" << 'EOF'
#!/bin/sh
case "$2" in
    backup) echo "a copy of the database" > "$3" ;;
    count) echo "players 2 submissions 11" ;;
esac
EOF
chmod +x "$work/bin/BS3D.Api"

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# Runs backup.sh with the variables given as arguments; its output goes to $work/out, its exit code to $status
run() {
    status=0
    env BACKUP_DIR="$work/backups" BS3D_API="$work/bin/BS3D.Api" "$@" bash "$repo/deploy/backup.sh" > "$work/out" 2>&1 || status=$?
}
field() { sed -n "s/^$1=//p" "$work/backups/last-offbox"; }
copies() { find "$1" -maxdepth 1 -name 'scores-*.db' -type f 2> /dev/null | wc -l; }
card=(BACKUP_OFFBOX_MOUNT="$work/card" BACKUP_OFFBOX_TARGET="$work/card/bs3d-api/")

# 1. No target: the copy on the box, and a record that says there is none off it
run
if [[ $status -eq 0 && $(copies "$work/backups") -eq 1 && $(field result) == none ]]; then pass "without a target: a copy on the box, and last-offbox says none"
else fail "without a target: exit $status, $(copies "$work/backups") copies, result '$(field result)'"; fi

# 2. The card mounted: the copy on it, recorded as the last good one
mount -t tmpfs bs3d-test-card "$work/card"
mkdir "$work/card/bs3d-api"
sleep 1
run "${card[@]}"
good=$(field copy)
if [[ $status -eq 0 && -f "$work/card/bs3d-api/$good" && $(field result) == ok && -n $(field ok) ]]; then pass "the card mounted: the copy on it, and last-offbox names it"
else fail "the card mounted: exit $status, result '$(field result)', copy '$good': $(cat "$work/out")"; fi

# 3. The card pulled: the run fails, the empty mount point stays empty, and the record keeps the last good copy
umount "$work/card"
sleep 1
run "${card[@]}"
if [[ $status -ne 0 ]]; then pass "the card pulled: the run fails"; else fail "the card pulled: the run succeeded"; fi
if [[ -z $(ls -A "$work/card") ]]; then pass "the card pulled: nothing is written into the empty mount point"
else fail "the card pulled: the mount point holds $(ls -A "$work/card")"; fi
if [[ $(field result) == failed && $(field detail) == *"no drive is mounted"* && $(field copy) == "$good" ]]; then pass "the card pulled: last-offbox says why, and keeps the last good copy"
else fail "the card pulled: result '$(field result)', detail '$(field detail)', copy '$(field copy)' (was '$good')"; fi
if [[ $(copies "$work/backups") -eq 3 ]]; then pass "the card pulled: the copy on the box is still made"
else fail "the card pulled: $(copies "$work/backups") copies on the box, not 3"; fi

# 4. A folder target without its drive named, or outside it: refused before anything is copied
run BACKUP_OFFBOX_TARGET="$work/card/bs3d-api/"
if [[ $status -ne 0 && $(field detail) == *"BACKUP_OFFBOX_MOUNT"* ]]; then pass "a folder target without BACKUP_OFFBOX_MOUNT is refused"
else fail "a folder target without BACKUP_OFFBOX_MOUNT: exit $status, detail '$(field detail)'"; fi
mount -t tmpfs bs3d-test-card "$work/card"
mkdir -p "$work/card/bs3d-api" "$work/elsewhere"
run BACKUP_OFFBOX_MOUNT="$work/card" BACKUP_OFFBOX_TARGET="$work/card/../elsewhere/"
if [[ $status -ne 0 && $(copies "$work/elsewhere") -eq 0 ]]; then pass "a folder outside the drive is refused, ../ and all"
else fail "a folder outside the drive: exit $status, $(copies "$work/elsewhere") copies there"; fi

# 5. A card that takes no copy fails the run. Read-only, as a card with errors is remounted: root writes through any
# mode, and a tmpfs cannot be remounted read-only in a user namespace, so the folder is bound onto itself read-only
mount --bind "$work/card/bs3d-api" "$work/card/bs3d-api"
mount -o remount,bind,ro "$work/card/bs3d-api"
run "${card[@]}"
umount "$work/card/bs3d-api"
if [[ $status -ne 0 && $(field result) == failed && $(field detail) == *rsync* ]]; then pass "a card that refuses the copy fails the run"
else fail "a card that refuses the copy: exit $status, result '$(field result)', detail '$(field detail)'"; fi

# 6. Pruning: on the box after 30 days, on the card after BACKUP_OFFBOX_KEEP_DAYS, the newer ones kept
touch -d '40 days ago' "$work/backups/scores-20200101-000000.db"
touch -d '20 days ago' "$work/backups/scores-20200201-000000.db"
touch -d '400 days ago' "$work/card/bs3d-api/scores-20190101-000000.db"
touch -d '300 days ago' "$work/card/bs3d-api/scores-20190201-000000.db"
touch -d '400 days ago' "$work/card/bs3d-api/notes.txt"
run "${card[@]}"
if [[ $status -eq 0 && ! -e "$work/backups/scores-20200101-000000.db" && -e "$work/backups/scores-20200201-000000.db" ]]; then pass "the box keeps 30 days"
else fail "the box's pruning: exit $status, $(ls "$work/backups")"; fi
if [[ ! -e "$work/card/bs3d-api/scores-20190101-000000.db" && -e "$work/card/bs3d-api/scores-20190201-000000.db" && -e "$work/card/bs3d-api/notes.txt" ]]; then
    pass "the card keeps 365 days, and only copies are deleted"
else fail "the card's pruning: $(ls "$work/card/bs3d-api")"; fi
run "${card[@]}" BACKUP_OFFBOX_KEEP_DAYS=100
if [[ ! -e "$work/card/bs3d-api/scores-20190201-000000.db" ]]; then pass "BACKUP_OFFBOX_KEEP_DAYS sets the card's days"
else fail "BACKUP_OFFBOX_KEEP_DAYS=100 kept a copy 300 days old"; fi

if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
