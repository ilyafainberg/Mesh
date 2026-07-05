# Self-hosting a Mesh relay

The Mesh relay is a small ASP.NET Core service that routes end-to-end-encrypted
messages between handles. It never sees message plaintext. Anyone can run their own
relay, and any Mesh client can point at it. This document explains how.

## What a relay does (and does not) see

- **Does not see**: message contents. Bodies are end-to-end encrypted to the
  recipient's device keys before they reach the relay.
- **Sees**: the handle directory (handle to device-public-key mappings), presence
  (who is connected), and routes ciphertext between handles. It stamps the
  authenticated sender on every message.

Running a relay does NOT give you access to anyone's messages. It is transport.

## Version compatibility

Relay and client share a registration protocol. Since v1.1.0 the relay requires a
signed proof-of-possession on handle registration (collision avoidance), so run a
client of v1.1.0 or newer against a v1.1.0 or newer relay. Older clients cannot
register on a v1.1.0 relay.

## Quick start (Docker)

```bash
# from the repo root
docker compose up mesh-relay
```

That starts a fully working relay on `http://localhost:8080` with in-memory storage
(single node, no free model). Point a Mesh client at `http://localhost:8080` (or the
machine's address on your network) in onboarding or Settings, Relay URL.

For anything public you should terminate TLS in front of it (a reverse proxy such as
Caddy, nginx, or a cloud load balancer) and give clients the `https://` URL. The
client uses secure WebSockets over the same URL.

## Run without Docker

The package ships self-contained binaries that need no .NET install. Pick your
platform folder under `bin/`:

```bash
# Linux
bin/linux-x64/run.sh            # or: PORT=9000 bin/linux-x64/run.sh

# Windows
bin\win-x64\run.cmd             # or: set PORT=9000 & bin\win-x64\run.cmd
```

Each folder holds a single self-contained executable; run it directly if you
prefer (`ASPNETCORE_URLS` controls the listen address).

## Configuration

All settings are environment variables (or the matching `Config:Key` in
`appsettings.json`). Everything is optional: with none set, the relay runs
in-memory, single node, with no hosted model.

| Env var | Purpose | Default |
|---|---|---|
| `ASPNETCORE_URLS` | Listen address | `http://+:8080` (Docker) |
| `COSMOS_CONNECTION` | Azure Cosmos connection string. When set, the handle registry, invites and offline inbox become durable. | in-memory |
| `COSMOS_DB` | Cosmos database name | `mesh` |
| `REDIS_CONNECTION` | Redis connection string. When set, presence, per-handle quota and cross-node message routing use Redis, so you can run multiple replicas. | in-memory |
| `MODEL_ENDPOINT` | OpenAI-compatible base URL for an optional hosted free model (OpenAI, Groq, Together, a local server, and so on). | none |
| `MODEL_API_KEY` | Key for `MODEL_ENDPOINT`. | none |
| `MODEL_NAME` | Model id to call. | `llama-3.3-70b-versatile` |
| `MODEL_DAILY_TOKEN_LIMIT` | Per-handle daily token budget for the free model. | `100000` |
| `MESH_MSG_RATE_PER_MIN` | Per-handle message rate limit (steady). | `120` |
| `MESH_MSG_BURST` | Per-handle burst capacity. | `30` |

If you do not set `MODEL_*`, the relay simply has no free model: clients on your
relay bring their own model key (or run one on-device), which is the recommended
setup for a private relay.

## Scaling

- **Single small relay**: the defaults are fine. In-memory state, one container.
- **Durable + multi-replica**: set both `COSMOS_CONNECTION` and `REDIS_CONNECTION`,
  then run as many replicas as you like behind a load balancer with sticky sessions
  (the SignalR WebSocket connection must stay on one replica). Redis handles presence
  and directed cross-replica message forwarding, so load stays proportional to
  delivered messages rather than fanning out to every node.

```bash
docker compose --profile redis up   # relay + Redis locally
```

## Health and metrics

- `GET /health` returns `{"status":"ok",...}`.
- `GET /metrics` returns aggregate counters (handles registered, messages routed,
  hosted-model calls, rate-limit rejections, connected count). No handles or PII are
  exposed, so it is safe to scrape.

## Pointing clients at your relay

Each user sets the Relay URL in the client:

- During onboarding: the model / relay screen.
- Later: Settings, Relay URL, then Reconnect relay.

A handle is registered per relay, so a user on your relay is independent from users
on any other relay. To message across relays, both parties must be on the same relay
(federation between relays is not implemented).

## Security notes

- Always put a public relay behind TLS.
- The relay authenticates every connection with a device-key challenge and verifies
  the signature on every message, so it asserts the real sender even though it cannot
  read message contents.
- A relay operator can see the handle directory and traffic metadata (who talks to
  whom, and when), but not message contents. Run your own relay if you want that
  metadata to stay with you.
