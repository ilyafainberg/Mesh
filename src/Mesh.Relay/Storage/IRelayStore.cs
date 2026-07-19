using Mesh.Relay.RateLimiting;

namespace Mesh.Relay.Storage;

/// <summary>
/// A persisted handle registration: the handle, its display name, and the set of
/// device public keys authorized to act as it. Serializable so it can live in a
/// durable store (Cosmos) or in memory. Device keys are base64 SubjectPublicKeyInfo.
/// </summary>
public sealed class StoredHandle
{
    public string Handle { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> DevicePublicKeys { get; set; } = new();

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
}

/// <summary>A pending device-link invite: single use, short lived, addressed to a handle.</summary>
public sealed class StoredInvite
{
    public string Handle { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A queued message for an offline recipient, awaiting delivery on next connect.</summary>
public sealed class StoredEnvelope
{
    public string To { get; set; } = "";
    public string Json { get; set; } = "";
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Durable state for the relay: the handle registry, pending link invites, and the
/// offline inbox. Presence (who is connected right now) is intentionally NOT here,
/// because live sockets cannot be persisted; that is the backplane's job.
///
/// Implementations must be safe for concurrent use. All methods are async so a
/// network-backed store (Cosmos) fits behind the same seam as the in-memory default.
/// </summary>
public interface IRelayStore
{
    /// <summary>Loads a handle registration, or null if the handle is unclaimed.</summary>
    Task<StoredHandle?> GetHandleAsync(string handle, CancellationToken ct = default);

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
    /// handle's pending invites and offline inbox. Returns false if the handle did not exist.
    /// Callers must authenticate the request before calling this.
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
        CancellationToken ct = default);

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

    /// <summary>Enqueues a message for an offline recipient.</summary>
    Task EnqueueAsync(string toHandle, string envelopeJson, CancellationToken ct = default);

    /// <summary>Drains and returns all queued messages for a handle (FIFO), removing them.</summary>
    Task<IReadOnlyList<string>> DrainInboxAsync(string toHandle, CancellationToken ct = default);

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
