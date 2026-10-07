#!/usr/bin/env bash
# Tests deploy/backup.sh (issues #3, #6) against a scratch folder: a stand-in for BS3D.Api that writes the copy, and a
# tmpfs as the backup card, mounted in a mount namespace of the test's own, so mountpoint, rsync and find are the real
# ones and nothing is mounted on the machine running it. The copy off the site is the real restic over SFTP, to a
# stand-in for ssh that records how it was called and runs the real sftp-server on a scratch folder, so no network is
# used. Root is needed for the mount only: CI runs it with sudo, and on the Pi it runs as
# unshare -r tests/deploy/backup.test.sh. It needs restic and sftp-server (Debian's restic and openssh-sftp-server).
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -r): it mounts a tmpfs in a namespace of its own" >&2; exit 1; }
command -v restic > /dev/null || { echo "restic is not installed: apt-get install restic" >&2; exit 1; }
sftp_server=
for candidate in /usr/lib/openssh/sftp-server /usr/lib/sftp-server /usr/libexec/openssh/sftp-server /usr/libexec/sftp-server; do
    if [[ -x $candidate ]]; then sftp_server=$candidate; break; fi
done
[[ -n $sftp_server ]] || { echo "no sftp-server: apt-get install openssh-sftp-server" >&2; exit 1; }
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
    env PATH="$work/bin:$PATH" BACKUP_DIR="$work/backups" BS3D_API="$work/bin/BS3D.Api" "$@" bash "$repo/deploy/backup.sh" > "$work/out" 2>&1 || status=$?
}
field() { sed -n "s/^$1=//p" "$work/backups/last-offbox"; }
site() { sed -n "s/^$1=//p" "$work/backups/last-offsite"; }
copies() { find "$1" -maxdepth 1 -name 'scores-*.db' -type f 2> /dev/null | wc -l; }
card=(BACKUP_OFFBOX_MOUNT="$work/card" BACKUP_OFFBOX_TARGET="$work/card/bs3d-api/")

# 1. No target: the copy on the box, and a record that says there is none off it
run
if [[ $status -eq 0 && $(copies "$work/backups") -eq 1 && $(field result) == none ]]; then pass "without a target: a copy on the box, and last-offbox says none"
else fail "without a target: exit $status, $(copies "$work/backups") copies, result '$(field result)'"; fi
if [[ $(site result) == none && $(site every) == 2 ]]; then pass "without a repository: last-offsite says none"
else fail "without a repository: last-offsite result '$(site result)', every '$(site every)'"; fi

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

# 3a. The log (#9): each run a line, the failed one keeping the last good copy's time, and the record's attempt its last
log="$work/backups/last-offbox.log"
lines=()
if [[ -f $log ]]; then mapfile -t lines < "$log"; fi
good_at=$(sed -n 's/.* ok=\([^ ]*\) .*/\1/p' <<< "${lines[1]:-}")
if [[ ${#lines[@]} -eq 3 && ${lines[0]} == *" result=none ok= every=1" && ${lines[1]} == *" result=ok ok="?*" every=1" \
    && ${lines[2]} == *" result=failed ok=$good_at every=1" && ${lines[2]} == "attempt=$(field attempt) "* \
    && $(wc -l < "$work/backups/last-offsite.log") -eq 3 ]]; then
    pass "each run adds its line to the record's log, a failure keeping the last good copy's time"
else fail "the log: $(cat "$log" 2> /dev/null || echo none)"; fi
for i in $(seq 1200); do echo "attempt=2020-01-01T00:00:00Z result=ok ok=2020-01-01T00:00:00Z every=1 #$i"; done > "$log"
run
if [[ $(wc -l < "$log") -eq 1000 && $(sed -n 1p "$log") == *" #202" && $(tail -1 "$log") == *" result=none ok=$good_at every=1" ]]; then
    pass "the log keeps the last 1000 runs, the newest last"
else fail "the log kept $(wc -l < "$log") lines, from '$(sed -n 1p "$log")' to '$(tail -1 "$log")'"; fi

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

# The copy off the site: a restic repository over SFTP. The stand-in for ssh records its arguments and runs the real
# sftp-server, which serves this machine's files, so the repository's absolute path is a scratch folder here
cat > "$work/bin/ssh" << EOF
#!/bin/sh
printf '%s\n' "\$@" > "$work/ssh-args"
exec $sftp_server
EOF
chmod +x "$work/bin/ssh"
mkdir -p "$work/creds" "$work/remote"
printf 'a password for the test' > "$work/creds/bs3d-api-offsite.password"
echo "a key" > "$work/creds/bs3d-api-offsite.key"
echo "[backup.example]:222 ssh-ed25519 AAAA" > "$work/creds/bs3d-api-offsite.known_hosts"
url="sftp://bs3dbackup@backup.example:222/$work/remote/bs3d-api"
site_vars=(CREDENTIALS_DIRECTORY="$work/creds" BACKUP_OFFSITE_REPOSITORY="$url")
rs() { env PATH="$work/bin:$PATH" restic --no-cache --repo "$url" --password-file "$work/creds/bs3d-api-offsite.password" "$@"; }
# Whether a snapshot holds the copy of that name. restic forget keeps the day's last copy only, so a count would drop
in_repo() { rs snapshots --json 2> /dev/null | grep -q "/$1\""; }
ago() { sed -i "s/^ok=.*/ok=$(date -u -d "$1" +%Y-%m-%dT%H:%M:%SZ)/" "$work/backups/last-offsite"; }
newest() { find "$work/backups" -maxdepth 1 -name 'scores-*.db' -printf '%f\n' | sort | tail -1; }
rs init > /dev/null 2>&1

# 7. Set up: the copy goes over SFTP with the unit's key and the host's recorded keys, never a prompt, and comes back
# from the repository byte for byte
sleep 1
run "${card[@]}" "${site_vars[@]}"
args=$(cat "$work/ssh-args" 2> /dev/null)
if [[ $status -eq 0 && $(site result) == ok && $(site copy) == "$(newest)" && $(site every) == 2 ]] && in_repo "$(newest)"; then
    pass "the copy off the site: a snapshot of the newest copy, and last-offsite names it"
else fail "the copy off the site: exit $status, result '$(site result)', detail '$(site detail)': $(cat "$work/out")"; fi
if [[ $args == *$'\n-p\n222\n'* && $args == *$'\n-l\nbs3dbackup\n'* && $args == *$'\n-i\n'"$work/creds/bs3d-api-offsite.key"$'\n'* \
    && $args == *"UserKnownHostsFile=$work/creds/bs3d-api-offsite.known_hosts"* && $args == *StrictHostKeyChecking=yes* \
    && $args == *BatchMode=yes* && $args == *IdentitiesOnly=yes* ]]; then
    pass "ssh gets the unit's key, the recorded host keys and no prompt"
else fail "ssh's arguments: $(echo "$args" | tr '\n' ' ')"; fi
rm -rf "$work/restored"
rs restore latest --target "$work/restored" > /dev/null 2>&1 || true
if cmp -s "$work/backups/$(site copy)" "$work/restored$work/backups/$(site copy)"; then pass "the copy off the site restores byte for byte"
else fail "the copy off the site did not restore as it was taken"; fi

# 8. Every other day: the next night is not due, a day and a half later is
attempt=$(site attempt)
sleep 1
run "${card[@]}" "${site_vars[@]}"
if [[ $status -eq 0 && $(site attempt) == "$attempt" ]] && ! in_repo "$(newest)"; then pass "the night after a copy off the site: none is due"
else fail "the night after: exit $status, attempt '$(site attempt)' (was '$attempt'), newest in the repository: $(in_repo "$(newest)" && echo yes || echo no)"; fi
ago '30 hours ago'
sleep 1
run "${card[@]}" "${site_vars[@]}"
if ! in_repo "$(newest)"; then pass "30 hours after the last copy off the site: none is due"
else fail "30 hours after: a copy was made"; fi
ago '37 hours ago'
sleep 1
run "${card[@]}" "${site_vars[@]}"
if [[ $status -eq 0 && $(site result) == ok ]] && in_repo "$(newest)"; then pass "37 hours after the last copy off the site: one is made"
else fail "37 hours after: exit $status, result '$(site result)', newest in the repository: $(in_repo "$(newest)" && echo yes || echo no)"; fi
ago '13 hours ago'
sleep 1
run "${card[@]}" "${site_vars[@]}" BACKUP_OFFSITE_EVERY_DAYS=1
if [[ $(site every) == 1 ]] && in_repo "$(newest)"; then pass "BACKUP_OFFSITE_EVERY_DAYS=1: a copy every night"
else fail "BACKUP_OFFSITE_EVERY_DAYS=1: every '$(site every)', newest in the repository: $(in_repo "$(newest)" && echo yes || echo no)"; fi
run "${card[@]}" "${site_vars[@]}" BACKUP_OFFSITE_EVERY_DAYS=often
if [[ $status -ne 0 && $(site result) == failed && $(site detail) == *BACKUP_OFFSITE_EVERY_DAYS* ]]; then pass "days between copies that are not a number fail the run"
else fail "BACKUP_OFFSITE_EVERY_DAYS=often: exit $status, result '$(site result)'"; fi

# 9. A copy off the site that fails: the run fails, the record says why and keeps the last good copy, and the card's
# copy is made all the same
good=$(site copy)
ago '3 days ago'
mkdir -p "$work/wrong" && cp "$work/creds/bs3d-api-offsite."{key,known_hosts} "$work/wrong/"
printf 'not the password' > "$work/wrong/bs3d-api-offsite.password"
sleep 1
run "${card[@]}" BACKUP_OFFSITE_REPOSITORY="$url" CREDENTIALS_DIRECTORY="$work/wrong"
if [[ $status -ne 0 && $(site result) == failed && $(site detail) == *"restic backup failed"* && $(site copy) == "$good" ]]; then
    pass "a copy off the site that fails: the run fails, and last-offsite says why and keeps the last good copy"
else fail "a failing copy off the site: exit $status, result '$(site result)', detail '$(site detail)', copy '$(site copy)'"; fi
if [[ $(field result) == ok && $(field copy) == "$(newest)" ]]; then pass "a copy off the site that fails: the card's copy is made all the same"
else fail "a failing copy off the site: the card's result '$(field result)', copy '$(field copy)'"; fi

# 10. The card pulled: the copy off the site is made all the same, and the run still fails
umount "$work/card"
sleep 1
run "${card[@]}" "${site_vars[@]}"
if [[ $status -ne 0 && $(field result) == failed && $(site result) == ok && $(site copy) == "$(newest)" ]]; then
    pass "the card pulled: the copy off the site is made all the same, and the run fails"
else fail "the card pulled with the site due: exit $status, card '$(field result)', site '$(site result)', copy '$(site copy)'"; fi
mount -t tmpfs bs3d-test-card "$work/card"
mkdir -p "$work/card/bs3d-api"

# 11. Credentials the unit did not import: refused before restic runs
ago '3 days ago'
run "${card[@]}" BACKUP_OFFSITE_REPOSITORY="$url"
if [[ $status -ne 0 && $(site detail) == *"imported no credentials"* ]]; then pass "no credentials imported: the copy off the site fails and says so"
else fail "no credentials imported: exit $status, detail '$(site detail)'"; fi
mkdir -p "$work/nokey" && cp "$work/creds/bs3d-api-offsite."{password,known_hosts} "$work/nokey/"
rm -f "$work/ssh-args"
run "${card[@]}" BACKUP_OFFSITE_REPOSITORY="$url" CREDENTIALS_DIRECTORY="$work/nokey"
if [[ $status -ne 0 && $(site detail) == *"no bs3d-api-offsite.key"* && ! -e "$work/ssh-args" ]]; then pass "no key among the credentials: refused before ssh runs"
else fail "no key among the credentials: exit $status, detail '$(site detail)'"; fi

# 12. The repository's policy: a copy a day, over one series although every copy's file has its own name. The oldest
# copy is planted first, because restic also keeps the oldest one while fewer days than the policy's are filled
for file in scores-20241201-033000.db scores-20250101-033000.db scores-20250101-153000.db; do
    echo "$file" > "$work/$file"
    rs backup --host bs3d-api --tag nightly --time "${file:7:4}-${file:11:2}-${file:13:2} ${file:16:2}:${file:18:2}:00" "$work/$file" > /dev/null 2>&1
done
ago '3 days ago'
sleep 1
run "${card[@]}" "${site_vars[@]}"
on_the_day=$(rs snapshots --json 2> /dev/null | grep -o '"time":"2025-01-01' | wc -l)
if [[ $status -eq 0 && $on_the_day -eq 1 ]] && in_repo "$(newest)" && in_repo scores-20250101-153000.db; then
    pass "restic forget keeps a day's last copy, across files of different names"
else fail "restic forget: $on_the_day snapshots left on 2025-01-01: $(cat "$work/out")"; fi

if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
