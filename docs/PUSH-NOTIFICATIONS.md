# Push notifications and iOS background sync

Mesh mobile clients can receive metadata-only push notifications when encrypted
work is queued at the relay. On iOS, eligible pushes can also grant a short,
best-effort background execution window. Mesh uses that window to connect to the
relay, drain encrypted envelopes, decrypt and persist passive updates, and
acknowledge only the records that were stored successfully.

The durable relay inbox remains authoritative. APNs and FCM never carry message
content, keys, topic text, group names, prompts, or agent responses. A dropped or
throttled push delays delivery but does not lose the queued envelope.

## Privacy model

The relay already sees the cleartext envelope `Kind`, `From` / `To` handles, device
routing ids, and an optional metadata-only `PushHint`. Message bodies remain
end-to-end encrypted. The relay can compose these visible alerts:

| Situation | Alert shown |
|---|---|
| A direct message or agent-addressed message is queued | `Message from @sender` |
| A group message is queued | `New group message` |
| A remotely hosted topic turn completes successfully | `Your agent replied in a topic` |

For the topic case, the terminal update carries `PushHint = topic.response`. This
discloses only that a successful response finished between two devices owned by
the same handle.

A pure background APNs payload contains only:

```json
{
  "aps": { "content-available": 1 },
  "mesh": { "type": "sync", "version": 1 }
}
```

## APNs delivery modes

The relay selects one of three APNs modes per envelope and device:

| Mode | Used when | APNs headers and payload |
|---|---|---|
| Alert | The work requires foreground processing, or background sync is disabled | `apns-push-type: alert`, priority `10`, visible alert only |
| Alert and background | A visible alert represents a passive update that iOS may persist immediately | Alert push, priority `10`, plus `content-available: 1` |
| Background | No visible alert is available or alert permission is disabled, and the queued kind is safe for passive sync | `apns-push-type: background`, priority `5`, collapse id `mesh-sync`, no alert text |

Foreground-only work is never converted into a silent wake when alert permission
is disabled. Set `PUSH_BACKGROUND_SYNC_ENABLED=false` to restore alert-only APNs
behavior without changing client builds.

## Passive background policy

An iOS background session may persist and acknowledge passive state updates such
as direct and group messages, responses, receipts, topic run updates, and
device-sync operation batches. It does not execute agents, public services, topic
turns, cancellation work, attachment transfer, or snapshot requests. Those
records remain queued for a foreground connection or an available linked device.

When iOS backgrounds Mesh, the normal SignalR connection is stopped so relay
presence clears and leased records are released. A silent wake uses a separate
`backgroundSync=1` connection. The relay suppresses atomic agent dispatch for that
connection, excludes it from ordinary online presence, and the client applies the
passive policy again before acknowledging an envelope.

Protocol 6 relays advertise `backgroundSync: true` with `durableDelivery: true` in
their health capabilities. The client requires both flags before opening a
background connection; protocol version alone is not sufficient. Older relays
continue normal foreground delivery and retain queued records until the app opens.

Topic streaming deltas are not rendered during a background wake. Durable topic
state, terminal updates, and committed device-sync conversation lines remain the
authoritative state shown when the app next opens.

## End-to-end flow

1. The client registers with APNs or FCM and signs `POST /handles/{handle}/push`
   with its device key. iOS requests an APNs device token independently of visible
   notification permission and reports the current alert authorization separately.
2. The relay stores `(deviceId -> platform, token, alertsEnabled)` with the handle.
   Cosmos-backed relays persist this state across restarts.
3. Every encrypted envelope is enqueued before live delivery is attempted. When a
   target device is offline, or an offline sibling should be updated, the relay
   chooses the appropriate push mode.
4. An iOS wake runs one coalesced synchronization session with a default 25-second
   budget. The session authenticates normally, drains the durable inbox, decrypts
   and persists safe records, then acknowledges each successful record.
5. `BGAppRefreshTask` provides an additional opportunistic refresh path. iOS, not
   Mesh, decides whether and when that task runs.
6. On sign-out the client signs `DELETE /handles/{handle}/push`, removing the token.
   APNs responses such as `Unregistered` also remove invalid tokens automatically.

## Delivery guarantees and limits

- Silent pushes and `BGAppRefreshTask` are opportunistic. iOS may delay or discard
  them, especially under power, network, usage, or system scheduling constraints.
- iOS does not deliver silent pushes to an app the user force-quit. Queued records
  are drained after the user opens Mesh again.
- Pure silent wakes are coalesced per device: at least 20 minutes apart and no more
  than three in one hour. Visible alerts with background content are not subject to
  this silent-wake throttle.
- Once APNs delivers a visible alert, iOS can render it without launching Mesh.
  The encrypted content still comes only from the relay inbox.
- A failed wake, expired execution budget, or transient network error leaves
  unacknowledged envelopes queued for normal redelivery.

## Relay configuration

Push is disabled unless at least one backend is configured. Environment variables
also have matching `Push:...` configuration keys.

### APNs

| Variable | Purpose |
|---|---|
| `APNS_KEY_ID` | The APNs auth-key id |
| `APNS_TEAM_ID` | Apple Developer team id |
| `APNS_BUNDLE_ID` | App bundle id, used as `apns-topic` |
| `APNS_PRIVATE_KEY` | `.p8` PEM contents, or a path to the `.p8` file |
| `APNS_PRODUCTION` | `true` for production APNs; otherwise the sandbox host is used |
| `PUSH_BACKGROUND_SYNC_ENABLED` | Set to `false` for alert-only APNs behavior; default is enabled |

### FCM

| Variable | Purpose |
|---|---|
| `FCM_SERVICE_ACCOUNT_JSON` | Google service-account JSON, or a path to the JSON file |

## Client provisioning

### iOS

1. Enable Push Notifications for the `net.meshrelay.mesh` App ID and create an
   APNs auth key.
2. Use a provisioning profile containing the Push Notifications capability.
3. The client already includes:
   - `aps-environment` entitlements for development and release builds.
   - `remote-notification` and `fetch` background modes.
   - the permitted background task id `net.meshrelay.mesh.sync.refresh`.
   - APNs callbacks for device tokens and silent wakes.
   - SQLCipher key and file protection that remains readable after the device's
     first unlock.
4. Match `APNS_PRODUCTION` to the provisioning environment. Sandbox and production
   device tokens are not interchangeable.

### Android

Android FCM is opt-in at build time so the default build needs no Firebase
provisioning.

1. Create a Firebase project and add an Android app with package name
   `net.meshrelay.mesh`. Download `google-services.json`.
2. Place it in `Platforms/Android/`. Do not commit it.
3. Build with `MeshPushEnabled=true`, for example:
   `dotnet build src/Mesh.App/Mesh.App.csproj -f net10.0-android -c Release -p:MeshPushEnabled=true`.
4. Configure `FCM_SERVICE_ACCOUNT_JSON` on the relay.

Android 13 and later require the `POST_NOTIFICATIONS` runtime permission. The
manifest declares it and the client requests it during registration.
