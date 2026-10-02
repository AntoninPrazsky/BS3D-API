# The traffic of tests/deploy/firewall-lab.sh, one role per call:
#   server               on the "router": a UDP and a TCP flow per address family that speak every 2 s, as an
#                        idling tunnel and a slow stream do: seldom enough that a flow is not seen by chance in the
#                        moment firewall.sh checks its rules, often enough that its warm-up sees every one
#   client <phase file>  on "pi": opens those flows before the rules load, writes <phase file> when they run, and once
#                        <phase file>.loaded exists, counts for 5 s what the far end still gets through while pi only
#                        acknowledges; then opens new connections
#   listen <port>...     on "pi": accepts and closes, for what the rules let in and what they do not
#   probe <source> <destination> <port> <expect>...   on the "router": connects from <source>; expect "open" or "dropped"
import os, socket, sys, time

ROUTER = {"IPv6": ("2001:db8:50::1", socket.AF_INET6), "IPv4": ("192.168.50.1", socket.AF_INET)}

def say(ok, what):
    print(f"{'ok   ' if ok else 'FAIL '} {what}", flush=True)

def server():
    socks = []
    for addr, af in ROUTER.values():
        u = socket.socket(af, socket.SOCK_DGRAM); u.bind((addr, 9000)); u.setblocking(False); socks.append(("udp", u))
        t = socket.socket(af, socket.SOCK_STREAM); t.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        t.bind((addr, 9001)); t.listen(8); t.setblocking(False); socks.append(("listen", t))
    peers, conns, end = {}, [], time.time() + 60
    while time.time() < end:
        for kind, s in socks:
            try:
                if kind == "udp":
                    while True:
                        _, peer = s.recvfrom(100); peers[(id(s), peer)] = (s, peer)
                else:
                    c, _ = s.accept(); c.setblocking(False); conns.append(c)
            except OSError:
                pass
        for s, peer in peers.values():
            try: s.sendto(b"s" * 100, peer)
            except OSError: pass
        for c in conns:
            try: c.recv(4096)
            except OSError: pass
            try: c.send(b"s" * 100)
            except OSError: pass
        time.sleep(2)

def client(phase):
    flows = {}
    for family, (addr, af) in ROUTER.items():
        u = socket.socket(af, socket.SOCK_DGRAM); u.connect((addr, 9000)); u.send(b"c"); u.setblocking(False)
        t = socket.socket(af, socket.SOCK_STREAM); t.connect((addr, 9001)); t.setblocking(False)
        flows[f"UDP flow over {family}"] = u
        flows[f"TCP connection over {family}"] = t

    def received(s):
        n = 0
        while True:
            try:
                d = s.recv(4096)
                if not d: break
                n += len(d)
            except OSError:
                break
        # A UDP flow answers what it got, as QUIC acknowledges; TCP's kernel acknowledges by itself
        if n and s.type == socket.SOCK_DGRAM:
            try: s.send(b"a")
            except OSError: pass
        return n

    time.sleep(1.5)
    for s in flows.values(): received(s)
    open(phase, "w").write("running")
    counted, end = {name: 0 for name in flows}, None
    while end is None or time.time() < end:
        loaded = os.path.exists(phase + ".loaded")
        if loaded and end is None: end = time.time() + 5
        for name, s in flows.items():
            n = received(s)
            if loaded: counted[name] += n
        time.sleep(0.1)
    for name in flows:
        say(counted[name] > 0, f"a {name} open before the rules loaded still gets the far end's data after")
    for family, (addr, af) in ROUTER.items():
        try:
            socket.create_connection((addr, 9001), timeout=3).close(); ok = True
        except OSError:
            ok = False
        say(ok, f"a new outbound TCP connection over {family} after the load")

def listen(ports):
    socks = []
    for port in ports:
        s = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        s.bind(("::", int(port))); s.listen(8); s.settimeout(0.2); socks.append(s)
    end = time.time() + 90
    while time.time() < end:
        for s in socks:
            try: s.accept()[0].close()
            except OSError: pass

def probe(args):
    for i in range(0, len(args), 4):
        source, destination, port, expect = args[i:i + 4]
        af = socket.AF_INET6 if ":" in destination else socket.AF_INET
        s = socket.socket(af, socket.SOCK_STREAM); s.settimeout(2); s.bind((source, 0))
        try:
            s.connect((destination, int(port))); got = "open"
        except OSError:
            got = "dropped"
        finally:
            s.close()
        say(got == expect, f"port {port} from {source}: {got}, as it should be" if got == expect
            else f"port {port} from {source}: {got}, where it should be {expect}")

role = sys.argv[1]
if role == "server": server()
elif role == "client": client(sys.argv[2])
elif role == "listen": listen(sys.argv[2:])
elif role == "probe": probe(sys.argv[2:])
