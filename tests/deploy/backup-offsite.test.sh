#!/usr/bin/env bash
# Tests deploy/backup-offsite.sh (issue #6) as root against a scratch folder. The hosting is stand-ins: ssh-keyscan shows
# a host key made here; sftp takes the account's password through SSH_ASKPASS and runs mkdir and put in a scratch folder,
# and lets a key log in only when .sftp/authorized_keys holds it in RFC 4716 form, as the owner's hosting does; ssh,
# which restic starts, runs the real sftp-server, so the repository is the real restic's, made and opened on this
# machine. systemctl records what it is asked. Root only because the script refuses anyone else: CI runs it with sudo,
# and on the Pi it runs as unshare -r tests/deploy/backup-offsite.test.sh. It needs restic and sftp-server.
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -r): the script under test refuses anyone else" >&2; exit 1; }
command -v restic > /dev/null || { echo "restic is not installed: apt-get install restic" >&2; exit 1; }
sftp_server=
for candidate in /usr/lib/openssh/sftp-server /usr/lib/sftp-server /usr/libexec/openssh/sftp-server /usr/libexec/sftp-server; do
    if [[ -x $candidate ]]; then sftp_server=$candidate; break; fi
done
[[ -n $sftp_server ]] || { echo "no sftp-server: apt-get install openssh-sftp-server" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/etc/bs3d-api" "$work/state" "$work/units" "$work/remote" "$work/backups"
cp "$repo/deploy/bs3d-api-backup.service" "$work/units/"
ssh-keygen -q -t ed25519 -N '' -C host -f "$work/state/hostkey"
echo "[host.example]:222 $(cut -d' ' -f1,2 "$work/state/hostkey.pub")" > "$work/state/scanned"
printf 'the account password' > "$work/state/sftp-password"

cat > "$work/bin/ssh-keyscan" << EOF
#!/bin/sh
echo scan >> "$work/state/calls"
echo "# host.example:222 SSH-2.0-mod_sftp"
cat "$work/state/scanned"
EOF
# The body of an RFC 4716 key, without its header lines and the comment that names who converted it
cat > "$work/bin/rfc4716-body" << 'EOF'
#!/bin/sh
grep -v -e '^----' -e '^Comment:' "$1"
EOF
cat > "$work/bin/sftp" << EOF
#!/usr/bin/env bash
key= batch=
while [[ \$# -gt 0 ]]; do
    case \$1 in -i) key=\$2; shift 2 ;; -b) batch=\$2; shift 2 ;; -P | -o) shift 2 ;; *) shift ;; esac
done
if [[ -n \$batch ]]; then
    echo key-login >> "$work/state/calls"
    cat > /dev/null
    [[ -n \$key && -s "$work/remote/.sftp/authorized_keys" ]] || exit 255
    head -1 "$work/remote/.sftp/authorized_keys" | grep -qx -- '---- BEGIN SSH2 PUBLIC KEY ----' || exit 255
    ssh-keygen -y -f "\$key" > "$work/state/login.pub" && ssh-keygen -e -f "$work/state/login.pub" > "$work/state/login.rfc"
    cmp -s <(rfc4716-body "$work/state/login.rfc") <(rfc4716-body "$work/remote/.sftp/authorized_keys") || exit 255
    exit 0
fi
echo password-login >> "$work/state/calls"
[[ "\$("\$SSH_ASKPASS")" == "\$(cat "$work/state/sftp-password")" ]] || { echo "Permission denied (publickey,password)." >&2; exit 255; }
while read -r command a b; do
    case \$command in
        mkdir) mkdir -p "$work/remote/\$a" ;;
        put) mkdir -p "\$(dirname "$work/remote/\$b")" && cp "\$a" "$work/remote/\$b" ;;
    esac
done
EOF
cat > "$work/bin/ssh" << EOF
#!/bin/sh
exec $sftp_server
EOF
cat > "$work/bin/systemctl" << EOF
#!/bin/sh
echo "systemctl \$*" >> "$work/state/calls"
printf 'attempt=now\nresult=ok\n' > "$work/backups/last-offsite"
EOF
chmod +x "$work/bin/"*

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

url="sftp://bs3dbackup.example.eu@host.example:222/$work/remote/bs3d-api"
store="$work/credstore"
env_file="$work/etc/bs3d-api/backup.env"
# Runs backup-offsite.sh with <argument> and what it reads from $work/stdin; its output to $work/out, its exit code to $status
run() {
    status=0
    : > "$work/state/calls"
    env PATH="$work/bin:$PATH" CREDSTORE="$store" ENV_FILE="$env_file" SECRETS_DIR="$work/etc/bs3d-api" \
        UNIT_DIR="$work/units" BACKUP_DIR="$work/backups" bash "$repo/deploy/backup-offsite.sh" "$1" < "$work/stdin" > "$work/out" 2>&1 || status=$?
}
passwords() { printf 'the account password' > "$work/etc/bs3d-api/offsite-sftp-password"; printf 'the repository password' > "$work/etc/bs3d-api/offsite-restic-password"; }
rs() { env PATH="$work/bin:$PATH" restic --no-cache --repo "$url" --password-file "$1" cat config 2> /dev/null; }
mode() { stat -c %a "$1" 2> /dev/null; }
called() { grep -qx -- "$1" "$work/state/calls"; }
: > "$work/stdin"
echo "BACKUP_OFFBOX_TARGET=/mnt/bs3d-backup/bs3d-api/" > "$env_file"

# 1. Refused before anything is written: a repository not over SFTP as the script takes it, and a unit that would not
# hand the backup its credentials
passwords
run "/srv/restic"
if [[ $status -ne 0 && $(cat "$work/out") == *"is not sftp://"* && ! -e $store && $(cat "$env_file") != *OFFSITE* ]]; then pass "a repository not of the form sftp://account@host:port//folder is refused"
else fail "a local repository: exit $status: $(cat "$work/out")"; fi
sed -i '/^ImportCredential=/d' "$work/units/bs3d-api-backup.service"
run "$url"
if [[ $status -ne 0 && $(cat "$work/out") == *"does not import the credentials"* && ! -e $store ]]; then pass "a backup unit without ImportCredential is refused"
else fail "a unit without ImportCredential: exit $status: $(cat "$work/out")"; fi
cp "$repo/deploy/bs3d-api-backup.service" "$work/units/"

# 2. The account's password wrong: the key does not log in, and nothing is written
printf 'not the password' > "$work/etc/bs3d-api/offsite-sftp-password"
run "$url"
if [[ $status -ne 0 && $(cat "$work/out") == *"does not log in"* && ! -e $store && $(cat "$env_file") != *OFFSITE* \
    && -e "$work/etc/bs3d-api/offsite-restic-password" ]]; then pass "the account's password wrong: refused, nothing written, nothing deleted"
else fail "a wrong account password: exit $status, credstore $(find "$store" -mindepth 1 -printf '%f ' 2> /dev/null): $(cat "$work/out")"; fi

# 3. Set up from the two files
passwords
run "$url"
if [[ $status -eq 0 ]]; then pass "set up: the script succeeds"; else fail "set up: exit $status: $(cat "$work/out")"; fi
if [[ $(mode "$store") == 700 && $(mode "$store/bs3d-api-offsite.key") == 600 && $(mode "$store/bs3d-api-offsite.known_hosts") == 600 \
    && $(mode "$store/bs3d-api-offsite.password") == 600 ]]; then pass "set up: the three credentials are root's alone, in a store that is"
else fail "set up: modes store $(mode "$store"), key $(mode "$store/bs3d-api-offsite.key"), known_hosts $(mode "$store/bs3d-api-offsite.known_hosts"), password $(mode "$store/bs3d-api-offsite.password")"; fi
if grep -qx "BACKUP_OFFSITE_REPOSITORY=$url" "$env_file" && grep -qx "BACKUP_OFFBOX_TARGET=/mnt/bs3d-backup/bs3d-api/" "$env_file" && [[ $(mode "$env_file") == 600 ]]; then
    pass "set up: backup.env names the repository, keeps its other lines and stays 0600"
else fail "set up: backup.env ($(mode "$env_file")): $(cat "$env_file")"; fi
if [[ ! -e "$work/etc/bs3d-api/offsite-sftp-password" && ! -e "$work/etc/bs3d-api/offsite-restic-password" ]]; then pass "set up: the two password files are deleted"
else fail "set up: password files left: $(ls "$work/etc/bs3d-api")"; fi
ssh-keygen -y -f "$store/bs3d-api-offsite.key" > "$work/state/stored.pub" 2> /dev/null && ssh-keygen -e -f "$work/state/stored.pub" > "$work/state/stored.rfc"
if cmp -s <("$work/bin/rfc4716-body" "$work/state/stored.rfc") <("$work/bin/rfc4716-body" "$work/remote/.sftp/authorized_keys"); then
    pass "set up: the stored key's public half is on the account, in RFC 4716 form"
else fail "set up: the account's .sftp/authorized_keys: $(cat "$work/remote/.sftp/authorized_keys" 2> /dev/null)"; fi
config=$(rs "$store/bs3d-api-offsite.password")
if [[ -n $config ]] && cmp -s "$store/bs3d-api-offsite.known_hosts" "$work/state/scanned"; then pass "set up: the repository is made, the stored password opens it, and the host's keys are recorded"
else fail "set up: config '$config', known_hosts $(cat "$store/bs3d-api-offsite.known_hosts")"; fi
if called "systemctl start bs3d-api-backup" && [[ $(cat "$work/out") == *"result=ok"* ]]; then pass "set up: one backup is run and its record shown"
else fail "set up: calls $(xargs < "$work/state/calls")"; fi

# 4. Run again, the passwords asked for: the key, the host's keys and the repository kept, the account's password not needed
cp "$store/bs3d-api-offsite.key" "$work/state/key.before"
printf 'the repository password\nthe repository password\n' > "$work/stdin"
run "$url"
if [[ $status -eq 0 ]] && cmp -s "$store/bs3d-api-offsite.key" "$work/state/key.before" && ! called password-login && ! called scan \
    && [[ $(rs "$store/bs3d-api-offsite.password") == "$config" ]]; then
    pass "run again: the same key and host keys, no account password, the repository opened and not made anew"
else fail "run again: exit $status, calls $(xargs < "$work/state/calls"): $(cat "$work/out")"; fi

# 5. Run again with a wrong repository password, or two that differ: refused, the stored one kept
printf 'a wrong password\na wrong password\n' > "$work/stdin"
run "$url"
if [[ $status -ne 0 && $(cat "$work/out") == *"neither make the repository nor open it"* && $(cat "$store/bs3d-api-offsite.password") == "the repository password" ]]; then
    pass "a wrong repository password: refused, and the stored one kept"
else fail "a wrong repository password: exit $status, stored '$(cat "$store/bs3d-api-offsite.password")': $(cat "$work/out")"; fi
printf 'the repository password\nthe repository passwort\n' > "$work/stdin"
run "$url"
if [[ $status -ne 0 && $(cat "$work/out") == *"the two differ"* ]]; then pass "two passwords that differ: refused"
else fail "two passwords that differ: exit $status: $(cat "$work/out")"; fi

# 6. --remove: the repository's line and the credentials gone, the other lines kept
: > "$work/stdin"
run --remove
if [[ $status -eq 0 && ! -e "$store/bs3d-api-offsite.key" && ! -e "$store/bs3d-api-offsite.known_hosts" && ! -e "$store/bs3d-api-offsite.password" \
    && $(cat "$env_file") == "BACKUP_OFFBOX_TARGET=/mnt/bs3d-backup/bs3d-api/" ]]; then
    pass "--remove: the credentials and backup.env's line gone, the other lines kept"
else fail "--remove: exit $status, store $(find "$store" -mindepth 1 -printf '%f ' 2> /dev/null), env $(cat "$env_file")"; fi

if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
