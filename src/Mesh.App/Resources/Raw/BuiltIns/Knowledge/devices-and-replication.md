---
id: builtin:knowledge:devices-and-replication
type: knowledge
title: Devices, replication, and custody
description: How linked Mesh devices share encrypted state and preserve device authorization history.
roles: owner,guest
keywords: devices,replication,sync,custody,linking,recovery,revocation,history
---

# Devices, replication, and custody

Each Mesh installation has device-specific cryptographic material. Linking a device authorizes it to participate in the owner's identity and to receive replicated profile and conversation state. Replication moves signed state changes between authorized devices; it does not turn the Relay into the durable owner of that state.

The device roster answers which keys are currently authorized. The custody history records how authority changed through linking, recovery, and revocation. Security decisions must be enforced by deterministic cryptographic and protocol checks, not by model instructions.

Healthy cross-device behavior requires:

1. Both devices to represent the same Mesh identity.
2. The relevant devices to be authorized by the current custody state.
3. Relay connectivity at a time when replication can be exchanged.
4. Local encrypted databases to open with their device-protected keys.

Revocation should prevent a removed device from authorizing itself again with stale proof. Recovery is an authority-changing operation and should be treated more carefully than ordinary message synchronization.
