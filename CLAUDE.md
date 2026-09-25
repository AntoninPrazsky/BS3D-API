# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**BS3D-API** is the score service behind the online per-level leaderboards of BS3D (https://github.com/AntoninPrazsky/BS3D): every cleared level the game submits, ranked on two boards per level — this calendar month (UTC) and all time. It runs at the owner's home on a **Raspberry Pi 5 (8 GB)**, published self-contained for `linux-arm64`, reached from the internet only through **Cloudflare Tunnel** — no port is opened on the home router.

It is a separate repository from the game because it targets a different runtime (`linux-arm64`, where the game is `net10.0-windows`), builds on a different CI runner, and lives a different life: the service runs continuously across game releases, and neither may ever need the other to be released.

**The contract is the game's**: contract v1 is written in BS3D#542, and the game's client (BS3D#546, `Game/Online/` in that repository) and its settings (BS3D#548) are built and verified against it. Change the contract there first, then here. What both sides key a board by — the level's file, its `LevelIdentity` hash and `ScoreKeeper.RulesVersion` — is defined in the game's library (BS3D#549); the per-level ceiling table the service whitelists submissions against is written by the game's `Tools/ScoreSim --ceilings` and attached to every game release as `BS3D-<version>-ceilings.json`.

The work is tracked in this repository's issues: **#1** the service (minimal API over SQLite, boards as views over an append-only log), **#2** security (Cloudflare Tunnel, what the service must enforce because the client is open source), **#3** the Pi as host (systemd, backups, a release the Pi pulls).

## Build, test, run

```powershell
dotnet build BS3D.Api.slnx
dotnet test BS3D.Api.slnx

# Listens where the game's local runs expect it: Settings.json "server": "http://localhost:5000"
dotnet run --project src/BS3D.Api --urls http://localhost:5000

# What the Pi runs
dotnet publish src/BS3D.Api/BS3D.Api.csproj -c Release -r linux-arm64 --self-contained true -o publish
```

`.github/workflows/build.yml` builds, tests and publishes for `linux-arm64` on every push, on `ubuntu-latest` — free, because the repository is public.

## Conventions

The same as BS3D's, deliberately, since one owner and the same agents work on both:

- **Branches are the unit of work, and there are no pull requests.** A branch off `origin/main` named `<issue-number>-<slug>`, merged into `main` with `git merge --no-ff <branch> -m "Merge branch '<branch>': <what it does> (#issue)"`, pushed, and deleted locally and on the remote in the same breath.
- **Line endings are LF**, and `.gitattributes` says so.
- **Nothing secret is ever committed**: the tunnel's token, the salt for hashed addresses and the database live on the Pi only. The code is public on purpose — the security model (#2) assumes an attacker has read it.
- **Tests come with the behaviour**, unlike the game, which has none: a ranking can be wrong on every level while every number in it looks right (BS3D#173 is that failure in the game's own scoring), and the cases that catch it are cheap to write here.
- Comments and documents are load-bearing: change them in the same commit as the behaviour, and state a number only when it was measured.
- The owner communicates in Czech; code, comments and documents are in English.
