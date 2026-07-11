# Mesh documentation

Documentation for Mesh, a private, end-to-end encrypted messenger with a personal AI
agent, and its open-source relay.

## Guides

| Document | Audience | What it covers |
|---|---|---|
| [User guide](USER-GUIDE.md) | Everyone using the Mesh client | Installing, creating an identity, choosing a model, messaging, contacts and circles, knowledge/skills/widgets, the Community and public services, sharing links, reporting AI content, backup and moving devices, settings, and troubleshooting. |
| [Architecture and security design](ARCHITECTURE-AND-SECURITY.md) | Security reviewers, architects | The system model, identity and key management, the end-to-end encryption scheme, transport and relay architecture, the trust model, and a threat model of what a relay can and cannot see. |
| [Developer guide (open source)](DEVELOPER-GUIDE.md) | Contributors and client authors | The open-source components (`Mesh.Relay`, `Mesh.Shared`): building and running the relay, the `MeshCrypto` API and wire protocol, the relay REST + SignalR surface, and how to write an interoperable client. |
| [Relay deployment guide](RELAY-DEPLOYMENT.md) | Operators self-hosting a relay | Deploying the relay with Docker or binaries, putting it behind TLS, the full configuration reference, storage and scaling (Cosmos + Redis), health and metrics, and operational best practices. |
| [Self-hosting (short version)](SELF-HOSTING.md) | Operators who want the quick path | A condensed quick start for running your own relay. See the deployment guide for the comprehensive version. |

## What is open source

Mesh is a mixed open and proprietary system:

- **Relay** (`Mesh.Relay`, AGPL-3.0): the ASP.NET Core SignalR service that routes
  end-to-end encrypted messages. It never sees message contents. Public repo:
  `MeshRelayAI/Relay`.
- **Shared library** (`Mesh.Shared`, Apache-2.0): the wire contracts and the crypto
  library (`MeshCrypto`). Permissively licensed so any client can link it. Public repo:
  `MeshRelayAI/Shared`.
- **Client** (proprietary, PolyForm Noncommercial): the reference desktop and mobile app.
  The documentation describes its user-facing behavior and its externally observable
  security properties, not its internal implementation.

The parts that carry your data in transit and define the wire format are fully auditable;
the client that holds your keys is proprietary but has publicly stated security properties.
