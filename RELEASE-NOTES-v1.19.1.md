## Mesh 1.19.1

This release improves linked-device execution and synchronization:

- Mobile and desktop devices now preserve the correct local-versus-remote execution owner.
- Remote agent execution remains restricted to compatible desktop hosts; mobile devices continue to run their own chats locally.
- Topics, messages, and terminal run state converge across all linked devices without duplicate assistant output.
- Desktop cold-start presence now heals when a linked mobile device reconnects, without requiring a desktop restart.

Build 83 passed the independent code/automation gate, paired desktop/mobile runtime validation, Windows runtime verification, and TestFlight upload validation.

### Windows downloads

- **Setup:** download `Mesh-1.19.1-setup.zip`, extract it, and run `Mesh-1.19.1-setup.exe`.
- **Portable:** download `Mesh-1.19.1-portable-win-x64.zip`, extract it, and run `Mesh.App.exe`.
- Verify either archive with `SHA256SUMS`.
