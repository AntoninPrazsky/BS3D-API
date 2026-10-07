#!/usr/bin/env bash
# Sets up the encrypted copy off the site of issue #6: a restic repository over SFTP, on the owner's web hosting. Run as
# root, once:
#   sudo /opt/bs3d-api/current/deploy/backup-offsite.sh sftp://<account>@<host>:<port>//<folder>
#   sudo /opt/bs3d-api/current/deploy/backup-offsite.sh --remove     stop (the repository on the host stays)
#
# Two passwords, each read from a root-only file when it is there, and asked for when it is not:
#   /etc/bs3d-api/offsite-sftp-password    the hosting account's, needed once to put this Pi's key on the account
#   /etc/bs3d-api/offsite-restic-password  the repository's. Keep it somewhere other than the Pi too: without it no
#                                          copy can be read, and losing the Pi is what the copy is for
# Both files are deleted once the setup has worked: from then on the key logs in, and the repository's password is
# where the backup reads it.
#
# It records the host's keys, trusted on first use (it prints their fingerprints), and makes an ed25519 key with no
# passphrase, or keeps the one made before. It puts the key's public half where the hosting's ProFTPD mod_sftp reads
# it: .sftp/authorized_keys in the account's folder, in RFC 4716 form (found on 2026-10-07: neither .ssh/authorized_keys
# nor the OpenSSH form logs in there), which replaces any key the account had. It checks that the key alone logs in,
# makes the repository, or opens the one there with the password, and writes:
#   /etc/credstore/bs3d-api-offsite.{key,known_hosts,password}   root's and 0600: bs3d-api-backup.service imports them
#                                          for its run alone, so the public service, which is bs3d-api too, never has them
#   BACKUP_OFFSITE_REPOSITORY in /etc/bs3d-api/backup.env, its other lines kept
# Then it runs one backup and shows what it recorded.
# CREDSTORE, ENV_FILE, SECRETS_DIR, UNIT_DIR and BACKUP_DIR exist for tests/deploy/backup-offsite.test.sh.
set -euo pipefail

CREDSTORE=${CREDSTORE:-/etc/credstore}
ENV_FILE=${ENV_FILE:-/etc/bs3d-api/backup.env}
SECRETS_DIR=${SECRETS_DIR:-/etc/bs3d-api}
UNIT_DIR=${UNIT_DIR:-/etc/systemd/system}
BACKUP_DIR=${BACKUP_DIR:-/var/lib/bs3d-api/backups}
NAME=bs3d-api-offsite

die() { echo "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "run it as root: sudo $0 ${*:-sftp://<account>@<host>:<port>//<folder>}"
[[ $# -eq 1 ]] || die "usage: sudo $0 sftp://<account>@<host>:<port>//<folder>, or: sudo $0 --remove"

# set_env KEY VALUE, or set_env KEY to take the key out; the file stays root's and 0600, its other lines kept
set_env() {
    local key=$1 tmp
    mkdir -p "$(dirname "$ENV_FILE")"
    tmp=$(mktemp "$ENV_FILE.XXXXXX")
    if [[ -f $ENV_FILE ]]; then grep -v "^$key=" "$ENV_FILE" > "$tmp" || true; fi
    if [[ $# -eq 2 ]]; then echo "$key=$2" >> "$tmp"; fi
    chmod 0600 "$tmp"
    mv -f "$tmp" "$ENV_FILE"
}

if [[ $1 == --remove ]]; then
    set_env BACKUP_OFFSITE_REPOSITORY
    rm -f "$CREDSTORE/$NAME.key" "$CREDSTORE/$NAME.known_hosts" "$CREDSTORE/$NAME.password"
    echo "No copy goes off the site any more: backup.env's line and the three credentials are gone. The repository on the host stays, and so does the key in its .sftp/authorized_keys: delete them there."
    exit 0
fi

grep -qx "ImportCredential=$NAME\.\*" "$UNIT_DIR/bs3d-api-backup.service" 2> /dev/null \
    || die "$UNIT_DIR/bs3d-api-backup.service does not import the credentials: update the service first (deploy/update.sh)"
for tool in restic sftp ssh-keyscan ssh-keygen; do
    command -v "$tool" > /dev/null || die "$tool is not installed: apt install restic openssh-client"
done
repo=$1
[[ $repo =~ ^sftp://([A-Za-z0-9._-]+)@([A-Za-z0-9.-]+):([0-9]+)//([A-Za-z0-9._/-]+)$ ]] \
    || die "$repo is not sftp://<account>@<host>:<port>//<folder>"
login="${BASH_REMATCH[1]}@${BASH_REMATCH[2]}"
host=${BASH_REMATCH[2]}
port=${BASH_REMATCH[3]}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# secret <name> <what to ask> [confirm]: the password from its file, or asked for, into $work/<name>
secret() {
    local file="$SECRETS_DIR/offsite-$1-password" one two
    if [[ -s $file ]]; then
        cp "$file" "$work/$1"
        return
    fi
    read -rsp "$2: " one || true
    echo
    [[ -n $one ]] || die "no password given: nothing changed"
    if [[ ${3:-} == confirm ]]; then
        read -rsp "The same once more: " two || true
        echo
        [[ $one == "$two" ]] || die "the two differ: nothing changed"
    fi
    printf '%s' "$one" > "$work/$1"
}

# The host's keys: the ones recorded before, so a host whose key changed is refused, or the ones it shows now
if [[ -s $CREDSTORE/$NAME.known_hosts ]]; then
    cp "$CREDSTORE/$NAME.known_hosts" "$work/known_hosts"
else
    ssh-keyscan -T 15 -p "$port" "$host" 2> /dev/null | grep -v '^#' > "$work/known_hosts" || true
    [[ -s $work/known_hosts ]] || die "$host answers no SSH on port $port: are the host and the port right? Nothing changed"
fi
echo "The host's keys, trusted from now on:"
ssh-keygen -lf "$work/known_hosts"

if [[ -s $CREDSTORE/$NAME.key ]]; then
    cp "$CREDSTORE/$NAME.key" "$work/key"
    ssh-keygen -y -f "$work/key" > "$work/key.pub"
else
    ssh-keygen -q -t ed25519 -N '' -C "bs3d-api-backup@$(hostname)" -f "$work/key"
fi

ssh_options=(-o "UserKnownHostsFile=$work/known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=30)
key_logs_in() {
    printf 'ls\n' | sftp -b - -P "$port" -i "$work/key" -o IdentitiesOnly=yes -o BatchMode=yes "${ssh_options[@]}" "$login" > /dev/null 2>&1
}

if key_logs_in; then
    echo "The key logs in already."
else
    secret sftp "The hosting account's password"
    ssh-keygen -e -f "$work/key.pub" > "$work/authorized_keys"
    # The password through SSH_ASKPASS from a root-only file, never on a command line; no batch mode, which allows keys only
    printf '#!/bin/sh\ncat %q\n' "$work/sftp" > "$work/askpass"
    chmod 0700 "$work/askpass"
    printf 'mkdir .sftp\nput %s .sftp/authorized_keys\n' "$work/authorized_keys" \
        | SSH_ASKPASS="$work/askpass" SSH_ASKPASS_REQUIRE=force sftp -P "$port" -o PreferredAuthentications=password \
            -o NumberOfPasswordPrompts=1 "${ssh_options[@]}" "$login" > "$work/upload" 2>&1 || true
    key_logs_in || die "the key does not log in after it was put on the account (is the password right?): $(tail -2 "$work/upload" | tr '\n' ' ') Nothing changed"
    echo "The key is on the account, and it logs in."
fi

secret restic "The repository's password (keep it off the Pi too)" confirm
restic_args=(--repo "$repo" --password-file "$work/restic" --no-cache
    -o "sftp.args=-i $work/key -o IdentitiesOnly=yes -o UserKnownHostsFile=$work/known_hosts -o StrictHostKeyChecking=yes -o BatchMode=yes -o ConnectTimeout=30")
# init refuses a repository that is there; then the password must open it
if restic "${restic_args[@]}" init > "$work/init" 2>&1; then
    echo "The repository is made, encrypted with the password."
elif restic "${restic_args[@]}" cat config > /dev/null 2> "$work/open"; then
    echo "The repository was there already, and the password opens it."
else
    die "restic could neither make the repository nor open it: $(tail -1 "$work/init") / $(tail -1 "$work/open") Nothing changed"
fi

install -d -m 0700 -o root -g root "$CREDSTORE"
install -m 0600 -o root -g root "$work/key" "$CREDSTORE/$NAME.key"
install -m 0600 -o root -g root "$work/known_hosts" "$CREDSTORE/$NAME.known_hosts"
install -m 0600 -o root -g root "$work/restic" "$CREDSTORE/$NAME.password"
set_env BACKUP_OFFSITE_REPOSITORY "$repo"
rm -f "$SECRETS_DIR/offsite-sftp-password" "$SECRETS_DIR/offsite-restic-password"

echo "The copy off the site is set up: $repo. One backup now:"
systemctl start bs3d-api-backup || true
cat "$BACKUP_DIR/last-offsite" 2> /dev/null || echo "(no record in $BACKUP_DIR/last-offsite: see journalctl -u bs3d-api-backup -n 30)"
