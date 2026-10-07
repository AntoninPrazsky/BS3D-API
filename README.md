# BS3D-API

The score service behind the online per-level leaderboards of [BS3D](https://github.com/AntoninPrazsky/BS3D): this month and all time, for every level. ASP.NET Core on .NET 10 over SQLite, running on a Raspberry Pi 5 behind Cloudflare Tunnel. The contract is BS3D#542's; how the code is laid out is in `CLAUDE.md`.

## Running it on a Raspberry Pi (issue #3)

Raspberry Pi OS **64-bit** (the release is `linux-arm64`, self-contained: nothing .NET is installed on the Pi).

**What it costs the Pi** (a Raspberry Pi 5 with 8 GB, v0.1.12, measured 2026-10-02 with 2 players, 15 clears and 148 boards; a board's cost grows with its clears):
- **Idle:** 0.02 % of one core over a minute, and about 100 MB resident (109 MB after the 600 requests below).
- **Requests on loopback, 200 of each:**

| Request | Median | 95th percentile | Service CPU per request |
|---|---|---|---|
| One board, `GET /v1/boards/{file}` | 1.9 ms | 3.0 ms | 3.0 ms |
| Every board, `GET /v1/boards` | 1.6 ms | 2.3 ms | 2.0 ms |
| `GET /v1/health` | 0.6 ms | 1.1 ms | 0.8 ms |

Through the tunnel the round trip to Cloudflare's edge comes on top.

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

Nothing is opened on the router: `cloudflared` on the Pi connects **out** to Cloudflare, and Cloudflare sends the public hostname's requests down that connection to `http://127.0.0.1:5000`. It needs a domain whose DNS Cloudflare serves. (That is the IPv4 picture; IPv6 has no NAT, and the Pi's own firewall covers it: see "Hardening the Pi".)

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
6. Optional but recommended: **Security → WAF → Rate limiting rules**, one rule for `POST` on `/v1/scores` and `/v1/notes`, per IP. The service limits too.
7. From outside the home network (a phone hotspot): `curl https://scores.<your domain>/v1/health`.

Then the game's `OnlineScores.DefaultServer` (BS3D) gets `https://scores.<your domain>` in a release, and a player's build submits there.

**To rotate the token:** in the dashboard, the tunnel's **Overview**: the button **Rotate token**, in the right-hand column under the heading "Refresh token" (below the tables on a narrow window). Then **Add a replica** and copy the token; on the Pi, `sudo /opt/bs3d-api/current/deploy/tunnel-token.sh` again. Never run the install command the dashboard shows. The script refuses the token already in place, which is what it gets when Rotate token was not pressed. A refreshed token stops new connections with the old one but not the open ones, so the tunnel must list exactly one connector afterwards: this Pi. On a Pi where `service install` was used before, the same script replaces that unit and removes the daily root `cloudflared-update` timer it left behind. If the old token may have leaked, or another connector is listed, delete all of the tunnel's connections (Cloudflare's "Rotate a compromised token"): `DELETE https://api.cloudflare.com/client/v4/accounts/<account id>/cfd_tunnel/<tunnel id>/connections` with an API token that has Cloudflare Tunnel Write. The Pi, already on the new token, reconnects by itself.

### Updating

```bash
sudo /opt/bs3d-api/current/deploy/update.sh latest       # or a tag: v0.2.0
```

It downloads the release and its checksum, verifies it, extracts it beside the old version with every file owned by root and writable by root only (it refuses the version otherwise), puts the release's systemd units in place (`bs3d-api.service` and the backup's two, root's and 0644, the ones they replace kept aside), switches `current`, restarts and waits for `/v1/health` — and switches back, units included, if the new version does not answer. It prints the row counts before and after; they must be equal. The old version's folder stays until you delete it. Up to v0.1.8 the script left the units alone, so the update that brings v0.1.9 or later runs that old script: run it once more for the same version, which puts that version's units in place.

After a **game** release, fetch its ceiling table: `sudo /opt/bs3d-api/current/deploy/update-ceilings.sh` (it restarts the service when a new table arrived).

**The system and `cloudflared`**, regularly: nothing else updates them (`unattended-upgrades` is not installed, and an apt-installed `cloudflared update` only says to use apt), and apt does not restart the running connector:

```bash
sudo apt update && sudo apt full-upgrade
sudo systemctl restart cloudflared      # or reboot, which a kernel or firmware upgrade wants anyway
```

### Backups and restoring

Every night at 03:30 (or at the next boot, if the Pi was off) `bs3d-api-backup.timer` writes a consistent copy of the live database to `/var/lib/bs3d-api/backups/scores-<time>.db` and deletes copies older than thirty days. **A copy on the Pi's own disk does not survive the disk** (#6), so each copy also goes to a microSD card in the Pi's slot, or a USB drive. Set the card up once. **It erases the card:**

```bash
lsblk
sudo /opt/bs3d-api/current/deploy/backup-card.sh /dev/mmcblk0
```

(`lsblk` shows the card: in the slot it is `mmcblk0`.) What the setup does:

- **It guards the other disks.** It takes only a whole disk in the SD slot or on USB. It refuses a disk with anything mounted, and the disk the system runs from. It erases nothing until the card's name is typed back.
- **It formats and mounts the card:** ext4, labelled `bs3d-backup`, mounted at `/mnt/bs3d-backup` through `/etc/fstab` with `nofail`, so a missing card never stops the Pi booting. The fstab from before is kept as `/etc/fstab.before-bs3d-backup`.
- **It points the backup at the card:** `BACKUP_OFFBOX_MOUNT` and `BACKUP_OFFBOX_TARGET` go into `/etc/bs3d-api/backup.env`. Then it runs one backup and shows the result.

From then on every nightly copy also goes to `/mnt/bs3d-backup/bs3d-api/`. Copies there are kept a year; `BACKUP_OFFBOX_KEEP_DAYS` in `backup.env` changes that. With the card pulled, the run fails, rather than writing into the empty mount point on the NVMe and calling it a success.

**Each run's result is recorded** in `/var/lib/bs3d-api/backups/last-offbox`, which, unlike the journal, survives a reboot. The admin page's overview shows how old the newest copy off the box is. It warns when none is set up, when the last copy failed, or when the newest one is over two days old. `--remove` stops using the card; the copies on it stay.

**Boot order:** the Pi 5 looks for a system on a card in the slot before the NVMe (`BOOT_ORDER=0xf461` here). This card holds none, so the Pi boots from the NVMe as before. Never leave a card with Raspberry Pi OS on it in the slot.

**What the card does not cover:** it survives the NVMe failing, not the Pi lost or the house burnt. The copy off the site, below, covers those. A target over SSH also works for `BACKUP_OFFBOX_TARGET` (`user@host:/backups/bs3d-api/`, by `rsync`, with a key for the `bs3d-api` user the host accepts), but not to Windows, which has no `rsync`.

#### The copy off the site

Every other day the newest copy also leaves the house, encrypted on the Pi by restic, for a restic repository over SFTP: on the owner's web hosting, in an account of its own that sees only its folder, so the host holds nothing it can read. Set it up once:

```bash
sudo apt install restic
sudo /opt/bs3d-api/current/deploy/backup-offsite.sh 'sftp://<account>@<host>:<port>//<folder>'
```

- **Two passwords:** the hosting account's, used once to put the Pi's key on the account, and the repository's, which encrypts every copy. **Keep both, and the address above, somewhere other than the Pi**, a password manager say: with the Pi gone, they are what reads the copies. The script reads each from a root-only file when it is there (`/etc/bs3d-api/offsite-sftp-password`, `/etc/bs3d-api/offsite-restic-password`), asks for it when it is not, and deletes the files once it has worked.
- **A key of its own:** it records the host's keys, trusted on first use (it prints their fingerprints). It makes an ed25519 key and puts its public half where the hosting's ProFTPD `mod_sftp` reads it: `.sftp/authorized_keys` in the account's folder, in RFC 4716 form. From then on only the key logs in, and a host whose key has changed is refused.
- **Root's files, the backup's for its run:** the key, the host's keys and the repository's password go in `/etc/credstore/bs3d-api-offsite.*`, root's and 0600. `bs3d-api-backup.service` imports them (`ImportCredential=`) for its run alone, so the public service, which runs as `bs3d-api` too, never has them. `BACKUP_OFFSITE_REPOSITORY` goes in `backup.env`. Then it runs one backup and shows the result.

A copy goes when the last good one is two days old, less half a day for the timer's jitter, so a failed one is tried again the next night; `BACKUP_OFFSITE_EVERY_DAYS` in `backup.env` changes the days. The repository keeps a copy a day for 30 days and one a month for 12 months (restic also keeps the oldest while there are fewer). Each result is recorded in `/var/lib/bs3d-api/backups/last-offsite`, and the admin page's overview shows it beside the card's. It warns when none is set up, when the last copy failed, or when the newest is older than its days and one more. Each copy is tried whether the other worked or not, and either failing fails the run. `--remove` stops the copies; the repository on the host stays.

Run a backup by hand with `sudo systemctl start bs3d-api-backup`. Read what it did with `journalctl -u bs3d-api-backup -n 30`, `sudo cat /var/lib/bs3d-api/backups/last-offbox` and `sudo cat /var/lib/bs3d-api/backups/last-offsite`.

To restore:

```bash
sudo systemctl stop bs3d-api
sudo cp /var/lib/bs3d-api/backups/scores-<time>.db /var/lib/bs3d-api/scores.db   # or from /mnt/bs3d-backup/bs3d-api/
sudo rm -f /var/lib/bs3d-api/scores.db-wal /var/lib/bs3d-api/scores.db-shm
sudo chown bs3d-api:bs3d-api /var/lib/bs3d-api/scores.db
sudo systemctl start bs3d-api
```

From the copy off the site, on any machine with restic: ssh asks for the account's password, and restic for the repository's. The copy lands in `restored/var/lib/bs3d-api/backups/`; then restore it as above.

```bash
restic -r 'sftp://<account>@<host>:<port>//<folder>' snapshots
restic -r 'sftp://<account>@<host>:<port>//<folder>' restore latest --target restored
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
admin notes                         # every note players sent from the game (#10), one JSON line each, oldest first
admin notes --after 41 --out /tmp/notes   # the notes after id 41, their pictures written as <id>.jpg (a folder bs3d-api may write)
admin delete-note <note id>         # a note and its picture, gone: anything that should not be on this disk goes at once
```

A note's picture is a file in `/var/lib/bs3d-api/note-pictures/`, not in the database, so **no backup copies the pictures**: read the notes within days. At most 512 MB of them are kept (`Scores__NotesMaxStoredPictureBytes`), and at most 5,000 notes (`Scores__NotesMaxStored`); past either, delete old notes.

**The admin page** (#5, read-only): an overview (players, clears, unfinished attempts and refusals per day, the database, the newest backup), a live view of the newest submissions and refusals, charts of how the players, the clears, the boards cleared, the time played and the refusals grow day by day (14 days, 90 days or all, drawn on the server with no script), a funnel of how many players cleared each level in play order, every board ranked as the game sees it with its scores charted against the ceiling (an unfinished attempt marked as one, its hidden players apart, the list in play order and chapter by chapter once the game's ceiling tables name chapters), and every player with their clears, how many addresses they came from and whom they share one with (never the addresses themselves). It works on a phone's width too, light or dark after the system's setting. On this Pi's own desktop, the **BS3D admin** icon runs `~/.local/bin/bs3d-admin-desktop` (the Pi's own, not in this repository): `bs3d-admin` in a terminal window, its link opened in the browser, and the page stopped when the window closes or with Ctrl+C. It is a separate process on the Pi's loopback, never behind the tunnel, and lives as long as the terminal that started it. Once, install its launcher: `bs3d-admin`, the runner it starts as `bs3d-api`, and the sudoers rule that lets the account running this start it without a password (`--remove` takes all three away):

```bash
sudo /opt/bs3d-api/current/deploy/install-admin.sh
```

Then, in a terminal on the Pi, or from the desktop in one line:

```bash
bs3d-admin                       # or: bs3d-admin --port 5002
ssh -t -o ExitOnForwardFailure=yes -L 127.0.0.1:5001:127.0.0.1:5001 rdt@<the Pi> bs3d-admin
```

It prints a link that works once, for 5 minutes: open it in the Pi's own browser, or in the desktop's while that ssh runs. One page runs per port: a second one, say over ssh while one is open on the Pi, says so and stops, and `--port 5002` starts another (from the desktop with `-L 127.0.0.1:5002:127.0.0.1:5002`). It stops after 30 minutes without use, when the terminal closes, or with Ctrl+C. No password is the choice made in #5: any process running as `rdt` can start the page and read every player through it, and in return starting it never authenticates sudo, so it never leaves a timestamp that makes anything root. The runner passes on nothing but `--port N`, into an environment of its own. `update.sh` says when a release carries a different launcher, and never installs one itself.

The service's log: `journalctl -u bs3d-api -f` (no sudo needed for a member of `adm`). It holds the start, the ceiling tables loaded and one entry per refusal, never an accepted submission or a client's address. An entry is two journal lines, `info: BS3D.Api.ScoreStore[0]` and then `Refused <method> <path>: <status> <reason> (<detail>)`, so `journalctl -u bs3d-api | grep Refused` lists the refusals. Since v0.1.4 the service also writes them to its database, for the admin page (#5): every refusal counted per day and reason in `refusal_days`, the last 7 days of them in `refusal_log`. On Raspberry Pi OS the journal lives in memory and is gone after a reboot.

### Hardening the Pi (issue #4)

What this Pi has beyond a fresh Raspberry Pi OS, each with its check:

- **SSH by key only.** `/etc/ssh/sshd_config.d/10-keys-only.conf` holds `PasswordAuthentication no` and `KbdInteractiveAuthentication no`. sshd keeps the first value it reads, and Raspberry Pi Imager's `50-cloud-init.conf` turns passwords on, so the file's name must sort before it. Apply with `sudo sshd -t && sudo systemctl reload ssh`, keeping the current session open until a new key login works. Check: `ssh -o PreferredAuthentications=none <user>@<the Pi>` answers `Permission denied (publickey)`.
- **rpcbind off.** Raspberry Pi OS installs it for NFS, and it listens on every address: `sudo systemctl disable --now rpcbind.service rpcbind.socket`. Check: `sudo ss -tulpn | grep -w 111` prints nothing, also after a reboot.
- **Inbound IPv6.** The Pi has a global IPv6 address, and sshd listens on `[::]`. Whether the router drops unsolicited inbound IPv6 can only be seen from outside: `curl -6 https://ifconfig.co/port/22` asks a public service to connect back to the asking address, and `"reachable": false` is the answer wanted. Nothing is meant to answer, so that service has no positive control here: it suggests, it does not prove.
- **The Pi's own firewall** (#4 item 5), because a router's IPv6 filtering can change with a firmware update or a UPnP/PCP pinhole. nftables drops every inbound packet except these:
  - SSH (22), VNC (5900) and mDNS (5353) from the home network: its IPv4 prefix, IPv6 link-local and its IPv6 prefixes;
  - ICMP, which IPv6 needs to work;
  - DHCP answers;
  - the answers to what the Pi asked for itself, the tunnel included.

  Nothing is forwarded, and outbound is free. `deploy/firewall.sh` writes `/etc/nftables.conf` from `deploy/nftables.conf`, filling in the prefixes of the network the default route leaves by. It checks the result with `nft -c` and arms an undo before loading it.

  Connections already open (the tunnel, an SSH or VNC login, Claude Code) are new to the kernel's connection tracking at that moment. So for 5 seconds the script lets conntrack watch them while nothing is dropped, and only then do the rules take over, finding them established. Loaded straight away, as v0.1.14 loaded them on 2026-10-02, the rules cut them: the tunnel lost every connection for about 7 minutes, and Claude Code on the Pi could not reach its API.
  ```bash
  sudo /opt/bs3d-api/current/deploy/firewall.sh              # load, flushed again in 3 minutes unless confirmed
  sudo /opt/bs3d-api/current/deploy/firewall.sh --confirm    # from a NEW SSH login: keep, and load at every boot
  sudo /opt/bs3d-api/current/deploy/firewall.sh --remove     # flush, stop loading at boot, put the old file back
  ```
  Run it again when the home network's prefixes change. Check: `sudo nft list ruleset` shows `policy drop`, also after a reboot, and the desktop still gets in over SSH (and VNC).
- **The sudo timestamp per terminal.** Raspberry Pi OS makes it global (`/etc/sudoers.d/010_global-tty`, from `raspberrypi-sys-mods`): after one `sudo` in any terminal, every process running as the same user could run `sudo -n` as root for 15 minutes. `/etc/sudoers.d/020_rdt-tty` (root, 0440, checked with `sudo visudo -cf` before it is installed) sorts after it and holds:
  ```
  Defaults:rdt timestamp_type=tty
  rdt ALL=(root) NOPASSWD: /opt/bs3d-api/current/deploy/update.sh, /opt/bs3d-api/current/deploy/update-ceilings.sh, /usr/local/sbin/bs3d-db-snapshot
  ```
  So a password counts only in the terminal it was typed in, and only these root-owned scripts, which `rdt` cannot change, run without one. `bs3d-db-snapshot` is this Pi's own: a consistent copy of the database into the caller's `~/bs3d-snapshots` for a SQLite browser. Check: after `sudo true` in one terminal, `sudo -n true` in another says a password is required, and `sudo -n -l` lists exactly those three, and with the admin page's launcher installed, `(bs3d-api) NOPASSWD: /usr/local/libexec/bs3d-admin-run`. Without Yama (this kernel has none), a compromised account can still capture the password from its own shells; this closes the free path, not every path.
- **The service apart from the admin page** (#5), which also runs as `bs3d-api`.
  - `bs3d-api.service` has `InaccessiblePaths=/dev/pts`, so the service cannot open the pseudo-terminal sudo gives the page, which belongs to `bs3d-api`, and write to the owner's terminal.
  - The page makes itself undumpable, so the service cannot ptrace it or read its memory.
  - `PrivatePIDs=yes` was in the unit from v0.1.9 to v0.1.14 and is gone since v0.1.15. systemd 257 took the service for a process not its own and stopped it with SIGKILL at once in 3 of 6 stops, skipping the graceful shutdown. What it added was that the page could be neither seen nor signalled, which protects nothing the service cannot read for itself.

  Check: `systemctl show bs3d-api -p InaccessiblePaths` says `/dev/pts`.

## Developing

See `CLAUDE.md`. What the service answers is contract v1 (BS3D#542), plus `GET /v1/boards` since #7, the summary of every board the game's High Scores screen asks for, since #8 unfinished attempts (0 stars), ranked below every clear and shown only for a player who has never cleared the board, and since #10 `POST /v1/notes`, a player's note from the game's Send a Note (BS3D#813) with its context and, unless they unticked it, a JPEG of the frame. `dotnet test BS3D.Api.slnx`, and for the deploy scripts `shellcheck deploy/*.sh tests/deploy/*.sh`, `sudo tests/deploy/tunnel-token.test.sh`, `sudo tests/deploy/install-admin.test.sh`, `sudo tests/deploy/update.test.sh`, `sudo tests/deploy/firewall.test.sh`, `sudo tests/deploy/backup.test.sh`, `sudo tests/deploy/backup-card.test.sh` and `sudo tests/deploy/backup-offsite.test.sh` (Linux; the backup's two need restic and sftp-server; all but the first also run without sudo, as `unshare -r`, and the firewall's as `unshare -rn`); a local run the game can submit to is `dotnet run --project src/BS3D.Api --urls http://localhost:5000` with `"server": "http://localhost:5000"` in the game's `Settings.json`.
