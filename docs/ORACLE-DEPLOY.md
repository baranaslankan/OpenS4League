# Deploying OpenS4L on Oracle Cloud Always Free

This puts the four servers, PostgreSQL, Redis and the public status page on one Oracle Cloud
**Always Free** Ampere (arm64) VM, for $0. You build on your own machine and copy the published
output to the VM, so the VM needs only Docker.

What the stack exposes and why is in `Server/Docker/README.md` ("Public deployment and status
page"). This guide covers the Oracle side and the order of the steps.

> **Status of this guide.** The Docker side (`compose.prod.yaml`, Caddy, the native arm64 game
> image, the load bot against a remote server) was tested on an Apple Silicon Mac. The Oracle
> console steps and the VM firewall behaviour (step 4) were not tested on a real Oracle VM yet.
> Facts about the free tier were checked against Oracle's
> [Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm)
> page in November 2026. Check it again before you start, because Oracle has changed these limits
> before.

## What Always Free gives you (and what it doesn't)

| | Always Free (Nov 2026) | This stack |
|---|---|---|
| Ampere A1 compute | 1,500 OCPU hours and 9,000 GB hours a month = **2 OCPUs, 12 GB** | about 0.5 GB RAM idle, under 1 GB with 40 bots |
| Block storage | 200 GB total, boot volumes included | one 50 GB boot volume |
| Outbound traffic | 10 TB a month | a few GB |
| Region | **home region only**, chosen at sign-up and never changeable | |

Things that can cost money or take the server away:

- **Do not upgrade to Pay As You Go.** After an upgrade Oracle charges for anything above the
  Always Free limits. A Free Tier account cannot be charged at all.
- **Idle instances can be reclaimed.** Oracle may reclaim an Always Free instance when, over 7
  days, its 95th-percentile CPU, its network use and (on A1) its memory use are **all below 20%**.
  A game server with nobody online fits that description. Plan for it: keep a database backup
  (step 9) and keep the steps below so you can recreate the VM. Generating artificial load to stay
  above the threshold works around the rule rather than within it; this guide does not do that.
- **"Out of host capacity"** when creating the instance means the region has no free A1 capacity
  at that moment. Retry later or in another availability domain. Do not upgrade the account to
  get around it.

## 1. Create the account

1. Sign up at [oracle.com/cloud/free](https://www.oracle.com/cloud/free/). A card is needed for
   identity verification. Free Tier accounts are not charged.
2. **Pick the home region carefully.** The free VM can only live there and you can't change it
   later. Choose the region closest to the people who will play.
3. Optional but recommended: *Billing & Cost Management → Budgets → Create budget* with an alert at
   $1, so any surprise charge emails you.

## 2. Create the VM

*Compute → Instances → Create instance:*

| Setting | Value |
|---|---|
| Image | Canonical **Ubuntu 24.04** (the aarch64 build is picked automatically for A1) |
| Shape | **VM.Standard.A1.Flex**, **2 OCPUs, 12 GB** memory |
| Networking | create a new VCN with a public subnet, **assign a public IPv4 address** |
| SSH keys | upload your public key (`~/.ssh/id_ed25519.pub`; create one with `ssh-keygen -t ed25519` if you have none) |
| Boot volume | default (50 GB) |

Note the instance's **public IP address**. It goes into `prod.env` as `PUBLIC_IP`, and the
commands below call it `<PUBLIC_IP>`.

Check that you can log in (the Ubuntu image's user is `ubuntu`):

```bash
ssh ubuntu@<PUBLIC_IP>
```

## 3. Open the ports in Oracle's firewall (security list)

*Networking → Virtual cloud networks → your VCN → Security → the subnet's security list → Add
ingress rules*, source CIDR `0.0.0.0/0` for each:

| Protocol | Ports | For |
|---|---|---|
| TCP | 22 | SSH (already there by default) |
| TCP | 80, 443 | status page (Caddy) |
| TCP | 28002 | auth |
| TCP | 28003 | chat |
| TCP | 28104 | game |
| TCP | 28005 | relay |
| UDP | 29000-29001 | relay (P2P) |

Do **not** open 5432 (Postgres), 6379 (Redis) or 22000 (WebApi). The prod stack doesn't publish
them anyway.

## 4. Prepare the VM

On the VM:

```bash
sudo apt-get update && sudo apt-get -y upgrade
sudo apt-get install -y ca-certificates curl make
```

Install Docker Engine and the Compose plugin from Docker's apt repository. Follow
[docs.docker.com/engine/install/ubuntu](https://docs.docker.com/engine/install/ubuntu/), section
"Install using the apt repository". Then let the `ubuntu` user run Docker and check the Compose
version (`compose.prod.yaml` needs **2.24 or newer**):

```bash
sudo usermod -aG docker ubuntu
exit
```

```bash
ssh ubuntu@<PUBLIC_IP>
docker compose version
```

**Host firewall.** Oracle's Ubuntu images ship their own iptables rules
(`/etc/iptables/rules.v4`) that reject incoming traffic except SSH. Docker inserts its own rules for
published container ports ahead of those, so the game ports usually work without touching that file.
Do not enable `ufw`: Docker bypasses it, and it gives a false sense of what is open. If a port is
open in the security list but unreachable, check that Docker's chains come before Oracle's REJECT
rule:

```bash
sudo iptables -L FORWARD -n --line-numbers | head
```

The `DOCKER-USER` / `DOCKER-FORWARD` rules should be listed above `REJECT`. If they are not,
`sudo systemctl restart docker` re-inserts them.

## 5. Copy the server to the VM

On your Mac, from the repository root, build and stage everything:

```bash
make -C Server/Docker publish
```

Copy the Docker folder and the published servers, keeping their relative layout. Compose mounts
`../opens4l/dist`, so both must sit under the same `Server/` folder on the VM:

```bash
rsync -azR --delete --exclude '.env' --exclude 'prod.env' Server/Docker/ Server/opens4l/dist/ ubuntu@<PUBLIC_IP>:opens4l/
```

**Game data.** The servers need the extracted client data in the `opens4l_clientdata` volume. It
stays on the server and is never served to players. Export it from your local volume, copy it over
and import it:

```bash
docker run --rm -v opens4l_clientdata:/d -v "$HOME":/out alpine tar czf /out/opens4l-clientdata.tgz -C /d .
```

```bash
scp ~/opens4l-clientdata.tgz ubuntu@<PUBLIC_IP>:
```

On the VM:

```bash
docker volume create opens4l_clientdata
docker run --rm -v opens4l_clientdata:/d -v "$HOME":/in alpine tar xzf /in/opens4l-clientdata.tgz -C /d
rm ~/opens4l-clientdata.tgz
```

Then delete the local copy on your Mac too (`rm ~/opens4l-clientdata.tgz`).

## 6. Configure and start

On the VM:

```bash
cd ~/opens4l/Server/Docker
cp prod.env.example prod.env
```

Fill in `prod.env`: `PUBLIC_IP`, plus a random value for each of `POSTGRES_PASSWORD`, `ADMIN_KEY`,
`ADMIN_PASSWORD` and `BOT_PASSWORD` (one `openssl rand -hex 24` each). Keep a copy of these
somewhere safe (a password manager), not in the repository.

```bash
make prod-config
make prod-up
make prod-provision
docker compose --env-file prod.env -f compose.yaml -f compose.prod.yaml ps
```

`make prod-config` fails with a clear message if a required value is missing. All seven services
(`postgres`, `redis`, `auth`, `chat`, `game`, `relay`, `caddy`) should be `Up`.

Check from your Mac:

```bash
curl http://<PUBLIC_IP>/api/metrics
```

and open `http://<PUBLIC_IP>/` for the status page. The architecture on the page should read
**Arm64**.

**HTTPS (optional).** Caddy gets a certificate automatically when `STATUS_SITE` is a host name that
points at the VM. Without a domain of your own, a wildcard DNS service such as
[sslip.io](https://sslip.io) gives one: `STATUS_SITE=<PUBLIC_IP with dashes>.sslip.io`, e.g.
`203-0-113-7.sslip.io`. Run `make prod-up` again after changing it. Certificate issuance for shared
domains like this can hit Let's Encrypt rate limits; plain HTTP on the IP keeps working either way.

## 7. Load test over the internet

On the VM, create the load-test accounts:

```bash
make prod-provision-bots BOTS=40
```

On your Mac, from `Server/Docker`, run the bots against the public server. `PASSWORD` is the
`BOT_PASSWORD` from `prod.env`:

```bash
make loadbot AUTH=<PUBLIC_IP>:28002 PASSWORD=<BOT_PASSWORD> SCENARIO=rooms ROOMS=10 ROOM_SIZE=4 USERPREFIX=bot STAY=60 REPORT=$PWD/status/loadtest.json
```

Unlike the local run, these numbers include the real network path from your machine to the
region. The `ping.game` row is the closest to what a player's connection feels like. Publish the
report on the status page:

```bash
scp status/loadtest.json ubuntu@<PUBLIC_IP>:opens4l/Server/Docker/status/
```

Note when and from where you ran it (home connection, distance to the region), and whether the
bots ran on the same machine as anything else heavy. The bots do their own RSA key exchange, which
inflates `*.connect` and `*.login` when many start at once.

## 8. Administer

The WebApi (22000) only listens on the VM's localhost. Reach it through an SSH tunnel:

```bash
ssh -N -L 22000:127.0.0.1:22000 ubuntu@<PUBLIC_IP>
```

With the tunnel open, `http://127.0.0.1:22000` on your Mac is the server's WebApi. Run the admin
console locally with the same `ADMIN_KEY` so its moderation actions are accepted
(`Tools/s4l-admin-console/README.md`):

```bash
cd Tools/s4l-admin-console
(cd web && pnpm install && pnpm run build)
ADMIN_KEY=<ADMIN_KEY> node server/server.js
```

It serves on `http://localhost:8020`. Its server cards and log tails read your local checkout, so
on a remote deployment only the WebApi-backed parts (players, rooms, moderation) show the VM.

Logs, on the VM:

```bash
cd ~/opens4l/Server/Docker
docker compose --env-file prod.env -f compose.yaml -f compose.prod.yaml logs -f game
```

## 9. Update, back up, stop

**Update** after code changes: on your Mac `make -C Server/Docker publish` and the same `rsync`
as in step 5, then on the VM `make prod-up` (`--build` is included, so a changed game image is
rebuilt too). The rsync excludes `prod.env`, so your secrets stay.

**Back up** the database (accounts, characters, inventories). On the VM:

```bash
docker exec opens4l-postgres pg_dumpall -U postgres | gzip > ~/opens4l-db-$(date +%F).sql.gz
```

Copy it off the VM (`scp ubuntu@<PUBLIC_IP>:opens4l-db-*.sql.gz .`), since a reclaimed instance
takes its disk with it. Restore into a fresh stack with
`gunzip -c <file> | docker exec -i opens4l-postgres psql -U postgres`.

**Stop** with `make prod-down` (keeps the database volume). To leave Oracle for good, terminate
the instance and its boot volume in the console.

## Troubleshooting

| Symptom | Check |
|---|---|
| `make prod-config` says `set ... in prod.env` | that value is empty in `prod.env` |
| `!override` / `!reset` error | Compose older than 2.24: install from Docker's repository (step 4) |
| Status page loads, shows "Game server unreachable" | `game` container logs; the game data volume (step 5) |
| A port is unreachable from outside | security list rule (step 3), then the iptables check in step 4 |
| Bots log in but never reach the game server | `PUBLIC_IP` in `prod.env` is the public address, not the 10.x private one; `make prod-up` after fixing it |
| `/api/admin/...` returns 404 through Caddy | intended. Admin routes are only reachable through the SSH tunnel |
