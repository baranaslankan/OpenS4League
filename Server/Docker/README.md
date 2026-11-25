# OpenS4L — Docker stack

Runs the **OpenS4L .NET 10 server** (Auth, Chat, Game, Relay) plus its infra in Docker:
**PostgreSQL** (the DB), **Redis** (cache/messaging), and a one-shot **provisioner** that creates
the admin account and loads the free shop.

## What you need to run the backend against a real client

1. **Docker** (Desktop on Windows/macOS, or the engine + compose plugin on Linux).
2. **.NET 10 SDK** on the host — to build/publish the servers (`make publish`). Docker only runs
   them; it doesn't build them.
3. **A real S4 League client**, build **`0.8.32.26995`** (Season 8 / EU v1267), running on the host
   (Windows). The servers accept exactly this client version (`ClientVersions: ["0.8.32.26995"]`).
4. **The server's game-content data** in a Docker named volume (see *Client data volume* below).
   This is the extracted `.x7` files + `language/` tables the server loads at `/app/data` — it's
   the authoritative game data, extracted from the client's `resource.s4hd`.
5. The client and the servers must be able to reach each other. The stack publishes the server
   ports to `127.0.0.1` on the host, so a client on the same machine connects to `127.0.0.1`
   (`ServerList.Address: 127.0.0.1` in the configs).

## Ports (published to 127.0.0.1 on the host)

| Port | Service | Protocol |
|---|---|---|
| 28002 | Auth | TCP |
| 28003 | Chat | TCP |
| 28004 | Game | TCP |
| 28005 | Relay | TCP |
| 29000 / 29001 | Relay (peer) | UDP |
| 5432 | PostgreSQL | TCP |
| 6379 | Redis | TCP |

## Client data volume

The auth + game servers mount the game-content data at `/app/data`, and the provisioner reads it
at `/data` (for `xml/item.x7`). Instead of a host path, this uses a Docker **named volume**
`opens4l_clientdata` so the source location isn't hard-coded in `compose.yaml`.

Create and populate it once:

```bash
# from Server/Docker
./populate-data.sh /path/to/the/extracted/client-data
#   e.g.  ./populate-data.sh "C:\path\to\the\extracted\client-data"
#   or via make:  make data SRC=/path/to/the/extracted/client-data
```

`populate-data.sh` creates the `opens4l_clientdata` volume (if missing) and copies the directory
into it using a throwaway container — so the host path only ever appears in the command you run,
never in `compose.yaml`. The source is the extracted game data (a folder containing `xml/` and
`language/`), e.g. the game data extracted from the client's `resource.s4hd`.

To inspect what's in the volume:

```bash
docker run --rm -v opens4l_clientdata:/data alpine ls /data/xml
```

## Quick start (Windows: git-bash + make)

```bash
# 1. Build + publish the servers, stage the plugins
make publish

# 2. Populate the game-data volume (see "Client data volume" above)
./populate-data.sh /path/to/the/extracted/client-data

# 3. First-time bring-up: up + provision (admin/admin) + reload game
make bootstrap

# follow the game log
make logs
```

Login to the client with **admin/admin** (created by the provisioner). The shop is free &
permanent with correct client tabs.

Later runs: `make up` then `make provision` if you reset the DB.

## Public deployment and status page

`compose.prod.yaml` layers a public profile on top of `compose.yaml` (for example on an Oracle
Cloud Always Free Ampere VM; the game image runs natively on arm64). Step-by-step for Oracle,
including the cloud firewall, copying the build and game data, and a remote load test:
[`docs/ORACLE-DEPLOY.md`](../../docs/ORACLE-DEPLOY.md).

```sh
cp prod.env.example prod.env   # PUBLIC_IP, POSTGRES_PASSWORD, ADMIN_KEY, ADMIN_PASSWORD
make publish
make prod-config               # fails fast if prod.env is incomplete
make prod-up
make prod-provision
```

- Published: auth 28002, chat 28003, game 28104, relay 28005 + UDP 29000-29001, and Caddy on
  80/443. Postgres and Redis are not published; the WebApi (22000) stays on 127.0.0.1.
- The servers advertise `PUBLIC_IP` through `OPENS4L_*` environment variables (any config key
  can be overridden that way, e.g. `OPENS4L_ServerList__Address`), so `config.hjson` stays as is.
- `/admin/*` on the WebApi needs the `X-Admin-Key` header once `ADMIN_KEY` is set; the admin
  console sends it from its own `ADMIN_KEY` environment variable.
- Caddy serves `status/index.html` and passes only read-only routes through `/api`
  (`/statistics`, `/metrics`, `/channels`, `/gamedata/maps`). `/players` and `/rooms` stay
  private: they carry nicknames, account ids and room passwords.

The status page polls `/api/metrics` every 5 s: players, rooms per game mode, channel load and
the game server's CPU and memory. To show load-test latency on it, write a load-bot report next
to it (gitignored):

```sh
make loadbot SCENARIO=rooms ROOMS=10 ROOM_SIZE=4 USERPREFIX=bot STAY=60 REPORT=$PWD/status/loadtest.json
```

To preview the page against the local stack:

```sh
docker run --rm -d --name opens4l-caddy-dev --network opens4l_default -p 127.0.0.1:8088:80 \
  -v "$PWD/Caddyfile:/etc/caddy/Caddyfile:ro" -v "$PWD/status:/srv/status:ro" caddy:2-alpine
# open http://localhost:8088
```

## Layout

```
Docker/
  compose.yaml            # the whole stack (uses the opens4l_clientdata volume, not a host path)
  Makefile                # build/publish/data/up/bootstrap helpers
  populate-data.sh        # creates + fills the opens4l_clientdata volume from a data dir
  .env                    # postgres password (default 1234)
  init.sql                # creates the 'game' database (postgres image makes 'auth' via POSTGRES_DB)
  config/<server>/config.hjson   # per-server config; DB/Redis point at postgres/redis
  provision/              # admin account + free-shop provisioner (python + psycopg2)
  plugins/                # per-server plugin folders (staged by `make plugins`)
```

## How it works

- **Servers** run from the published output in `../opens4l/dist/<server>` on the
  `mcr.microsoft.com/dotnet/aspnet:10.0` runtime image. Each mounts its `config.hjson` (DB/Redis
  at the `postgres`/`redis` service names) and its own `plugins/` folder.
- **PostgreSQL** is initialised by the image (`POSTGRES_DB=auth`, user `postgres`) plus `init.sql`
  (creates the `game` database). The servers use **EF Core 10 + Npgsql** and create their schema on
  first start (`RunMigration: true`).
- **Game-data volume** `opens4l_clientdata` is mounted into auth + game at `/app/data` and into
  the provisioner at `/data`. Populate it with `./populate-data.sh` before first `up`.
- **Provisioner** waits for the schema, then creates `admin`/`admin` and loads every item from
  `data/xml/item.x7` into a free, permanent shop.

## Config notes

- The Docker `config/<server>/config.hjson` are copies of the source configs with: `Listener`
  bound to `0.0.0.0`, and DB/Redis hostnames set to `postgres` / `redis`. Connection strings use
  PostgreSQL syntax (`Host=postgres;Port=5432;Database=auth;Username=postgres;Password=1234;`).
  `ServerList.Address` stays `127.0.0.1` so the client connects to the host loopback.
- Passwords default to `1234` (see `.env`). If you change them, update the connection strings in
  the `config/*/config.hjson` files too.

## Current migration status / caveats

The server is a work-in-progress .NET 10 rebuild (see `../README.md` and the migration plan). It
now uses **PostgreSQL/Npgsql** (EF Core 10, regenerated migrations — old MySQL data was dropped,
as intended). Verified in Docker: the Auth server connects to PostgreSQL and **runs its EF Core
migrations** (creates `accounts`, `bans`, `login_history`, … in the `auth` DB), then boots until
it needs the game data. Known areas to watch when testing against a real client:

- **Game data required**: the servers boot the host and DB, but need the game-content volume
  (`opens4l_clientdata` with `xml/` + `language/`) to fully start. Without it they exit with a
  missing-file error — run `./populate-data.sh <data-dir>` first.
- **Relay UDP**: the relay publishes 29000/29001 as UDP. If in-room peer networking fails, the
  relay's UDP binding may need `network_mode: host` (add it to the `relay` service).
- The migration is not finished (ProudNet still on DotNetty, some old package versions remain).
  Runtime behaviour vs the old 2.1 stack isn't fully verified — this Docker setup is the harness.
