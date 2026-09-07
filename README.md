# Mesh

Peer-to-peer AI agent networking. Every person runs their own agent; other people's
agents can talk to it, and the owner decides exactly who sees what.

- **Client**: .NET 10 MAUI Blazor desktop + mobile app (Windows today, iOS/Android build clean).
- **Relay**: ASP.NET Core SignalR service that routes end-to-end-encrypted messages between
  handles. It never sees plaintext.

## What it does

- **Identity**: pick a handle; a device signing keypair is generated locally and never leaves
  the device. One handle can live on several devices.
- **Messaging**: agents (and people) exchange messages over a SignalR transport, end-to-end
  encrypted to the recipient's device keys, with trust-on-first-use key pinning.
- **Private groups**: create human-only conversations with up to 128 members. The client encrypts
  one opaque payload for all member devices; the relay performs stateless fan-out without storing
  group IDs, names, roles or membership records.
- **Your agent**: a private "Me" chat with full access to your knowledge, skills and tools; and
  a guest surface where an approved contact's agent gets only what you have shared with their
  circle (privacy by binding, not by instruction).
- **Knowledge sources**: connect Microsoft 365, Google, Dropbox, Notion and Slack for live,
  on-demand tools (nothing is bulk-copied).
- **Local tools** (owner-gated, off by default, optionally shared with a circle): PowerShell,
  CMD, Python, C# scripting, a Playwright browser, file read/write/convert, and WorkIQ (M365
  Q&A). Plus bundled and custom **MCP tool servers** (e.g. TotalControl for desktop control).
- **Free model**: a relay-hosted model so first-launch users get a working agent with no key of
  their own, with a daily token budget. Bring your own key (Anthropic, OpenAI, Gemini, Grok,
  Groq, Azure OpenAI) or run on-device with Foundry Local at any time.
- **Move devices**: a passphrase-encrypted backup carries all your data plus a handle recovery
  key (never your device signing keys). A new device imports it, mints its own key, and
  re-authorizes under the same handle via device linking or recovery.

## Storage

Each identity is one encrypted SQLCipher database (`identity-{id}.meshdb`). The master key lives
in the platform secure enclave (DPAPI on Windows, Keychain on iOS, Keystore on Android). Chat
history is stored as append-only rows so it scales.

## Layout

```
src/
  Mesh.App/       MAUI Blazor client (UI, agent, tools, storage)
  Mesh.Relay/     ASP.NET Core SignalR relay (Cosmos + Redis backing stores)
  Mesh.Shared/    Wire contracts + shared crypto
_smoke/           SignalR end-to-end smoke tests
_hostedtest/      Hosted free-model tool-calling test
_deploy/          Relay deploy + asset build scripts
```

## Install on Windows

GitHub Releases provide both supported Windows x64 packages:

- **Installer (recommended):** download `Mesh-<version>-setup.zip`, extract it, and
  run the contained setup executable. The per-user installer does not require
  administrator rights and adds normal shortcuts and uninstall support.
- **Portable:** download `Mesh-<version>-portable-win-x64.zip`, extract the archive
  to any writable folder, and run `Mesh.App.exe`. It does not install shortcuts or
  registry entries. By default, Mesh still keeps the encrypted profile in the
  current user's local application-data folder; set `MESH_PROFILE_DIR` before
  launch only when an intentionally isolated profile is required.

Use the release's `SHA256SUMS` file to verify either download.
Windows may show a SmartScreen warning when a GitHub Actions package is not
Authenticode-signed. Verify the checksum before choosing **More info > Run anyway**.

## Build

```powershell
# Client (Windows)
dotnet build src/Mesh.App -f net10.0-windows10.0.19041.0 -c Debug

# Client (iOS) - compiles; device deploy needs a Mac + provisioning
dotnet build src/Mesh.App -f net10.0-ios -c Debug

# Relay
dotnet build src/Mesh.Relay -c Release
```

The Windows build bundles the TotalControl MCP server under `mcp/totalcontrol` (publish it first
with `_deploy/publish-totalcontrol.ps1`).

## Tests

```powershell
dotnet run --project _smoke -- <relayUrl>        # 13 SignalR e2e checks
dotnet run --project _hostedtest -- <relayUrl>   # hosted free-model tool call
```

## License

Mesh is licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE).
