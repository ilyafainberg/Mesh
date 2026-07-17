# Self-hosting a Mesh relay

The Mesh relay is a small ASP.NET Core service that routes end-to-end-encrypted
messages between handles. It never sees message plaintext. Anyone can run their own
relay, and any Mesh client can point at it. This document explains how.

## What a relay does (and does not) see

- **Does not see**: message contents. Bodies are end-to-end encrypted to the
  recipient's device keys before they reach the relay.
- **Sees**: the handle directory (handle to device-public-key mappings), presence
  (who is connected), and routes ciphertext between handles. For a fan-out it also
  sees the sender, transient recipient cohort, timing, and ciphertext size. It stamps
  the authenticated sender on every message.

A fan-out carries one ciphertext and 1 to 128 transient recipient handles. The relay
does not persist a group or cohort object; it clones ordinary envelopes and stores only
unavoidable per-recipient offline inbox records. Repeated cohorts can permit group
inference. Mesh does not claim traffic-analysis resistance.

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

All settings are environment variables (or the matching key in
`appsettings.json`). Everything is optional: with none set, the relay runs
in-memory, single node, with no hosted model.

| Env var | appsettings key | Purpose | Default |
|---|---|---|---|
| `ASPNETCORE_URLS` | standard ASP.NET Core | Listen address | `http://+:8080` (Docker) |
| `COSMOS_CONNECTION` | `Cosmos:Connection` | Azure Cosmos connection string. Makes handles, rate policies, invites, and offline inbox durable. | in-memory |
| `COSMOS_DB` | `Cosmos:Database` | Cosmos database name | `mesh` |
| `REDIS_CONNECTION` | `Redis:Connection` | Shares presence, live Direct/Group buckets, quota, and cross-node routing across replicas. | in-memory |
| `MODEL_ENDPOINT` | `Model:Endpoint` | OpenAI-compatible base URL for an optional hosted free model; inactive unless `MODEL_API_KEY` is set. | `https://openrouter.ai/api` |
| `MODEL_API_KEY` | `Model:ApiKey` | Key for `MODEL_ENDPOINT`. | none |
| `MODEL_NAME` | `Model:Model` | Model id to call. | `openrouter/auto` |
| `MODEL_DAILY_TOKEN_LIMIT` | `Model:DailyTokenLimit` | Per-handle daily token budget for the free model. | `100000` |
| `MESH_MSG_RATE_PER_MIN` | `Mesh:MessageRatePerMinute` | Default Direct logical-message refill rate per minute. | `120` |
| `MESH_MSG_BURST` | `Mesh:MessageBurst` | Default Direct bucket capacity. | `30` |
| `MESH_GROUP_RATE_PER_MIN` | `Mesh:GroupMessageRatePerMinute` | Default Group logical-message refill rate per minute. | `120` (falls back to Direct rate if no Group setting exists) |
| `MESH_GROUP_BURST` | `Mesh:GroupMessageBurst` | Default Group bucket capacity. | `30` (falls back to Direct burst if no Group setting exists) |
| `MESH_MAX_FANOUT_RECIPIENTS` | `Mesh:MaxFanoutRecipients` | Default per-handle fan-out limit, clamped to the hard cap of 128. | `128` |
| `MESH_RATE_POLICY_CACHE_SECONDS` | `Mesh:RatePolicyCacheSeconds` | Per-replica effective-policy cache duration. | `60` |
| `MESH_ADMIN_KEY` | `Mesh:AdminKey` | Secret required in `X-Mesh-Admin-Key` for rate-policy administration. | none |

Direct and Group buckets are separate and count logical messages. One fan-out consumes
one Group token, not one token per recipient. For example, 120/minute with burst 30
allows 30 immediate sends and then refills at 2/second. Sends return explicit accepted,
`rate_limited`, or other rejection results instead of silent success.

Administrative per-handle overrides take precedence over configured defaults. They are
stored durably in Cosmos `rate-policies` (or non-durably in the in-memory store) and
cached for `MESH_RATE_POLICY_CACHE_SECONDS`; Redis stores live shared bucket balances.
Without Redis, each process uses local in-memory buckets.

Admin-only `GET`, `PUT`, and `DELETE`
`/admin/handles/{handle}/rate-policy` require `X-Mesh-Admin-Key`. PUT replaces the
complete policy (`messagesPerMinute`, `burstCapacity`, `groupMessagesPerMinute`,
`groupBurstCapacity`, `maxFanoutRecipients`, `enabled`); DELETE restores defaults.
With no configured admin key every request is unauthorized. Protect the endpoint with
TLS and network controls, and keep a high-entropy admin key in a secret manager. Users
cannot change policy through this endpoint.

If you do not set `MODEL_*`, the relay simply has no free model: clients on your
relay bring their own model key (or run one on-device), which is the recommended
setup for a private relay.

## Scaling

- **Single small relay**: the defaults are fine. In-memory state, one container.
- **Durable + multi-replica**: set both `COSMOS_CONNECTION` and `REDIS_CONNECTION`,
  then run as many replicas as you like behind a load balancer with sticky sessions
  (the SignalR WebSocket connection must stay on one replica). Cosmos stores durable
  per-handle policy overrides; Redis handles presence, shared live rate buckets, and
  directed cross-replica message forwarding.

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
- Online fan-out dispatch is concurrent; offline users receive later. An accepted send
  is not an atomic or simultaneous physical-delivery guarantee.
- A relay operator can see the handle directory and traffic metadata (who talks to
  whom, and when), but not message contents. Run your own relay if you want that
  metadata to stay with you.
