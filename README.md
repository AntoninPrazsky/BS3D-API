# BS3D-API

The score service behind the online per-level leaderboards of [BS3D](https://github.com/AntoninPrazsky/BS3D): this month and all time, for every level. ASP.NET Core on .NET 10 over SQLite, running on a Raspberry Pi 5 behind Cloudflare Tunnel. The contract is BS3D#542's; how the code is laid out is in `CLAUDE.md`.

## Running it on a Raspberry Pi (issue #3)

Raspberry Pi OS **64-bit** (the release is `linux-arm64`, self-contained: nothing .NET is installed on the Pi).

### A fresh Pi

```bash
curl -fsSL https://raw.githubusercontent.com/AntoninPrazsky/BS3D-API/main/deploy/install.sh | sudo bash
```

It creates the `bs3d-api` user, `/var/lib/bs3d-api` (the database, the ceiling tables, the backups), a root-only `/etc/bs3d-api/env` with a freshly generated salt for hashed addresses, installs the newest release to `/opt/bs3d-api/<version>` with `/opt/bs3d-api/current` pointing at it, fetches every ceiling table the game's releases carry, and enables the service and the nightly backup. Then check it:

```bash
curl http://127.0.0.1:5000/v1/health        # {"status":"ok","contract":1,"schema":1,"boards":120}
systemctl status bs3d-api
journalctl -u bs3d-api -n 50
```

`boards` is how many levels the service will take clears for. **Zero means no ceiling table was found, and every submission will be refused** — run `sudo /opt/bs3d-api/current/deploy/update-ceilings.sh`.

### The tunnel (issues #2, #4)

Nothing is opened on the router: `cloudflared` on the Pi connects **out** to Cloudflare, and Cloudflare sends the public hostname's requests down that connection to `http://127.0.0.1:5000`. It needs a domain whose DNS Cloudflare serves. (That is the IPv4 picture; IPv6 has no NAT, see "Hardening the Pi".)

1. **`cloudflared` from Cloudflare's apt repository.** Check the key before trusting it: fingerprint `CC94 B39C 77AE 7342 A68B 8962 8A68 2D30 8D4E 5E73`, "CloudFlare Software Packaging 2025", the same on keys.openpgp.org and keyserver.ubuntu.com on 2026-10-01.
   ```bash
   curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg -o /tmp/cloudflare-main.gpg
   gpg --show-keys --with-fingerprint /tmp/cloudflare-main.gpg
   sudo install -m 0644 /tmp/cloudflare-main.gpg /usr/share/keyrings/cloudflare-main.gpg
   echo 'deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared any main' | sudo tee /etc/apt/sources.list.d/cloudflared.list
   sudo apt update && sudo apt install cloudflared
   ```
2. In the Cloudflare dashboard: **Networking → Tunnels → Create a tunnel**, named `bs3d-api`. It shows an install command that carries the token. **Do not run it**: `sudo cloudflared service install <token>` puts the token on sudo's command line, and sudo logs every command line to the journal (#4).
3. On the Pi, run `sudo /opt/bs3d-api/current/deploy/tunnel-token.sh` and paste the token at its prompt (or the whole command; the input is not shown). It writes `/etc/cloudflared/token` (root, 0600), installs `deploy/cloudflared.service` (the connector as a throwaway user, given the token as a systemd credential), enables it and waits for the tunnel to connect.
4. Back in the dashboard, on the tunnel's **Routes** tab: **Add route → Published application**, `scores.<your domain>` → `HTTP` → `127.0.0.1:5000`.
5. **Leave Bot Fight Mode off** for the domain: it challenges clients that are not browsers, and the game is one.
6. Optional but recommended: **Security → WAF → Rate limiting rules**, one rule for `POST` on `/v1/scores`, per IP. The service limits too.
7. From outside the home network (a phone hotspot): `curl https://scores.<your domain>/v1/health`.

Then the game's `OnlineScores.DefaultServer` (BS3D) gets `https://scores.<your domain>` in a release, and a player's build submits there.

**To rotate the token:** in the dashboard, the tunnel's **Overview**: the button **Rotate token**, in the right-hand column under the heading "Refresh token" (below the tables on a narrow window). Then **Add a replica** and copy the token; on the Pi, `sudo /opt/bs3d-api/current/deploy/tunnel-token.sh` again. Never run the install command the dashboard shows. The script refuses the token already in place, which is what it gets when Rotate token was not pressed. A refreshed token stops new connections with the old one but not the open ones, so the tunnel must list exactly one connector afterwards: this Pi. On a Pi where `service install` was used before, the same script replaces that unit and removes the daily root `cloudflared-update` timer it left behind. If the old token may have leaked, or another connector is listed, delete all of the tunnel's connections (Cloudflare's "Rotate a compromised token"): `DELETE https://api.cloudflare.com/client/v4/accounts/<account id>/cfd_tunnel/<tunnel id>/connections` with an API token that has Cloudflare Tunnel Write. The Pi, already on the new token, reconnects by itself.

### Updating

```bash
sudo /opt/bs3d-api/current/deploy/update.sh latest       # or a tag: v0.2.0
```

It downloads the release and its checksum, verifies it, extracts it beside the old version with every file owned by root and writable by root only (it refuses the version otherwise), switches `current`, restarts and waits for `/v1/health` — and switches back if the new version does not answer. It prints the row counts before and after; they must be equal. The old version's folder stays until you delete it.

After a **game** release, fetch its ceiling table: `sudo /opt/bs3d-api/current/deploy/update-ceilings.sh` (it restarts the service when a new table arrived).

**The system and `cloudflared`**, regularly: nothing else updates them (`unattended-upgrades` is not installed, and an apt-installed `cloudflared update` only says to use apt), and apt does not restart the running connector:

```bash
sudo apt update && sudo apt full-upgrade
sudo systemctl restart cloudflared      # or reboot, which a kernel or firmware upgrade wants anyway
```

### Backups and restoring

Every night at 03:30 (or at the next boot, if the Pi was off) `bs3d-api-backup.timer` writes a consistent copy of the live database to `/var/lib/bs3d-api/backups/scores-<time>.db` and deletes copies older than thirty days. **A copy on the same SD card does not survive the card**, so set an off-box target once:

```bash
echo 'BACKUP_OFFBOX_TARGET=you@desktop:/backups/bs3d-api/' | sudo tee /etc/bs3d-api/backup.env
```

(`rsync` over SSH, so the `bs3d-api` user needs a key the target accepts.) Run a backup by hand with `sudo systemctl start bs3d-api-backup` and read what it did with `journalctl -u bs3d-api-backup -n 20`.

To restore:

```bash
sudo systemctl stop bs3d-api
sudo cp /var/lib/bs3d-api/backups/scores-<time>.db /var/lib/bs3d-api/scores.db
sudo rm -f /var/lib/bs3d-api/scores.db-wal /var/lib/bs3d-api/scores.db-shm
sudo chown bs3d-api:bs3d-api /var/lib/bs3d-api/scores.db
sudo systemctl start bs3d-api
```

### Administration

On the Pi, as the service user and against the live database — there is no admin endpoint:

```bash
admin() { sudo -u bs3d-api env ASPNETCORE_ENVIRONMENT=Production Scores__Database=/var/lib/bs3d-api/scores.db Scores__AddressSalt=admin /opt/bs3d-api/current/BS3D.Api admin "$@"; }
admin count
admin hide-player <player id>       # off every board and every count; nothing deleted
admin show-player <player id>
admin rename <player id> <nickname>
admin export > export.jsonl
```

**The admin page** (#5, read-only): an overview (players, clears and refusals per day, the database, the newest backup), a live view of the newest clears and refusals, every board ranked as the game sees it (its hidden players apart), and every player with their clears, how many addresses they came from and whom they share one with (never the addresses themselves). It is a separate process on the Pi's loopback, never behind the tunnel, and lives as long as the terminal that started it:

```bash
cd / && sudo -u bs3d-api env Scores__Database=/var/lib/bs3d-api/scores.db Scores__CeilingsDirectory=/var/lib/bs3d-api/ceilings /opt/bs3d-api/current/BS3D.Api admin web
```

It prints a link that works once, for 5 minutes: open it in the Pi's own browser, or from the desktop through `ssh -L 5001:127.0.0.1:5001 rdt@<the Pi>`. It stops after 30 minutes without use, when the terminal closes, or with Ctrl+C. A shorter launcher comes with the next part of #5.

The service's log: `journalctl -u bs3d-api -f` (no sudo needed for a member of `adm`). It holds the start, the ceiling tables loaded and one entry per refusal, never an accepted submission or a client's address. An entry is two journal lines, `info: BS3D.Api.ScoreStore[0]` and then `Refused <method> <path>: <status> <reason> (<detail>)`, so `journalctl -u bs3d-api | grep Refused` lists the refusals. Since v0.1.4 the service also writes them to its database, for the admin page (#5): every refusal counted per day and reason in `refusal_days`, the last 7 days of them in `refusal_log`. On Raspberry Pi OS the journal lives in memory and is gone after a reboot.

### Hardening the Pi (issue #4)

What this Pi has beyond a fresh Raspberry Pi OS, each with its check:

- **SSH by key only.** `/etc/ssh/sshd_config.d/10-keys-only.conf` holds `PasswordAuthentication no` and `KbdInteractiveAuthentication no`. sshd keeps the first value it reads, and Raspberry Pi Imager's `50-cloud-init.conf` turns passwords on, so the file's name must sort before it. Apply with `sudo sshd -t && sudo systemctl reload ssh`, keeping the current session open until a new key login works. Check: `ssh -o PreferredAuthentications=none <user>@<the Pi>` answers `Permission denied (publickey)`.
- **rpcbind off.** Raspberry Pi OS installs it for NFS, and it listens on every address: `sudo systemctl disable --now rpcbind.service rpcbind.socket`. Check: `sudo ss -tulpn | grep -w 111` prints nothing, also after a reboot.
- **Inbound IPv6.** The Pi has a global IPv6 address, and sshd listens on `[::]`. Whether the router drops unsolicited inbound IPv6 can only be seen from outside: `curl -6 https://ifconfig.co/port/22` asks a public service to connect back to the asking address, and `"reachable": false` is the answer wanted. Nothing is meant to answer, so that service has no positive control here: it suggests, it does not prove (#4 item 5 holds the firewall for the Pi itself).
- **The sudo timestamp per terminal.** Raspberry Pi OS makes it global (`/etc/sudoers.d/010_global-tty`, from `raspberrypi-sys-mods`): after one `sudo` in any terminal, every process running as the same user could run `sudo -n` as root for 15 minutes. `/etc/sudoers.d/020_rdt-tty` (root, 0440, checked with `sudo visudo -cf` before it is installed) sorts after it and holds:
  ```
  Defaults:rdt timestamp_type=tty
  rdt ALL=(root) NOPASSWD: /opt/bs3d-api/current/deploy/update.sh, /opt/bs3d-api/current/deploy/update-ceilings.sh, /usr/local/sbin/bs3d-db-snapshot
  ```
  So a password counts only in the terminal it was typed in, and only these root-owned scripts, which `rdt` cannot change, run without one. `bs3d-db-snapshot` is this Pi's own: a consistent copy of the database into the caller's `~/bs3d-snapshots` for a SQLite browser. Check: after `sudo true` in one terminal, `sudo -n true` in another says a password is required, and `sudo -n -l` lists exactly those three. Without Yama (this kernel has none), a compromised account can still capture the password from its own shells; this closes the free path, not every path.

## Developing

See `CLAUDE.md`. `dotnet test BS3D.Api.slnx`, and for the deploy scripts `shellcheck deploy/*.sh tests/deploy/*.sh` and `sudo tests/deploy/tunnel-token.test.sh` (Linux); a local run the game can submit to is `dotnet run --project src/BS3D.Api --urls http://localhost:5000` with `"server": "http://localhost:5000"` in the game's `Settings.json`.
