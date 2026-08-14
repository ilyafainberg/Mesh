---
id: builtin:knowledge:offline-delivery-limitations
type: knowledge
title: Offline delivery limitations
description: What continues to work offline and which Mesh operations require reachable peers or providers.
roles: owner,guest,service
keywords: offline,delivery,pending,retry,relay,model,network,limitations
---

# Offline delivery limitations

The installed Mesh client can open local encrypted history, use packaged built-in content, and run capabilities that are genuinely local while the network is unavailable. Network-dependent work cannot be completed merely because the local UI is open.

Common online requirements include:

- A Relay connection for live contact, service, and cross-device traffic.
- The recipient device being reachable for online-only forwarding.
- Provider connectivity for cloud-hosted and browser-backed models.
- Connector connectivity for remote accounts and data sources.

When delivery or synchronization is pending, preserve the local state, report the dependency clearly, and retry after connectivity returns. Do not describe a locally queued action as remotely delivered until the receiving side or protocol acknowledgment confirms it.
