# Mesh Relay - Deployment Guide

A complete, operator-focused guide to self-hosting the Mesh relay on your own
infrastructure.

The relay is the open-source (AGPL-3.0) ASP.NET Core SignalR service that routes
end-to-end-encrypted messages between handles. It never sees plaintext. Anyone can
run one and point Mesh clients at it.

- Source: [MeshRelayAI/Relay](https://github.com/MeshRelayAI/Relay)
- Prebuilt image: `ghcr.io/meshrelayai/relay`
- Short version of this document: [docs/SELF-HOSTING.md](./SELF-HOSTING.md)

> This is the long, comprehensive guide. If you just want the fastest possible
> path, read [SELF-HOSTING.md](./SELF-HOSTING.md) instead. If you want to
> understand every knob, scaling model, and operational trade-off, keep reading
> here.

---

## Table of contents

1. [Overview](#1-overview)
2. [Prerequisites](#2-prerequisites)
3. [Quick start (Docker, in-memory, localhost)](#3-quick-start-docker-in-memory-localhost)
4. [Deployment methods](#4-deployment-methods)
5. [Putting it behind TLS](#5-putting-it-behind-tls)
6. [Configuration reference](#6-configuration-reference)
7. [Storage and scaling](#7-storage-and-scaling)
8. [Health, metrics, and monitoring](#8-health-metrics-and-monitoring)
9. [Pointing clients at your relay](#9-pointing-clients-at-your-relay)
10. [Version compatibility](#10-version-compatibility)
11. [Security and operational best practices](#11-security-and-operational-best-practices)
12. [Troubleshooting](#12-troubleshooting)

---

## 1. Overview

The Mesh relay is a transport. Clients encrypt every message end-to-end to the
recipient's device keys before the message ever reaches the relay, so the relay
moves sealed payloads it cannot open.

### What the relay does and does not see

| The relay DOES see | The relay does NOT see |
| --- | --- |
| The handle directory (handle -> device public keys) | Message contents (they are end-to-end encrypted to recipient device keys) |
| Presence (which handles are currently connected) | Anything that would let it decrypt traffic |
| Traffic metadata (who talks to whom, and when) | Plaintext of any message, attachment, or payload |
| For fan-out: sender, transient recipient cohort, timing, and ciphertext size | Group ID, name, membership metadata, inner control/message type, or group-message plaintext |

One generic fan-out request carries one ciphertext and 1 to 128 transient recipient
handles. The relay clones ordinary envelopes and routes online recipients concurrently,
queueing ordinary per-recipient inbox records for offline recipients. It does not create
or persist a group or fan-out object. Repeated recipient cohorts may still permit group
inference, so Mesh makes no traffic-analysis-resistance claim.

On every message the relay stamps the authenticated sender: it verifies a
device-key signature and asserts the real origin handle. It cannot forge or read
content, only route it and attest who sent it.

### Trust summary (one paragraph)

Running a relay does not grant access to anyone's messages. It is a router for
sealed, end-to-end-encrypted payloads. As an operator you can observe the handle
directory, who is online, and traffic metadata (who talks to whom, when), but not
the contents of any message. Self-hosting means that metadata stays on
infrastructure you control instead of someone else's. The relay authenticates
every connection with a device-key challenge and verifies the signature on every
message, so it can prove the real sender even though it can never read what was
sent.

---

## 2. Prerequisites

You need one of the following, depending on the deployment method you choose.

**For Docker deployments (recommended):**

- Docker Engine 24+ with the Docker Compose v2 plugin (`docker compose ...`).
- A host reachable by your clients (a laptop for local testing, or a VM / server
  with a public IP or hostname for shared use).

**For running the prebuilt image:**

- Docker, and outbound access to `ghcr.io` to pull `ghcr.io/meshrelayai/relay`.

**For self-contained binaries:**

- Nothing extra. Self-contained builds bundle the .NET runtime, so no .NET
  install is required on the host.

**For a source build with the .NET SDK:**

- The .NET 10 SDK (`dotnet --version` should report 10.x).
- The relay source checked out from
  [MeshRelayAI/Relay](https://github.com/MeshRelayAI/Relay).

**For any public relay:**

- A DNS name pointing at your host and a reverse proxy or load balancer that can
  terminate TLS (see [section 5](#5-putting-it-behind-tls)).

**Optional, only for durable or multi-replica deployments:**

- An Azure Cosmos DB account (for durable storage).
- A Redis instance (for presence, shared live rate buckets, and cross-replica routing
  at more than one replica).

With none of the optional pieces, the relay runs fully in-memory as a single node
with no hosted model. That is a valid, working configuration for personal and test
use.

---

## 3. Quick start (Docker, in-memory, localhost)

The goal here is one running relay on `http://localhost:8080` in under a minute,
with in-memory storage (single node, state lost on restart, no hosted model).

### Option A: pull the prebuilt image

```bash
docker run --rm -p 8080:8080 ghcr.io/meshrelayai/relay
```

### Option B: build and run from source with compose

From the repository root:

```bash
docker compose up mesh-relay
```

This builds the multi-stage image and starts a working relay on
`http://localhost:8080` with in-memory storage.

### Verify it is up

```bash
curl http://localhost:8080/health
# -> {"status":"ok",...}
```

For current clients, confirm the nested `capabilities` object reports
`"protocolVersion": 8`, `"durableDelivery": true`, and `"backgroundSync": true`.

You can also open `http://localhost:8080/` for service status (including the
instance id) and `http://localhost:8080/metrics` for aggregate counters.

### Point a client at it

In the Mesh client, set the Relay URL to `http://localhost:8080` (onboarding
model/relay screen, or later in Settings -> Relay URL -> Reconnect). The client
registers a handle on your relay and you are live.

> For local testing over plain HTTP this is fine. For anything reachable by other
> people, put the relay behind TLS first (see [section 5](#5-putting-it-behind-tls))
> and give clients the `https://` URL.

---

## 4. Deployment methods

The image is a standard multi-stage .NET container: it builds on
`mcr.microsoft.com/dotnet/sdk:10.0` and runs on
`mcr.microsoft.com/dotnet/aspnet:10.0`. Inside the container it listens on
`http://+:8080` (set via `ASPNETCORE_URLS`, and the port is `EXPOSE`d as 8080).

### (a) Docker compose from source

Building from source gives you a reproducible image pinned to the code you have
checked out. A minimal `docker-compose.yml` for a single in-memory node:

```yaml
services:
  mesh-relay:
    build: .
    image: mesh-relay:local
    ports:
      - "8080:8080"
    environment:
      ASPNETCORE_URLS: "http://+:8080"
    restart: unless-stopped
```

Bring it up:

```bash
docker compose up mesh-relay
# or detached:
docker compose up -d mesh-relay
```

To make this node durable, add `COSMOS_CONNECTION` (and optionally `COSMOS_DB`)
to the `environment` block. See [section 7](#7-storage-and-scaling).

### (b) Prebuilt GHCR image

Pull and run the published image directly, no build step required.

`docker run`:

```bash
docker run -d --name mesh-relay \
  -p 8080:8080 \
  ghcr.io/meshrelayai/relay
```

`docker-compose.yml` using the prebuilt image:

```yaml
services:
  mesh-relay:
    image: ghcr.io/meshrelayai/relay
    ports:
      - "8080:8080"
    environment:
      ASPNETCORE_URLS: "http://+:8080"
      # Optional durable storage:
      # COSMOS_CONNECTION: "${COSMOS_CONNECTION}"
      # COSMOS_DB: "mesh"
    restart: unless-stopped
```

```bash
docker compose up -d
```

Pass secrets (such as `COSMOS_CONNECTION`) from your platform secret store or an
`.env` file that is not committed, never inline in the compose file that lives in
version control.

### (c) Self-contained binaries or dotnet

**Self-contained binaries (no .NET install needed).** Publish produces
per-platform folders, each with a run script. The runtime is bundled, so the host
does not need .NET installed. Set `ASPNETCORE_URLS` to control the listen address:

```bash
# Linux/macOS
ASPNETCORE_URLS="http://+:8080" ./run.sh
```

```powershell
# Windows (PowerShell)
$env:ASPNETCORE_URLS = "http://+:8080"
.\run.ps1
```

**Source build with the .NET 10 SDK.** From the relay source:

```bash
# Run directly from source
ASPNETCORE_URLS="http://+:8080" dotnet run

# Or run a published DLL
ASPNETCORE_URLS="http://+:8080" dotnet Mesh.Relay.dll
```

All configuration is supplied the same way regardless of method: environment
variables (or the matching `appsettings` keys). See
[section 6](#6-configuration-reference).

---

## 5. Putting it behind TLS

For anything reachable outside your machine, terminate TLS with a reverse proxy
or cloud load balancer and give clients the `https://` URL. The relay itself can
keep speaking plain HTTP on `8080` on a private network behind the proxy.

**Why wss:** the client uses secure WebSockets (wss) over the same URL you give
it. When the client is configured with `https://relay.example.com`, its SignalR
WebSocket connects as `wss://relay.example.com`. That means your proxy must both
terminate TLS and correctly forward the WebSocket upgrade to the relay.

### Caddy (2-3 lines)

Caddy fetches and renews certificates automatically:

```caddy
relay.example.com {
    reverse_proxy localhost:8080
}
```

That is the whole config. Caddy proxies WebSockets transparently, so no extra
directives are needed.

### nginx WebSocket proxy block

nginx needs the `Upgrade` and `Connection` headers set explicitly so the
WebSocket handshake is forwarded to the relay:

```nginx
server {
    listen 443 ssl;
    server_name relay.example.com;

    # ssl_certificate / ssl_certificate_key configured elsewhere

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;

        # Required for the SignalR WebSocket upgrade:
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";

        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Give long-lived WebSocket connections room to breathe:
        proxy_read_timeout 3600s;
    }
}
```

If you run more than one replica, the load balancer in front must also use sticky
sessions so each WebSocket stays pinned to one replica. See
[section 7](#7-storage-and-scaling).

---

## 6. Configuration reference

Every setting is an environment variable, or the matching `appsettings` key. All
of them are optional. With none set, the relay runs in-memory, as a single node,
with no hosted model. Only the settings you actually provide change behaviour.

### Networking

| Env var | appsettings key | Purpose | Default |
| --- | --- | --- | --- |
| `ASPNETCORE_URLS` | (standard ASP.NET Core) | Listen address / port. | `http://+:8080` in Docker |

### Storage

| Env var | appsettings key | Purpose | Default |
| --- | --- | --- | --- |
| `COSMOS_CONNECTION` | `Cosmos:Connection` | Azure Cosmos DB connection string. When set, the handle registry, invites, and offline inbox become durable. | in-memory |
| `COSMOS_DB` | `Cosmos:Database` | Cosmos database name. | `mesh` |
| `MESH_REQUIRE_DURABLE_STORAGE` | `Mesh:RequireDurableStorage` | When `true`, fail startup unless `COSMOS_CONNECTION` is configured. Recommended for every hosted relay. | `false` |
| `REDIS_CONNECTION` | `Redis:Connection` | Redis connection string. When set, presence, live Direct/Group token buckets, per-handle quota, and cross-node routing are shared across replicas. | in-memory |
| `BLOB_CONNECTION` | `Blob:Connection` | Azure Storage connection string (must include an account key). When set, the relay issues short-lived SAS URLs so clients upload and download end-to-end-encrypted attachment ciphertext directly to blob storage; the envelope carries only a pointer. Unset disables attachments. | disabled |
| `BLOB_ATTACHMENTS_CONTAINER` | `Blob:AttachmentsContainer` | Private container holding attachment ciphertext. Created on demand. | `attachments` |

Attachments never travel inline: the relay caps every envelope body under the 2 MB Cosmos item
limit, so clients upload each attachment (encrypted, up to 20 MB) to blob storage through a
relay-issued SAS URL and send only a pointer. Apply a lifecycle rule so attachment blobs auto-delete
14 days after creation, matching the offline-inbox TTL, so a stored attachment never outlives its
message:

```powershell
pwsh _deploy/apply-attachments-lifecycle.ps1 -Account <storage-account> -ResourceGroup <rg>
```

### Hosted model (optional free model)

The hosted model is an optional convenience feature. It is only active if you set
`MODEL_API_KEY`. Without a key, there is effectively no hosted model, even though
the code carries a default base URL.

| Env var | appsettings key | Purpose | Default |
| --- | --- | --- | --- |
| `MODEL_ENDPOINT` | `Model:Endpoint` | OpenAI-compatible base URL for an optional hosted free model. | none (code default base is `https://openrouter.ai/api`, but the feature is inactive unless `MODEL_API_KEY` is also set) |
| `MODEL_API_KEY` | `Model:ApiKey` | Key for the hosted model endpoint. Required to enable the feature. | none |
| `MODEL_NAME` | `Model:Model` | Model id to call. | `openrouter/auto` |
| `MODEL_DAILY_TOKEN_LIMIT` | `Model:DailyTokenLimit` | Per-handle daily token budget for the free model. | `100000` |

### Rate limiting

| Env var | appsettings key | Purpose | Default |
| --- | --- | --- | --- |
| `MESH_MSG_RATE_PER_MIN` | `Mesh:MessageRatePerMinute` | Per-handle steady message rate limit. | `120` |
| `MESH_MSG_BURST` | `Mesh:MessageBurst` | Per-handle burst capacity. | `30` |
| `MESH_GROUP_RATE_PER_MIN` | `Mesh:GroupMessageRatePerMinute` | Per-handle Group logical-message refill rate. | `120` (falls back to Direct rate if no Group setting exists) |
| `MESH_GROUP_BURST` | `Mesh:GroupMessageBurst` | Per-handle Group bucket capacity. | `30` (falls back to Direct burst if no Group setting exists) |
| `MESH_MAX_FANOUT_RECIPIENTS` | `Mesh:MaxFanoutRecipients` | Default fan-out recipient limit; values are clamped to the hard cap of 128. | `128` |
| `MESH_RATE_POLICY_CACHE_SECONDS` | `Mesh:RatePolicyCacheSeconds` | Per-replica effective-policy cache duration. | `60` |
| `MESH_ADMIN_KEY` | `Mesh:AdminKey` | Secret required in `X-Mesh-Admin-Key` for rate-policy admin endpoints. | none |
| `MESH_LIVE_FAULTS_ENABLED` | `Mesh:LiveFaultsEnabled` | Runtime gate for an admin-only hook that exists only in the explicit test-relay build flavor. Production-default artifacts ignore it. | `false` |
| `MESH_LIVE_FAULT_MAX_TTL_SECONDS` | `Mesh:LiveFaultMaxTtlSeconds` | Upper bound for a fault rule TTL (hard maximum 3600). | `3600` |
| `MESH_LIVE_FAULT_MAX_USES` | `Mesh:LiveFaultMaxUses` | Upper bound for bounded-use fault rules (hard maximum 1000). | `1000` |

Rate limits count **logical sends**, not physical recipient envelopes. An ordinary send
consumes one Direct token. One fan-out consumes one Group token whether it addresses 1
or 128 handles. Direct and Group buckets are independent. With a 120/minute refill and
burst 30, a full bucket permits 30 immediate sends and then refills at 2 tokens per
second. Rejections return explicit `rate_limited` results with a retry delay; accepted,
validation, and other rejection outcomes are also explicit rather than silently dropped.

Configured defaults apply unless an administrative per-handle override exists. A policy
object contains `messagesPerMinute`, `burstCapacity`, `groupMessagesPerMinute`,
`groupBurstCapacity`, `maxFanoutRecipients`, and `enabled`. The complete override takes
precedence over defaults, is stored durably in Cosmos `rate-policies` (or non-durably in
the in-memory store), and is cached per replica for
`MESH_RATE_POLICY_CACHE_SECONDS`. Live bucket balances are atomic Redis state across
replicas, or local in-memory state when Redis is absent.

### Rate-policy administration

| Method | Path | Effect |
| --- | --- | --- |
| `GET` | `/admin/handles/{handle}/rate-policy` | Read the effective policy and whether an override exists. |
| `PUT` | `/admin/handles/{handle}/rate-policy` | Create or replace the complete override and invalidate its local cache entry. |
| `DELETE` | `/admin/handles/{handle}/rate-policy` | Remove the override, invalidate cache, and restore configured defaults. |

All three endpoints are admin-only and require the exact `X-Mesh-Admin-Key` value from
`MESH_ADMIN_KEY` / `Mesh:AdminKey`; if no key is configured, every request is
unauthorized. Do not expose them without TLS. Keep the key in a
secret manager, use a high-entropy value, restrict network access, and rotate it. There
is currently no user endpoint for changing policy; user-controlled lower limits may be
added later.

### Live-fault test hook

Production relay artifacts do **not** contain live-fault activation routes or interception code.
The default `MeshRelayTestFaults=false` build removes the source at compile time. A Release build
with `MeshRelayTestFaults=true` is rejected by MSBuild, so a stray runtime flag cannot activate a
production Release artifact. The flavors also use physically separate output and intermediate
directories and different assembly identities:

| Flavor | Assembly |
| --- | --- |
| Production default | `src\Mesh.Relay\bin\<Configuration>\net10.0\production\Mesh.Relay.dll` |
| Debug test hooks | `src\Mesh.Relay\bin\Debug\net10.0\test-hooks\Mesh.Relay.TestHooks.dll` |

A test build therefore cannot overwrite or be loaded as the ordinary production assembly.

Build the clearly separate test relay in Debug:

```powershell
dotnet build .\src\Mesh.Relay\Mesh.Relay.csproj -c Debug -p:MeshRelayTestFaults=true
$env:ASPNETCORE_ENVIRONMENT = 'Test' # Development is also accepted
$env:MESH_LIVE_FAULTS_ENABLED = 'true'
$env:MESH_ADMIN_KEY = '<secret-from-vault>'
dotnet .\src\Mesh.Relay\bin\Debug\net10.0\test-hooks\Mesh.Relay.TestHooks.dll
```

All four gates are required: test-relay compile flavor, Development/Test environment, explicit
runtime enable flag, and a configured admin key. Every request also requires the exact
`X-Mesh-Admin-Key`. A test-flavor binary refuses startup when the enable flag is set in another
environment. A production-default relay returns 404 because the routes are absent. Missing
configuration or authentication fails closed.

Rules are replica-local, in-memory, TTL-bound, bounded-use, and require a source account plus an
explicit target device. Optional source device, target account, kind, SHA-256 stable-envelope ID
hash, and ordinal narrow the match further. `online-frame` is the only kind exposed for encrypted
replication frames because the relay never reads ciphertext. Control envelopes can use their public
control kind. Audit entries contain rule metadata, IDs/hashes, timestamps, and counts only—never
plaintext or ciphertext.

| Method | Path | Effect |
| --- | --- | --- |
| `POST` | `/admin/live-faults` | Idempotently activate a named rule. |
| `GET` | `/admin/live-faults` | List status, expiry, ordinal, and use counts. |
| `GET` | `/admin/live-faults/{ruleId}` | Read one rule. |
| `DELETE` | `/admin/live-faults/{ruleId}` | Idempotently deactivate one rule. |
| `POST` | `/admin/live-faults/cleanup` | Mark expired rules inactive immediately. |
| `GET` | `/admin/live-faults/audit` | Read metadata-only activation/consumption/expiry audit. |
| `GET` | `/admin/live-faults/runtime` | Read ordered hashed control-send attempts and handshake evidence. |

Runtime evidence never returns raw envelope IDs. It includes only SHA-256 stable-ID hashes,
per-ID attempt numbers, and the existing authenticated handshake test fields, and is protected by
the same compile-time, environment, runtime-enable, and admin-key gates as every other hook route.

Mode semantics are exact: `RejectBeforeForwarding` does not forward and returns
`test_fault_rejected`; `DropBeforeForwarding` does not forward and returns `not_online`;
`SuccessDropBeforeDestination` does not forward or persist at the destination but returns the
normal relay success (`delivered` for an online frame, `accepted` for a control). A success-drop is
not entered in relay frame de-duplication, so a stable-ID application retry can proceed after the
one-shot rule is consumed. Inbound rules return a delivered backplane receipt only for
`SuccessDropBeforeDestination`; the two failure modes return not-delivered.

For a live gate, use `_deploy/test-relay/Invoke-MeshLiveFault.ps1`.
Its `Run` action activates a scoped rule, runs the supplied gate script, and deactivates the rule
returned by the relay in `finally`. The CLI reports an explicit refusal if pointed at a
production-default relay.

```powershell
$env:MESH_ADMIN_KEY = '<secret-from-vault>'
.\_deploy\test-relay\Invoke-MeshLiveFault.ps1 `
  -Action Run -BaseUri https://test-relay.example `
  -SourceAccount alice -SourceDevice 0123456789ab `
  -TargetAccount alice -TargetDevice abcdef012345 `
  -Mode SuccessDropBeforeDestination -Kind topic.run.update `
  -StableId '<stable-envelope-id>' -TtlSeconds 180 -MaxUses 1 `
  -GateScript .\_deploy\validation\run-m08-gate.ps1
```

### Connector OAuth (optional)

Only needed if you run your own OAuth broker. Most private relays do not need
this. The relay holds confidential client secrets server-side; only public client
IDs ever reach clients.

| Env var | appsettings key | Purpose | Default |
| --- | --- | --- | --- |
| `CONNECTOR_{KEY}_CLIENT_ID` | `Connectors:{key}:clientId` | Public OAuth client id for connector `{key}`. Reaches clients. | none |
| `CONNECTOR_{KEY}_SECRET` | `Connectors:{key}:secret` | Confidential OAuth client secret for connector `{key}`. Stays server-side. | none |

---

## 7. Storage and scaling

Pick the smallest tier that meets your durability and availability needs.

### Tier 1: single small relay (in-memory)

Defaults are fine: in-memory, one container, nothing to configure. State
(handle directory, agent-response routing and queued dispatches, presence, and
offline inbox) lives in process memory and is lost on restart. This is appropriate
for a personal relay or a test relay.

### Tier 2: durable single node (Cosmos)

Set `COSMOS_CONNECTION` (and optionally `COSMOS_DB`, default `mesh`). Now the
handle directory, agent-response routing, queued atomic agent dispatches, invites,
and offline inbox persist across restarts. Offline messages are queued while their
recipient is disconnected; atomic agent questions are queued while neither configured
response device is eligible. Queued records use the default 14-day TTL; reserved
system inbox records never expire.

Device-sync snapshot requests are idempotent control records. If one is delivered but
not acknowledged, the relay removes it on the next lease instead of redelivering it indefinitely.

Cosmos containers used (created and managed for you, no pre-creation needed):

- handle directory
- administrative per-handle policies (`rate-policies`)
- invites (native per-item TTL)
- offline inbox (14-day default TTL)
- atomic agent dispatches (`agent-dispatches`, 14-day default TTL)
- services directory

### Tier 3: durable and multi-replica (Cosmos + Redis)

Set both `COSMOS_CONNECTION` and `REDIS_CONNECTION`, then run N replicas behind a
load balancer. Three requirements:

1. **Sticky sessions are mandatory.** The SignalR WebSocket must stay on one
   replica for the life of the connection. Configure your load balancer for
   session affinity.
2. **Redis carries presence, live rate buckets, and cross-replica routing.** Redis
   atomically shares each handle's Direct and Group bucket balances, tracks who is
   connected, and forwards directed messages to the replica that holds the
   recipient's connection. Because forwarding is directed rather than broadcast,
   load stays proportional to delivered messages rather than to replica count.
3. **Atomic delivery requires protocol 8 on every replica.** Replicas use
   acknowledged single-connection delivery internally. Mixed-version rolling
   upgrades are unsupported; stop and upgrade every replica together.

Atomic agent routing uses both services: Cosmos provides the shared compare-and-swap
dispatch fence and Redis provides exact device presence plus acknowledged directed
forwarding. A confirmed miss may use the configured decrypt-capable failover, while an
acknowledgement timeout or remote send exception remains fenced as an uncertain delivery.
For durable at-most-one response behavior across replicas, configure both Cosmos and Redis.

Compose example using the `redis` profile:

```yaml
services:
  mesh-relay:
    image: ghcr.io/meshrelayai/relay
    ports:
      - "8080:8080"
    environment:
      ASPNETCORE_URLS: "http://+:8080"
      COSMOS_CONNECTION: "${COSMOS_CONNECTION}"
      COSMOS_DB: "mesh"
      REDIS_CONNECTION: "redis:6379"
    depends_on:
      - redis
    restart: unless-stopped

  redis:
    image: redis:7
    profiles: ["redis"]
    restart: unless-stopped
```

Start it with the profile enabled:

```bash
docker compose --profile redis up
```

In production you would run several `mesh-relay` replicas behind your load
balancer (with sticky sessions) rather than a single container, and point them
all at the same Cosmos account and Redis instance.

### Scaling summary

| Need | Configuration |
| --- | --- |
| Personal / test, state loss on restart is fine | Defaults (in-memory, one node) |
| Durable single node | `COSMOS_CONNECTION` (+ `COSMOS_DB`) |
| Durable and horizontally scalable | `COSMOS_CONNECTION` + `REDIS_CONNECTION`, N replicas, sticky sessions |

---

## 8. Health, metrics, and monitoring

The relay exposes plain HTTP endpoints for status and observability.

| Endpoint | Returns |
| --- | --- |
| `GET /health` | Liveness/readiness plus transport capabilities, including protocol version and atomic agent dispatch support |
| `GET /` | Service status, including the instance id |
| `GET /metrics` | Aggregate counters (see below) |

`GET /metrics` reports aggregate counters only:

- handles registered
- messages routed
- hosted-model calls
- rate-limit rejections
- connected count
- envelopes enqueued
- deliveries acknowledged
- deliveries redelivered
- queued envelopes cancelled

Normal and fan-out hub sends return explicit accepted or rejected results. Ordinary envelopes are enqueued before live delivery. Protocol-8 authentication fails closed on version mismatch, the relay confirms routable presence before any backlog work, and the client emits its ready signal immediately after `PresenceConfirmed`. Durable inbox work and per-device queue drains happen afterward and are acknowledged explicitly. Until then, the leased item can be redelivered after a disconnect or dropped acknowledgement. An accepted fan-out still has no atomic simultaneous physical-delivery guarantee.

Protocol 8 adds per-device queues for device-sync traffic (enqueue/drain/ack), priority-separated inbox leasing, resumable snapshot manifests/chunks, online-only ephemeral topic progress, explicit client processing outcomes, authoritative queued/running topic state, and signed exact-device revocation. Device-sync envelope kinds are rejected through the old SendEnvelope path and must use the new QueueEnqueue hub method. Snapshot chunks are Bulk priority and cannot starve Control traffic. Revoking a device removes its registration, purges only its device-specific partition and per-device queue, and rejects later sends to the removed device ID; it does not touch sibling device inboxes.

Atomic agent sends are different: `agent_dispatch_accepted` means the request reached
its assigned device fence, while `agent_dispatch_queued` means the relay accepted
it into its configured store but neither response device is currently eligible. Both
are successful send results. Only one matching response can complete that dispatch.

The metrics endpoint contains no handles and no PII, so it is safe to scrape.

**Recommended monitoring:** wire an uptime or monitor check to `GET /health` so
you are alerted if the relay stops responding, and scrape `GET /metrics` into your
metrics stack to watch message throughput, connection counts, and rate-limit
rejections over time.

---

## 9. Pointing clients at your relay

Each user sets the Relay URL in the client:

- During onboarding, on the model/relay screen, or
- Later, in Settings -> Relay URL -> Reconnect.

Give clients the public URL of your relay. If it is behind TLS, that is the
`https://` URL (the client will connect over `wss` automatically).

### Per-relay handle model

A handle is registered per relay. Users on your relay are independent from users
on any other relay: the same handle name on a different relay is a different
registration.

### No federation

To message across relays, both parties must be on the same relay. Federation
between relays is not implemented. If you stand up your own relay, invite the
people you want to talk to onto that same relay.

---

## 10. Protocol version

Protocol 8 is the only supported relay/client protocol. Handle registration, health
capability checks, and SignalR connection setup all require version 8 exactly.
Mismatches are rejected with the required and actual versions; there is no fallback
or downgrade.

Treat this as a coordinated breaking deployment:

1. Stop every relay replica.
2. Discard old Cosmos ordinary-inbox and device-sync queue data.
3. Deploy all protocol-8 relay replicas.
4. Release protocol-8 clients.

Do not perform a rolling mixed-version relay deployment. Existing handle and device
registrations may be retained only if their device metadata is re-registered as
protocol 8 before routing is enabled.

---

## 11. Security and operational best practices

**Always put a public relay behind TLS.** Terminate TLS at a reverse proxy or
load balancer and hand clients the `https://` URL. See
[section 5](#5-putting-it-behind-tls).

**Authentication and message integrity are built in.** The relay authenticates
every connection with a device-key challenge and verifies the signature on every
message. It therefore asserts the real sender on every message even though it
cannot read contents. You do not need to add an application-layer auth system for
this; it is intrinsic to the protocol.

**Understand the metadata you hold.** As an operator you can see the handle
directory, traffic metadata (who talks to whom, and when), selected agent-response
device ids, and atomic dispatch lifecycle metadata, but not message contents.
Self-hosting keeps that metadata on your infrastructure instead of a third party's.
Treat it as sensitive: it reveals communication and device-availability patterns.

**Manage secrets properly.** Cosmos and Redis connection strings, `MESH_ADMIN_KEY`, any
`MODEL_API_KEY`, and any connector secrets should come from your platform secret
store (for example, environment injection from a secrets manager, Docker
secrets, or your orchestrator's secret mechanism). Never commit them to version
control and never inline them in a compose file that is checked in.

**Back up Cosmos if you rely on durable delivery or recovery.** If you use Cosmos
for durable storage, its data (handle directory and agent routing, queued atomic
dispatches, rate policies, invites, offline inbox, services directory) is your source
of truth for recovery. Enable backups on the Cosmos account according to your
recovery objectives.

**Honor the AGPL-3.0 obligations.** The relay is licensed AGPL-3.0. If you modify
the relay and offer it to users over a network, you must offer those users your
modified source. Link back to the upstream source at
[MeshRelayAI/Relay](https://github.com/MeshRelayAI/Relay) and publish your
changes.

---

## 12. Troubleshooting

### Client cannot connect

- Confirm the client's Relay URL is correct and reachable from the client's
  network.
- If the relay is behind a proxy, verify the proxy forwards the WebSocket upgrade:
  the `Upgrade` and `Connection: upgrade` headers must reach the relay (see the
  [nginx block](#nginx-websocket-proxy-block)). Missing upgrade headers is the
  most common cause of "connects then immediately drops."
- Confirm the relay is actually listening where you expect. Check `ASPNETCORE_URLS`
  and that the port is published (`-p 8080:8080`) and open.
- Check host and cloud firewall / security group rules for the relay port (or 443
  if fronted by TLS).
- Hit `GET /health` directly against the relay to confirm the process is up
  independent of the proxy.

### Messages lost when running multiple replicas

- Enable **sticky sessions** on the load balancer so each SignalR WebSocket stays
  pinned to one replica.
- Ensure **Redis is configured** (`REDIS_CONNECTION`) so presence and directed
  cross-replica forwarding work. Multi-replica without Redis will misroute or drop
  messages because replicas do not share connection state.

### State lost on restart

- This is expected in the default in-memory configuration.
- Set `COSMOS_CONNECTION` (and optionally `COSMOS_DB`) for durable storage, so the
  handle directory, agent-response routing, queued atomic dispatches, invites, and
  offline inbox persist across restarts.

### Agent questions remain queued

- Check `GET /health` and confirm `capabilities.atomicAgentDispatch` is `true`.
- Ask the recipient to open **Settings > Agent responses** and confirm the selected primary, plus optional failover, is a compatible desktop. The device must be online and advertise a configured model.
- With failover set to **None**, questions intentionally stay queued until the primary is ready.
- A queued request can run only on a configured device whose key existed when the sender encrypted it. After linking or selecting a new desktop, the sender may need to verify the updated contact identity and send a new question, or the owner can choose an older decrypt-capable failover.
- Do not perform a mixed-version rolling relay upgrade. Stop and upgrade all replicas together.
- In a multi-replica deployment, configure both Cosmos and Redis. Without shared durable dispatch state and shared device presence, atomic routing is not production-safe across replicas.
- Cosmos dispatch records use a 14-day TTL. A question that remains unserviceable past that retention window expires.

### Free (hosted) model not working

- The hosted model is only active when `MODEL_API_KEY` is set. Setting only
  `MODEL_ENDPOINT` (or relying on the code default base URL) does nothing without
  a key.
- Once enabled, check `MODEL_NAME` and the per-handle `MODEL_DAILY_TOKEN_LIMIT`
  if calls are rejected after working initially.
- Watch the hosted-model call counter and rate-limit rejections in
  `GET /metrics`.

---

## Appendix: minimal end-to-end example

A durable single node behind Caddy, using the prebuilt image.

`docker-compose.yml`:

```yaml
services:
  mesh-relay:
    image: ghcr.io/meshrelayai/relay
    environment:
      ASPNETCORE_URLS: "http://+:8080"
      COSMOS_CONNECTION: "${COSMOS_CONNECTION}"
      COSMOS_DB: "mesh"
    restart: unless-stopped
    # No published port: Caddy reaches it over the compose network.
```

`Caddyfile`:

```caddy
relay.example.com {
    reverse_proxy mesh-relay:8080
}
```

Bring it up (with `COSMOS_CONNECTION` provided from your secret store or an
uncommitted `.env`):

```bash
docker compose up -d
curl https://relay.example.com/health
# -> {"status":"ok",...}
```

Then have each user set their Relay URL to `https://relay.example.com` and
reconnect.
