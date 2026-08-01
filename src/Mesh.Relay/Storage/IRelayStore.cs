using Mesh.Relay.RateLimiting;
using Mesh.Shared;

namespace Mesh.Relay.Storage;

/// <summary>
/// A persisted handle registration: the handle, its display name, and the set of
/// device public keys authorized to act as it. Serializable so it can live in a
/// durable store (Cosmos) or in memory. Device keys are base64 SubjectPublicKeyInfo.
///
/// Protocol 9: this record is metadata only. The relay is an online-only opaque
/// forwarder and never persists message, sync, attachment, or agent payloads, so this
/// store holds identity/authorization metadata exclusively (handles, device and recovery
/// keys, auth generation, custody head, push token metadata, invites, service directory,
/// and administrative rate policy).
/// </summary>
public sealed class StoredHandle
{
    public string Handle { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> DevicePublicKeys { get; set; } = new();

    /// <summary>
    /// Monotonic authorization generation for the handle's custody chain. A device revocation or
    /// custody append advances this value so a client can prove it is presenting current authority
    /// during the Protocol 9 connect handshake. Strictly non-decreasing.
    /// </summary>
    public long AuthGeneration { get; set; }

    /// <summary>
    /// The current head hash of the handle's custody chain (Protocol 9). Presented by a client at
    /// connect so the relay can reject a device that is not operating against current custody.
    /// Empty until the first custody entry is recorded.
    /// </summary>
    public string CustodyHead { get; set; } = "";

    /// <summary>
    /// The handle's recovery public key, captured at registration. Used to authorize a brand-new
    /// device via <c>POST /handles/{handle}/recover</c> when no existing device can issue a link
    /// invite. Null when the handle was registered without recovery support. First writer wins:
    /// once set it is never overwritten, so a later attacker cannot replace it.
    /// </summary>
    public string? RecoveryPublicKey { get; set; }

    /// <summary>
    /// Friendly per-device names, keyed by the stable device id (see <c>DeviceProtocol.DeviceId</c>).
    /// A device may set a name so the owner can pick a "home device" from the directory
    /// (GET /handles/{handle}/devices). Absence of an entry just means the device is unnamed.
    /// </summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new();

    /// <summary>Platform identifiers keyed by stable device id.</summary>
    public Dictionary<string, string> DevicePlatforms { get; set; } = new();

    /// <summary>Remote-agent opt-in state keyed by stable device id.</summary>
    public Dictionary<string, bool> DeviceRemoteAgentEnabled { get; set; } = new();

    /// <summary>Atomic agent-dispatch protocol support keyed by stable device id.</summary>
    public Dictionary<string, bool> DeviceAgentHostEnabled { get; set; } = new();

    /// <summary>Mesh protocol version keyed by stable device id.</summary>
    public Dictionary<string, int> DeviceProtocolVersions { get; set; } = new();

    /// <summary>The owner's selected device for answering agent-addressed messages.</summary>
    public string? AgentPrimaryDeviceId { get; set; }

    /// <summary>An optional second device used only while the primary is unavailable.</summary>
    public string? AgentFailoverDeviceId { get; set; }

    public string AgentRoutingVersion { get; set; } = "";
    public bool AgentPrimaryWasSelectedAutomatically { get; set; }

    /// <summary>
    /// Push tokens keyed by stable device id, used to wake a backgrounded device via APNs/FCM.
    /// The relay only sends a content-free wake; it never puts message contents here.
    /// </summary>
    public Dictionary<string, DevicePushToken> DevicePushTokens { get; set; } = new();
}

/// <summary>A registered push token for one device: the push platform and the opaque APNs/FCM token.</summary>
public sealed class DevicePushToken
{
    public string Platform { get; set; } = "";
    public string Token { get; set; } = "";
    public bool AlertsEnabled { get; set; } = true;
    public DateTimeOffset? BackgroundPushWindowStartedAt { get; set; }
    public int BackgroundPushCount { get; set; }
    public DateTimeOffset? LastBackgroundPushAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A pending device-link invite: single use, short lived, addressed to a handle.</summary>
public sealed class StoredInvite
{
    public string Handle { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>The outcome of revoking one device from a handle's authorized set.</summary>
public sealed record DeviceRevocationResult(bool Revoked, long AuthGeneration);

/// <summary>
/// Metadata store for the relay. Protocol 9 is online-only: the relay never persists message,
/// sync, attachment, or agent payloads, so this seam holds identity and authorization metadata
/// exclusively. Presence (who is connected right now) is intentionally NOT here, because live
/// sockets cannot be persisted; that is the backplane's job.
///
/// Implementations must be safe for concurrent use. All methods are async so a network-backed
/// store (Cosmos) fits behind the same seam as the in-memory default.
/// </summary>
public interface IRelayStore
{
    /// <summary>Loads a handle registration, or null if the handle is unclaimed.</summary>
    Task<StoredHandle?> GetHandleAsync(string handle, CancellationToken ct = default);

    /// <summary>
    /// Loads a handle for authorization of an idempotent deletion retry, including a registration
    /// whose deletion tombstone is already present. Must not be used by registration or routing.
    /// </summary>
    Task<StoredHandle?> GetHandleForDeletionAsync(string handle, CancellationToken ct = default);

    /// <summary>
    /// Atomically creates the handle if unclaimed, or adds the device key to an existing
    /// registration only when the key is already authorized (idempotent re-assert) or the
    /// caller passes <paramref name="allowNewDevice"/> (device-link redemption).
    /// Returns the resulting record and whether the supplied device key is authorized on it.
    /// </summary>
    Task<(StoredHandle record, bool deviceAuthorized)> UpsertHandleAsync(
        string handle, string devicePublicKey, string? displayName, bool allowNewDevice, CancellationToken ct = default);

    /// <summary>
    /// Removes a handle registration entirely, freeing the name to be claimed again. Also drops the
    /// handle's pending invites and administrative rate policy. Returns false if the handle did not
    /// exist. Callers must authenticate the request before calling this.
    /// </summary>
    Task<bool> DeleteHandleAsync(string handle, CancellationToken ct = default);

    /// <summary>Updates only the display name of an existing handle. No-op if missing.</summary>
    Task SetDisplayNameAsync(string handle, string displayName, CancellationToken ct = default);

    /// <summary>
    /// Sets a friendly name for one device (by stable device id) under a handle, so the per-device
    /// directory can show it as a pickable "home device". No-op if the handle is missing.
    /// </summary>
    Task SetDeviceNameAsync(string handle, string deviceId, string name, CancellationToken ct = default);

    /// <summary>Updates the directory metadata for one authorized device. No-op if missing.</summary>
    Task SetDeviceMetadataAsync(
        string handle,
        string deviceId,
        string? name,
        string platform,
        bool remoteAgentEnabled,
        bool agentHostEnabled,
        int protocolVersion = MeshProtocol.Version,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes one device from a handle's authorized set, advancing the handle's auth generation so
    /// any presented authority for the removed device is stale. Returns whether a device was removed
    /// and the resulting auth generation. Refuses to remove the handle's last device.
    /// </summary>
    Task<DeviceRevocationResult> RevokeDeviceAsync(
        string handle,
        string targetDeviceId,
        string? authorizingPublicKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Advances the handle's custody head and auth generation after a validated custody append.
    /// The update is strict: <paramref name="expectedAuthGeneration"/> must match the stored value
    /// or the update is refused (optimistic custody progression). Returns false if the handle is
    /// missing or the expected generation did not match.
    /// </summary>
    Task<bool> AdvanceCustodyAsync(
        string handle,
        long expectedAuthGeneration,
        long newAuthGeneration,
        string newCustodyHead,
        CancellationToken ct = default);

    Task<bool> SetAgentRoutingAsync(
        string handle,
        string primaryDeviceId,
        string? failoverDeviceId,
        string expectedVersion,
        CancellationToken ct = default);

    /// <summary>Registers or refreshes a device's push token (APNs/FCM) under a handle. No-op if the handle is missing.</summary>
    Task SetDevicePushTokenAsync(
        string handle, string deviceId, string platform, string token, bool alertsEnabled,
        CancellationToken ct = default);

    /// <summary>Atomically reserves one rate-limited silent background wake for a device.</summary>
    Task<bool> TryAcquireBackgroundPushAsync(
        string handle,
        string deviceId,
        DateTimeOffset now,
        TimeSpan minimumInterval,
        TimeSpan window,
        int maxCount,
        CancellationToken ct = default);

    /// <summary>Removes a device's push token (for example on sign-out). No-op if absent.</summary>
    Task RemoveDevicePushTokenAsync(string handle, string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Sets the handle's recovery public key, but only if one is not already stored (first writer
    /// wins). This prevents a later attacker who has gained a device key from overwriting the
    /// recovery key. No-op if the handle is missing or already has a recovery key.
    /// </summary>
    Task SetRecoveryKeyAsync(string handle, string recoveryPublicKey, CancellationToken ct = default);

    /// <summary>Stores a single-use invite. Expired invites are cleaned up opportunistically.</summary>
    Task AddInviteAsync(StoredInvite invite, CancellationToken ct = default);

    /// <summary>
    /// Atomically consumes a live invite by code hash. Returns true only if a matching,
    /// unexpired invite existed and was removed (single use).
    /// </summary>
    Task<bool> ConsumeInviteAsync(string handle, string codeHash, CancellationToken ct = default);

    /// <summary>Loads an administrative per-handle rate-policy override, or null for defaults.</summary>
    Task<HandleRatePolicy?> GetHandleRatePolicyAsync(string handle, CancellationToken ct = default);

    /// <summary>Creates or replaces the administrative rate-policy override for a handle.</summary>
    Task SetHandleRatePolicyAsync(string handle, HandleRatePolicy policy, CancellationToken ct = default);

    /// <summary>Deletes a per-handle override so configured defaults apply again.</summary>
    Task<bool> DeleteHandleRatePolicyAsync(string handle, CancellationToken ct = default);

    // ---- Capability directory + reputation ----------------------------------

    /// <summary>
    /// Publishes a new service or updates an existing one. Only the public metadata
    /// (name/description/category) is written; existing reputation state (votes and attested users)
    /// is preserved across updates so a re-publish cannot reset a service's standing.
    /// </summary>
    Task UpsertServiceAsync(StoredService svc, CancellationToken ct = default);

    /// <summary>
    /// Unpublishes a service, but only when it is owned by <paramref name="handle"/>. Returns false
    /// when the service does not exist or is owned by a different handle.
    /// </summary>
    Task<bool> RemoveServiceAsync(string handle, string serviceId, CancellationToken ct = default);

    /// <summary>Loads a service by id, or null when it is not published.</summary>
    Task<StoredService?> GetServiceAsync(string serviceId, CancellationToken ct = default);

    /// <summary>
    /// Lists published services. When <paramref name="query"/> is non-empty it filters (case
    /// insensitive) on name, description, or category; null or whitespace returns everything.
    /// </summary>
    Task<IReadOnlyList<StoredService>> ListServicesAsync(string? query, CancellationToken ct = default);

    /// <summary>
    /// Records an attested usage event: adds <paramref name="userHandle"/> to the service's user set.
    /// This is what later unlocks that handle's ability to vote. No-op if the service is missing.
    /// </summary>
    Task RecordServiceUsageAsync(string serviceId, string userHandle, CancellationToken ct = default);

    /// <summary>Returns true when <paramref name="userHandle"/> has an attested usage event for the service (vote gate).</summary>
    Task<bool> HasUsedServiceAsync(string serviceId, string userHandle, CancellationToken ct = default);

    /// <summary>
    /// Sets, updates, or clears a voter's vote on a service. <paramref name="vote"/> is +1/-1 to set
    /// (one updatable vote per voter) or 0 to remove the voter's vote. No-op if the service is missing.
    /// </summary>
    Task SetServiceVoteAsync(string serviceId, string voterHandle, int vote, CancellationToken ct = default);
}
