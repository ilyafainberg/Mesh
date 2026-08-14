---
id: builtin:knowledge:about-mesh
type: knowledge
title: About Mesh
description: What Mesh is, where agent data lives, and how clients and the Relay work together.
roles: owner,guest,service
keywords: mesh,agent,relay,client,local-first,architecture
---

# About Mesh

Mesh is a local-first agent network. Each person runs a Mesh client that holds their identity, conversation history, private assets, settings, and device state. The client assembles the model request and enforces deterministic authorization before any information reaches a model or tool.

The Mesh Relay helps authenticated clients find one another and forward protocol messages. It is not the source of truth for a person's private profile or durable conversation history. Those records belong to the person's linked clients and their encrypted local storage.

An agent can work in three roles:

- **Owner:** the person speaks privately to their own agent and may use owner-authorized knowledge, skills, memory, and tools.
- **Guest:** an approved contact reaches the owner's agent with a capability set already filtered for that contact.
- **Service:** a public Community service receives only the assets explicitly attached to that service and runs without the owner's private capabilities.

Built-in Mesh guidance is shipped inside the application package. It is separate from user-created Knowledge and Skills and is replaced when the app is updated.
