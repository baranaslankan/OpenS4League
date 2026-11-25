# Hosting OpenS4L on your own Mac (no cloud, no card)

The same prod stack as [ORACLE-DEPLOY.md](ORACLE-DEPLOY.md) (`compose.prod.yaml`, Caddy, the
status page), but running on an Apple Silicon Mac at home. Players reach it through
[Tailscale](https://tailscale.com), a private network between invited devices. There's no router
setup, it works behind CGNAT, and nothing is exposed to the open internet.

Cost: Tailscale's Personal plan is free (up to 6 users, non-commercial use). No card is needed at
any step below. If any sign-up page asks for one, stop.

The trade-offs: the Mac has to stay on and awake while people play, and only invited people can
join.

## 1. Start the stack (once)

From `Server/Docker`, with the client data volume filled (`make data SRC=...`):

```bash
make publish
cp prod.env.example prod.env
```

In `prod.env`:

- `PUBLIC_IP`: the Mac's Tailscale address (step 2). Until then the LAN address
  (`ipconfig getifaddr en0`) works for devices on the same Wi-Fi.
- `POSTGRES_PASSWORD=1234`: the value the dev stack already uses, since both share one database
  volume. Postgres is not published in prod, so it is only reachable inside Docker.
- A random `ADMIN_KEY`, `ADMIN_PASSWORD` and `BOT_PASSWORD`: one `openssl rand -hex 12` each.

```bash
make prod-up
make prod-secure     # admin and bot accounts made with the dev default "admin" get the prod.env passwords
```

`make up` switches back to the dev stack. Both use the same database, so accounts and characters
carry over.

## 2. Tailscale

1. On the Mac: install Tailscale (Mac App Store, or `brew install --cask tailscale`) and sign in
   with Google, Microsoft or GitHub.
2. Copy the Mac's Tailscale IPv4 address (the menu bar icon, or `tailscale ip -4`; it starts with
   `100.`), put it in `prod.env` as `PUBLIC_IP` and run `make prod-up` again. The servers now tell
   clients to connect to that address.
3. Each player installs Tailscale on their Windows PC. Invite them from the
   [admin console](https://login.tailscale.com/admin) (*Users → Invite*), or share only the Mac
   (*Machines → the Mac → Share*) so they can't see your other devices.

## 3. Accounts for players

The auth server has no sign-up page, so you create each player's account:

```bash
make prod-account NAME=alice
```

It prints the username and a new password once. Send them privately. Running it again for the same
name resets the password.

Players bring their own copy of the game client and point it at `<Mac Tailscale IP>:28002`, the
same way as for a local server. Never send them client files from this machine.

## 4. Status page and load test

- The status page is at `http://<Mac Tailscale IP>/` for everyone on the tailnet.
- To show it to people outside the tailnet, run a temporary Cloudflare Quick Tunnel
  (`brew install cloudflared`, then `cloudflared tunnel --url http://localhost:80`). It needs no
  account. The link changes on every start, and the tunnel is meant for demos, not uptime. Only
  the status page and the read-only `/api` routes go through it, never the game ports.
- Load test through the same path players use:

  ```bash
  make loadbot AUTH=<PUBLIC_IP>:28002 PASSWORD=<BOT_PASSWORD> SCENARIO=rooms ROOMS=10 ROOM_SIZE=4 USERPREFIX=bot STAY=60 REPORT=$PWD/status/loadtest.json
  ```

  Run from the same Mac, the numbers show server-side cost, not internet latency. For a real
  network number, run it from another machine on the tailnet.

## Before a play session

- Keep the Mac awake: `caffeinate -dis` in a terminal for the duration, or turn off sleep
  while it's on power.
- Docker Desktop is running, and `make prod-up` was run after any `make publish`.
- Back up first:
  `docker exec opens4l-postgres pg_dumpall -U postgres | gzip > ~/opens4l-db-$(date +%F).sql.gz`
