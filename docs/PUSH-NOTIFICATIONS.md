# Push notifications (mobile wake)

Mesh mobile clients (iOS and Android) can be woken by a push notification when a
notifiable message reaches their handle or when an agent finishes a topic
response on another device while the receiving device is offline (app
backgrounded, screen off, or suspended by the OS). A phone is also notified when
a message was delivered live to another device, such as an open desktop. This
document explains the privacy model, how it works end to end, and the
provisioning an operator and a client build need.

Push is optional. With nothing configured, the relay behaves exactly as before:
messages still queue in the per-device inbox and are delivered when the client
next reconnects. Push only improves timeliness; it never carries message content.

## Privacy model (Option 1)

The relay sees the cleartext envelope `Kind`, `From` / `To` handles, and an
optional metadata-only `PushHint`. It never sees message bodies (they are
end-to-end encrypted), a group's name, a topic title, a prompt, or an agent
response. The relay composes exactly three metadata-only alerts:

| Situation | Alert shown |
|---|---|
| A direct message is queued for an offline device | `Message from @sender` |
| A group (fanout) message is queued for an offline device | `New group message` |
| An agent successfully completes a remotely hosted topic turn | `Your agent replied in a topic` |

For the third case, the terminal update carries `PushHint = topic.response`.
That discloses only that a successful topic response finished between two of the
same owner's devices. The prompt, response, topic title, and all run progress
remain encrypted. APNs or FCM sees that a wake was sent plus the small alert text
above, but no Mesh content.

## How it works end to end

1. On sign-in the client asks the OS for a push token (APNs on iOS, FCM on
   Android) and registers it with the relay at `POST /handles/{handle}/push`.
   The request is signed with the device key (proof of possession), so only a
   device already authorized under the handle can register a token for it.
2. The relay stores `(deviceId -> platform, token)` alongside the handle record
   (durable when Cosmos is configured).
3. When `MeshRouter` handles a notifiable envelope it fires a fire-and-forget
   wake through the matching sender (APNs or FCM):
     - Handle-wide direct and group messages wake each registered device that is
       not currently connected on any relay instance. If one device received the
       message live, offline siblings are still woken and receive content by
       device sync when they reconnect.
     - A successful topic response is a device-targeted `topic.run.update` with
       `PushHint = topic.response`. If the originating device is offline, the
       encrypted terminal update is queued for that device and only its token is
       woken.
   Device presence is read from the backplane, so the online/offline split is
   correct across replicas. Ordinary topic progress, failed or cancelled runs,
   sync traffic, receipts, and control envelopes never push.
4. On sign-out the client calls `DELETE /handles/{handle}/push` (also signed) so
   a signed-out device is no longer woken.

### Guaranteed vs best-effort

- The visible alert (drawn by the OS from the payload) is the reliable tier. It
  is shown whenever the push is delivered, even if the app is not running.
- Silent background wake-and-enrich (replacing the alert with friendlier text on
  the device) is deliberately not relied on. iOS throttles and may drop silent
  pushes, and never delivers them after a force-quit. A dropped push is not a
  lost message: the inbox or device sync still delivers it on the next reconnect.

## Relay configuration

The relay sends pushes only when at least one backend is configured. All settings
are environment variables (or the matching `Push:...` config key). See also the
push section in [SELF-HOSTING.md](./SELF-HOSTING.md).

APNs (iOS):

| Variable | Purpose |
|---|---|
| `APNS_KEY_ID` | The 10-character APNs auth-key id |
| `APNS_TEAM_ID` | Your Apple Developer team id |
| `APNS_BUNDLE_ID` | The app bundle id (`net.meshrelay.mesh`), sent as the apns-topic |
| `APNS_PRIVATE_KEY` | The `.p8` PEM contents, or a path to the `.p8` file |
| `APNS_PRODUCTION` | `true` for the production APNs host; otherwise the sandbox host is used |

FCM (Android):

| Variable | Purpose |
|---|---|
| `FCM_SERVICE_ACCOUNT_JSON` | The Google service-account JSON, or a path to the `.json` file |

## Client provisioning

### iOS (APNs)

APNs needs no build flag; it is wired through entitlements and a push-enabled
provisioning profile.

1. In the Apple Developer portal, enable the Push Notifications capability on the
   `net.meshrelay.mesh` App ID and create an APNs auth key (`.p8`). Give the key
   id, team id, and `.p8` to the relay (see the APNs variables above).
2. The client already ships the required pieces:
   - `Platforms/iOS/Entitlements.plist` sets `aps-environment` (start with
     `development`, switch to `production` for App Store / TestFlight builds).
   - `Platforms/iOS/Info.plist` declares the `remote-notification` background
     mode.
   - `AppDelegate` forwards the APNs token to `ApplePushService` and presents
     alerts while the app is foregrounded.
3. Build with a provisioning profile that includes the Push Notifications
   capability, or device signing will fail.

### Android (FCM)

Android FCM is opt-in at build time so the default build needs no Firebase
provisioning.

1. Create a Firebase project and add an Android app with package name
   `net.meshrelay.mesh`. Download its `google-services.json`.
2. Place `google-services.json` in `Platforms/Android/`. Do not commit it.
3. Build with `MeshPushEnabled=true`, for example:
   `dotnet build src/Mesh.App/Mesh.App.csproj -f net10.0-android -c Release -p:MeshPushEnabled=true`.
   That defines `MESH_FIREBASE`, which references `Xamarin.Firebase.Messaging`
   and compiles the FCM code paths in `FirebasePushService` and
   `MeshFirebaseMessagingService`. Align the package version in the csproj with
   your installed .NET Android Firebase workload.
4. Create a Firebase service account and give its JSON to the relay via
   `FCM_SERVICE_ACCOUNT_JSON`.

Android 13+ requires the `POST_NOTIFICATIONS` runtime permission, which the app
requests on first registration; the manifest already declares it and the FCM
default notification channel.
