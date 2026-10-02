#!/usr/bin/env bash
# Behaviour, end to end (issue #4): deploy/firewall.sh with the real ip and nft, in a network namespace of its own ("pi")
# wired by a veth cable to another ("router"), while flows that predate the rules run, as the tunnel's and Claude Code's
# did on the Pi on 2026-10-02, when loading the rules straight away cut both. Prints ok/FAIL lines.
# Run by tests/deploy/firewall.test.sh inside `unshare -n`, as root (sudo, or a user namespace's root).
#   firewall-lab.sh <repository> <folder with systemctl and systemd-run stand-ins>
set -u

repo=$1
stubs=$2
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
work=$(mktemp -d)
pids=()
trap 'kill "${pids[@]}" 2> /dev/null; rm -rf "$work"' EXIT

ip link set lo up
ip link add v-pi type veth peer name v-router
unshare -n sleep 120 &
router=$!
pids+=("$router")
sleep 0.3
ip link set v-router netns "$router"
in_router() { nsenter -t "$router" -n "$@"; }

ip addr add 192.168.50.2/24 dev v-pi
ip -6 addr add 2001:db8:50::2/64 dev v-pi nodad
ip link set v-pi up
ip route add default via 192.168.50.1
ip -6 route add default via 2001:db8:50::1
# The router also holds addresses outside the home network's prefixes, to knock from
in_router ip link set lo up
in_router ip addr add 192.168.50.1/24 dev v-router
in_router ip addr add 10.9.9.9/32 dev v-router
in_router ip -6 addr add 2001:db8:50::1/64 dev v-router nodad
in_router ip -6 addr add 2001:db8:99::1/128 dev v-router nodad
in_router ip link set v-router up
sleep 1

python3 "$here/firewall-lab.py" listen 22 8080 &
pids+=($!)
in_router python3 "$here/firewall-lab.py" server &
pids+=($!)
sleep 0.5
python3 "$here/firewall-lab.py" client "$work/phase" &
client=$!
for _ in $(seq 1 50); do [[ -f "$work/phase" ]] && break; sleep 0.1; done

if PATH="$stubs:$PATH" NFT_CONF="$work/nftables.conf" WARMUP_SECONDS=${WARMUP_SECONDS:-3} \
    bash "$repo/deploy/firewall.sh" > "$work/firewall.out" 2>&1; then
    echo "ok    firewall.sh loads its rules into a namespace with the real ip and nft"
else
    echo "FAIL  firewall.sh in the namespace: $(cat "$work/firewall.out")"
fi
# --confirm against the real nft, five times: its check once looked at a listing nft was still writing
confirmed=0
for _ in 1 2 3 4 5; do
    if PATH="$stubs:$PATH" bash "$repo/deploy/firewall.sh" --confirm > /dev/null 2>&1; then confirmed=$((confirmed + 1)); fi
done
if (( confirmed == 5 )); then echo "ok    --confirm sees the loaded rules, 5 times of 5"; else echo "FAIL  --confirm saw the loaded rules $confirmed times of 5"; fi
touch "$work/phase.loaded"
# Neighbour caches emptied, so that what follows needs IPv6 neighbour discovery through the rules (ARP is not IP)
ip neigh flush all
ip -6 neigh flush all
in_router ip neigh flush all
in_router ip -6 neigh flush all
wait "$client"

in_router python3 "$here/firewall-lab.py" probe \
    192.168.50.1 192.168.50.2 22 open \
    192.168.50.1 192.168.50.2 8080 dropped \
    10.9.9.9 192.168.50.2 22 dropped \
    2001:db8:50::1 2001:db8:50::2 22 open \
    2001:db8:50::1 2001:db8:50::2 8080 dropped \
    2001:db8:99::1 2001:db8:50::2 22 dropped
