#!/usr/bin/env bash
# Tests deploy/backup-card.sh (issue #6) as root against a scratch folder, with stand-ins for every tool that touches a
# disk (lsblk, findmnt, wipefs, parted, udevadm, mkfs.ext4, blkid, mount, umount, mountpoint) and for systemctl, over a
# disk table of their own: the NVMe the system runs from, a card in the slot, a card with a partition mounted, a USB
# drive and a SATA disk. Nothing real is erased or mounted. Root only because the script refuses anyone else: CI runs
# it with sudo, and on the Pi it runs as unshare -r tests/deploy/backup-card.test.sh
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -r): the script under test refuses anyone else" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/etc/bs3d-api" "$work/state"

cat > "$work/disks" << 'EOF'
nvme0n1   disk nvme -       -
nvme0n1p1 part nvme nvme0n1 /boot/firmware
nvme0n1p2 part nvme nvme0n1 /
mmcblk0   disk -    -       -
mmcblk0p1 part -    mmcblk0 -
mmcblk1   disk -    -       -
mmcblk1p1 part -    mmcblk1 /media/rdt/OLDCARD
sda       disk usb  -       -
sdb       disk sata -       -
EOF

# lsblk answers the forms backup-card.sh asks, from the table: a disk's TYPE or TRAN, a partition's PKNAME, and a
# disk's own row and its partitions' for MOUNTPOINTS and NAME,TYPE
cat > "$work/bin/lsblk" << 'EOF'
#!/usr/bin/env python3
import os, sys
args = sys.argv[1:]
dev = os.path.basename(args[-1])
rows = [l.split() for l in open(os.environ["STUB_DISKS"]) if l.strip()]
row = next((r for r in rows if r[0] == dev), None)
if row is None:
    print(f"lsblk: {args[-1]}: not a block device", file=sys.stderr); sys.exit(32)
cols = args[-2]
tree = [row] + [r for r in rows if r[3] == dev]
show = lambda v: "" if v == "-" else v
if cols == "TYPE": print(row[1])
elif cols == "TRAN": print(show(row[2]))
elif cols == "PKNAME": print(show(row[3]))
elif cols == "MOUNTPOINTS": print("\n".join(show(r[4]) for r in tree))
elif cols == "NAME,TYPE": print("\n".join(f"{r[0]} {r[1]}" for r in tree))
else: print("\n".join(" ".join(r) for r in tree))
EOF
cat > "$work/bin/findmnt" << 'EOF'
#!/bin/sh
echo /dev/nvme0n1p2
EOF
cat > "$work/bin/blkid" << 'EOF'
#!/bin/sh
for last; do :; done
echo "uuid-of-$(basename "$last")"
EOF
cat > "$work/bin/mountpoint" << 'EOF'
#!/bin/sh
for last; do :; done
[ -e "$STUB_STATE/mounted" ] && [ "$(cat "$STUB_STATE/mounted")" = "$last" ]
EOF
cat > "$work/bin/mount" << 'EOF'
#!/bin/sh
echo "mount $*" >> "$STUB_LOG"
echo "$1" > "$STUB_STATE/mounted"
EOF
cat > "$work/bin/umount" << 'EOF'
#!/bin/sh
echo "umount $*" >> "$STUB_LOG"
rm -f "$STUB_STATE/mounted"
EOF
# The rest only say they were called; $* and $STUB_LOG are the stand-in's own, expanded when it runs
for tool in wipefs parted udevadm mkfs.ext4 systemctl; do
    # shellcheck disable=SC2016
    printf '#!/bin/sh\necho "%s $*" >> "$STUB_LOG"\n' "$tool" > "$work/bin/$tool"
done
chmod +x "$work/bin/"*

fstab="$work/etc/fstab"
envfile="$work/etc/bs3d-api/backup.env"
printf '%s\n' 'proc            /proc           proc    defaults          0       0' \
    'PARTUUID=0065b2e1-01  /boot/firmware  vfat    defaults          0       2' \
    'PARTUUID=0065b2e1-02  /               ext4    defaults,noatime  0       1' > "$fstab"
printf 'BACKUP_KEEP_DAYS=45\n' > "$envfile"
cp "$fstab" "$work/fstab.original"
cp "$envfile" "$work/env.original"

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# Runs backup-card.sh with $1 typed back and the rest as its arguments; output to $work/out, the stand-ins' record to
# $work/calls, the exit code to $status
run() {
    local typed=$1
    shift
    : > "$work/calls"
    status=0
    echo "$typed" | env PATH="$work/bin:$PATH" STUB_LOG="$work/calls" STUB_STATE="$work/state" STUB_DISKS="$work/disks" \
        FSTAB="$fstab" ENV_FILE="$envfile" MOUNT_DIR="$work/mnt/bs3d-backup" SERVICE_USER=root BACKUP_DIR="$work/backups" \
        bash "$repo/deploy/backup-card.sh" "$@" > "$work/out" 2>&1 || status=$?
}

# A refused disk: a non-zero exit, nothing erased or mounted, fstab and backup.env as they were
refused() {
    local what=$1
    if [[ $status -ne 0 ]] && ! grep -qE '^(wipefs|parted|mkfs|mount) ' "$work/calls" && cmp -s "$fstab" "$work/fstab.original" \
        && cmp -s "$envfile" "$work/env.original"; then
        pass "$what is refused, nothing touched: $(tail -1 "$work/out")"
    else
        fail "$what: exit $status, calls: $(tr '\n' ';' < "$work/calls")"
    fi
}

run nvme0n1 /dev/nvme0n1
refused "the NVMe the system runs from"
run mmcblk0p1 /dev/mmcblk0p1
refused "a partition rather than the disk"
run mmcblk1 /dev/mmcblk1
refused "a card with a partition mounted"
run sdb /dev/sdb
refused "a disk that is not on USB"
run mmcblk9 /dev/mmcblk9
refused "a disk this machine does not have"
run mmcblk1 /dev/mmcblk0
refused "the right card with the wrong name typed back"
run "" /dev/mmcblk0
refused "the right card with nothing typed back"

# The card, its name typed back: erased, formatted, mounted by UUID with nofail, and backup.env pointed at it
run mmcblk0 /dev/mmcblk0
calls=$(cat "$work/calls")
if [[ $status -eq 0 && $calls == *"wipefs -aq /dev/mmcblk0p1"* && $calls == *"wipefs -aq /dev/mmcblk0"* \
    && $calls == *"parted -s /dev/mmcblk0 mklabel gpt mkpart bs3d-backup ext4 1MiB 100%"* \
    && $calls == *"mkfs.ext4 -q -F -m 0 -L bs3d-backup /dev/mmcblk0p1"* ]]; then pass "the card is erased and formatted, and only the card"
else fail "the card: exit $status, calls: $(tr '\n' ';' < "$work/calls") $(cat "$work/out")"; fi
if grep -qE '^(wipefs|parted|mkfs).*(nvme|mmcblk1|sd)' "$work/calls"; then fail "a disk other than the card was touched"; fi
line="UUID=uuid-of-mmcblk0p1 $work/mnt/bs3d-backup ext4 nofail,noatime,x-systemd.device-timeout=10s 0 2"
if [[ $(tail -1 "$fstab") == "$line" ]] && head -3 "$fstab" | cmp -s - "$work/fstab.original" && cmp -s "$fstab.before-bs3d-backup" "$work/fstab.original"; then
    pass "fstab gains one line by UUID with nofail, keeps every other, and the one from before is kept"
else fail "fstab: $(cat "$fstab")"; fi
if [[ $(cat "$envfile") == $'BACKUP_KEEP_DAYS=45\nBACKUP_OFFBOX_MOUNT='"$work/mnt/bs3d-backup"$'\nBACKUP_OFFBOX_TARGET='"$work/mnt/bs3d-backup/bs3d-api/" \
    && $(stat -c %a "$envfile") == 600 ]]; then pass "backup.env names the drive and the folder, keeps its other lines, and stays 0600"
else fail "backup.env ($(stat -c %a "$envfile")): $(cat "$envfile")"; fi
if [[ -d "$work/mnt/bs3d-backup/bs3d-api" && $(stat -c %a "$work/mnt/bs3d-backup/bs3d-api") == 750 \
    && $calls == *"mount $work/mnt/bs3d-backup"* && $calls == *"systemctl daemon-reload"* && $calls == *"systemctl start bs3d-api-backup"* ]]; then
    pass "the card is mounted through fstab, the folder made 0750, and one backup run"
else fail "mount, folder or backup: $(tr '\n' ';' < "$work/calls")"; fi

# Once set up, a second run is refused while the card is mounted
run mmcblk0 /dev/mmcblk0
if [[ $status -ne 0 && $(grep -c "bs3d-backup" "$fstab") -eq 1 ]]; then pass "a second card while one is mounted is refused"
else fail "a second card while one is mounted: exit $status, $(grep -c "bs3d-backup" "$fstab") fstab lines"; fi

# A USB drive after --remove: one fstab line still, its own; backup.env's keys not doubled
run "" --remove
if [[ $status -eq 0 && $(cat "$work/calls") == *"umount $work/mnt/bs3d-backup"* ]] && cmp -s "$fstab" "$work/fstab.original" \
    && cmp -s "$envfile" "$work/env.original"; then pass "--remove unmounts, and puts fstab and backup.env back as they were"
else fail "--remove: exit $status, fstab: $(cat "$fstab"), env: $(cat "$envfile")"; fi
sed -i 's/^sda .*/sda       disk usb  -       -\nsda1      part usb  sda     -/' "$work/disks"
run sda /dev/sda
if [[ $status -eq 0 && $(grep -c "bs3d-backup" "$fstab") -eq 1 && $(grep -c "uuid-of-sda1" "$fstab") -eq 1 \
    && $(grep -c "^BACKUP_OFFBOX_TARGET=" "$envfile") -eq 1 ]]; then pass "a USB drive is taken, with one fstab line and one of each key"
else fail "a USB drive: exit $status, fstab: $(cat "$fstab"), env: $(cat "$envfile")"; fi

# A Pi that runs from a USB disk: that disk passes as USB, and is still refused
run "" --remove
cp "$fstab" "$work/fstab.original"
cp "$envfile" "$work/env.original"
printf '%s\n' 'sdc       disk usb  -       -' 'sdc1      part usb  sdc     /boot/firmware' 'sdc2      part usb  sdc     /' >> "$work/disks"
printf '#!/bin/sh\necho /dev/sdc2\n' > "$work/bin/findmnt"
run sdc /dev/sdc
refused "a USB disk the system runs from"

if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
