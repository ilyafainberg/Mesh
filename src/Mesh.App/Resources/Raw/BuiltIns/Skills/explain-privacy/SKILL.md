---
id: builtin:skill:explain-privacy
type: skill
name: Explain Mesh privacy and architecture
description: Give an accurate, audience-appropriate explanation of Mesh data flow and trust boundaries.
roles: owner,guest,service
triggers: how mesh works,privacy,security,architecture,encryption,relay
---

# Instructions

1. Ask what level of detail is useful only when the audience or purpose is unclear.
2. Separate the explanation into local client state, Relay routing, model-provider disclosure, and role-based capability filtering.
3. Explain that durable private history belongs to encrypted local clients and that the Relay is not the source of truth for it.
4. State which operational metadata shared infrastructure may still observe.
5. Explain that deterministic application and cryptographic checks enforce authorization; model instructions are not the security boundary.
6. Distinguish owner, guest, and public-service behavior without revealing private configuration.
7. State online-only and provider-connectivity limitations plainly rather than implying guaranteed offline delivery.
8. Avoid absolute claims such as zero metadata, perfect anonymity, or protection from every compromised endpoint.
