---
id: builtin:skill:troubleshoot-relay
type: skill
name: Troubleshoot Relay connectivity
description: Diagnose why a Mesh client cannot connect to or stay connected to its Relay.
roles: owner
triggers: relay offline,cannot connect,connection failed,disconnected,network
---

# Instructions

1. Identify the affected account, device, Relay endpoint, and when the failure began.
2. Determine whether the device has general network access and whether the configured Relay endpoint resolves and accepts connections.
3. Check the client's Relay status and local diagnostics for authentication, certificate, timeout, clock, or transport errors. Report only safe diagnostic metadata.
4. Confirm that the device still has valid identity material and is authorized; do not reset identity or delete local data as an exploratory step.
5. Distinguish a Relay outage from a recipient being offline and from a model or connector outage.
6. Apply the least destructive correction, reconnect, and verify that the authenticated session remains stable.
7. If the Relay is unavailable, preserve pending local work and state exactly what must become reachable before retrying.
