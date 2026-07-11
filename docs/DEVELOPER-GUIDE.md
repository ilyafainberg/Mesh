# Mesh Developer's Guide (Open-Source Components)

This guide covers the two open-source components of Mesh: **Mesh.Relay** (the SignalR relay server) and **Mesh.Shared** (the wire-contract and cryptography library). It is written for developers who want to build, run, self-host, extend, or write an interoperable client against the open Mesh protocol.

> Style note for contributors: this document, and all Mesh source and docs, must **never** use the em-dash character (U+2014). Use hyphens instead. This is a hard rule.

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Getting Started](#2-getting-started)
3. [Repository and Solution Layout](#3-repository-and-solution-layout)
4. [Mesh.Shared Reference](#4-meshshared-reference)
5. [Mesh.Relay Reference](#5-meshrelay-reference)
6. [Writing an Interoperable Client](#6-writing-an-interoperable-client)
7. [Building and Running with Docker](#7-building-and-running-with-docker)
8. [Testing and Contributing](#8-testing-and-contributing)
9. [Extending the Relay](#9-extending-the-relay)

---

## 1. Introduction

Mesh is a system for handle-addressed, end-to-end-encrypted messaging and service discovery between devices. It is split into three components, two of which are open source and documented here.

### 1.1 The components

| Component | Purpose | License | Public repo(s) | Documented here |
|-----------|---------|---------|----------------|-----------------|
| **Mesh.Relay** | ASP.NET Core minimal-API + SignalR relay server. Routes envelopes, registers handles, brokers connectors and a hosted model, serves the capability directory. | AGPL-3.0 | `MeshRelayAI/Relay` | Yes (full) |
| **Mesh.Shared** | Wire contracts (envelope, kinds, request/response records, protocol helpers) plus the `MeshCrypto` library. | Apache-2.0 | `MeshRelayAI/Shared`, also vendored into `MeshRelayAI/Relay` | Yes (full) |
| **Mesh.App** | The reference client. Closed-source. Links Mesh.Shared. | PolyForm Noncommercial | (not published) | No (existence only) |

Mesh.App exists and is the reference client. It is closed-source under PolyForm Noncommercial and links Mesh.Shared. Its internals are out of scope for this guide; only the open protocol surface is documented here so that anyone can build an interoperable client.

### 1.2 Why the license split

The split is deliberate:

- **Mesh.Shared is Apache-2.0 (permissive).** It carries the wire contracts and crypto primitives that every participant needs, including the closed reference client. A permissive license lets the closed client link Mesh.Shared without copyleft infection.
- **Mesh.Relay is AGPL-3.0 (strong copyleft).** The relay is the network service. AGPL ensures that modifications deployed as a network service are shared back.

Because the contracts and crypto live in the permissively licensed Mesh.Shared, any client (open or closed) can speak the protocol, while the relay itself remains copyleft.

### 1.3 Repo mirroring

Canonical development happens in a **private monorepo**. The open components are mirrored out to public repositories:

| Monorepo path | Public repo | License |
|---------------|-------------|---------|
| `src/Mesh.Relay` | `MeshRelayAI/Relay` | AGPL-3.0 |
| `src/Mesh.Shared` | `MeshRelayAI/Shared` (and vendored into `MeshRelayAI/Relay`) | Apache-2.0 |

Only source is mirrored. Each public repo keeps its own `LICENSE`, `README`, `Dockerfile`, `docker-compose`, `SELF-HOSTING` guide, CI workflows, `.slnx` solution file, and `.gitignore`. Mesh.Shared is vendored (copied) into the Relay repo so the Relay repo is self-contained and buildable on its own.

---

## 2. Getting Started

### 2.1 Prerequisites

- **.NET 10 SDK** (required to build and run the relay and to consume Mesh.Shared).
- **Docker** (optional, only if you want to build/run the container image).
- A client that speaks the Mesh protocol (the reference client, or one you write following [Section 6](#6-writing-an-interoperable-client)).

### 2.2 Clone

Clone the public relay repo (which vendors Mesh.Shared and is self-contained):

```bash
git clone https://github.com/MeshRelayAI/Relay.git
cd Relay
```

Or clone Mesh.Shared on its own if you only need the contracts/crypto library:

```bash
git clone https://github.com/MeshRelayAI/Shared.git
```

### 2.3 Build the relay

From the public Relay repo:

```bash
dotnet build src/Mesh.Relay
```

From the monorepo (Release configuration):

```bash
dotnet build src/Mesh.Relay -c Release
```

Mesh.Relay references Mesh.Shared, so building the relay also builds the shared library.

### 2.4 Run locally (in-memory)

With no Cosmos or Redis configured, the relay runs fully in-memory, which is ideal for local development:

```bash
dotnet run --project src/Mesh.Relay
```

By default the app listens on the URLs configured by `ASPNETCORE_URLS`. Inside the container image the default is `http://+:8080` (see [Section 7](#7-building-and-running-with-docker)). Verify it is up:

```bash
curl http://localhost:8080/health
```

### 2.5 Point a client at it

Configure your client with the relay base URL (for example `http://localhost:8080`). The client will:

1. Register a handle via `POST /handles`.
2. Open the SignalR hub at the route defined by `MeshHubProtocol.Route`.
3. Answer the device-key challenge.
4. Send and receive envelopes.

See [Section 6](#6-writing-an-interoperable-client) for the full protocol walkthrough.

---

## 3. Repository and Solution Layout

Only the open components are described here. The private monorepo contains additional (closed) projects that are not mirrored and not documented.

### 3.1 Open components

```
src/
  Mesh.Shared/        # Apache-2.0: contracts + MeshCrypto
  Mesh.Relay/         # AGPL-3.0: minimal-API app (Program.cs) + SignalR hub
```

- **Mesh.Shared** is a class library. It has no server dependencies and can be linked by any .NET client. It contains `MeshCrypto` (static crypto helpers) and the `Contracts` (wire protocol records and protocol-helper classes).
- **Mesh.Relay** is an ASP.NET Core app. Its entry point is `Program.cs`, which wires configuration, chooses storage and backplane implementations, configures the rate limiter and connector catalog, and maps the REST endpoints and the SignalR hub. Mesh.Relay references Mesh.Shared for all wire types and crypto.

### 3.2 Public-repo extras

Each public repo additionally ships its own `LICENSE`, `README`, `Dockerfile`, `docker-compose`, `SELF-HOSTING` guide, CI workflows, `.slnx`, and `.gitignore`. These are maintained per-repo and are not mirrored from the monorepo.

### 3.3 Monorepo-only test projects

Two test harnesses live in the monorepo and are referenced in [Section 8](#8-testing-and-contributing):

- `_smoke` (SignalR end-to-end checks)
- `_hostedtest` (hosted-model tool-calling checks)

These live in the monorepo, but the smoke checks can be run by anyone against a running relay.

---

## 4. Mesh.Shared Reference

Mesh.Shared is Apache-2.0. It contains the cryptography (`MeshCrypto`) and the wire contracts. All contracts serialize with **System.Text.Json web defaults** (camelCase property names, case-insensitive read).

### 4.1 MeshCrypto

`MeshCrypto` is a static class. It uses ECDSA and ECDH over the NIST **P-256** curve. Public keys are base64-encoded **SubjectPublicKeyInfo**; private keys are **PKCS8**. The library never persists keys; key storage is the caller's responsibility.

#### 4.1.1 Signature verification

| Method | Inputs | Output | Behavior |
|--------|--------|--------|----------|
| `Verify` | `publicKeyB64`, `message`, `signatureB64` | `bool` | Verifies an ECDSA/P-256 signature of `message` against a single base64 SubjectPublicKeyInfo public key. |
| `VerifyAny` | `keys` (collection of public keys), `message`, `sig` | `bool` | Returns true if the signature verifies against **any** of the supplied keys. Used where a handle has multiple device keys. |

#### 4.1.2 Encryption (ECIES-P256-AESGCM)

`Encrypt(plaintext, recipientDeviceKeysB64)` returns a **self-describing JSON string**, or `null` if there are no usable recipient keys. It supports **multiple recipients**.

The scheme, identified as `"ECIES-P256-AESGCM"`, works as follows:

1. Generate a random **32-byte content key**.
2. AES-256-GCM encrypt the plaintext with the content key.
3. Generate an **ephemeral P-256 ECDH keypair**.
4. For each recipient device key: derive a per-recipient **KEK** via ECDH (`DeriveKeyFromHash`, SHA-256) and AES-GCM-wrap the content key under that KEK.

The resulting payload JSON has this shape:

```json
{
  "v": 1,
  "alg": "ECIES-P256-AESGCM",
  "epk": "<ephemeral public key, base64>",
  "iv": "<content AES-GCM nonce, base64>",
  "ct": "<ciphertext, base64>",
  "tag": "<content AES-GCM tag, base64>",
  "keys": {
    "<deviceId>": { "iv": "<wrap nonce>", "wrap": "<wrapped content key>", "tag": "<wrap tag>" },
    "<deviceId>": { "iv": "...", "wrap": "...", "tag": "..." }
  }
}
```

- `epk` is the ephemeral public key used for ECDH.
- `iv`, `ct`, `tag` are the content encryption nonce, ciphertext, and GCM tag.
- `keys` maps each recipient **deviceId** to that recipient's wrapped copy of the content key (its own wrap nonce, wrapped key, and wrap tag).

> Field names (`v`, `alg`, `epk`, `iv`, `ct`, `tag`, `keys`, `wrap`) are part of the wire format. Do not rename them if you want interoperability.

#### 4.1.3 Detection and decryption

| Method | Inputs | Output | Behavior |
|--------|--------|--------|----------|
| `IsEncrypted` | `body` | `bool` | True if `body` is a Mesh encrypted JSON payload: it starts with `{` and its `alg` matches the Mesh scheme. |
| `TryDecrypt` | `body`, `myPrivateKeyB64`, `myPublicKeyB64` | `(ok, plaintext)` | Attempts to decrypt. Finds the caller's wrapped content key by its deviceId, unwraps it via ECDH with the caller's private key, then AES-GCM-decrypts the content. Returns a success flag and the plaintext. |

#### 4.1.4 Device identity

| Method | Inputs | Output | Behavior |
|--------|--------|--------|----------|
| `DeviceId` | `publicKeyB64` | `string` | The first **12 hex characters** of `SHA-256(publicKeyB64)`, lowercase. This is the canonical device-id scheme and matches the relay's device-id computation. |

The `DeviceId` value is the key used both in the `keys` map of an encrypted payload and in per-device routing on the relay.

### 4.2 Contracts (wire protocol)

All contracts are plain records/types serialized with System.Text.Json web defaults.

#### 4.2.1 MeshEnvelope

The envelope is the unit of routing:

| Field | Meaning |
|-------|---------|
| `To` | Destination handle. |
| `From` | Source handle. On routed messages the relay stamps the authenticated value. |
| `Kind` | A string kind constant (see `MeshKinds`). |
| `Body` | The payload. May be an encrypted JSON payload (see 4.1.2) or plaintext, depending on kind. |
| `FromDevice` (optional, trailing) | Sending device's routing id. Stamped by the relay from the authenticated connection. |
| `ToDevice` (optional, trailing) | Target device routing id; enables per-device delivery with broadcast fallback. |

#### 4.2.2 MeshKinds

`MeshKinds` holds the string kind constants carried in `MeshEnvelope.Kind`:

| Kind (conceptual) | Purpose |
|-------------------|---------|
| `chat` | Handle-to-handle chat message. |
| `direct` | Direct message. |
| agent request / agent response | Agent invocation request and its response. |
| remote-agent request / remote-agent response | Cross-handle (remote) agent invocation and response. |
| `service.request` | Invoke a published service (capability). |
| `service.response` | Response to a service request. |
| `report` | Report message (see `ReservedHandles`, e.g. the system report handle). |

> The exact string values live in `MeshKinds`. Treat that class as the source of truth; do not hard-code kind strings if you can reference the constants.

#### 4.2.3 Registration, linking, and recovery

| Type | Role |
|------|------|
| `RegisterHandleRequest` | Claim/register a handle. Carries `handle`, `devicePublicKey`, a `signature` proof-of-possession, a `recoveryPublicKey`, `deviceName`, and `displayName`. |
| `RegisterHandleResponse` | Result of registration. |
| `ClaimProtocol` | Builds the **canonical claim message** that the device signs to prove possession of the device key when claiming a handle. |
| `LinkInviteRequest` / `LinkInviteResponse` | Begin linking a new device to an existing handle. |
| `LinkRedeemRequest` | Redeem a link invite from the new device. |
| `RecoverHandleRequest` | Recover a handle using the recovery key. |
| `LinkProtocol` | Canonical pairing invite/redeem messages plus a `Normalize` helper. |

#### 4.2.4 Devices and directory

| Type | Role |
|------|------|
| `DeviceInfo` | Per-device metadata. |
| `DeviceProtocol.DeviceId` | Computes the per-device routing id (consistent with `MeshCrypto.DeviceId`). |
| `HandleInfo` | Public directory view of a handle. |

#### 4.2.5 Connectors and hosted model

| Type | Role |
|------|------|
| `ConnectorTokenRequest` | Request a brokered OAuth token from the relay's connector broker. |
| `ConnectorProtocol` | Connector broker protocol helpers. |
| `HostedModelRequest` | Request to the hosted free-model proxy. |
| `HostedModelMessage` | A message in a hosted-model conversation. |

#### 4.2.6 Capability directory (services)

| Type | Role |
|------|------|
| `ServiceListing` | A published service entry in the directory. |
| `PublishServiceRequest` | Publish a service (signed). |
| `UnpublishServiceRequest` | Unpublish a service (signed). |
| `ServiceVoteRequest` | Cast a usage-gated vote on a service (signed). |
| `ServiceProtocol` | Service protocol helpers, including `ServiceTurn` for framing a service-request transcript. |
| `ServiceDirectoryProtocol` | Directory helpers, including `WilsonScore` (ranking/scoring). |
| `ServiceCategories` | The fixed list of service categories. |

#### 4.2.7 System handles and links

| Type | Role |
|------|------|
| `ReservedHandles` | System handles (for example the report handle `meshreport`) with `IsReserved` and `Coerce` helpers. |
| `DeepLink` | Parser/builders for `mesh://service` and `mesh://user` deep links. |
| `UniversalLink` | Builders/parsers for HTTPS universal links: `/s/{handle}/{serviceId}` (service) and `/u/{handle}` (user). |
| `MeshHubProtocol` | SignalR hub contract: the hub `Route` path and the method names `Challenge`, `Ready`, and `Receive`. |

---

## 5. Mesh.Relay Reference

Mesh.Relay is AGPL-3.0. It is a minimal-API ASP.NET Core application (`Program.cs`) plus a SignalR hub.

### 5.1 Architecture

```
                +-------------------------------------------+
   REST  ---->  |  Program.cs (minimal API)                 |
                |   - config helper (env var -> appsettings)|
                |   - rate limiter                          |
                |   - connector catalog                     |
                |   - endpoint mapping                      |
                +----------------+--------------------------+
                                 |
   SignalR ---> |  MeshHub (Challenge/Ready/Receive)        |
                |   - device-key challenge auth on connect  |
                |   - verify signature on every message     |
                |   - stamp authenticated From/FromDevice   |
                |   - MeshRouter                            |
                +----------------+--------------------------+
                                 |
             +-------------------+--------------------+
             |                                        |
     IRelayStore (storage)                    Backplane (presence/forward)
     - InMemoryRelayStore                     - in-memory
     - CosmosRelayStore                       - Redis
```

`Program.cs` reads configuration, chooses storage (in-memory vs Cosmos) and backplane (in-memory vs Redis), sets up the rate limiter and connector catalog, and maps all endpoints and the hub. A small config helper reads an environment variable first and then falls back to the matching appsettings key (see the [config table](#54-configuration)).

### 5.2 REST endpoints

All signed endpoints verify an ECDSA/P-256 signature against the relevant device/handle key(s) using `MeshCrypto`. "Public" means no auth required.

| Method | Path | Purpose | Auth |
|--------|------|---------|------|
| GET | `/` | Status. | Public |
| GET | `/health` | Health check. | Public |
| GET | `/metrics` | Aggregate counters, no PII. | Public |
| POST | `/handles` | Register/claim a handle with a signed proof of possession. A claim with a **different key** to an existing handle returns **409**. | Signed (proof of possession) |
| POST | `/handles/{handle}/link/invite` | Create a device-link invite. | Signed |
| POST | `/handles/{handle}/link/redeem` | Redeem a device-link invite from the new device. | Signed |
| POST | `/handles/{handle}/recover` | Recover a handle using the recovery key. | Signed (recovery) |
| GET | `/handles/{handle}` | Public handle info. | Public |
| GET | `/handles/{handle}/devices` | Device directory for a handle. | Public |
| DELETE | `/handles/{handle}` | Delete a handle. | Signed |
| GET | `/connectors` | Public OAuth connector catalog. | Public |
| POST | `/connectors/{provider}/token` | Brokered OAuth token exchange; the relay holds the provider secret. | See connector policy |
| POST | `/model/chat` | Hosted free-model proxy. Per-handle daily token limit. | Device-key auth |
| GET | `/capabilities` | Public capability-directory read (list). | Public |
| GET | `/capabilities/{serviceId}` | Public read of one service. | Public |
| POST | `/capabilities` | Publish a service. | Signed |
| DELETE | `/capabilities/{serviceId}` | Unpublish a service. | Signed |
| POST | `/capabilities/{serviceId}/used` | Attest usage of a service. | Signed |
| POST | `/capabilities/{serviceId}/vote` | Cast a usage-gated vote. | Signed |
| GET | `/s/{handle}/{serviceId}` | Universal-link landing page (service). | Public |
| GET | `/u/{handle}` | Universal-link landing page (user). | Public |
| (SignalR) | `MeshHubProtocol.Route` | SignalR hub endpoint. | Device-key challenge on connect |

### 5.3 SignalR hub: connect, auth handshake, and routing

The hub is mapped at `MeshHubProtocol.Route`. Method names come from `MeshHubProtocol` (`Challenge`, `Ready`, `Receive`).

#### 5.3.1 Connect and auth handshake

1. On connect the hub issues a **`Challenge`** with a nonce.
2. The client **signs** the nonce with its device private key and calls **`Ready`** with the signature (and its device public key).
3. The hub verifies the signature. Once verified, the connection is authenticated and associated with the handle/device.

#### 5.3.2 Message routing

For every routed message the hub:

1. **Verifies the signature** on the message.
2. **Stamps** the authenticated `From` and `FromDevice` (clients cannot spoof these; the relay overwrites them from the authenticated connection).
3. Routes via the **MeshRouter**:
   - **Local delivery** if the recipient is connected to this instance.
   - **Redis directed cross-instance forward** if the recipient is connected to another instance (owner lookup via the backplane).
   - **Offline enqueue** (inbox) otherwise.

Per-device routing honors `ToDevice`: if set, the message targets that device, with **broadcast fallback** to all of the handle's devices when appropriate. A device sending to its own handle is **excluded from its own connection** (you do not receive an echo of your own send).

Delivery to clients uses the **`Receive`** method.

### 5.4 Storage and backplane abstractions

#### 5.4.1 Storage: `IRelayStore`

`IRelayStore` abstracts persistence. Two implementations ship:

- **`InMemoryRelayStore`** for local/dev and single-instance use.
- **`CosmosRelayStore`** for production. Cosmos containers:

| Container | Contents | TTL behavior |
|-----------|----------|--------------|
| handles / directory | Handle registrations and public directory. | - |
| invites | Device-link invites. | Native TTL. |
| inbox | Offline message queue. | `DefaultTimeToLive` of **14 days**. Reserved handles get a per-item **ttl of -1** (never expire). |
| services directory | Published capability/service listings. | - |

#### 5.4.2 Backplane

The backplane abstracts presence and cross-instance forwarding. A **Redis** implementation provides:

- **Presence keys** with a ~**30s TTL**.
- **Per-handle quota** tracking.
- **Cross-instance publish** to the owner instance for directed forwarding.

With no Redis configured the relay uses an in-memory backplane (single-instance).

### 5.5 Configuration

`Program.cs` reads each setting from an environment variable first, then the matching appsettings key, then a default.

| Env var | appsettings key | Default | Notes |
|---------|-----------------|---------|-------|
| `COSMOS_CONNECTION` | `Cosmos:Connection` | none | If unset, storage is in-memory. |
| `COSMOS_DB` | `Cosmos:Database` | `mesh` | Cosmos database name. |
| `REDIS_CONNECTION` | `Redis:Connection` | none | If unset, backplane is in-memory. |
| `MODEL_ENDPOINT` | `Model:Endpoint` | `https://openrouter.ai/api` | Hosted-model upstream base. |
| `MODEL_API_KEY` | `Model:ApiKey` | none | Hosted-model upstream API key. |
| `MODEL_NAME` | `Model:Model` | `openrouter/auto` | Hosted-model model id. |
| `MODEL_DAILY_TOKEN_LIMIT` | `Model:DailyTokenLimit` | `100000` | Per-handle daily token cap for `/model/chat`. |
| `MESH_MSG_RATE_PER_MIN` | `Mesh:MessageRatePerMinute` | `120` | Message rate limit per minute. |
| `MESH_MSG_BURST` | `Mesh:MessageBurst` | `30` | Message burst allowance. |
| `ASPNETCORE_URLS` | (ASP.NET Core) | (host default) | Listen URLs. Container default `http://+:8080`. |
| `CONNECTOR_{KEY}_CLIENT_ID` | `Connectors:{key}:clientId` | none | Per-connector OAuth client id. |
| `CONNECTOR_{KEY}_SECRET` | `Connectors:{key}:secret` | none | Per-connector OAuth secret (held only by the relay). |

---

## 6. Writing an Interoperable Client

This walkthrough uses only the primitives above. It is protocol-accurate but intentionally does not prescribe a language beyond .NET where `MeshCrypto` is available; a non-.NET client must reimplement the same primitives (ECDSA/ECDH P-256, AES-256-GCM, the payload JSON shape, and the deviceId scheme).

### 6.1 Step 1: Generate keys

Generate a P-256 keypair for the device:

- Device **public key**: base64 SubjectPublicKeyInfo.
- Device **private key**: PKCS8 (kept locally; never sent).
- Compute the **deviceId** = first 12 hex chars of `SHA-256(publicKeyB64)`, lowercase (`MeshCrypto.DeviceId`).

Optionally generate a **recovery keypair** for handle recovery.

### 6.2 Step 2: Register the handle (sign the claim)

1. Build the canonical claim message using `ClaimProtocol`.
2. Sign it with the device private key (this is the proof of possession).
3. `POST /handles` with a `RegisterHandleRequest`:

```jsonc
// POST /handles
{
  "handle": "alice",
  "devicePublicKey": "<base64 SPKI>",
  "signature": "<base64 signature over the canonical claim>",
  "recoveryPublicKey": "<base64 SPKI>",
  "deviceName": "laptop",
  "displayName": "Alice"
}
```

If the handle already exists and you present a **different key**, the relay returns **409 Conflict**. A matching key is treated as the owner.

### 6.3 Step 3: Connect the hub

Open a SignalR connection to `MeshHubProtocol.Route` on the relay base URL.

### 6.4 Step 4: Answer the auth challenge

1. The hub invokes `Challenge` with a nonce.
2. Sign the nonce with your device private key.
3. Call `Ready` with your device public key and the signature.
4. The hub verifies and marks the connection authenticated.

### 6.5 Step 5: Send an end-to-end-encrypted envelope

1. Fetch the recipient's device public keys from `GET /handles/{handle}/devices`.
2. Encrypt the plaintext to those keys:
   - `MeshCrypto.Encrypt(plaintext, recipientDeviceKeysB64)` returns the self-describing JSON payload (see [4.1.2](#412-encryption-ecies-p256-aesgcm)) or `null` if no keys are usable.
3. Put that payload in the envelope `Body`, set `To`, `Kind` (for example the chat kind), and optionally `ToDevice`:

```jsonc
{
  "to": "bob",
  "kind": "chat",
  "body": "{ \"v\":1, \"alg\":\"ECIES-P256-AESGCM\", \"epk\":\"...\", \"iv\":\"...\", \"ct\":\"...\", \"tag\":\"...\", \"keys\": { \"<deviceId>\": { \"iv\":\"...\", \"wrap\":\"...\", \"tag\":\"...\" } } }"
  // "toDevice": "<deviceId>"   // optional: target a specific device
}
```

Do not set `From`/`FromDevice` yourself; the relay stamps the authenticated values.

### 6.6 Step 6: Receive and decrypt

1. Handle the `Receive` method to get inbound envelopes.
2. Check `MeshCrypto.IsEncrypted(envelope.Body)`.
3. If encrypted, call `MeshCrypto.TryDecrypt(body, myPrivateKeyB64, myPublicKeyB64)` and use the returned plaintext when `ok` is true.

### 6.7 Multi-recipient and multi-device

`Encrypt` supports multiple recipient keys, producing one wrapped content key per deviceId in the `keys` map. This is how a message reaches all of a handle's devices. When a specific device is targeted with `ToDevice`, the relay routes to that device with broadcast fallback.

---

## 7. Building and Running with Docker

### 7.1 Multi-stage Dockerfile

The relay image is built from source with a multi-stage Dockerfile:

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copy the vendored shared library first, then the relay
COPY Mesh.Shared/ Mesh.Shared/
COPY Mesh.Relay/ Mesh.Relay/
RUN dotnet restore Mesh.Relay
RUN dotnet publish Mesh.Relay -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mesh.Relay.dll"]
```

Key points:

- Build stage uses the .NET 10 **SDK** image; it copies `Mesh.Shared/` then `Mesh.Relay/`, restores, and publishes.
- Runtime stage uses the .NET 10 **ASP.NET** image.
- The container listens on `http://+:8080` (`ASPNETCORE_URLS`), exposes port **8080**, and the entry point is `dotnet Mesh.Relay.dll`.

### 7.2 docker compose

Each public repo ships its own `docker-compose`. A minimal local run maps port 8080 and, if desired, wires Cosmos/Redis via the env vars in [Section 5.5](#54-configuration). With no `COSMOS_CONNECTION` or `REDIS_CONNECTION` set, the relay runs fully in-memory.

Build and run the image directly:

```bash
docker build -t mesh-relay .
docker run -p 8080:8080 mesh-relay
curl http://localhost:8080/health
```

### 7.3 GHCR image

A container image is built from source and published to **GitHub Container Registry** on release and on pushes to `main`:

```
ghcr.io/meshrelayai/relay
```

Pull and run:

```bash
docker pull ghcr.io/meshrelayai/relay
docker run -p 8080:8080 ghcr.io/meshrelayai/relay
```

---

## 8. Testing and Contributing

### 8.1 Smoke and hosted-model tests

Two harnesses live in the monorepo:

| Project | Purpose | Command |
|---------|---------|---------|
| `_smoke` | SignalR end-to-end checks (connect, auth, route). | `dotnet run --project _smoke -- <relayUrl>` |
| `_hostedtest` | Hosted-model tool-calling checks. | `dotnet run --project _hostedtest -- <relayUrl>` |

These are monorepo projects, but contributors to the public relay can still run the **smoke checks** against any running relay by passing its URL:

```bash
dotnet run --project _smoke -- http://localhost:8080
```

### 8.2 Coding conventions

- **Never use the em-dash character (U+2014).** Use hyphens. This applies to all source, comments, and docs.
- **Keep the wire protocol backward compatible.** The contracts in Mesh.Shared are consumed by independent clients (including the closed reference client). Do not rename or repurpose existing fields (`v`, `alg`, `epk`, `iv`, `ct`, `tag`, `keys`, `wrap`, envelope fields, kind constants). Add new fields/kinds additively.
- Contracts serialize with **System.Text.Json web defaults**; keep new members compatible with that convention.

### 8.3 License obligations

- Contributions to **Mesh.Relay are AGPL-3.0**. If you deploy a modified relay as a network service, AGPL obligations apply (you must make your modified source available to users of that service).
- Contributions to **Mesh.Shared are Apache-2.0**. Keep the permissive licensing intact so the contracts and crypto remain linkable by any client.

---

## 9. Extending the Relay

Extend the relay only through the abstractions it already defines. Do not assume backends or hooks beyond what is listed here.

### 9.1 Add a storage backend

Implement **`IRelayStore`** and wire it in `Program.cs` alongside the existing `InMemoryRelayStore` and `CosmosRelayStore` selection. The storage selection is driven by configuration (`COSMOS_CONNECTION` presence chooses Cosmos vs in-memory today). A new backend should honor the same responsibilities: handles/directory, invites (with TTL), inbox (offline queue with the 14-day default TTL and the reserved-handle never-expire rule), and the services directory.

### 9.2 Add a backplane backend

Implement the backplane abstraction (the Redis implementation is the reference) to provide presence (short TTL, ~30s), per-handle quota, and cross-instance publish to the owner instance. Selection is configuration-driven (`REDIS_CONNECTION` presence chooses Redis vs in-memory).

### 9.3 Configure the hosted-model endpoint

The hosted `/model/chat` proxy is configured entirely via env vars: `MODEL_ENDPOINT`, `MODEL_API_KEY`, `MODEL_NAME`, and the per-handle `MODEL_DAILY_TOKEN_LIMIT`. Point these at any compatible upstream to change the hosted model without code changes.

### 9.4 Configure the connector catalog

The connector broker's catalog and secrets are configured via env vars per connector: `CONNECTOR_{KEY}_CLIENT_ID` and `CONNECTOR_{KEY}_SECRET` (or the `Connectors:{key}:clientId` / `Connectors:{key}:secret` appsettings keys). The relay holds the secret and performs the brokered OAuth token exchange at `POST /connectors/{provider}/token`; the public catalog is served from `GET /connectors`.

---

*End of guide.*
