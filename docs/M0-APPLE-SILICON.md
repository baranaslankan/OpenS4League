# M0 — Reproducible build on Apple Silicon

Scope: build, tests, Docker stack, provisioning, LoadBot and CI on an M3 Pro (macOS 26, arm64).
This does **not** show that the game is playable. That needs the real 2013 client (M1).

## Prerequisites

| Tool | Version verified | Notes |
|---|---|---|
| .NET SDK | 10.0.404 (osx-arm64) | Homebrew's `dotnet-sdk` cask is .NET 11, so use Microsoft's `dotnet-install.sh --channel 10.0` (installs per-user to `~/.dotnet`, no sudo) |
| Docker Desktop | Engine 27.5.1, Compose v2.32.4 | containerd image store (keeps the arm64 and amd64 variants side by side) |
| Python 3, make | system / Homebrew | the Makefiles use Python for `help` and plugin staging |

Shell setup used for every command below:

```bash
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:/Applications/Docker.app/Contents/Resources/bin:$PATH
```

The Docker.app `bin` folder supplies `docker-credential-desktop`. Without it, `docker compose build`
fails with "docker-credential-desktop: executable file not found" in shells that don't load Docker
Desktop's PATH.

## SharpLzo on ARM64 (verified, not assumed)

`SharpLzo 0.1.2` (wtfblub, **only on the MyGet feed `wtfblub`, not on nuget.org**) ships exactly
three native binaries:

| File | Format |
|---|---|
| `MiniLzo.so` | ELF 64-bit, **x86-64** |
| `MiniLzo_x64.dll` | PE32+, Windows x86-64 |
| `MiniLzo_x86.dll` | PE32, Windows i386 |

There is no linux-arm64 or macOS build. The only server call site is
`OpenS4L.Server.Game/Handlers/ResourceHandler.cs` (`NewShopUpdateCheckReq` → `CompressLZO`), which
compresses the shop tables before sending them to the client.

A probe that references the repo's own `OpenS4L.Network` and round-trips 100 kB through
`CompressLZO`/`DecompressLZO` gave:

| Runtime | Result |
|---|---|
| macOS 26 arm64 (host) | **FAIL**: `TypeInitializationException` in `SharpLzo.MiniLzo` |
| `mcr.microsoft.com/dotnet/aspnet:10.0` linux/arm64 | **FAIL**: same exception |
| `mcr.microsoft.com/dotnet/aspnet:10.0` linux/amd64 (emulated on M3) | **OK**: 100000 → 1221 bytes, roundtrip equal |

**Chosen mitigation (no code or protocol change):** `platform: linux/amd64` on the `game` service
in `Server/Docker/compose.yaml`. Game runs emulated (`uname -m` = `x86_64`). Auth, Chat and Relay
stay native arm64 because they never call LZO. On x64 hosts the setting changes nothing.

Consequences:
- Running the **Game server natively on macOS** (`dotnet run`) will throw the first time a client
  requests shop data. Use the Docker stack for Game.
- Emulation costs some CPU. Fine for development; production should be a linux/amd64 host.
- No existing test calls LZO, which is why the suite is green on macOS.

Alternatives considered and **not** done in M0 (each needs a decision):
1. A managed LZO1X implementation in place of SharpLzo. The repo's own C# MiniLZO port
   (`Tools/.../MiniLzo.cs`) is **GPLv2+**, so linking it into the MIT server has licensing
   implications. The output must also stay byte-compatible with what the client accepts
   (protocol surface).
2. Building `minilzo` for linux-arm64/osx-arm64 and shipping the native lib ourselves: GPL code
   plus a native build pipeline.
3. Mirroring `SharpLzo 0.1.2` into a feed we control, so restore no longer depends on a personal
   MyGet feed. Recommended soon; it is a supply-chain fix, not an ARM fix.

## Client data used for the local stack

The bootstrap needs `xml/*.x7` and `language/xml/*.x7` from a client. A local 2014 resource dump
(not in the repo, not committed, only copied into the local Docker volume `opens4l_clientdata`)
was used: just the 17 files the servers and provisioner read.

**Data-version issue found:** the 2014 `item.x7` has `sex="all"` on 7 items (4033000, 4033001,
4033002, 4033004, 4033006, 4051322, 4051323). `GameDataService.ParseGender` only accepts
`unisex`/`man`/`woman`, so with this data the Game server crash-loops on startup
(`System.Exception: Invalid gender all`). For M0 only, the **staged copy** was patched
(`all` → `unisex`). No server code was changed. M1 needs data extracted from the actual 2013
`0.8.32.26995` client, which should not need the patch. Also with this data:
`start_items: no starter items found` (new characters get the default dagger and skill fallback),
and 2 weapons have no `_eu_weapon.x7` entry (2000036, 2040007).

## Steps (as executed)

```bash
make -C Server build                    # 0 errors, 427 warnings (pre-existing)
make test                               # 316 passed, 0 failed, 0 skipped (incl. 7 Postgres tests)
cp Server/Docker/.env.example Server/Docker/.env   # only if 5432 is taken; set POSTGRES_HOST_PORT
make -C Server/Docker data SRC=/path/to/extracted-client-data
make -C Server/Docker bootstrap
docker compose -f Server/Docker/compose.yaml ps
make -C Server/Docker provision-bots BOTS=10
make -C Server/Docker loadbot SCENARIO=chat BOTS=10 USERPREFIX=bot STAY=45
make -C Server/Docker loadbot SCENARIO=single-channel BOTS=10 USERPREFIX=bot STAY=10
```

## LoadBot notes

- `chat` with 10 bots: each bot did the ProudNet handshake, auth login, read the server list, game
  login, `CharacterFirstCreate` (nickname), entered a channel, logged into chat and sent channel
  messages. Exit 0, "10/10 bot(s) reached their channel". The chat server's `/statistics`
  `MessagesSent` equalled exactly the number of `MessageChatReq` sends in the bot log (130).
- Upstream quirk: `MESSAGES=` is ignored by the `chat` scenario (`Program.cs` builds
  `ChatScenario` without the message count), so bots chat every ~3 s until `STAY` ends. Only
  `all-channels-chat` honours it. Left unchanged (test tooling, out of M0 scope).
- The LoadBot never creates rooms, plays matches, uses the relay or opens the shop, so those paths
  remain unverified (M1).

## CI

`.github/workflows/server-ci.yml`: build + test on `ubuntu-latest` (x64, full suite) and
`macos-latest` (ARM64, `FullyQualifiedName!~PostgresTests`, because GitHub's macOS runners have
no Docker). The 7 excluded tests run on the ubuntu job and locally.

First run on the fork (commit `d3ef582`, run 45403827379): **success**
- ubuntu-x64 (`uname -m` x86_64): build 0 errors; tests 316 passed / 0 failed / 0 skipped
- macos-arm64 (`uname -m` arm64): build 0 errors; tests 309 passed / 0 failed / 0 skipped
