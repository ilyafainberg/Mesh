## Mesh v1.4.0

Signed Windows installer.

### Fixes and improvements
- **In-chat generated apps now run JavaScript.** Sandboxed previews switched from `srcdoc` to Blob URLs so scripts execute (sandbox remains locked down: no same-origin access).
- **Guest agent answers questions instead of only greeting.** Fixed the message-channel tagging so an incoming agent request is kept in the guest agent's context; it now uses shared knowledge and tools to reply properly.
- **Correct message labels.** Replies are labelled by who they are addressed to ("to their agent", "to them", "their agent", "them") rather than mislabelling a reply to a person as "to their agent".
- **Type while the agent is thinking.** The composer is no longer blocked during a turn; messages you send are queued and answered on the next turn (both the "Me" self-chat and conversations).
- **Web search works on mobile.** Android/iOS now use a browserless DuckDuckGo path (desktop keeps the headless-browser engine).

### Install
Download `Mesh-Setup-v1.4.0.zip`, extract, run `Mesh-Setup-v1.4.0.exe`.
