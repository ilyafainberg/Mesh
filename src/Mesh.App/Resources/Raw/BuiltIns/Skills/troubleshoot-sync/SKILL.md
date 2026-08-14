---
id: builtin:skill:troubleshoot-sync
type: skill
name: Troubleshoot cross-device synchronization
description: Diagnose missing or inconsistent Mesh state between linked devices.
roles: owner
triggers: not syncing,missing messages,device connection,replication,linked devices
---

# Instructions

1. Identify the Mesh identity, the devices involved, the missing state, and the last time both devices agreed.
2. Confirm that each device can open its encrypted local database and that both are linked to the same current identity.
3. Check whether the devices are authorized by the current device roster and custody state.
4. Determine which devices are online and connected to the Relay at the same time.
5. Inspect safe replication status, pending work, acknowledgments, and diagnostics without exposing message bodies or keys.
6. Separate transport failure, authorization failure, local database failure, and conflict handling before changing state.
7. Retry the smallest safe replication path and verify the previously missing item on the receiving device.
8. Use recovery or device removal only when the evidence shows an authority problem and the owner understands the consequence.
