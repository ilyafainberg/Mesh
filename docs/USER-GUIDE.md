# Mesh User Guide

Welcome to Mesh. This guide explains what the Mesh app does and how to use it, step by step. It is written for everyday users, not developers. You do not need any technical background to follow along.

---

## Table of Contents

1. [Introduction: What Mesh Is](#1-introduction-what-mesh-is)
2. [Installing and First Run](#2-installing-and-first-run)
3. [Core Concepts](#3-core-concepts)
4. [Your Agent (Me and Threads)](#4-your-agent-me-and-threads)
5. [Messaging People and Agents](#5-messaging-people-and-agents)
6. [Contacts and Circles](#6-contacts-and-circles)
7. [Knowledge, Skills, and Widgets](#7-knowledge-skills-and-widgets)
8. [Connecting Live Knowledge Sources](#8-connecting-live-knowledge-sources)
9. [Local Tools and MCP](#9-local-tools-and-mcp)
10. [Community and Public Services](#10-community-and-public-services)
11. [Sharing and Deep Links](#11-sharing-and-deep-links)
12. [Reporting AI Content](#12-reporting-ai-content)
13. [Managing Multiple Identities](#13-managing-multiple-identities)
14. [Backup, Moving Devices, and Recovery](#14-backup-moving-devices-and-recovery)
15. [Settings Reference](#15-settings-reference)
16. [Troubleshooting and FAQ](#16-troubleshooting-and-faq)
17. [Glossary](#17-glossary)

---

## 1. Introduction: What Mesh Is

Mesh is a private messenger with a personal AI agent built in. It combines three ideas that usually live in separate apps:

- **A secure messenger.** You talk to other people using a simple handle (like a username). Your conversations are end-to-end encrypted, which means only the intended people can read them.
- **A personal AI agent.** Every Mesh identity comes with its own agent: a private assistant that can chat with you, remember the knowledge you give it, run skills you define, build small apps for you, and (with your permission) talk to other people's agents on your behalf.
- **An open relay.** Messages travel through a relay server whose only job is to route sealed, encrypted messages between devices. The relay cannot read your messages. It never sees the contents.

The Mesh client is a desktop and mobile app. It runs on Windows and Android, with iOS builds as well. The look and behavior are consistent across platforms, though the layout adapts to each screen size.

### What makes Mesh different

- **No email or password account.** You are identified by a handle and by cryptographic keys that are created and stored on your own device.
- **Privacy by design.** Your keys and your data stay on your device in encrypted storage. Nothing is bulk-uploaded to a server.
- **You control what your agent shares.** Other people's agents only see what you have deliberately shared with them, and only within the boundaries you set.
- **You can go fully private or fully public.** Keep everything to yourself, share selected knowledge with a small circle of contacts, or publish a public service that anyone on your relay can use.

---

## 2. Installing and First Run

### Getting the app

Install Mesh on your device (Windows or Android; iOS builds are also available). Once installed, launch the app to begin onboarding.

### Step 1: Create your identity

On first run, Mesh asks you to **create a new identity** by picking a **handle**. A handle is like a username: it is how other people find and message you.

When you create your identity, the app generates a **device signing keypair** locally. This key is what proves that messages from your handle really come from your device. This key **never leaves your device**. There is no email address, no password, and no central account to sign into.

A few things to know about handles:

- One handle can live on **several devices** at once (for example, your laptop and your phone).
- A handle is tied to a **relay** (see [Core Concepts](#3-core-concepts)). To message someone, you both need to be on the same relay.

### Step 2: Choose a model for your agent

Your agent needs an AI model to think with. During onboarding you choose one of the following. You can change this later in **Settings**.

| Option | What it is | Notes |
|---|---|---|
| **Mesh-hosted model (free)** | A model hosted by Mesh that works immediately | Has a **daily token budget**. When you reach it, the free model pauses until the next day. Routing is automatic (no model picker). |
| **Bring your own API key** | Use your own account with a provider | Supported: **Anthropic, OpenAI, Gemini, Grok, Groq, Azure OpenAI, OpenRouter**. You paste your key; usage is billed by that provider. |
| **On-device with Foundry Local** | Run a model directly on your own machine | Keeps everything local. Good for maximum privacy and offline-friendly use. |

Notes on model picking:

- For **OpenRouter** and the **Mesh-hosted model**, routing is **automatic**: you do not pick a specific model, the app selects one for you.
- For other providers, you may be able to choose a specific model.

Once your identity and model are set, you land in the main app.

---

## 3. Core Concepts

A short section to make everything else easier to understand.

### Handles

A **handle** is your public name on Mesh, like a username. People message you by your handle. You can run the same handle on multiple devices.

### Identities

An **identity** is a handle plus the keys that belong to it. You can keep **multiple identities on one device** and switch between them (see [Managing Multiple Identities](#13-managing-multiple-identities)). For example, you might keep a personal handle and a work handle separate.

### The relay

The **relay** is the server that routes messages between devices. It only moves sealed, encrypted messages; it cannot read them. A handle is **per relay**, which means:

- To message someone, you both must be connected to the **same relay**.
- You can point Mesh at a different relay if you want (see [Settings Reference](#15-settings-reference)).

### End-to-end encryption (E2EE), in one paragraph

Every message you send is encrypted on your device before it leaves, and it can only be decrypted by its intended recipient devices. The relay in the middle only sees a sealed envelope, never the contents. Your private keys never leave your device, and your data is kept in encrypted storage on the device. In short: your conversations are for you and the people you are talking to, and nobody else, including the relay operator.

---

## 4. Your Agent (Me and Threads)

The **Me** screen is a private chat between you and your own agent. This is your personal space. Your agent here has **full access** to everything you have given it: your knowledge, your skills, your tools, and your connected sources.

Use the Me chat to:

- Ask questions and get help.
- Have your agent search or summarize your knowledge.
- Run your skills.
- Ask your agent to build a widget (a small app) for you.
- Use any local tools you have enabled.

### Threads (topic chats)

The Me chat supports **multiple topic threads**, which work like separate chats. Each thread keeps its own history, so you can keep different subjects apart (for example, one thread for travel planning and another for a work project).

For each thread you can:

- **Rename** it to something meaningful.
- **Clear** it to wipe its history but keep the thread.
- **Delete** it entirely.

Threads help you stay organized without cluttering a single long conversation.

---

## 5. Messaging People and Agents

The **Messages** screen is where you talk to other people. You start a conversation by their **handle**.

### Talking to a person or to their agent

Each conversation has a per-conversation **Agent / Person toggle**. This decides where your message goes:

- **Agent:** your message goes to the other person's **agent**, which may **auto-reply** on their behalf (depending on how they have set up their approval modes).
- **Person:** your message goes to the **person directly**, like a normal chat.

This toggle is powerful: it lets you get a quick answer from someone's agent (for example, "what are your office hours?") without interrupting the person, or reach the person directly when you need them.

### Delivery receipts

Messages show delivery status so you know what happened:

- **Sent:** the relay explicitly accepted the message for routing or offline queueing.
- **Delivered:** the message reached the recipient's device.

The relay does not silently report success. If it rejects a send, for example because your handle is temporarily rate limited, the app receives an explicit result and can tell you to retry. Direct messages and group messages have separate per-handle allowances. One group send consumes one group-message allowance regardless of whether it has 2 or 128 recipients.

### Unread markers

Conversations show **unread markers** so you can quickly see where there are new messages waiting for you.

### Trust on first use and re-verification

Mesh uses **trust-on-first-use**. The first time you talk to someone, your app remembers their identity keys. If a contact's identity keys ever **change** (for example, they moved to a brand new device, or in the rare case something is wrong), Mesh **holds your messages** and asks you to **re-verify** before continuing. This protects you from someone impersonating a contact. When this happens, review the prompt and re-verify to resume the conversation.

### Creating and using a group

Groups are private, human-to-human conversations created from **Messages**:

1. Select **New group**.
2. Enter a **group name**.
3. Enter the other members' Mesh handles, separated by commas, spaces, semicolons, or new lines. You are included automatically.
4. Select **Create**.

A group must contain you and at least one other person. It can contain at most **128 people total**. Duplicate handles and your own handle in the member box are ignored.

Group conversations are always **Person** conversations. There is no Agent / Person toggle, group agents, or automatic agent reply. Every message shows which member sent it. Your client resolves all members' device keys, encrypts the group content once to the union of those keys, and sends one generic fan-out request containing that ciphertext and the transient recipient handles. The relay has no persistent group object or membership list. It clones ordinary sealed envelopes for routing and queues only the unavoidable per-recipient inbox record for an offline member.

Online dispatch is concurrent, while offline members receive their sealed envelope after they reconnect. A successful send means the relay accepted the logical message for routing or queueing. It does not guarantee atomic or physically simultaneous delivery to every member.

If Mesh cannot find usable device keys for every member, group creation or sending fails rather than sending any group data as plaintext.

### Clearing or deleting a group locally

- **Clear** removes the group's message history from this device but keeps the group and its member list.
- **Delete** removes the group, its local metadata, and its history from this device.

These actions are **local only**. They do not clear another member's history, remove anyone from the group, or send a deletion notice. Because the MVP has no rejoin or history-backfill flow, deleting a group can prevent later messages for that group from being accepted on that device.

### Group MVP limitations

Groups are create-only in this MVP. The creator is the owner, but there are no owner controls after creation. You cannot add or remove members, leave or dissolve a group for everyone, rename it for everyone, use invite links, backfill earlier history, or see per-member read receipts. Group messaging is human-only.

The relay sees the sender, transient recipient cohort, timing, and ciphertext size. Repeated cohorts may let a relay operator infer that the same people form a group even though the group name, ID, membership metadata, control type, and message contents remain encrypted. Mesh does not claim traffic-analysis resistance.

---

## 6. Contacts and Circles

### Contacts

Add contacts by their **handle**. Your contacts list is how you keep track of the people you talk to.

### Circles

You organize contacts into **circles**. A circle is a named group of contacts (for example, "Family," "Work," or "Book club"). Circles are the heart of how Mesh keeps sharing private.

### Sharing is scoped by circle

This is the key privacy idea. When another person's agent talks to your agent, it only sees the **knowledge, skills, and widgets you have shared with that person's circle**. Nothing else. This is called **privacy by binding**: what a contact can access is bound to the circle you placed them in.

For example:

- You share a "Recipes" knowledge item with your **Family** circle.
- A contact in your **Work** circle asks your agent about recipes.
- Your agent will not surface the recipes, because that contact is not in the circle you shared them with.

### Approval modes

Approval modes control whether your agent **auto-replies** to an approved contact or **waits for you to approve** each reply. This lets you decide how hands-off you want to be:

- Let trusted contacts' requests be answered automatically by your agent.
- Or require your approval before your agent responds, so you stay in the loop.

You can tune this per your comfort level. Approved contacts are still limited to what their circle can see.

---

## 7. Knowledge, Skills, and Widgets

These three building blocks give your agent its abilities. All three can be shared with circles (see above) and some can be published publicly (see [Community](#10-community-and-public-services)).

### Knowledge

**Knowledge** is documents and notes your agent can use. Add the things you want your agent to know about: reference material, personal notes, project details, FAQs, and so on. Your agent can search and use this knowledge when it helps you or an approved contact.

### Skills

**Skills** are reusable instructions or capabilities. Think of a skill as a saved way of doing something: a set of instructions your agent follows whenever the skill applies. Skills let you teach your agent to handle repeated tasks consistently.

### Widgets

**Widgets** are mini HTML apps that your agent can **build** for you. You can **pin** widgets so they are easy to reach, and **share** them with circles. Widgets are handy for small interactive tools, dashboards, calculators, trackers, and similar lightweight apps that live inside Mesh.

---

## 8. Connecting Live Knowledge Sources

Beyond documents you add directly, you can connect **live knowledge sources** so your agent can look things up **on demand**. Supported sources include:

- **Microsoft 365**
- **Google**
- **Dropbox**
- **Notion**
- **Slack**

These are connected as **on-demand tools**. That is an important distinction: **nothing is bulk-copied** into Mesh. Your agent reaches into the source only when it needs an answer, and only for what is relevant to your request. Your information stays where it lives, and Mesh pulls just what it needs, when it needs it.

---

## 9. Local Tools and MCP

Mesh can give your agent access to **local tools** that run on your device. These are powerful, so they are treated with extra care.

### Safety posture

- **Owner-gated:** only you, the owner of the device, can turn these on.
- **Off by default:** every local tool starts disabled. Nothing runs until you deliberately enable it.
- **Optionally shared to a circle:** if you choose, you can make a local tool available to a specific circle. It is never shared unless you decide to.

### Available local tools

| Tool | What it does |
|---|---|
| **PowerShell** | Run PowerShell commands |
| **CMD** | Run Windows command-prompt commands |
| **Python** | Run Python code |
| **C# scripting** | Run C# scripts |
| **Playwright browser** | Drive a web browser to fetch or interact with pages |
| **File read/write/convert** | Read, write, and convert files on your device |
| **WorkIQ** | Ask questions about your Microsoft 365 content |

### MCP tool servers

Mesh also supports **MCP tool servers**, which add extra capabilities to your agent. Some are **bundled** with the app, and you can add your own **custom** ones. As with local tools, these expand what your agent can do, so enable only what you trust.

> Tip: because these tools can act on your device, only enable the ones you understand and need, and be thoughtful before sharing any of them to a circle.

---

## 10. Community and Public Services

Mesh lets you go public in a controlled way by publishing a **service**. A service is a sandboxed, public version of your agent that anyone on your relay can talk to.

### What a public service is

You can **publish** some of your **Knowledge, Skills, and Widgets** as a public service. When someone uses your service, they are talking to a **sandboxed version of your agent** that can only use the items you attached to that service. It **never** exposes your private items or your local tools. Your personal Me chat, your private knowledge, and your device tools stay private.

### How each service is defined

Each service you publish has:

- **A fixed Category.** You choose a category that describes the service so people can find it.
- **Per-service capabilities.** A service exposes **only** the specific capabilities you attach to it. Different services can offer different things.
- **A token budget in MTokens.** Every service has a spending limit measured in **MTokens** (see below). This includes a **total lifetime budget** plus a **per-person-per-day budget**.
- **A per-person daily request limit.** This caps how many requests a single person can make to your service each day. Together with the budgets, this protects you from abuse and runaway costs.

### Understanding MTokens

**MTokens** is the app-wide token unit shown throughout Mesh. AI models measure work in tokens; MTokens make big numbers readable.

- **1 MTokens = 1,000,000 tokens.**
- MTokens are shown to **one decimal place** (for example, 2.5 MTokens).

When you set budgets for a service, you set them in MTokens. When people use your service, their usage counts against those budgets.

### Discovering and rating services

- Browse and **discover** services in the **Community** tab.
- Services have a **usage-gated upvote/downvote reputation**. This means you can only rate a service after you have actually used it, which keeps ratings honest.

### Service conversations

Talking to a service opens a **real conversation thread in Messages**. This is not a one-off query: it becomes an ongoing thread, so you can **follow up**, ask more questions, and continue where you left off.

### Using and testing your own services

You can **use and test your own services** just like anyone else would. This is a great way to check that a service behaves the way you expect before others rely on it, and to experience it from a visitor's point of view.

---

## 11. Sharing and Deep Links

Both **services** and **handles** have shareable links so others can reach you quickly.

### Two kinds of links

| Link type | What it does |
|---|---|
| **mesh:// link** | Opens the Mesh app directly to that service or handle |
| **Public https link (meshrelay.net)** | Works for anyone. If the person does not have Mesh, it offers to **install** it. |

The `mesh://` link is ideal for people who already have Mesh. The public `https` link is the one to share widely, because it works even for people who have never installed the app: it will guide them to install and then open the right place.

### Where to find the share buttons

- **For a handle:** open **Settings**, then your **identities**, and use the share button there.
- **For a service:** open the **Community** tab, go to **Your services**, and use the share button there.

The share button copies the link so you can paste it into any chat, email, or message.

---

## 12. Reporting AI Content

Mesh gives you a direct way to report inappropriate AI-generated content.

### The flag icon

**Any chat**, including your private **Me** chat, has a **flag icon** in its header, right **next to the trash icon**. Tap the flag to report inappropriate AI-generated content in that chat.

When you report:

- A dialog appears showing you **exactly what will be shared** in the report. There are no hidden details.
- You must **tick a consent checkbox** before you can submit. This confirms you agree to share the shown content.

### Report a problem (About page)

There is also a **Report a problem** entry on the **About** page for more general issues.

### Where reports go

Reports are sent **privately to the Mesh operator**. They are not posted anywhere public.

---

## 13. Managing Multiple Identities

You can keep several handles on a single device and switch between them. This is useful for separating, say, a personal handle from a work or hobby handle.

- Find your identities under **Settings**, in **Identities on this device**.
- Switch between them there.
- Each identity has its own agent, knowledge, skills, contacts, and settings. They are kept separate.

You can also share any handle from this same area using its share button (see [Sharing and Deep Links](#11-sharing-and-deep-links)).

---

## 14. Backup, Moving Devices, and Recovery

Because your keys and data live on your device, backups and device moves work a little differently from typical apps. Mesh makes this safe and straightforward.

### Creating a backup

Create a **passphrase-encrypted backup**. This backup carries:

- **All your data** (your identities, knowledge, skills, widgets, settings, and so on).
- A **handle recovery key**, which lets you re-establish your handle on a new device.

Important: the backup **never** includes your **device signing keys**. Those are unique to each device and are never exported. Choose a strong passphrase and keep it safe, because it is what protects the backup.

### Moving to a new device

On a new device, **import** your backup. The new device does two things:

1. It **mints its own key** (a fresh device signing key, unique to that device).
2. It **re-authorizes under the same handle**.

There are two ways the new device gets re-authorized:

| Method | How it works |
|---|---|
| **Device linking** | An already-authorized device issues a **QR code or link invite**. Scan or open it on the new device to link it under your handle. |
| **Recovery** | Use the **handle recovery key** from your backup to re-establish the handle on the new device. |

Device linking is the smooth path when you still have an authorized device handy. Recovery is there for when you do not (for example, if your old device is lost).

### Why this design is good for you

You never move secret device keys between machines. Each device holds its own key, and your handle can be trusted across all of them. Losing one device does not expose the others, and you can always recover your handle with your backup.

---

## 15. Settings Reference

Everything you can configure lives in **Settings**. Here are the settings that matter most.

### Model

Choose how your agent thinks: the free **Mesh-hosted model**, **bring your own API key** (Anthropic, OpenAI, Gemini, Grok, Groq, Azure OpenAI, OpenRouter), or **on-device with Foundry Local**. See [Installing and First Run](#2-installing-and-first-run) for details. You can change this at any time.

### Relay URL

You can point Mesh at a **different relay**. Set the **Relay URL** (in onboarding or in Settings) and then **Reconnect**. Remember:

- A handle is **per relay**.
- To message someone, you must be on the **same relay** as them.

### Home device (mobile)

You can designate a **home device**. When you use "ask my home agent" from mobile, the request reaches that **one** designated device rather than all of your devices. Set this under **Settings**. This is handy when one device (say, your always-on desktop) holds the tools and knowledge you want your phone to reach.

### Updates (Windows)

On Windows, the app **checks for updates** and can **install** them from **Settings**. Mesh runs as a **single instance**, so you will not accidentally open multiple copies.

### Identities on this device

Manage your multiple handles and switch between them here (see [Managing Multiple Identities](#13-managing-multiple-identities)).

---

## 16. Troubleshooting and FAQ

### I cannot message someone. What is wrong?

Most often, you are not on the **same relay**. A handle is per relay, so both people must be connected to the same relay to exchange messages. Check your **Relay URL** in Settings and confirm it matches the other person's relay. After changing it, use **Reconnect**.

### A contact's keys changed and my messages are held.

This is Mesh's **trust-on-first-use** protection at work. If a contact's identity keys change (for example, they moved to a new device), Mesh holds your messages until you **re-verify** the contact. Review the prompt and re-verify to resume the conversation. This step keeps you safe from impersonation.

### The free model stopped responding ("paused").

The **Mesh-hosted model** has a **daily token budget**. When you reach it, it pauses until the next day. You have two options:

- Wait for the daily budget to reset.
- **Bring your own API key** (Anthropic, OpenAI, Gemini, Grok, Groq, Azure OpenAI, or OpenRouter), or switch to **on-device with Foundry Local**, so you are not limited by the daily budget.

Change this under **Settings** in the model section.

### My report did not seem to send.

Reports go privately to the Mesh operator. If you are **offline** when you submit, the report is **queued** and will send once you are back online. Give it a moment after reconnecting.

### Someone I shared with cannot see my knowledge/skills/widgets.

Sharing is **scoped by circle**. Make sure the contact is in the **circle** you shared those items with. If they are in a different circle, they will not see items shared to another circle. This is intentional privacy by binding.

### My agent will not use a local tool.

Local tools are **off by default** and **owner-gated**. Open **Settings** and enable the specific tool you want. If you want a contact's circle to use it, share it to that circle explicitly.

### A connected source is not returning anything.

Live sources (Microsoft 365, Google, Dropbox, Notion, Slack) are **on-demand**. Confirm the connection is set up, and note that your agent only reaches into the source when a request needs it. Nothing is pre-copied, so results appear only when relevant.

### My phone's "ask my home agent" reaches the wrong place.

Set your **home device** in **Settings** so that mobile requests go to the one device you intend, rather than all of your devices.

### How do I move to a new phone or computer?

Create a **passphrase-encrypted backup** on your current device, then **import** it on the new device. The new device mints its own key and re-authorizes under your handle via **device linking** (scan a QR/link from an authorized device) or **recovery** (using the handle recovery key in your backup). See [Backup, Moving Devices, and Recovery](#14-backup-moving-devices-and-recovery).

### Can people read my messages on the relay?

No. Messages are **end-to-end encrypted**. The relay only routes sealed envelopes and cannot read the contents. Your keys and data stay on your device in encrypted storage.

### Can others use my agent without my permission?

Only in the ways you allow. Approved contacts see only what their **circle** can access. A **public service** exposes only the capabilities you attach to it, never your private items or local tools, and it is protected by budgets and daily request limits.

---

## 17. Glossary

| Term | Meaning |
|---|---|
| **Handle** | Your public name on Mesh, like a username. |
| **Identity** | A handle plus its keys. You can keep several on one device. |
| **Relay** | The server that routes sealed, encrypted messages. It cannot read them. |
| **E2EE (end-to-end encryption)** | Only you and your recipient can read your messages. |
| **Agent** | Your personal AI assistant tied to an identity. |
| **Me** | Your private chat with your own agent. |
| **Thread** | A separate topic chat within Me. |
| **Agent / Person toggle** | Chooses whether a message goes to a contact's agent or to them directly. |
| **Group** | A create-only, human-to-human conversation whose state is stored on members' devices. |
| **Contact** | Someone you have added by handle. |
| **Circle** | A named group of contacts that scopes what you share. |
| **Privacy by binding** | What a contact can access is bound to their circle. |
| **Knowledge** | Documents and notes your agent can use. |
| **Skill** | Reusable instructions or capabilities for your agent. |
| **Widget** | A mini HTML app your agent can build, pin, and share. |
| **Local tool** | A capability that runs on your device; owner-gated and off by default. |
| **MCP tool server** | An add-on that gives your agent extra capabilities. |
| **Service** | A public, sandboxed version of your agent that others can use. |
| **Category** | The fixed label that describes a published service. |
| **MTokens** | The app-wide token unit. 1 MTokens = 1,000,000 tokens, shown to one decimal. |
| **Community** | The tab where you discover and manage public services. |
| **Home device** | The single device your mobile "ask my home agent" reaches. |
| **Backup** | A passphrase-encrypted export of your data plus a handle recovery key. |
| **Device linking** | Authorizing a new device via a QR/link invite from an existing device. |
| **Recovery** | Re-establishing your handle using the recovery key from your backup. |

---

*Thank you for using Mesh. Your conversations are yours, your agent works for you, and you decide what to share and with whom.*
