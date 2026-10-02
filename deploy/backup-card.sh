#!/usr/bin/env bash
# Sets up a microSD card, or a USB drive, as the off-box backup of issue #6. Run as root:
#   sudo /opt/bs3d-api/current/deploy/backup-card.sh /dev/mmcblk0    ERASE the card and keep the backups on it
#   sudo /opt/bs3d-api/current/deploy/backup-card.sh --remove        stop using it (the copies on it stay)
#
# It erases the whole card and makes one GPT partition on it, ext4, labelled bs3d-backup. It mounts the partition at
# /mnt/bs3d-backup through an /etc/fstab line by its UUID with nofail, so a missing card never stops the Pi booting
# (the fstab from before is kept as fstab.before-bs3d-backup). It makes the folder bs3d-api on it for the bs3d-api
# user, and puts BACKUP_OFFBOX_MOUNT and BACKUP_OFFBOX_TARGET in /etc/bs3d-api/backup.env, other lines kept. Then it
# runs one backup and shows what it recorded. From that night on, deploy/backup.sh copies every backup to the card and
# fails the run, visibly on the admin page, when the card is not there.
#
# So that a typo never erases the wrong disk, it takes only a whole disk in the SD slot (mmcblk) or on USB. It refuses
# a disk with anything on it mounted or used as swap, and the disk the running system is on. It shows the disk and
# erases nothing until its name is typed back.
#
# The Pi 5 looks for a system to boot on a card in the slot first (BOOT_ORDER 0xf461 on this Pi). This card holds
# none, so it goes on to the NVMe a moment later; but a card with Raspberry Pi OS on it, left in the slot, would boot.
# FSTAB, ENV_FILE, MOUNT_DIR, SERVICE_USER and BACKUP_DIR exist for tests/deploy/backup-card.test.sh.
set -euo pipefail

FSTAB=${FSTAB:-/etc/fstab}
ENV_FILE=${ENV_FILE:-/etc/bs3d-api/backup.env}
MOUNT_DIR=${MOUNT_DIR:-/mnt/bs3d-backup}
SERVICE_USER=${SERVICE_USER:-bs3d-api}
BACKUP_DIR=${BACKUP_DIR:-/var/lib/bs3d-api/backups}
LABEL=bs3d-backup
TARGET="$MOUNT_DIR/bs3d-api/"

die() { echo "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "run it as root: sudo $0 ${*:-/dev/mmcblk0}"
[[ $# -eq 1 ]] || die "usage: sudo $0 /dev/mmcblk0   (the card: see lsblk), or: sudo $0 --remove"

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

# The fstab without its line for MOUNT_DIR, with <line> added when one is given. Checked before it replaces the file:
# every other line kept, and the root file system's still there
write_fstab() {
    local line=${1:-} tmp kept
    [[ -f "$FSTAB.before-bs3d-backup" ]] || cp -p "$FSTAB" "$FSTAB.before-bs3d-backup"
    tmp=$(mktemp "$FSTAB.XXXXXX")
    awk -v dir="$MOUNT_DIR" '$2 != dir' "$FSTAB" > "$tmp"
    kept=$(( $(wc -l < "$FSTAB") - $(awk -v dir="$MOUNT_DIR" '$2 == dir' "$FSTAB" | wc -l) ))
    if [[ $(wc -l < "$tmp") -ne $kept ]] || ! awk '$2 == "/" { found = 1 } END { exit !found }' "$tmp"; then
        rm -f "$tmp"
        die "the new $FSTAB did not check out: nothing changed"
    fi
    if [[ -n $line ]]; then echo "$line" >> "$tmp"; fi
    chmod 0644 "$tmp"
    mv -f "$tmp" "$FSTAB"
    systemctl daemon-reload
}

if [[ $1 == --remove ]]; then
    if mountpoint -q "$MOUNT_DIR"; then umount "$MOUNT_DIR"; fi
    write_fstab
    set_env BACKUP_OFFBOX_MOUNT
    set_env BACKUP_OFFBOX_TARGET
    echo "The backups no longer go to a card: the fstab line and backup.env's two lines are gone. The copies on the card stay."
    exit 0
fi

dev=$1
name=$(basename "$dev")
type=$(lsblk -dno TYPE "$dev" 2> /dev/null) || die "$dev is not a disk this Pi has. The card in the slot is usually /dev/mmcblk0: see lsblk"
[[ $type == disk ]] || die "$dev is not a whole disk (a partition?): give the disk, e.g. /dev/mmcblk0"
case $name in
    mmcblk[0-9] | mmcblk[0-9][0-9]) ;;
    sd[a-z] | sd[a-z][a-z])
        [[ $(lsblk -dno TRAN "$dev") == usb ]] || die "$dev is not a USB drive: only the SD slot or USB is taken" ;;
    *) die "$dev is neither in the SD slot (mmcblk) nor on USB (sd): only those are taken" ;;
esac
root_disk=$(lsblk -no PKNAME "$(findmnt -no SOURCE /)")
[[ $name != "$root_disk" ]] || die "$dev holds the running system: never"
in_use=$(lsblk -nro MOUNTPOINTS "$dev")
[[ -z ${in_use//[[:space:]]/} ]] || die "$dev has something mounted or used as swap ($(echo "$in_use" | xargs)): unmount it first, or it is the wrong disk"
if mountpoint -q "$MOUNT_DIR"; then die "something is already mounted at $MOUNT_DIR: run $0 --remove first"; fi

lsblk -o NAME,SIZE,TYPE,FSTYPE,LABEL,MODEL "$dev"
echo
echo "EVERYTHING on $dev will be erased. Type its name ($name) to go on, anything else to stop:"
read -r answer || true
[[ $answer == "$name" ]] || die "Not erased: nothing changed."

# The old partitions' signatures first, then the disk's, so nothing of the old layout is found again
for part in $(lsblk -nro NAME,TYPE "$dev" | awk '$2 == "part" { print $1 }'); do wipefs -aq "/dev/$part"; done
wipefs -aq "$dev"
parted -s "$dev" mklabel gpt mkpart "$LABEL" ext4 1MiB 100%
udevadm settle
part=$(lsblk -nro NAME,TYPE "$dev" | awk '$2 == "part" { print "/dev/" $1; exit }')
[[ -n $part ]] || die "the new partition on $dev did not appear: see lsblk"
# No blocks reserved for root: nothing but the backups goes on this card
mkfs.ext4 -q -F -m 0 -L "$LABEL" "$part"
uuid=$(blkid -s UUID -o value "$part")
[[ -n $uuid ]] || die "the new file system on $part has no UUID: see blkid"

mkdir -p "$MOUNT_DIR"
write_fstab "UUID=$uuid $MOUNT_DIR ext4 nofail,noatime,x-systemd.device-timeout=10s 0 2"
mount "$MOUNT_DIR"
install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 0750 "$TARGET"
set_env BACKUP_OFFBOX_MOUNT "$MOUNT_DIR"
set_env BACKUP_OFFBOX_TARGET "$TARGET"

echo "The card is mounted at $MOUNT_DIR and the backups will go to $TARGET. One backup now:"
systemctl start bs3d-api-backup || true
cat "$BACKUP_DIR/last-offbox" 2> /dev/null || echo "(no record in $BACKUP_DIR/last-offbox: see journalctl -u bs3d-api-backup -n 20)"
