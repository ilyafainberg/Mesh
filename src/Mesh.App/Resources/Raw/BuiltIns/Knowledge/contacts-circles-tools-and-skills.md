---
id: builtin:knowledge:contacts-circles-tools-and-skills
type: knowledge
title: Contacts, circles, tools, and skills
description: How Mesh scopes reusable content and capabilities for owners, contacts, and services.
roles: owner,guest
keywords: contacts,circles,tools,skills,knowledge,visibility,sharing,permissions
---

# Contacts, circles, tools, and skills

A **contact** is another Mesh handle the owner has approved. A **circle** groups contacts so the owner can grant the same user-created Knowledge, Skills, widgets, connector folders, or other capabilities to several people without configuring each contact independently.

User-created assets have visibility settings. Before a guest request reaches a model, Mesh resolves the contact's circles and supplies only assets and tools that match those grants. Public Community services are stricter: they receive only the Knowledge, Skills, and widgets explicitly attached to that service, and service execution has no owner tools.

A **tool** performs an operation or retrieves live data. Tool availability is an application decision, and permission prompts remain authoritative. A **skill** is repeatable guidance for how the agent should perform a workflow. Skills do not grant tool access by themselves.

Built-in content differs from user content:

- It is available by agent role rather than by circle assignment.
- It is selected internally when relevant.
- It cannot be browsed, edited, enabled, disabled, shared, exported, or attached to a service by the user.
- Updating Mesh atomically replaces the packaged built-in catalog.
