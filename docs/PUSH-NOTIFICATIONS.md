# Cross-platform notifications and background sync

Mesh exposes notification context only after authenticated decryption and a durable local commit.
Protocol 9 replication remains online-only: senders retain encrypted events, and the relay stores no
replication frame or notification intent.

APNs and FCM are wake channels. Their payloads contain no message text, sender identity, topic title,
prompt, agent response, event id, frame id, key, or deep link. A device with alert permission may
receive the generic text `Mesh` / `New activity`; exact titles, previews, routes, mute decisions, and
badges are computed locally from SQLCipher state.

## Push payloads

Only the authenticated relay `Wake` operation can invoke push delivery. Failed ordinary forwarding
does not trigger a push. A wake targets one authorized device and carries a stable opaque SHA-256 wake
id plus a notification-worthy flag. If that device is already online, the relay accepts the wake
without sending APNs or FCM.

The relay selects `AlertAndSync` only when the wake is notification-worthy and the target device has
alerts enabled. Every other wake uses `SyncOnly`.

APNs uses one of two modes:

| Mode | Headers and payload |
|---|---|
| `SyncOnly` | `apns-push-type: background`, priority `5`, `content-available: 1` |
| `AlertAndSync` | `apns-push-type: alert`, priority `10`, generic alert, sound, and `content-available: 1` |

Both APNs modes include nested Mesh metadata:

```json
{
  "aps": { "content-available": 1 },
  "mesh": {
    "type": "sync",
    "v": 9,
    "wake_id": "opaque-stable-id"
  }
}
```

`AlertAndSync` additionally adds the generic `aps.alert` and `aps.sound` values. iOS still accepts the
legacy flat Mesh fields while deployed clients transition.

FCM sends only a high-priority data message with collapse key `mesh-sync`:

```json
{
  "mesh_type": "sync",
  "mesh_version": "9",
  "wake_id": "opaque-stable-id",
  "show_alert": "0"
}
```

`show_alert` is `1` only for `AlertAndSync`. Android renders the generic alert itself only while the
app is backgrounded. iOS suppresses generic wake presentation in the foreground.

## Local notification pipeline

1. A Protocol 9 domain envelope carries an encrypted `NotificationIntent` beside the encrypted
   domain mutation.
2. The receiving device authenticates and decrypts the event, commits the domain projection and
   replication cursor, then publishes a committed activity.
3. A SQLCipher notification ledger deduplicates the stable id and records one of `pending`,
   `scheduled`, `suppressed`, or `read`.
4. Local policy applies historical suppression, origin-account suppression, current-view state,
   do-not-disturb, contact mute, preview mode, sound, and whether the OS already displayed a generic
   remote alert.
5. The platform notifier uses the stable id to replace or remove the native notification and updates
   the application badge from ledger attention state.

A process restart or account activation retries ledger entries left in `pending`. Stable native ids
make retry safe if the OS accepted a notification immediately before the process stopped. Suppression
and attention are separate: do-not-disturb, mute, or an already-visible remote alert suppresses the
contextual banner but keeps the item unread until the user opens it. Historical and `Notify=false`
entries never require attention.

Bootstrap snapshots carry an explicit historical-suppression intent, so old messages and topic lines
never become new alerts. Owner-sent sibling message copies are marked read and never create a visible
alert. The default preview mode is `Never`; message, decision, and topic content is hidden until the
user opts in.

Notification activation uses exact local routes:

- `mesh://messages/{conversationId}`
- `mesh://me/{threadId}`
- `mesh://me/{threadId}/ask/{promptId}`
- `mesh://requests`
- `mesh://approvals`

A generic remote-alert tap first runs a bounded synchronization and then opens the highest-priority
unread ledger activity. Account changes clear delivered notifications before recovering pending work
and applying the active account's badge.

A visible remote alert is intentionally device-wide because the relay cannot inspect the encrypted
entity. Per-conversation mute suppresses the contextual local banner after synchronization, but it
cannot retract a generic alert that APNs or Android already displayed. Device do-not-disturb disables
relay-visible alert mode and therefore uses silent wakes.

## Background synchronization

When iOS or Android backgrounds Mesh, the foreground SignalR connection is stopped. A push wake
acquires a bounded connection lease, authenticates with the normal Protocol 9 challenge, drains
eligible legacy records, pulls missing Protocol 9 events from online peers, persists them, sends
signed receipts, and disconnects after quiescence. The default wake budget is 25 seconds.

Background sessions may persist passive updates such as messages, receipts, terminal topic state,
and device-sync batches. They do not execute agents, public services, topic turns, cancellation work,
attachment transfer, or snapshot requests. Nonterminal streaming deltas are stored in bounded
SQLCipher deferred-update rows and replayed on foreground activation; durable terminal state deletes
obsolete deferred deltas and remains authoritative.

Android coalesces process callbacks by stable wake id and enqueues one unique WorkManager job with
`ExistingWorkPolicy.Keep`, so concurrent wakes share one synchronization pass. iOS also schedules
`BGAppRefreshTask` as an opportunistic fallback.

Protocol 9 relays must advertise `onlineReplication`, `onlineWake`, and `contentlessPush` before the
client uses this path.

## End-to-end flow

1. The client registers its APNs or FCM token with a device-key-signed request. Alert authorization
   is reported separately from token availability, and do-not-disturb disables alert mode.
2. The relay persists `(handle, deviceId, platform, token, alertsEnabled)` when durable storage is
   configured. Refreshing a device token preserves that device's visible and silent throttle state.
3. A sender with outstanding device-specific custody calls the authenticated, ephemeral `Wake`
   operation. No encrypted probe or replication payload is stored at the relay.
4. The sender derives a stable wake id from notification or synchronization context. The relay and
   mobile process deduplicate that id without receiving the underlying notification id.
5. The mobile client runs one bounded synchronization session and records notification context only
   after local decryption and persistence.
6. Signed receipts advance device-specific custody. Failed or expired sessions leave work pending for
   the next wake or foreground connection.
7. Sign-out and account switching unregister the previous account identity's token. APNs/FCM
   invalid-token responses also remove stale registrations.

## Delivery guarantees and limits

- Silent APNs delivery and `BGAppRefreshTask` are opportunistic. iOS may delay or discard them, and
  does not deliver silent pushes after the user force-quits the app.
- Visible wakes are spaced by at least 5 seconds per device and limited to 60 per rolling hour.
- Silent wakes are spaced by at least 30 seconds per device and limited to 12 per rolling hour.
- Visible and silent throttle windows are independent.
- Wake ids are deduplicated for one hour, and the sender keeps unsatisfied encrypted custody locally.
- A receipt from one device does not mark sibling devices caught up; each authorized device advances
  independently.
- A device linked after an event is not woken for that event unless its key appears in the encrypted
  recipient slots.
- Visible APNs alerts may be rendered without launching Mesh. Encrypted content appears only after a
  later successful synchronization.
- Physical APNs/FCM delivery still depends on valid production credentials, provisioning, OS policy,
  network availability, and a real device.

## Relay configuration

Push is disabled unless at least one backend is configured. Environment variables also have matching
`Push:...` configuration keys.

### APNs

| Variable | Purpose |
|---|---|
| `APNS_KEY_ID` | APNs auth-key id |
| `APNS_TEAM_ID` | Apple Developer team id |
| `APNS_BUNDLE_ID` | App bundle id used as `apns-topic` |
| `APNS_PRIVATE_KEY` | `.p8` PEM contents or a path to the key file |
| `APNS_PRODUCTION` | `true` for production APNs; otherwise sandbox |

### FCM

| Variable | Purpose |
|---|---|
| `FCM_SERVICE_ACCOUNT_JSON` | Google service-account JSON or a path to it |

## Client provisioning

### iOS

1. Enable Push Notifications for `net.meshrelay.mesh` and create an APNs auth key.
2. Use a provisioning profile with Push Notifications.
3. The app includes `aps-environment`, `remote-notification`, `fetch`, the background task id
   `net.meshrelay.mesh.sync.refresh`, and protected SQLCipher storage.
4. Match `APNS_PRODUCTION` to the provisioning environment.

### Android

Android FCM is opt-in, so the default build needs no Firebase configuration.

1. Create a Firebase Android app for `net.meshrelay.mesh` and download `google-services.json`.
2. Keep the file outside source control. By default place it at
   `Platforms\Android\google-services.json`; CI can pass an alternate path with
   `-p:MeshGoogleServicesJson=C:\secure\google-services.json`.
3. Build with `-p:MeshPushEnabled=true`.
4. Configure `FCM_SERVICE_ACCOUNT_JSON` on the relay.

Android 13 and later require `POST_NOTIFICATIONS`; the manifest declares it and the client requests it
during registration. Android notification channels are `Mesh messages`, `Mesh topics`, and
`Mesh decisions`.
