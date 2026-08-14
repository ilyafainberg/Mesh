---
id: builtin:knowledge:security-and-local-storage
type: knowledge
title: Security, privacy, and local storage
description: Mesh encryption boundaries, local SQLCipher storage, device secrets, and metadata limits.
roles: owner,guest
keywords: security,privacy,encryption,e2ee,sqlcipher,keys,storage,metadata
---

# Security, privacy, and local storage

Mesh stores durable private state in a local SQLCipher database. The database key is protected through the platform's secret-storage mechanism. If that protected key is unavailable, Mesh must surface the failure rather than silently creating a blank replacement identity or database.

End-to-end encryption protects message bodies between authorized devices. Signatures and authenticated protocol checks must cover the fields that affect meaning and routing. A model prompt is never a security boundary: circles, service attachments, device authorization, and tool permissions are filtered or enforced before content is assembled.

The Relay may still observe operational metadata needed to route traffic, such as participating handles, device presence, timing, and message size. A configured cloud or browser-backed model receives the prompt content selected for that request. Local models can reduce that disclosure but still operate under the same Mesh authorization rules.

Built-in content is read-only package data. It is not inserted into SQLCipher, synchronized, backed up, assigned to circles, published as a Community asset, or returned by private-data search tools.
