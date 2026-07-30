# Self-hosting a Mesh relay

The Mesh relay is a small ASP.NET Core service that routes end-to-end-encrypted
messages between handles. It never sees message plaintext. Anyone can run their own
relay, and any Mesh client can point at it. This document explains how.

## What a relay does (and does not) see

- **Does not see**: message contents. Bodies are end-to-end encrypted to the
  recipient's device keys before they reach the relay.
- **Sees**: the handle directory (handle to device-public-key mappings), presence
  (who is connected), agent-response device policy and dispatch lifecycle metadata,
  and routing metadata for ciphertext between handles. For a fan-out it also sees
  the sender, transient recipient cohort, timing, and ciphertext size. It stamps
  the authenticated sender on every message.

A fan-out carries one ciphertext and 1 to 128 transient recipient handles. The relay
does not persist a group or cohort object; it clones ordinary envelopes and stores only
unavoidable per-recipient offline inbox records. Repeated cohorts can permit group
inference. Mesh does not claim traffic-analysis resistance.

Running a relay does NOT give you access to anyone's messages. It is transport.

## Protocol version

Relay and client must both run protocol 8. Registration, health capability checks,
and SignalR connection setup reject every other version. Required synchronization
capabilities include `durableDelivery`, `backgroundSync`, and `deviceSync`; atomic
single-device responses additionally require `atomicAgentDispatch`. Missing
capabilities fail closed.

Protocol 8 is a coordinated breaking change. Stop all replicas, discard old Cosmos
ordinary-inbox and device-sync queue data, deploy the relay, and then release the
matching clients. Do not mix relay versions or preserve old queued synchronization
records.

## Quick start (Docker)

```bash
# from the repo root
docker compose up mesh-relay
```

That starts a fully working relay on `http://localhost:8080` with in-memory storage
(single node, no free model). Acknowledgement and redelivery work while the process is
running, but queued messages are lost when it restarts. Point a Mesh client at
`http://localhost:8080` (or the machine's address on your network) in onboarding or
Settings, Relay URL.

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
| `COSMOS_CONNECTION` | `Cosmos:Connection` | Azure Cosmos connection string. Makes handles, agent-response routing and queued dispatches, rate policies, invites, and offline inbox durable. | in-memory |
| `COSMOS_DB` | `Cosmos:Database` | Cosmos database name | `mesh` |
| `MESH_REQUIRE_DURABLE_STORAGE` | `Mesh:RequireDurableStorage` | When `true`, fail startup unless `COSMOS_CONNECTION` is configured. Recommended for hosted relays. | `false` |
| `REDIS_CONNECTION` | `Redis:Connection` | Shares presence, live Direct/Group buckets, quota, and cross-node routing across replicas. | in-memory |
| `BLOB_CONNECTION` | `Blob:Connection` | Azure Storage connection string (with account key). Enables blob-backed attachments: clients upload encrypted attachment ciphertext to a relay-issued SAS URL and send only a pointer. Apply `_deploy/apply-attachments-lifecycle.ps1` for the 14-day auto-expiry. | disabled |
| `BLOB_ATTACHMENTS_CONTAINER` | `Blob:AttachmentsContainer` | Private container for attachment ciphertext. | `attachments` |
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

## Push notifications (optional)

The relay can wake an offline mobile device (APNs on iOS, FCM on Android) when a
message or passive state update is queued for it. Push is metadata-only: the relay
composes it from routing metadata and never includes encrypted message contents.
Visible alerts are limited to:

- "Message from @sender" for a direct message.
- "New group message" for a group message (the relay never sees the group name; it is
  end-to-end encrypted).
- "Your agent replied in a topic" for a successful remotely hosted topic response.

Push is off until you configure at least one backend. Devices register their token
and visible-alert authorization with a signed `POST /handles/{handle}/push`, and
clear the token with `DELETE`.

On iOS, eligible alerts include `content-available: 1`, and safe updates can use a
pure silent wake when no visible alert is available. The app then performs a bounded
drain, decrypt, persist, and acknowledge session. It never executes agents, services,
or topic turns in the background. Silent delivery is opportunistic, unavailable after
a user force-quit, and coalesced to at least 20 minutes between wakes with a maximum of
three per hour per device. The durable inbox remains authoritative.

| Env var | Config key | Purpose | Default |
|---|---|---|---|
| `APNS_KEY_ID` | `Push:Apns:KeyId` | APNs auth-key id (iOS). | none |
| `APNS_TEAM_ID` | `Push:Apns:TeamId` | Apple Developer team id. | none |
| `APNS_BUNDLE_ID` | `Push:Apns:BundleId` | App bundle id (sent as apns-topic). | none |
| `APNS_PRIVATE_KEY` | `Push:Apns:PrivateKey` | The `.p8` key PEM contents, or a path to it. | none |
| `APNS_PRODUCTION` | `Push:Apns:Production` | `true` for the production APNs host, else sandbox. | `false` |
| `PUSH_BACKGROUND_SYNC_ENABLED` | `Push:BackgroundSyncEnabled` | Set `false` to restore alert-only APNs behavior. | `true` |
| `FCM_SERVICE_ACCOUNT_JSON` | `Push:Fcm:ServiceAccountJson` | Google service-account JSON, or a path to it. | none |

Set the APNs group (all four required) to enable iOS, and/or `FCM_SERVICE_ACCOUNT_JSON`
to enable Android. With none set, the relay behaves exactly as before (no push). See
[PUSH-NOTIFICATIONS.md](./PUSH-NOTIFICATIONS.md) for delivery modes and limitations.

## Scaling

- **Single small relay**: the defaults are fine. In-memory state, one container.
- **Durable + multi-replica**: set both `COSMOS_CONNECTION` and `REDIS_CONNECTION`,
  then run as many replicas as you like behind a load balancer with sticky sessions
  (the SignalR WebSocket connection must stay on one replica). Cosmos stores durable
  handle records, acknowledged ordinary inboxes, queued atomic agent dispatches, and
  per-handle policy overrides;
  Redis handles device presence, shared live rate buckets, and directed cross-replica
  message forwarding. Both services are required for durable atomic dispatch across replicas.
  Protocol-8 replicas use acknowledged single-connection delivery through Redis.
  Mixed-version rolling upgrades are unsupported; stop and upgrade all replicas together.

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
