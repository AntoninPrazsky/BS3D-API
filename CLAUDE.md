# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**BS3D-API** is the score service behind the online per-level leaderboards of BS3D (https://github.com/AntoninPrazsky/BS3D): every cleared level the game submits, ranked on two boards per level — this calendar month (UTC) and all time. It runs at the owner's home on a **Raspberry Pi 5 (8 GB)**, published self-contained for `linux-arm64`, reached from the internet only through **Cloudflare Tunnel** — no port is opened on the home router.

It is a separate repository from the game because it targets a different runtime (`linux-arm64`, where the game is `net10.0-windows`), builds on a different CI runner, and lives a different life: the service runs continuously across game releases, and neither may ever need the other to be released.

**The contract is the game's**: contract v1 is written in BS3D#542, and the game's client (BS3D#546, `Game/Online/` in that repository) and its settings (BS3D#548) are built and verified against it. Change the contract there first, then here. What both sides key a board by — the level's file, its `LevelIdentity` hash and `ScoreKeeper.RulesVersion` — is defined in the game's library (BS3D#549); the per-level ceiling table the service whitelists submissions against is written by the game's `Tools/ScoreSim --ceilings` and attached to every game release as `BS3D-<version>-ceilings.json`.

The work is tracked in this repository's issues: **#1** the service (minimal API over SQLite, boards as views over an append-only log), **#2** security (Cloudflare Tunnel, what the service must enforce because the client is open source), **#3** the Pi as host (systemd, backups, a release the Pi pulls), **#4** hardening the Pi (the tunnel token kept off sudo's log, `deploy/cloudflared.service`, SSH, IPv6, sudo), **#5** the admin page (a separate process on the Pi's loopback; its groundwork is built: the separation tests and the refusals in the database), **#6** a backup that survives the Pi (to be done).

## Architecture (issue #1)

One ASP.NET Core minimal API, one SQLite file, no ORM.

- `Contracts.cs` — contract v1 as the wire carries it, and every refusal's reason code (`Reasons`). The game's copy is BS3D's `Game/Online/ScoreSubmission.cs`.
- `Endpoints.cs` — `POST /v1/scores`, `GET /v1/boards/{file}`, `PUT` and `DELETE /v1/players/{id}`, with every check issue #2 says the service must make: a known board (`Ceilings`), a score not over its ceiling, 1–4 stars, shots between the level's fewest and its budget, a duration no faster than `MinSecondsPerShot` per shot, a nickname by the rule, a well-formed game version, per-address and per-player rate limits (429 with `Retry-After`), and a token per player — trusted on first use, stored as its SHA-256, compared in constant time. A repeated `submissionId` is answered with its stored first answer and counts once. Every refusal is one log line with its reason and one entry for `Refusals`, and no line or entry holds a client's address: a rate-limited address is named by the same salted hash the audit column keeps. Nothing about an accepted submission is logged.
- `ScoreStore.cs` — the schema (applied at every start) and every query. **Boards are views over an append-only log**: a board is the best clear per visible player on `(level file, level hash, rules version)`, over one UTC month or all time, ranked by score and then by arrival (`rowid`), so ties go to the earlier submission. Only `players.name`, `players.hidden` and a submission's stored answer are ever updated in the log; `refusal_days` and `refusal_log` (schema 2) stand outside it: their counts are added to and their old rows deleted. Transactions are plain `BEGIN IMMEDIATE`/`COMMIT` SQL, because Microsoft.Data.Sqlite refuses commands without their `Transaction` set while a `SqliteTransaction` object is open.
- `Ceilings.cs` — every `BS3D-<version>-ceilings.json` in `Scores:CeilingsDirectory` (the union: old game versions stay in the wild). `Scores:RequireKnownBoard` is false in Development only, so a local run takes the test levels the game is pointed at with `levelfile=`.
- `Nicknames.cs` — 3–16 characters after NFC, trimming and closing runs of spaces; letters, digits, single spaces, `_`, `-`; not a denied name. The game is stricter (only letters its fonts draw).
- `Guards.cs` — tokens, the salted address hash (`CF-Connecting-IP` via the forwarded-headers middleware, trusted from loopback only), the rate windows.
- `Refusals.cs` — every refusal gathered in memory, and `RefusalFlusher`, which writes them every `Scores:RefusalFlushSeconds` and at stop, off the request path: all of them counted per UTC day and reason in `refusal_days`, the recent ones in `refusal_log` (as many as `Scores:RefusalQueueCapacity` per flush, the rest one "dropped" row), pruned to `Scores:RefusalLogDays` and `Scores:RefusalLogMaxRows`. What the admin page will show instead of the journal (#5), whose read access is every unit's lines.
- `AdminCli.cs` — `BS3D.Api admin hide-player|show-player|rename|export|backup|count`, run on the box. The service the tunnel reaches has no admin endpoint: the admin page (#5) will be a separate process, and `AdminSeparationTests` keep this one free of it.
- **Configuration** is the `Scores` section (`ScoresOptions`), overridden by environment variables (`Scores__AddressSalt`, …). Outside Development the service **refuses to start without `Scores:AddressSalt`** — an unsalted hash of an address is a register of addresses. The content root is the binary's own folder, not the working directory (`Program.cs`): the Pi starts the service in `/var/lib/bs3d-api`, and without its `appsettings.json` the defaults log every request, player ids in the paths included.

**Tests** (`tests/BS3D.Api.Tests`, xunit over `WebApplicationFactory`, a fresh database and a `FakeTimeProvider` per test) cover the ranking cases first — a month board that ignores last month's better score, a tie to the earlier submission, a hidden player gone from every count, a retried id counted once — then every refusal and the player endpoints. The ranking tests were seen to fail: flipping the tie order and dropping the month filter each failed exactly the test written for it. `ProcessTests` starts the built service as its own process, in Production, from a foreign working directory — the factory sets the content root itself and cannot show what the service does on its own; it was seen to fail on both of its assertions before the content root was fixed. `AdminSeparationTests` pin the public route table to the contract and refuse a web root's files and the antiforgery an admin form needs; mapping an admin route, serving a static admin file and adding antiforgery each failed them. `RefusalLogTests` write refusals through `RefusalFlusher`; writing on the request path, an unbounded queue, no pruning by age, no pruning by size, an address in a 429's detail, no flush at stop and a timer that never flushes each failed exactly the test written for it. `tests/deploy/tunnel-token.test.sh` tests what root runs on the Pi: `deploy/tunnel-token.sh` against a scratch folder, with stand-ins for `systemctl`, `journalctl` and `cloudflared` on `PATH`. It needs root, because the script refuses anyone else, and writes only to a temporary folder.

## Build, test, run

```powershell
dotnet build BS3D.Api.slnx
dotnet test BS3D.Api.slnx

# Listens where the game's local runs expect it: Settings.json "server": "http://localhost:5000"
dotnet run --project src/BS3D.Api --urls http://localhost:5000

# What the Pi runs
dotnet publish src/BS3D.Api/BS3D.Api.csproj -c Release -r linux-arm64 --self-contained true -o publish
```

The deploy scripts, on Linux only (the Pi, WSL or CI):

```bash
shellcheck deploy/*.sh tests/deploy/*.sh
sudo tests/deploy/tunnel-token.test.sh
```

`.github/workflows/build.yml` builds, tests (both suites, and shellcheck over the deploy scripts) and publishes for `linux-arm64` on every push, on `ubuntu-latest` — free, because the repository is public.

## Conventions

The same as BS3D's, deliberately, since one owner and the same agents work on both:

- **Branches are the unit of work, and there are no pull requests.** A branch off `origin/main` named `<issue-number>-<slug>`, merged into `main` with `git merge --no-ff <branch> -m "Merge branch '<branch>': <what it does> (#issue)"`, pushed, and deleted locally and on the remote in the same breath.
- **Line endings are LF**, and `.gitattributes` says so.
- **Nothing secret is ever committed**: the tunnel's token, the salt for hashed addresses and the database live on the Pi only. The code is public on purpose — the security model (#2) assumes an attacker has read it.
- **Tests come with the behaviour**, unlike the game, which has none: a ranking can be wrong on every level while every number in it looks right (BS3D#173 is that failure in the game's own scoring), and the cases that catch it are cheap to write here.
- Comments and documents are load-bearing: change them in the same commit as the behaviour, and state a number only when it was measured.
- The owner communicates in Czech; code, comments and documents are in English.
