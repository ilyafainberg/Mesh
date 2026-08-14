---
id: builtin:knowledge:online-only-architecture
type: knowledge
title: Online-only architecture
description: How live Mesh routing differs from durable local history and why online presence matters.
roles: owner,guest,service
keywords: online,relay,routing,delivery,availability,architecture
---

# Online-only architecture

Mesh keeps durable user data on linked clients. The Relay is a live routing and coordination service, not a durable archive of private payloads. A successful Relay connection proves the client identity for that session and allows the Relay to forward traffic to currently reachable recipients.

Do not promise that the Relay will retain an encrypted message until an offline recipient returns. A recipient that is disconnected cannot receive live traffic, and a sender may need to retry after both sides are connected. Client UI may keep local pending state, but that is different from Relay-side payload retention.

This separation limits the amount of private ciphertext held by shared infrastructure and makes local encrypted state the durable source of truth. It also means connectivity, device presence, and retry behavior are part of normal troubleshooting.

Built-in policies, knowledge, and skills do not depend on the Relay. They load from the installed app package and remain available without network access.
