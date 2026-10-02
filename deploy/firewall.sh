#!/usr/bin/env bash
# The Pi's own firewall (issue #4, item 5). The router's NAT keeps unsolicited IPv4 out, but IPv6 has no NAT, the Pi has
# a global IPv6 address, and a router's IPv6 filtering can change with a firmware update or a UPnP/PCP pinhole. So the
# Pi drops every inbound packet but SSH, VNC and mDNS from the home network and the answers to what it asked for itself.
#
#   sudo /opt/bs3d-api/current/deploy/firewall.sh            write /etc/nftables.conf from this release's nftables.conf
#                                                            and load it, with an undo armed: unless confirmed, the
#                                                            rules are flushed again 3 minutes later
#   sudo /opt/bs3d-api/current/deploy/firewall.sh --confirm  from a NEW SSH login: keep the rules, and load them at boot
#   sudo /opt/bs3d-api/current/deploy/firewall.sh --remove   flush them, stop loading them at boot, put the old file back
#
# The home network is read when it runs, from the interface the default route leaves by: its IPv4 prefix, IPv6
# link-local and its IPv6 prefixes. Run it again after the home network's prefixes change.
#
# NFT_CONF and UNDO_SECONDS exist for tests/deploy/firewall.test.sh.
set -euo pipefail

CONF=${NFT_CONF:-/etc/nftables.conf}
UNDO_SECONDS=${UNDO_SECONDS:-180}
UNDO=bs3d-firewall-undo
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

[[ $EUID -eq 0 ]] || { echo "run it with sudo: sudo $0 [--confirm | --remove]" >&2; exit 1; }
command -v nft > /dev/null || { echo "nftables is not installed: sudo apt install nftables" >&2; exit 1; }

disarm() { systemctl stop "$UNDO.timer" "$UNDO.service" > /dev/null 2>&1 || true; systemctl reset-failed "$UNDO.timer" "$UNDO.service" > /dev/null 2>&1 || true; }

case "${1:-}" in
    "") ;;
    --confirm)
        # Only rules this script loaded are kept: a confirm after the undo already ran would enable an empty policy
        if ! nft list chain inet filter input 2> /dev/null | grep -q "policy drop"; then
            echo "The rules are not loaded (the undo may have run already). Run it without --confirm first." >&2
            exit 1
        fi
        disarm
        # Started as well as enabled, so its state says what is loaded; starting it loads the same file again
        systemctl enable --now nftables > /dev/null 2>&1
        echo "Kept. nftables.service loads $CONF at every boot."
        exit 0 ;;
    --remove)
        disarm
        systemctl disable --now nftables > /dev/null 2>&1 || true
        nft flush ruleset
        if [[ -f "$CONF.before-bs3d" ]]; then mv -f "$CONF.before-bs3d" "$CONF"; fi
        echo "Removed: no rules loaded, none at boot, and $CONF is the one from before."
        exit 0 ;;
    *) echo "usage: sudo $0 [--confirm | --remove]" >&2; exit 2 ;;
esac

[[ -f "$here/nftables.conf" ]] || { echo "no nftables.conf beside $0" >&2; exit 1; }

dev=$(ip -4 route show default | awk '{ for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit } }')
[[ -n "$dev" ]] || { echo "no default route: cannot tell which network is the home one. Nothing changed." >&2; exit 1; }
# Prefixes, not addresses: 192.168.0.239/24 is 192.168.0.0/24
lan4=$(ip -4 -o addr show dev "$dev" scope global | awk '{ print $4 }' | python3 -c '
import ipaddress, sys
nets = sorted({str(ipaddress.ip_interface(a).network) for a in sys.stdin.read().split()})
print(("{ " + ", ".join(nets) + " }") if len(nets) > 1 else (nets[0] if nets else ""))')
[[ -n "$lan4" ]] || { echo "$dev has no IPv4 address. Nothing changed." >&2; exit 1; }
lan6=$(ip -6 -o addr show dev "$dev" scope global | awk '{ print $4 }' | python3 -c '
import ipaddress, sys
nets = sorted({str(ipaddress.ip_interface(a).network) for a in sys.stdin.read().split()})
print("{ " + ", ".join(["fe80::/10"] + nets) + " }")')

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
sed -e "s|@LAN4@|$lan4|g" -e "s|@LAN6@|$lan6|g" "$here/nftables.conf" > "$work/nftables.conf"
nft -c -f "$work/nftables.conf" || { echo "nft refused the rules. Nothing changed." >&2; exit 1; }

echo "Home network on $dev: IPv4 $lan4, IPv6 $lan6"
if [[ -f "$CONF" && ! -f "$CONF.before-bs3d" ]]; then cp -p "$CONF" "$CONF.before-bs3d"; fi
install -m 0644 -o root -g root "$work/nftables.conf" "$CONF"

# The undo first, so a rule that cuts this very login off is gone again within minutes
disarm
systemd-run --quiet --on-active="${UNDO_SECONDS}s" --unit="$UNDO" "$(command -v nft)" flush ruleset
nft -f "$CONF"

echo "Loaded. They are flushed again at $(date -d "+${UNDO_SECONDS} seconds" '+%H:%M:%S') unless you confirm:"
echo "open a NEW SSH login (and VNC, if you use it), and from there run: sudo $0 --confirm"
