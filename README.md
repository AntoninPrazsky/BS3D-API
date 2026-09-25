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

### The tunnel (issue #2)

Nothing is opened on the router: `cloudflared` on the Pi connects **out** to Cloudflare, and Cloudflare sends the public hostname's requests down that connection to `http://127.0.0.1:5000`. It needs a domain whose DNS Cloudflare serves.

1. In the Cloudflare dashboard: **Zero Trust → Networks → Tunnels → Create a tunnel** (Cloudflared), name it `bs3d-api`, and copy the install command it shows — it carries the token.
2. On the Pi, install `cloudflared` from Cloudflare's Debian repository (the dashboard shows the lines for Debian arm64) and run the `sudo cloudflared service install <token>` it gave you.
3. Back in the dashboard, add a **public hostname** to the tunnel: e.g. `scores.<your domain>` → service `HTTP` → `127.0.0.1:5000`.
4. **Leave Bot Fight Mode off** for the domain: it challenges clients that are not browsers, and the game is one.
5. Optional but recommended: **Security → WAF → Rate limiting rules**, one rule for `POST` on `/v1/scores`, per IP. The service limits too.
6. From outside the home network (a phone hotspot): `curl https://scores.<your domain>/v1/health`.

Then the game's `OnlineScores.DefaultServer` (BS3D) gets `https://scores.<your domain>` in a release, and a player's build submits there.

To rotate the token: create a new one in the dashboard, `sudo cloudflared service uninstall`, then `sudo cloudflared service install <new token>`.

### Updating

```bash
sudo /opt/bs3d-api/current/deploy/update.sh latest       # or a tag: v0.2.0
```

It downloads the release and its checksum, verifies it, extracts it beside the old version, switches `current`, restarts and waits for `/v1/health` — and switches back if the new version does not answer. It prints the row counts before and after; they must be equal. The old version's folder stays until you delete it.

After a **game** release, fetch its ceiling table: `sudo /opt/bs3d-api/current/deploy/update-ceilings.sh` (it restarts the service when a new table arrived).

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

## Developing

See `CLAUDE.md`. `dotnet test BS3D.Api.slnx`; a local run the game can submit to is `dotnet run --project src/BS3D.Api --urls http://localhost:5000` with `"server": "http://localhost:5000"` in the game's `Settings.json`.
