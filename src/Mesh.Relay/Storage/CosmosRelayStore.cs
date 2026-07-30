using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mesh.Relay.RateLimiting;
using Mesh.Shared;
using Microsoft.Azure.Cosmos;

namespace Mesh.Relay.Storage;

/// <summary>
/// Azure Cosmos DB (serverless) backed implementation of <see cref="IRelayStore"/>.
/// Persists the handle registry, pending device-link invites, offline inbox, capability
/// directory, and administrative rate-policy overrides so relay state survives restarts
/// and can be shared across scaled-out instances.
///
/// Six containers are provisioned idempotently on first use:
/// <list type="bullet">
///   <item>"handles" (partition key "/handle"): one document per registered handle.</item>
///   <item>"rate-policies" (partition key "/handle"): administrative per-handle rate-policy
///   overrides, stored separately from public handle registrations.</item>
///   <item>"invites" (partition key "/handle"): single-use link invites, expired automatically
///   via native per-item TTL (container DefaultTimeToLive = -1).</item>
///   <item>"inbox" (partition key "/to"): queued envelopes for offline recipients, expired
///   after 14 days via a container DefaultTimeToLive of 1209600 seconds.</item>
///   <item>"agent-dispatches" (partition key "/to"): opaque atomic agent requests and fencing state,
///   expired after 14 days.</item>
///   <item>"services" (partition key "/serviceId"): published capabilities and reputation.</item>
/// </list>
/// </summary>
public sealed class CosmosRelayStore : IRelayStore
{
    private const int InboxTtlSeconds = 1209600; // 14 days
    private const string InboxAdmissionControlId = "__inbox-admission-control";
    private const int InboxPurgePageSize = 99;
    private const string DeviceQueueControlId = "__device-queue-control";
    private const int DeviceQueuePurgePageSize = 99;

    private readonly CosmosClient client;
    private readonly string databaseName;
    private readonly SemaphoreSlim initLock = new(1, 1);

    private Container handlesContainer = null!;
    private Container invitesContainer = null!;
    private Container inboxContainer = null!;
    private Container agentDispatchesContainer = null!;
    private Container servicesContainer = null!;
    private Container ratePoliciesContainer = null!;
    private volatile bool initialized;

    /// <summary>
    /// Creates a store bound to the given Cosmos connection string. The database and
    /// containers are provisioned lazily on the first operation, not in the constructor.
    /// </summary>
    /// <param name="connectionString">A Cosmos DB account connection string.</param>
    /// <param name="databaseName">The database name to use (created if absent).</param>
    public CosmosRelayStore(string connectionString, string databaseName = "mesh")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A Cosmos connection string is required.", nameof(connectionString));

        this.databaseName = string.IsNullOrWhiteSpace(databaseName) ? "mesh" : databaseName;
        client = new CosmosClient(
            connectionString,
            new CosmosClientOptions { Serializer = new SystemTextJsonCosmosSerializer() });
    }

    /// <summary>
    /// Provisions the database and its containers once, behind a semaphore so
    /// concurrent callers do not race. A transient setup failure is allowed to propagate
    /// so the caller sees a clear error instead of a silently broken store.
    /// </summary>
    private async Task EnsureInitAsync(CancellationToken ct)
    {
        if (initialized) return;
        await initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (initialized) return;

            Database db = await client
                .CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: ct)
                .ConfigureAwait(false);

            handlesContainer = await db
                .CreateContainerIfNotExistsAsync(new ContainerProperties("handles", "/handle"), cancellationToken: ct)
                .ConfigureAwait(false);

            ratePoliciesContainer = await db
                .CreateContainerIfNotExistsAsync(
                    new ContainerProperties("rate-policies", "/handle"),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            // DefaultTimeToLive = -1 enables TTL but expires items only when they carry a per-item ttl.
            invitesContainer = await db
                .CreateContainerIfNotExistsAsync(
                    new ContainerProperties("invites", "/handle") { DefaultTimeToLive = -1 },
                    cancellationToken: ct)
                .ConfigureAwait(false);

            inboxContainer = await db
                .CreateContainerIfNotExistsAsync(
                    new ContainerProperties("inbox", "/to") { DefaultTimeToLive = InboxTtlSeconds },
                    cancellationToken: ct)
                .ConfigureAwait(false);

            agentDispatchesContainer = await db
                .CreateContainerIfNotExistsAsync(
                    new ContainerProperties("agent-dispatches", "/to") { DefaultTimeToLive = InboxTtlSeconds },
                    cancellationToken: ct)
                .ConfigureAwait(false);

            // Capability directory: one document per published service, keyed on "/serviceId". No TTL:
            // services persist until explicitly unpublished. Reputation (votes + attested users) lives
            // on the same document so a vote/usage mutation is a single-partition read-modify-write.
            servicesContainer = await db
                .CreateContainerIfNotExistsAsync(new ContainerProperties("services", "/serviceId"), cancellationToken: ct)
                .ConfigureAwait(false);

            initialized = true;
        }
        finally
        {
            initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<StoredHandle?> GetHandleAsync(string handle, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await handlesContainer
                .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                .ConfigureAwait(false);
            return response.Resource.Deleting ? null : ToStored(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredHandle?> GetHandleForDeletionAsync(
        string handle,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await handlesContainer
                .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                .ConfigureAwait(false);
            return ToStored(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<(StoredHandle record, bool deviceAuthorized)> UpsertHandleAsync(
        string handle, string devicePublicKey, string? displayName, bool allowNewDevice, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc? doc = null;
            string? etag = null;

            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                doc = null;
            }

            if (doc is null)
            {
                var fresh = new HandleDoc
                {
                    Id = handle,
                    Handle = handle,
                    DisplayName = displayName,
                    RegisteredAt = DateTimeOffset.UtcNow,
                    DevicePublicKeys = new List<string> { devicePublicKey }
                };
                EnsureDeviceQueueGenerations(fresh);

                try
                {
                    await handlesContainer
                        .CreateItemAsync(fresh, new PartitionKey(handle), cancellationToken: ct)
                        .ConfigureAwait(false);
                    await ActivateCurrentInboxesAsync(fresh, ct).ConfigureAwait(false);
                    await ActivateCurrentDeviceQueuesAsync(fresh, ct).ConfigureAwait(false);
                    return (ToStored(fresh), fresh.DevicePublicKeys.Contains(devicePublicKey));
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict && attempt < maxAttempts)
                {
                    continue; // A concurrent create won the race; re-read and merge.
                }
            }
            else
            {
                if (doc.Deleting || doc.QueueAdmissionBlocked)
                    return (ToStored(doc), false);
                if (displayName is not null) doc.DisplayName = displayName;
                if (!doc.DevicePublicKeys.Contains(devicePublicKey) && allowNewDevice)
                    doc.DevicePublicKeys.Add(devicePublicKey);

                // Existing device queues stay valid when another device joins the handle.
                EnsureDeviceQueueGenerations(doc);

                try
                {
                    var options = etag is null ? null : new ItemRequestOptions { IfMatchEtag = etag };
                    await handlesContainer
                        .UpsertItemAsync(doc, new PartitionKey(handle), options, ct)
                        .ConfigureAwait(false);
                    await ActivateCurrentInboxesAsync(doc, ct).ConfigureAwait(false);
                    var registeredDeviceId = DeviceProtocol.DeviceId(devicePublicKey);
                    if (doc.DeviceQueueGenerations!.TryGetValue(
                            registeredDeviceId, out var registeredGeneration))
                    {
                        await ActivateDeviceQueueAsync(
                            doc.Handle,
                            registeredDeviceId,
                            registeredGeneration,
                            doc.QueueAdmissionGeneration!,
                            ct).ConfigureAwait(false);
                    }
                    return (ToStored(doc), doc.DevicePublicKeys.Contains(devicePublicKey));
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
                {
                    continue; // Lost the optimistic concurrency check; retry the read-modify-write.
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteHandleAsync(string handle, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        HandleDoc deleting;
        string deletingEtag;
        const int maxAttempts = 5;
        for (var attempt = 0; ; attempt++)
        {
            ItemResponse<HandleDoc> read;
            try
            {
                read = await handlesContainer.ReadItemAsync<HandleDoc>(
                    handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            deleting = read.Resource;
            if (deleting.Deleting)
            {
                deletingEtag = read.ETag;
                break;
            }
            deleting.Deleting = true;
            EnsureDeviceQueueGenerations(deleting);
            try
            {
                var replaced = await handlesContainer.ReplaceItemAsync(
                    deleting,
                    deleting.Id,
                    new PartitionKey(handle),
                    new ItemRequestOptions { IfMatchEtag = read.ETag },
                    ct).ConfigureAwait(false);
                deletingEtag = replaced.ETag;
                break;
            }
            catch (CosmosException ex) when (
                ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
            }
        }

        var queueFences = deleting.DeviceQueueGenerations!
            .Concat(deleting.DeviceQueueFences ?? [])
            .DistinctBy(pair => (pair.Key, pair.Value))
            .ToArray();
        foreach (var (deviceId, generation) in queueFences)
            await FenceAndPurgeDeviceQueueAsync(
                RelayDeviceQueueKey.Create(handle, deviceId), generation, ct).ConfigureAwait(false);
        await FenceAndPurgeInboxAsync(
            NormalizeHandle(handle), deleting.InboxGeneration!, ct).ConfigureAwait(false);
        foreach (var (deviceId, generation) in deleting.DeviceQueueGenerations!)
            await FenceAndPurgeInboxAsync(
                RelayInboxKey.Device(handle, deviceId),
                InboxAdmissionGeneration(deleting.InboxGeneration!, generation),
                ct).ConfigureAwait(false);
        await PurgeHandleInboxPartitionsAsync(handle, ct).ConfigureAwait(false);
        await CreateDeletedHandleInboxFenceAsync(handle, ct).ConfigureAwait(false);
        await PurgeHandleAgentDispatchesAsync(handle, ct).ConfigureAwait(false);
        await DeletePartitionItemsAsync(
            invitesContainer,
            new PartitionKey(handle),
            "SELECT c.id, c._etag FROM c",
            "handle invite purge",
            ct).ConfigureAwait(false);
        // Keep the tombstone until every cross-partition side effect has completed. A retry can
        // then resume cleanup using the original authorized device keys.
        await DeleteHandleRatePolicyAsync(handle, ct).ConfigureAwait(false);
        for (var attempt = 0; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await handlesContainer.DeleteItemAsync<HandleDoc>(
                    handle,
                    new PartitionKey(handle),
                    new ItemRequestOptions { IfMatchEtag = deletingEtag },
                    ct).ConfigureAwait(false);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }
            catch (CosmosException ex) when (
                ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                try
                {
                    var current = await handlesContainer.ReadItemAsync<HandleDoc>(
                        handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
                    if (!current.Resource.Deleting) return false;
                    deletingEtag = current.ETag;
                }
                catch (CosmosException readEx) when (readEx.StatusCode == HttpStatusCode.NotFound)
                {
                    return true;
                }
            }
        }
        throw new InvalidOperationException("Handle tombstone deletion did not converge.");
    }

    /// <inheritdoc />
    public async Task SetDisplayNameAsync(string handle, string displayName, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return; // No-op if the handle does not exist.
            }

            doc.DisplayName = displayName;
            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <inheritdoc />
    public async Task SetDeviceNameAsync(string handle, string deviceId, string name, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return; // No-op if the handle does not exist.
            }

            doc.DeviceNames ??= new Dictionary<string, string>();
            doc.DeviceNames[deviceId] = name;
            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <inheritdoc />
    public async Task SetDeviceMetadataAsync(
        string handle,
        string deviceId,
        string? name,
        string platform,
        bool remoteAgentEnabled,
        bool atomicAgentDispatchEnabled,
        int protocolVersion = MeshProtocol.Version,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            doc.DeviceNames ??= new Dictionary<string, string>();
            doc.DevicePlatforms ??= new Dictionary<string, string>();
            doc.DeviceRemoteAgentEnabled ??= new Dictionary<string, bool>();
            doc.DeviceAtomicAgentDispatchEnabled ??= new Dictionary<string, bool>();
            doc.DeviceProtocolVersions ??= new Dictionary<string, int>();
            if (!string.IsNullOrWhiteSpace(name))
                doc.DeviceNames[deviceId] = name;
            doc.DevicePlatforms[deviceId] = platform;
            doc.DeviceRemoteAgentEnabled[deviceId] = remoteAgentEnabled;
            doc.DeviceAtomicAgentDispatchEnabled[deviceId] = atomicAgentDispatchEnabled;
            doc.DeviceProtocolVersions[deviceId] = protocolVersion;
            if (string.IsNullOrWhiteSpace(doc.AgentPrimaryDeviceId)
                && Mesh.Shared.DevicePlatforms.IsDesktop(platform)
                && atomicAgentDispatchEnabled)
            {
                doc.AgentPrimaryDeviceId = deviceId;
                doc.AgentRoutingVersion = Guid.NewGuid().ToString("n");
                doc.AgentPrimaryWasSelectedAutomatically = true;
            }

            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <inheritdoc />
    public async Task<DeviceRevocationResult> RevokeDeviceAsync(
        string handle,
        string targetDeviceId,
        string? authorizingPublicKey = null,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var revoked = false;
        var purgedInbox = 0;
        string? revokedQueueGeneration = null;
        HandleDoc? pending = null;
        const int maxAttempts = 5;
        for (var attempt = 0; ; attempt++)
        {
            HandleDoc? doc;
            string? etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                doc = null;
                etag = null;
            }

            if (doc is null
                || authorizingPublicKey is not null
                   && !doc.DevicePublicKeys.Contains(authorizingPublicKey, StringComparer.Ordinal))
                return new DeviceRevocationResult(false, 0);
            EnsureDeviceQueueGenerations(doc);
            if (doc.QueueAdmissionBlocked)
            {
                if (!string.Equals(
                        doc.PendingRevokedDeviceId, targetDeviceId, StringComparison.Ordinal))
                    return new DeviceRevocationResult(false, 0);
                revokedQueueGeneration = doc.PendingRevocationGeneration;
                pending = doc;
                break;
            }
            var publicKey = doc.DevicePublicKeys.FirstOrDefault(key =>
                string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal));
            if (publicKey is null)
            {
                doc.DeviceQueueFences?.TryGetValue(targetDeviceId, out revokedQueueGeneration);
                break;
            }
            if (doc.DevicePublicKeys.Count <= 1)
                return new DeviceRevocationResult(false, 0);

            doc.DeviceQueueGenerations!.TryGetValue(targetDeviceId, out revokedQueueGeneration);
            doc.QueueAdmissionBlocked = true;
            doc.PendingRevokedDeviceId = targetDeviceId;
            doc.PendingRevocationGeneration = revokedQueueGeneration;
            doc.PendingAdmissionGeneration = Guid.NewGuid().ToString("n");

            try
            {
                var replaced = await handlesContainer.ReplaceItemAsync(
                    doc,
                    doc.Id,
                    new PartitionKey(handle),
                    new ItemRequestOptions { IfMatchEtag = etag },
                    ct).ConfigureAwait(false);
                pending = replaced.Resource;
                break;
            }
            catch (CosmosException ex) when (
                ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
            }
        }

        if (pending is not null)
        {
            foreach (var (deviceId, generation) in pending.DeviceQueueGenerations!)
                await FenceDeviceQueueAdmissionAsync(
                    RelayDeviceQueueKey.Create(handle, deviceId),
                    generation,
                    pending.QueueAdmissionGeneration!,
                    ct).ConfigureAwait(false);
            if (pending.DeviceQueueGenerations.TryGetValue(targetDeviceId, out var inboxDeviceGeneration))
            {
                await FenceInboxAdmissionAsync(
                    RelayInboxKey.Device(handle, targetDeviceId),
                    InboxAdmissionGeneration(pending.InboxGeneration!, inboxDeviceGeneration),
                    ct).ConfigureAwait(false);
                purgedInbox = await PurgeInboxPartitionAsync(
                    RelayInboxKey.Device(handle, targetDeviceId), ct).ConfigureAwait(false);
            }

            for (var attempt = 0; ; attempt++)
            {
                var read = await handlesContainer.ReadItemAsync<HandleDoc>(
                    handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
                var doc = read.Resource;
                if (!doc.QueueAdmissionBlocked
                    || !string.Equals(
                        doc.PendingRevokedDeviceId, targetDeviceId, StringComparison.Ordinal))
                    break;
                var publicKey = doc.DevicePublicKeys.FirstOrDefault(key =>
                    string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal));
                if (publicKey is not null)
                    doc.DevicePublicKeys.Remove(publicKey);
                doc.DeviceNames?.Remove(targetDeviceId);
                doc.DevicePlatforms?.Remove(targetDeviceId);
                doc.DeviceRemoteAgentEnabled?.Remove(targetDeviceId);
                doc.DeviceAtomicAgentDispatchEnabled?.Remove(targetDeviceId);
                doc.DeviceProtocolVersions?.Remove(targetDeviceId);
                doc.DevicePushTokens?.Remove(targetDeviceId);
                doc.DeviceQueueGenerations?.Remove(targetDeviceId);
                doc.DeviceQueueFences ??= new Dictionary<string, string>(StringComparer.Ordinal);
                doc.DeviceQueueFences[targetDeviceId] = revokedQueueGeneration!;
                doc.QueueAdmissionGeneration = doc.PendingAdmissionGeneration;
                doc.QueueAdmissionBlocked = false;
                doc.PendingRevokedDeviceId = null;
                doc.PendingRevocationGeneration = null;
                doc.PendingAdmissionGeneration = null;
                if (string.Equals(doc.AgentPrimaryDeviceId, targetDeviceId, StringComparison.Ordinal))
                    doc.AgentPrimaryDeviceId = null;
                if (string.Equals(doc.AgentFailoverDeviceId, targetDeviceId, StringComparison.Ordinal))
                    doc.AgentFailoverDeviceId = null;
                doc.AgentRoutingVersion = Guid.NewGuid().ToString("n");
                doc.AgentPrimaryWasSelectedAutomatically = false;
                try
                {
                    await handlesContainer.ReplaceItemAsync(
                        doc,
                        doc.Id,
                        new PartitionKey(handle),
                        new ItemRequestOptions { IfMatchEtag = read.ETag },
                        ct).ConfigureAwait(false);
                    revoked = true;
                    break;
                }
                catch (CosmosException ex) when (
                    ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
                {
                }
            }
        }

        var purged = purgedInbox;
        if (revokedQueueGeneration is not null)
        {
            purged += await FenceAndPurgeDeviceQueueAsync(
                RelayDeviceQueueKey.Create(handle, targetDeviceId),
                revokedQueueGeneration,
                ct).ConfigureAwait(false);
            await CompleteDeviceQueueFenceAsync(
                handle, targetDeviceId, revokedQueueGeneration, ct).ConfigureAwait(false);
        }
        try
        {
            var current = await handlesContainer.ReadItemAsync<HandleDoc>(
                handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            if (!current.Resource.Deleting)
            {
                await ActivateCurrentInboxesAsync(current.Resource, ct).ConfigureAwait(false);
                await ActivateCurrentDeviceQueuesAsync(current.Resource, ct).ConfigureAwait(false);
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // A concurrent handle deletion already fenced and removed every queue.
        }
        return new DeviceRevocationResult(revoked, purged);
    }
    public async Task<bool> SetAgentRoutingAsync(
        string handle,
        string primaryDeviceId,
        string? failoverDeviceId,
        string expectedVersion,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!string.Equals(doc.AgentRoutingVersion ?? "", expectedVersion, StringComparison.Ordinal))
                return false;

            doc.AgentPrimaryDeviceId = primaryDeviceId;
            doc.AgentFailoverDeviceId = failoverDeviceId;
            doc.AgentRoutingVersion = Guid.NewGuid().ToString("n");
            doc.AgentPrimaryWasSelectedAutomatically = false;

            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <inheritdoc />
    public async Task SetDevicePushTokenAsync(
        string handle, string deviceId, string platform, string token, bool alertsEnabled,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            doc.DevicePushTokens ??= new Dictionary<string, DevicePushToken>();
            doc.DevicePushTokens.TryGetValue(deviceId, out var previous);
            var preserveWakeState = previous is not null
                && string.Equals(previous.Platform, platform, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.Token, token, StringComparison.Ordinal);
            doc.DevicePushTokens[deviceId] = new DevicePushToken
            {
                Platform = platform,
                Token = token,
                AlertsEnabled = alertsEnabled,
                BackgroundPushWindowStartedAt = preserveWakeState ? previous!.BackgroundPushWindowStartedAt : null,
                BackgroundPushCount = preserveWakeState ? previous!.BackgroundPushCount : 0,
                LastBackgroundPushAt = preserveWakeState ? previous!.LastBackgroundPushAt : null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    public async Task<bool> TryAcquireBackgroundPushAsync(
        string handle,
        string deviceId,
        DateTimeOffset now,
        TimeSpan minimumInterval,
        TimeSpan window,
        int maxCount,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (doc.DevicePushTokens is null
                || !doc.DevicePushTokens.TryGetValue(deviceId, out var token))
                return false;
            if (token.LastBackgroundPushAt is { } last && now - last < minimumInterval)
                return false;
            if (token.BackgroundPushWindowStartedAt is null
                || now - token.BackgroundPushWindowStartedAt.Value >= window)
            {
                token.BackgroundPushWindowStartedAt = now;
                token.BackgroundPushCount = 0;
            }
            if (token.BackgroundPushCount >= maxCount) return false;
            token.BackgroundPushCount++;
            token.LastBackgroundPushAt = now;

            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    public async Task RemoveDevicePushTokenAsync(string handle, string deviceId, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            if (doc.DevicePushTokens is null || !doc.DevicePushTokens.Remove(deviceId))
                return;

            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    public async Task SetRecoveryKeyAsync(string handle, string recoveryPublicKey, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            HandleDoc doc;
            string etag;
            try
            {
                var read = await handlesContainer
                    .ReadItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return; // No-op if the handle does not exist.
            }

            // First writer wins: never overwrite an existing recovery key.
            if (!string.IsNullOrEmpty(doc.RecoveryPublicKey))
                return;

            doc.RecoveryPublicKey = recoveryPublicKey;
            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <inheritdoc />
    public async Task AddInviteAsync(StoredInvite invite, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        var secondsUntilExpiry = (int)Math.Ceiling((invite.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        var ttl = Math.Max(1, secondsUntilExpiry);

        var doc = new InviteDoc
        {
            Id = invite.CodeHash,
            Handle = invite.Handle,
            CodeHash = invite.CodeHash,
            ExpiresAt = invite.ExpiresAt,
            Ttl = ttl
        };

        await invitesContainer
            .UpsertItemAsync(doc, new PartitionKey(invite.Handle), cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ConsumeInviteAsync(string handle, string codeHash, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        InviteDoc doc;
        try
        {
            var read = await invitesContainer
                .ReadItemAsync<InviteDoc>(codeHash, new PartitionKey(handle), cancellationToken: ct)
                .ConfigureAwait(false);
            doc = read.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (doc.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        try
        {
            await invitesContainer
                .DeleteItemAsync<InviteDoc>(codeHash, new PartitionKey(handle), cancellationToken: ct)
                .ConfigureAwait(false);
            return true; // The successful delete is the single-use consume.
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false; // Lost the race to another consumer.
        }
    }

    /// <inheritdoc />
    public async Task<InboxEnqueueResult> EnqueueAsync(
        string toHandle,
        string envelopeId,
        string fromHandle,
        string envelopeJson,
        int priority = RelayInboxPriority.Normal,
        bool requiresForeground = false,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var normalizedInbox = NormalizeInboxKey(toHandle);
        var admissionGeneration = await GetInboxAdmissionGenerationAsync(
            normalizedInbox, ct).ConfigureAwait(false);
        var deliveryId = InboxDeliveryId.Create(fromHandle, envelopeId);
        var doc = new InboxDoc
        {
            Id = deliveryId,
            EnvelopeId = envelopeId,
            From = NormalizeHandle(fromHandle),
            To = normalizedInbox,
            Json = envelopeJson,
            QueuedAt = DateTimeOffset.UtcNow,
            Priority = priority,
            RequiresForeground = requiresForeground,
            AdmissionGeneration = admissionGeneration
        };
        if (RelayInboxPolicy.NeverExpires(normalizedInbox))
        {
            doc.Ttl = -1;
        }
        else
        {
            doc.ExpiresAt = doc.QueuedAt + RelayInboxPolicy.Retention;
            doc.Ttl = InboxTtlSeconds;
        }

        if (admissionGeneration is "")
            return new InboxEnqueueResult(
                deliveryId, Accepted: false, Created: false, "inbox_admission_rejected");

        var accepted = true;
        var created = true;
        try
        {
            if (admissionGeneration is null)
            {
                await inboxContainer
                    .CreateItemAsync(doc, new PartitionKey(normalizedInbox), cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await ActivateInboxAsync(normalizedInbox, admissionGeneration, ct).ConfigureAwait(false);
                var escapedGeneration = EscapeFilterValue(admissionGeneration);
                var batch = inboxContainer.CreateTransactionalBatch(new PartitionKey(normalizedInbox))
                    .PatchItem(
                        InboxAdmissionControlId,
                        [PatchOperation.Set("/active", true)],
                        new TransactionalBatchPatchItemRequestOptions
                        {
                            FilterPredicate =
                                $"FROM c WHERE c.active = true AND c.generation = '{escapedGeneration}'"
                        })
                    .CreateItem(doc);
                using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (BatchContainsStatus(
                            response,
                            HttpStatusCode.Conflict,
                            HttpStatusCode.NotFound,
                            HttpStatusCode.PreconditionFailed))
                    {
                        created = false;
                        accepted = await InboxItemExistsForAdmissionAsync(
                            normalizedInbox, deliveryId, admissionGeneration, ct).ConfigureAwait(false);
                    }
                    else
                        ThrowBatchFailure(response, "inbox admission");
                }
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Stable sender/envelope ids make retries idempotent. The first accepted ciphertext wins.
            created = false;
            accepted = true;
        }
        return new InboxEnqueueResult(
            deliveryId,
            accepted,
            created,
            accepted ? null : "inbox_admission_rejected");
    }

    private async Task<bool> InboxItemExistsForAdmissionAsync(
        string inboxKey,
        string deliveryId,
        string? admissionGeneration,
        CancellationToken ct)
    {
        try
        {
            var response = await inboxContainer.ReadItemAsync<InboxDoc>(
                deliveryId,
                new PartitionKey(inboxKey),
                cancellationToken: ct).ConfigureAwait(false);
            return InboxItemMatchesAdmission(response.Resource, admissionGeneration);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEnvelope>> LeaseInboxAsync(
        string toHandle,
        string leaseOwner,
        int maxItems = RelayInboxPolicy.DeliveryWindow,
        TimeSpan? leaseDuration = null,
        bool includeForeground = true,
        CancellationToken ct = default)
    {
        if (maxItems <= 0) return [];
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var admissionGeneration = await GetInboxAdmissionGenerationAsync(
            NormalizeInboxKey(toHandle), ct).ConfigureAwait(false);
        if (admissionGeneration is "") return [];
        var now = DateTimeOffset.UtcNow;
        var partition = new PartitionKey(toHandle);
        var countQuery = new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.leaseOwner = @owner AND c.leaseUntil > @now")
            .WithParameter("@owner", leaseOwner)
            .WithParameter("@now", now);
        var options = new QueryRequestOptions { PartitionKey = partition };
        long outstanding = 0;
        using (var countIterator = inboxContainer.GetItemQueryIterator<long>(
                   countQuery, requestOptions: options))
        {
            while (countIterator.HasMoreResults)
            {
                var page = await countIterator.ReadNextAsync(ct).ConfigureAwait(false);
                outstanding += page.Resource.Sum();
            }
        }

        var capacity = Math.Max(0, maxItems - (int)Math.Min(int.MaxValue, outstanding));
        if (capacity == 0) return [];
        var leaseUntil = now + (leaseDuration ?? RelayInboxPolicy.LeaseDuration);
        var result = new List<StoredEnvelope>(capacity);
        var first = await ReadNextAvailableInboxItemAsync(
            partition, now, includeForeground, ct).ConfigureAwait(false);
        if (first is not null)
        {
            try
            {
                var doc = first.Value.Doc;
                doc.Priority ??= RelayInboxPriority.Normal;
                doc.LeaseOwner = leaseOwner;
                doc.LeaseUntil = leaseUntil;
                doc.DeliveryAttempts++;
                if (!InboxItemMatchesAdmission(doc, admissionGeneration))
                    await TryDeleteInboxWithAdmissionAsync(
                        toHandle, doc.Id, admissionGeneration, ct).ConfigureAwait(false);
                else if (await TryReplaceInboxWithAdmissionAsync(
                             toHandle, doc, first.Value.ETag, admissionGeneration, ct).ConfigureAwait(false))
                    result.Add(ToStoredEnvelope(doc));
            }
            catch (CosmosException ex) when (
                ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
            }
        }
        foreach (var priority in InboxPriorityOrder)
        {
            if (result.Count >= capacity) break;
            var query = InboxAvailableQuery(priority, now, includeForeground);
            var queryOptions = new QueryRequestOptions
            {
                PartitionKey = partition,
                MaxItemCount = Math.Min(capacity - result.Count, RelayInboxPolicy.DeliveryWindow)
            };
            using var iterator = inboxContainer.GetItemQueryIterator<InboxDoc>(
                query, requestOptions: queryOptions);
            while (iterator.HasMoreResults && result.Count < capacity)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                foreach (var candidate in page)
                {
                    if (result.Count >= capacity) break;
                    try
                    {
                        var read = await inboxContainer
                            .ReadItemAsync<InboxDoc>(candidate.Id, partition, cancellationToken: ct)
                            .ConfigureAwait(false);
                        var doc = read.Resource;
                        if (!RefreshInboxTtl(doc, now))
                        {
                            await inboxContainer.DeleteItemAsync<InboxDoc>(
                                doc.Id,
                                partition,
                                new ItemRequestOptions { IfMatchEtag = read.ETag },
                                ct).ConfigureAwait(false);
                            continue;
                        }
                        if (doc.LeaseUntil > now) continue;
                        if (!includeForeground && doc.RequiresForeground != false) continue;
                        doc.Priority ??= RelayInboxPriority.Normal;
                        doc.LeaseOwner = leaseOwner;
                        doc.LeaseUntil = leaseUntil;
                        doc.DeliveryAttempts++;
                        if (!InboxItemMatchesAdmission(doc, admissionGeneration))
                            await TryDeleteInboxWithAdmissionAsync(
                                toHandle, doc.Id, admissionGeneration, ct).ConfigureAwait(false);
                        else if (await TryReplaceInboxWithAdmissionAsync(
                                     toHandle, doc, read.ETag, admissionGeneration, ct).ConfigureAwait(false))
                            result.Add(ToStoredEnvelope(doc));
                    }
                    catch (CosmosException ex) when (
                        ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                    {
                    }
                }
            }
        }
        return result;
    }
    /// <inheritdoc />
    public async Task<StoredEnvelope?> AcknowledgeInboxAsync(
        string toHandle,
        string deliveryId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var normalizedInbox = NormalizeInboxKey(toHandle);
        var partition = new PartitionKey(normalizedInbox);
        try
        {
            var read = await inboxContainer.ReadItemAsync<InboxDoc>(
                deliveryId, partition, cancellationToken: ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (!string.Equals(read.Resource.LeaseOwner, leaseOwner, StringComparison.Ordinal)
                || read.Resource.LeaseUntil is null
                || read.Resource.LeaseUntil <= now)
                return null;
            await inboxContainer.DeleteItemAsync<InboxDoc>(
                deliveryId,
                partition,
                new ItemRequestOptions { IfMatchEtag = read.ETag },
                ct).ConfigureAwait(false);
            return ToStoredEnvelope(read.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryLeaseInboxItemAsync(
        string toHandle,
        string deliveryId,
        string leaseOwner,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var admissionGeneration = await GetInboxAdmissionGenerationAsync(
            NormalizeInboxKey(toHandle), ct).ConfigureAwait(false);
        if (admissionGeneration is "") return false;
        var partition = new PartitionKey(toHandle);
        var now = DateTimeOffset.UtcNow;
        var next = await ReadNextAvailableInboxItemAsync(
            partition, now, includeForeground: true, ct: ct).ConfigureAwait(false);
        if (next is null || !string.Equals(next.Value.Doc.Id, deliveryId, StringComparison.Ordinal))
            return false;
        var doc = next.Value.Doc;
        doc.Priority ??= RelayInboxPriority.Normal;
        doc.LeaseOwner = leaseOwner;
        doc.LeaseUntil = now + (leaseDuration ?? RelayInboxPolicy.LeaseDuration);
        doc.DeliveryAttempts++;
        try
        {
            if (!InboxItemMatchesAdmission(doc, admissionGeneration))
            {
                await TryDeleteInboxWithAdmissionAsync(
                    toHandle, doc.Id, admissionGeneration, ct).ConfigureAwait(false);
                return false;
            }
            return await TryReplaceInboxWithAdmissionAsync(
                toHandle, doc, next.Value.ETag, admissionGeneration, ct).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (
            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }
    /// <inheritdoc />
    public async Task ReleaseInboxLeaseAsync(
        string toHandle,
        string deliveryId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var partition = new PartitionKey(toHandle);
        try
        {
            var read = await inboxContainer.ReadItemAsync<InboxDoc>(
                deliveryId, partition, cancellationToken: ct).ConfigureAwait(false);
            var doc = read.Resource;
            if (!string.Equals(doc.LeaseOwner, leaseOwner, StringComparison.Ordinal)) return;
            if (!RefreshInboxTtl(doc, DateTimeOffset.UtcNow))
            {
                await inboxContainer.DeleteItemAsync<InboxDoc>(
                    deliveryId,
                    partition,
                    new ItemRequestOptions { IfMatchEtag = read.ETag },
                    ct).ConfigureAwait(false);
                return;
            }
            doc.LeaseOwner = null;
            doc.LeaseUntil = null;
            doc.DeliveryAttempts = Math.Max(0, doc.DeliveryAttempts - 1);
            await inboxContainer.ReplaceItemAsync(
                doc,
                deliveryId,
                partition,
                new ItemRequestOptions { IfMatchEtag = read.ETag },
                ct).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
        }
    }
    /// <inheritdoc />
    public async Task<bool> CancelInboxAsync(
        string toHandle,
        string deliveryId,
        string fromHandle,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        try
        {
            var read = await inboxContainer.ReadItemAsync<InboxDoc>(
                deliveryId, new PartitionKey(toHandle), cancellationToken: ct).ConfigureAwait(false);
            if (!string.Equals(read.Resource.From, NormalizeHandle(fromHandle), StringComparison.Ordinal))
                return false;
            if (read.Resource.DeliveryAttempts != 0
                || read.Resource.LeaseUntil is not null)
                return false;
            await inboxContainer.DeleteItemAsync<InboxDoc>(
                deliveryId,
                new PartitionKey(toHandle),
                new ItemRequestOptions { IfMatchEtag = read.ETag },
                ct).ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseInboxLeasesAsync(
        string toHandle,
        string leaseOwner,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.to = @to AND c.leaseOwner = @owner")
            .WithParameter("@to", toHandle)
            .WithParameter("@owner", leaseOwner);
        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(toHandle) };
        using var iterator = inboxContainer.GetItemQueryIterator<InboxDoc>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            foreach (var candidate in page)
            {
                try
                {
                    var read = await inboxContainer.ReadItemAsync<InboxDoc>(
                        candidate.Id, new PartitionKey(toHandle), cancellationToken: ct).ConfigureAwait(false);
                    var doc = read.Resource;
                    if (!string.Equals(doc.LeaseOwner, leaseOwner, StringComparison.Ordinal)) continue;
                    if (!RefreshInboxTtl(doc, DateTimeOffset.UtcNow))
                    {
                        await inboxContainer.DeleteItemAsync<InboxDoc>(
                            doc.Id,
                            new PartitionKey(toHandle),
                            new ItemRequestOptions { IfMatchEtag = read.ETag },
                            ct).ConfigureAwait(false);
                        continue;
                    }
                    doc.LeaseOwner = null;
                    doc.LeaseUntil = null;
                    await inboxContainer.ReplaceItemAsync(
                        doc, doc.Id, new PartitionKey(toHandle),
                        new ItemRequestOptions { IfMatchEtag = read.ETag }, ct).ConfigureAwait(false);
                }
                catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DrainInboxAsync(string toHandle, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        var admissionGeneration = await GetInboxAdmissionGenerationAsync(
            NormalizeInboxKey(toHandle), ct).ConfigureAwait(false);
        if (admissionGeneration is "") return [];
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.to = @to"
                + " AND (NOT IS_DEFINED(c.type) OR c.type = 'inbox') ORDER BY c.queuedAt ASC")
            .WithParameter("@to", toHandle);

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(toHandle) };

        var pending = new List<InboxDoc>();
        using (var iterator = inboxContainer.GetItemQueryIterator<InboxDoc>(query, requestOptions: options))
        {
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                pending.AddRange(page);
            }
        }

        var result = new List<string>(pending.Count);
        foreach (var doc in pending)
        {
            if (!InboxItemMatchesAdmission(doc, admissionGeneration))
            {
                await TryDeleteInboxWithAdmissionAsync(
                    toHandle, doc.Id, admissionGeneration, ct).ConfigureAwait(false);
                continue;
            }
            if (await TryDeleteInboxWithAdmissionAsync(
                    toHandle, doc.Id, admissionGeneration, ct).ConfigureAwait(false))
                result.Add(doc.Json);
        }

        return result;
    }
    public async Task<RelayInboxStats> GetInboxStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        const string activeItems =
            "((NOT IS_DEFINED(c.type) OR c.type = 'inbox')"
            + " AND (NOT IS_DEFINED(c.expiresAt) OR c.expiresAt > @now))"
            + " OR (c.type = 'device-queue' AND c.expiresAt > @now)";
        long count = 0;
        using (var countIterator = inboxContainer.GetItemQueryIterator<long>(
                   new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {activeItems}")
                       .WithParameter("@now", now)))
        {
            while (countIterator.HasMoreResults)
            {
                var page = await countIterator.ReadNextAsync(ct).ConfigureAwait(false);
                count += page.Resource.Sum();
            }
        }

        DateTimeOffset? oldestInbox = null;
        using (var oldestIterator = inboxContainer.GetItemQueryIterator<InboxQueuedAtProjection>(
                   new QueryDefinition(
                           "SELECT TOP 1 c.queuedAt FROM c"
                           + " WHERE (NOT IS_DEFINED(c.type) OR c.type = 'inbox')"
                           + " AND (NOT IS_DEFINED(c.expiresAt) OR c.expiresAt > @now)"
                           + " ORDER BY c.queuedAt ASC")
                       .WithParameter("@now", now)))
        {
            if (oldestIterator.HasMoreResults)
            {
                var page = await oldestIterator.ReadNextAsync(ct).ConfigureAwait(false);
                oldestInbox = page.Resource.FirstOrDefault()?.QueuedAt;
            }
        }
        DateTimeOffset? oldestQueue = null;
        using (var oldestIterator = inboxContainer.GetItemQueryIterator<DeviceQueueEnqueuedAtProjection>(
                   new QueryDefinition(
                           "SELECT TOP 1 c.enqueuedAt FROM c"
                           + " WHERE c.type = 'device-queue' AND c.expiresAt > @now"
                           + " ORDER BY c.enqueuedAt ASC")
                       .WithParameter("@now", now)))
        {
            if (oldestIterator.HasMoreResults)
            {
                var page = await oldestIterator.ReadNextAsync(ct).ConfigureAwait(false);
                oldestQueue = page.Resource.FirstOrDefault()?.EnqueuedAt;
            }
        }
        var oldest = oldestInbox is null
            ? oldestQueue
            : oldestQueue is null || oldestInbox <= oldestQueue ? oldestInbox : oldestQueue;
        return new RelayInboxStats(count, oldest);
    }

    public async Task<QueueEnqueueResult> EnqueueDeviceQueueAsync(
        string handle,
        QueueEnqueue request,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(request);
        var queueKey = RelayDeviceQueueKey.Create(handle, request.TargetDeviceId);
        var entryId = DeviceQueueEntryIdProtocol.Create(
            request.SourceDeviceId,
            request.TargetDeviceId,
            request.OperationId);
        var admission = await ReadQueueAdmissionGenerationAsync(
            handle, request.SourceDeviceId, request.TargetDeviceId, ct).ConfigureAwait(false);
        if (admission is null)
            return new QueueEnqueueResult(false, entryId, "sync_target_unknown");
        var now = DateTimeOffset.UtcNow;
        await PurgeExpiredDeviceQueueAsync(queueKey, now, ct).ConfigureAwait(false);
        var doc = new DeviceQueueDoc
        {
            Id = entryId,
            To = queueKey,
            Type = "device-queue",
            Handle = NormalizeHandle(handle),
            SourceDeviceId = request.SourceDeviceId,
            SourceGeneration = admission.Value.SourceGeneration,
            TargetDeviceId = request.TargetDeviceId,
            OperationId = request.OperationId,
            Payload = request.Payload,
            EnqueuedAt = now,
            ExpiresAt = now + DeviceQueueProtocol.EntryTtl,
            // Retain logically expired entries until the counter and entries can be purged atomically.
            Ttl = -1
        };
        var partition = new PartitionKey(queueKey);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var existing = await ReadDeviceQueueEntryWithEtagAsync(
                queueKey, entryId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (string.Equals(
                        existing.Value.Doc.SourceGeneration,
                        admission.Value.SourceGeneration,
                        StringComparison.Ordinal))
                    return new QueueEnqueueResult(true, entryId, Created: false);
                await RemoveStaleDeviceQueueEntryAsync(
                    queueKey, entryId, existing.Value.ETag, ct).ConfigureAwait(false);
                continue;
            }

            var batch = inboxContainer.CreateTransactionalBatch(partition)
                .PatchItem(
                    DeviceQueueControlId,
                    [PatchOperation.Increment("/count", 1)],
                    new TransactionalBatchPatchItemRequestOptions
                    {
                        FilterPredicate =
                            $"FROM c WHERE c.active = true"
                            + $" AND c.generation = '{admission.Value.TargetGeneration}'"
                            + $" AND c.admissionGeneration = '{admission.Value.AdmissionGeneration}'"
                            + $" AND c.count < {DeviceQueueProtocol.MaxEntries}"
                    })
                .CreateItem(doc);

            using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return new QueueEnqueueResult(true, entryId, Created: true);

            existing = await ReadDeviceQueueEntryWithEtagAsync(
                queueKey, entryId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (string.Equals(
                        existing.Value.Doc.SourceGeneration,
                        admission.Value.SourceGeneration,
                        StringComparison.Ordinal))
                    return new QueueEnqueueResult(true, entryId, Created: false);
                await RemoveStaleDeviceQueueEntryAsync(
                    queueKey, entryId, existing.Value.ETag, ct).ConfigureAwait(false);
                continue;
            }
            if (BatchContainsStatus(
                    response, HttpStatusCode.NotFound, HttpStatusCode.PreconditionFailed))
            {
                var control = await ReadDeviceQueueControlAsync(queueKey, ct).ConfigureAwait(false);
                var full = control is { Active: true, Count: >= DeviceQueueProtocol.MaxEntries }
                    && string.Equals(
                        control.Generation, admission.Value.TargetGeneration, StringComparison.Ordinal)
                    && string.Equals(
                        control.AdmissionGeneration,
                        admission.Value.AdmissionGeneration,
                        StringComparison.Ordinal);
                return new QueueEnqueueResult(
                    false,
                    entryId,
                    full ? DeviceQueueProtocol.BoundedQueueFull : "sync_target_unknown");
            }
            if (!BatchContainsStatus(response, HttpStatusCode.Conflict))
                ThrowBatchFailure(response, "device queue admission");
        }
        throw new InvalidOperationException("Device queue admission did not converge.");
    }

    public async Task<QueueDrainResponse> DrainDeviceQueueAsync(
        string handle,
        string deviceId,
        string leaseOwner,
        int maxEntries = DeviceQueueProtocol.DeliveryWindow,
        CancellationToken ct = default)
    {
        if (maxEntries <= 0)
            return new QueueDrainResponse([]);
        await EnsureInitAsync(ct).ConfigureAwait(false);
        if (await IsQueueAdmissionBlockedAsync(handle, ct).ConfigureAwait(false))
            return new QueueDrainResponse([]);
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        var now = DateTimeOffset.UtcNow;
        await PurgeExpiredDeviceQueueAsync(queueKey, now, ct).ConfigureAwait(false);
        var control = await ReadDeviceQueueControlAsync(queueKey, ct).ConfigureAwait(false);
        if (control is not { Active: true })
            return new QueueDrainResponse([]);
        var controlGeneration = control.Generation;
        var admissionGeneration = control.AdmissionGeneration;
        var leaseUntil = now + DeviceQueueProtocol.LeaseDuration;
        var result = new List<QueueEntry>();
        using var iterator = inboxContainer.GetItemQueryIterator<DeviceQueueDoc>(
            new QueryDefinition(
                    "SELECT * FROM c WHERE c.to = @to AND c.type = 'device-queue' AND (NOT IS_DEFINED(c.leaseUntil) OR c.leaseUntil <= @now) AND c.expiresAt > @now ORDER BY c.enqueuedAt ASC")
                .WithParameter("@to", queueKey)
                .WithParameter("@now", now),
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(queueKey),
                MaxItemCount = Math.Min(maxEntries, DeviceQueueProtocol.DeliveryWindow)
            });
        while (iterator.HasMoreResults && result.Count < maxEntries)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            foreach (var candidate in page)
            {
                try
                {
                    var read = await inboxContainer.ReadItemAsync<DeviceQueueDoc>(
                        candidate.Id,
                        new PartitionKey(queueKey),
                        cancellationToken: ct).ConfigureAwait(false);
                    var doc = read.Resource;
                    if (doc.ExpiresAt <= now || doc.LeaseUntil is { } existingLease && existingLease > now)
                        continue;
                    var preLeaseValidation = await ValidateDeviceQueueEntryAsync(
                        handle,
                        doc.SourceDeviceId,
                        doc.TargetDeviceId,
                        doc.SourceGeneration,
                        controlGeneration,
                        ct).ConfigureAwait(false);
                    if (preLeaseValidation == DeviceQueueEntryValidation.Retry)
                        return new QueueDrainResponse(result);
                    if (preLeaseValidation == DeviceQueueEntryValidation.Stale)
                    {
                        await RemoveStaleDeviceQueueEntryAsync(
                            queueKey, doc.Id, read.ETag, ct).ConfigureAwait(false);
                        continue;
                    }
                    doc.LeaseOwner = leaseOwner;
                    doc.LeaseUntil = leaseUntil;
                    var escapedControlGeneration = EscapeFilterValue(controlGeneration);
                    var escapedAdmissionGeneration = EscapeFilterValue(admissionGeneration);
                    var leaseBatch = inboxContainer.CreateTransactionalBatch(new PartitionKey(queueKey))
                        .PatchItem(
                            DeviceQueueControlId,
                            [PatchOperation.Set("/lastLeaseAt", now)],
                            new TransactionalBatchPatchItemRequestOptions
                            {
                                FilterPredicate =
                                    $"FROM c WHERE c.active = true"
                                    + $" AND c.generation = '{escapedControlGeneration}'"
                                    + $" AND c.admissionGeneration = '{escapedAdmissionGeneration}'"
                            })
                        .ReplaceItem(
                            doc.Id,
                            doc,
                            new TransactionalBatchItemRequestOptions { IfMatchEtag = read.ETag });
                    using var leaseResponse = await leaseBatch.ExecuteAsync(ct).ConfigureAwait(false);
                    if (!leaseResponse.IsSuccessStatusCode)
                    {
                        if (BatchContainsStatus(
                                leaseResponse,
                                HttpStatusCode.NotFound,
                                HttpStatusCode.PreconditionFailed))
                        {
                            var latestControl = await ReadDeviceQueueControlAsync(
                                queueKey, ct).ConfigureAwait(false);
                            if (latestControl is not { Active: true }
                                || !string.Equals(
                                    latestControl.Generation,
                                    controlGeneration,
                                    StringComparison.Ordinal)
                                || !string.Equals(
                                    latestControl.AdmissionGeneration,
                                    admissionGeneration,
                                    StringComparison.Ordinal))
                                return new QueueDrainResponse(result);
                            continue;
                        }
                        ThrowBatchFailure(leaseResponse, "device queue fenced lease");
                    }
                    var validation = await ValidateDeviceQueueEntryAsync(
                        handle,
                        doc.SourceDeviceId,
                        doc.TargetDeviceId,
                        doc.SourceGeneration,
                        controlGeneration,
                        ct).ConfigureAwait(false);
                    if (validation == DeviceQueueEntryValidation.Retry)
                    {
                        await ReleaseDeviceQueueLeaseAsync(
                            queueKey, doc.Id, leaseOwner, ct).ConfigureAwait(false);
                        return new QueueDrainResponse(result);
                    }
                    if (validation == DeviceQueueEntryValidation.Stale)
                    {
                        await AcknowledgeDeviceQueueAsync(
                            handle,
                            deviceId,
                            doc.Id,
                            leaseOwner,
                            ct).ConfigureAwait(false);
                        continue;
                    }
                    result.Add(ToQueueEntry(doc));
                    if (result.Count >= maxEntries)
                        break;
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                }
            }
        }
        return new QueueDrainResponse(result);
    }

    private async Task ReleaseDeviceQueueLeaseAsync(
        string queueKey,
        string entryId,
        string leaseOwner,
        CancellationToken ct)
    {
        var escapedOwner = EscapeFilterValue(leaseOwner);
        try
        {
            await inboxContainer.PatchItemAsync<DeviceQueueDoc>(
                entryId,
                new PartitionKey(queueKey),
                [PatchOperation.Remove("/leaseOwner"), PatchOperation.Remove("/leaseUntil")],
                new PatchItemRequestOptions
                {
                    FilterPredicate = $"FROM c WHERE c.leaseOwner = '{escapedOwner}'"
                },
                ct).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (
            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
        }
    }

    public async Task<bool> AcknowledgeDeviceQueueAsync(
        string handle,
        string deviceId,
        string entryId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        var now = DateTimeOffset.UtcNow;
        await PurgeExpiredDeviceQueueAsync(queueKey, now, ct).ConfigureAwait(false);
        ItemResponse<DeviceQueueDoc> read;
        try
        {
            read = await inboxContainer.ReadItemAsync<DeviceQueueDoc>(
                entryId,
                new PartitionKey(queueKey),
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        var entry = read.Resource;
        if (!string.Equals(entry.LeaseOwner, leaseOwner, StringComparison.Ordinal)
            || entry.LeaseUntil is null
            || entry.LeaseUntil <= now
            || entry.ExpiresAt <= now)
            return false;

        var batch = inboxContainer.CreateTransactionalBatch(new PartitionKey(queueKey))
            .DeleteItem(
                entryId,
                new TransactionalBatchItemRequestOptions
                {
                    IfMatchEtag = read.ETag
                })
            .PatchItem(
                DeviceQueueControlId,
                [PatchOperation.Increment("/count", -1)],
                new TransactionalBatchPatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.count > 0"
                });
        using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return true;
        if (BatchContainsStatus(
                response,
                HttpStatusCode.NotFound,
                HttpStatusCode.PreconditionFailed,
                HttpStatusCode.Conflict))
            return false;
        ThrowBatchFailure(response, "device queue acknowledgement");
        return false;
    }

    public async Task ReleaseDeviceQueueLeasesAsync(
        string handle,
        string deviceId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        var escapedOwner = leaseOwner.Replace("'", "''", StringComparison.Ordinal);
        for (var pageNumber = 0; pageNumber < 8; pageNumber++)
        {
            IReadOnlyList<string> ids;
            using (var iterator = inboxContainer.GetItemQueryIterator<DeviceQueueIdProjection>(
                       new QueryDefinition(
                               "SELECT c.id FROM c"
                               + " WHERE c.type = 'device-queue' AND c.leaseOwner = @owner")
                           .WithParameter("@owner", leaseOwner),
                       requestOptions: new QueryRequestOptions
                       {
                           PartitionKey = new PartitionKey(queueKey),
                           MaxItemCount = 100
                       }))
            {
                if (!iterator.HasMoreResults)
                    return;
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                ids = page.Resource.Take(100).Select(item => item.Id).ToArray();
            }
            if (ids.Count == 0)
                return;
            foreach (var id in ids)
            {
                try
                {
                    await inboxContainer.PatchItemAsync<DeviceQueueDoc>(
                        id,
                        new PartitionKey(queueKey),
                        [PatchOperation.Remove("/leaseOwner"), PatchOperation.Remove("/leaseUntil")],
                        new PatchItemRequestOptions
                        {
                            FilterPredicate =
                                $"FROM c WHERE c.leaseOwner = '{escapedOwner}'"
                        },
                        ct).ConfigureAwait(false);
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                }
            }
        }
        throw new InvalidOperationException("Device queue lease release did not converge.");
    }

    public async Task<int> GetDeviceQueueSizeAsync(
        string handle,
        string deviceId,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        var now = DateTimeOffset.UtcNow;
        long count = 0;
        using var countIterator = inboxContainer.GetItemQueryIterator<long>(
            new QueryDefinition(
                    "SELECT VALUE COUNT(1) FROM c WHERE c.to = @to AND c.type = 'device-queue' AND c.expiresAt > @now")
                .WithParameter("@to", queueKey)
                .WithParameter("@now", now),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(queueKey) });
        while (countIterator.HasMoreResults)
        {
            var page = await countIterator.ReadNextAsync(ct).ConfigureAwait(false);
            count += page.Resource.Sum();
        }
        return (int)Math.Min(int.MaxValue, count);
    }

    /// <inheritdoc />
    public async Task<AgentDispatchCreateResult> CreateAgentDispatchAsync(
        StoredAgentDispatch dispatch,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var generation = await GetHandleInboxGenerationAsync(dispatch.To, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(generation))
            return new AgentDispatchCreateResult(AgentDispatchCreateStatus.Conflict, "", null);
        var doc = ToDoc(dispatch);
        doc.AdmissionGeneration = generation;
        try
        {
            var created = await agentDispatchesContainer
                .CreateItemAsync(doc, new PartitionKey(dispatch.To), cancellationToken: ct)
                .ConfigureAwait(false);
            if (!string.Equals(
                    generation,
                    await GetHandleInboxGenerationAsync(dispatch.To, ct).ConfigureAwait(false),
                    StringComparison.Ordinal))
            {
                try
                {
                    await agentDispatchesContainer.DeleteItemAsync<AgentDispatchDoc>(
                        doc.Id,
                        new PartitionKey(dispatch.To),
                        new ItemRequestOptions { IfMatchEtag = created.ETag },
                        ct).ConfigureAwait(false);
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                }
                return new AgentDispatchCreateResult(AgentDispatchCreateStatus.Conflict, "", null);
            }
            return new AgentDispatchCreateResult(
                AgentDispatchCreateStatus.Created, dispatch.State, dispatch.AssignedDeviceId);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetAgentDispatchAsync(dispatch.To, dispatch.Id, ct).ConfigureAwait(false);
            if (existing is null)
                return new AgentDispatchCreateResult(AgentDispatchCreateStatus.Conflict, "", null);
            var duplicate = string.Equals(existing.RequestId, dispatch.RequestId, StringComparison.Ordinal)
                && string.Equals(existing.From, dispatch.From, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.To, dispatch.To, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.EnvelopeHash, dispatch.EnvelopeHash, StringComparison.Ordinal);
            return new AgentDispatchCreateResult(
                duplicate ? AgentDispatchCreateStatus.Duplicate : AgentDispatchCreateStatus.Conflict,
                existing.State,
                existing.AssignedDeviceId);
        }
    }

    /// <inheritdoc />
    public async Task<StoredAgentDispatch?> GetAgentDispatchAsync(
        string toHandle,
        string dispatchId,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await agentDispatchesContainer
                .ReadItemAsync<AgentDispatchDoc>(dispatchId, new PartitionKey(toHandle), cancellationToken: ct)
                .ConfigureAwait(false);
            if (!await AgentDispatchMatchesCurrentHandleAsync(
                    response.Resource, ct).ConfigureAwait(false))
                return null;
            return ToStored(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task AssignPendingAgentDispatchesAsync(
        string toHandle,
        IReadOnlyList<string> candidateDeviceIds,
        CancellationToken ct = default)
    {
        var readyDevices = candidateDeviceIds
            .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (readyDevices.Length == 0) return;
        var pending = await QueryAgentDispatchesAsync(toHandle, AgentDispatchStates.Pending, ct).ConfigureAwait(false);
        var assigned = await QueryAgentDispatchesAsync(toHandle, AgentDispatchStates.Assigned, ct).ConfigureAwait(false);
        var delivering = await QueryAgentDispatchesAsync(
            toHandle, AgentDispatchStates.Delivering, ct).ConfigureAwait(false);
        var candidates = pending.Concat(assigned).Concat(delivering)
            .DistinctBy(candidate => candidate.Id)
            .OrderBy(candidate => candidate.QueuedAt)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            try
            {
                var read = await agentDispatchesContainer
                    .ReadItemAsync<AgentDispatchDoc>(candidate.Id, new PartitionKey(toHandle), cancellationToken: ct)
                    .ConfigureAwait(false);
                var doc = read.Resource;
                if (!await AgentDispatchMatchesCurrentHandleAsync(doc, ct).ConfigureAwait(false))
                    continue;
                if (doc.State == AgentDispatchStates.Delivering)
                {
                    if (doc.DeliveryLeaseUntil > DateTimeOffset.UtcNow) continue;
                    doc.State = AgentDispatchStates.Assigned;
                    doc.DeliveryLeaseOwner = null;
                    doc.DeliveryLeaseUntil = null;
                }
                // Assigned is pre-delivery and may be reclaimed safely. Delivered is never reassigned.
                if (doc.State is not (AgentDispatchStates.Pending or AgentDispatchStates.Assigned)) continue;
                var deviceId = AgentDispatchRecipientPolicy.ChooseDevice(
                    doc.RecipientDeviceIds, readyDevices);
                if (deviceId is null)
                {
                    if (doc.State == AgentDispatchStates.Pending) continue;
                    doc.State = AgentDispatchStates.Pending;
                    doc.AssignedDeviceId = null;
                    doc.AssignedAt = null;
                }
                else
                {
                    if (doc.State == AgentDispatchStates.Assigned
                        && string.Equals(doc.AssignedDeviceId, deviceId, StringComparison.Ordinal))
                        continue;
                    doc.State = AgentDispatchStates.Assigned;
                    doc.AssignedDeviceId = deviceId;
                    doc.AssignedAt = DateTimeOffset.UtcNow;
                }
                doc.DeliveredAt = null;
                await agentDispatchesContainer
                    .ReplaceItemAsync(doc, doc.Id, new PartitionKey(toHandle),
                        new ItemRequestOptions { IfMatchEtag = read.ETag }, ct)
                    .ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
                // Another relay instance assigned, delivered, reassigned, or expired this request first.
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredAgentDispatch>> TakeAssignedAgentDispatchesAsync(
        string toHandle,
        string deviceId,
        string leaseOwner,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default)
    {
        var result = new List<StoredAgentDispatch>();
        var assigned = await QueryAgentDispatchesAsync(
            toHandle, AgentDispatchStates.Assigned, ct).ConfigureAwait(false);
        var delivering = await QueryAgentDispatchesAsync(
            toHandle, AgentDispatchStates.Delivering, ct).ConfigureAwait(false);
        foreach (var candidate in assigned.Concat(delivering)
                     .OrderBy(candidate => candidate.QueuedAt)
                     .ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            try
            {
                var read = await agentDispatchesContainer
                    .ReadItemAsync<AgentDispatchDoc>(candidate.Id, new PartitionKey(toHandle), cancellationToken: ct)
                    .ConfigureAwait(false);
                var doc = read.Resource;
                if (!await AgentDispatchMatchesCurrentHandleAsync(doc, ct).ConfigureAwait(false))
                    continue;
                var now = DateTimeOffset.UtcNow;
                var claimable = doc.State == AgentDispatchStates.Assigned
                    || (doc.State == AgentDispatchStates.Delivering
                        && doc.DeliveryLeaseUntil <= now);
                if (!claimable
                    || !string.Equals(doc.AssignedDeviceId, deviceId, StringComparison.Ordinal))
                    continue;
                doc.State = AgentDispatchStates.Delivering;
                doc.DeliveryLeaseOwner = leaseOwner;
                doc.DeliveryLeaseUntil = now + (leaseDuration ?? RelayInboxPolicy.LeaseDuration);
                var replaced = await agentDispatchesContainer
                    .ReplaceItemAsync(doc, doc.Id, new PartitionKey(toHandle),
                        new ItemRequestOptions { IfMatchEtag = read.ETag }, ct)
                    .ConfigureAwait(false);
                result.Add(ToStored(replaced.Resource));
                return result;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
                // Another relay instance delivered or expired this request first.
            }
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAgentDispatchDeliveredAsync(
        string toHandle,
        string dispatchId,
        string deviceId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        try
        {
            var read = await agentDispatchesContainer
                .ReadItemAsync<AgentDispatchDoc>(
                    dispatchId, new PartitionKey(toHandle), cancellationToken: ct)
                .ConfigureAwait(false);
            var doc = read.Resource;
            if (!await AgentDispatchMatchesCurrentHandleAsync(doc, ct).ConfigureAwait(false)
                || !OwnsLiveDeliveryLease(doc, deviceId, leaseOwner, DateTimeOffset.UtcNow))
                return false;
            doc.State = AgentDispatchStates.Delivered;
            doc.DeliveredAt = DateTimeOffset.UtcNow;
            doc.DeliveryLeaseOwner = null;
            doc.DeliveryLeaseUntil = null;
            await agentDispatchesContainer
                .ReplaceItemAsync(doc, doc.Id, new PartitionKey(toHandle),
                    new ItemRequestOptions { IfMatchEtag = read.ETag }, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (
            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAgentDispatchAsync(
        string toHandle,
        string dispatchId,
        string deviceId,
        string leaseOwner,
        string? nextDeviceId = null,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        try
        {
            var read = await agentDispatchesContainer
                .ReadItemAsync<AgentDispatchDoc>(dispatchId, new PartitionKey(toHandle), cancellationToken: ct)
                .ConfigureAwait(false);
            var doc = read.Resource;
            if (!await AgentDispatchMatchesCurrentHandleAsync(doc, ct).ConfigureAwait(false))
                return false;
            if (!OwnsLiveDeliveryLease(doc, deviceId, leaseOwner, DateTimeOffset.UtcNow))
                return false;
            if (string.IsNullOrWhiteSpace(nextDeviceId))
            {
                doc.State = AgentDispatchStates.Pending;
                doc.AssignedDeviceId = null;
                doc.AssignedAt = null;
            }
            else
            {
                doc.State = AgentDispatchStates.Assigned;
                doc.AssignedDeviceId = nextDeviceId;
                doc.AssignedAt = DateTimeOffset.UtcNow;
            }
            doc.DeliveredAt = null;
            doc.DeliveryLeaseOwner = null;
            doc.DeliveryLeaseUntil = null;
            await agentDispatchesContainer
                .ReplaceItemAsync(doc, doc.Id, new PartitionKey(toHandle),
                    new ItemRequestOptions { IfMatchEtag = read.ETag }, ct)
                .ConfigureAwait(false);
            return true;
        }

        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    private static bool OwnsLiveDeliveryLease(
        AgentDispatchDoc dispatch,
        string deviceId,
        string leaseOwner,
        DateTimeOffset now)
        => dispatch.State == AgentDispatchStates.Delivering
           && string.Equals(dispatch.AssignedDeviceId, deviceId, StringComparison.Ordinal)
           && string.Equals(dispatch.DeliveryLeaseOwner, leaseOwner, StringComparison.Ordinal)
           && dispatch.DeliveryLeaseUntil > now;

    /// <inheritdoc />
    public async Task<AgentDispatchResponseStageResult> StageAgentDispatchResponseAsync(
        string toHandle,
        string dispatchId,
        string fromHandle,
        string dispatchToken,
        string respondingDeviceId,
        string responseId,
        string responseJson,
        string responseHash,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var read = await agentDispatchesContainer
                    .ReadItemAsync<AgentDispatchDoc>(
                        dispatchId, new PartitionKey(toHandle), cancellationToken: ct)
                    .ConfigureAwait(false);
                var doc = read.Resource;
                if (!await AgentDispatchMatchesCurrentHandleAsync(doc, ct).ConfigureAwait(false)
                    || !string.Equals(doc.From, fromHandle, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(doc.DispatchToken, dispatchToken, StringComparison.Ordinal)
                    || !string.Equals(doc.AssignedDeviceId, respondingDeviceId, StringComparison.Ordinal))
                    return new AgentDispatchResponseStageResult(false, false, false, null);

                if (doc.State is AgentDispatchStates.ResponsePending or AgentDispatchStates.Completed)
                {
                    var duplicate = string.Equals(doc.ResponseId, responseId, StringComparison.Ordinal)
                        && string.Equals(doc.ResponseHash, responseHash, StringComparison.Ordinal);
                    return new AgentDispatchResponseStageResult(
                        duplicate,
                        false,
                        duplicate && doc.State == AgentDispatchStates.Completed,
                        duplicate ? doc.ResponseJson : null);
                }
                if (!string.Equals(doc.State, AgentDispatchStates.Delivered, StringComparison.Ordinal))
                    return new AgentDispatchResponseStageResult(false, false, false, null);

                doc.State = AgentDispatchStates.ResponsePending;
                doc.ResponseId = responseId;
                doc.ResponseJson = responseJson;
                doc.ResponseHash = responseHash;
                doc.ResponseStagedAt = DateTimeOffset.UtcNow;
                doc.EnvelopeJson = "";
                doc.RecipientDeviceIds = new List<string>();
                await agentDispatchesContainer
                    .ReplaceItemAsync(doc, doc.Id, new PartitionKey(toHandle),
                        new ItemRequestOptions { IfMatchEtag = read.ETag }, ct)
                    .ConfigureAwait(false);
                return new AgentDispatchResponseStageResult(true, true, false, responseJson);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new AgentDispatchResponseStageResult(false, false, false, null);
            }
        }
        throw new InvalidOperationException("Agent response staging did not converge.");
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAgentDispatchResponseAsync(
        string toHandle,
        string dispatchId,
        string responseId,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var read = await agentDispatchesContainer
                    .ReadItemAsync<AgentDispatchDoc>(
                        dispatchId, new PartitionKey(toHandle), cancellationToken: ct)
                    .ConfigureAwait(false);
                var doc = read.Resource;
                if (string.Equals(doc.State, AgentDispatchStates.Completed, StringComparison.Ordinal)
                    && string.Equals(doc.ResponseId, responseId, StringComparison.Ordinal))
                    return true;
                if (!string.Equals(doc.State, AgentDispatchStates.ResponsePending, StringComparison.Ordinal)
                    || !string.Equals(doc.ResponseId, responseId, StringComparison.Ordinal))
                    return false;
                doc.State = AgentDispatchStates.Completed;
                doc.CompletedAt = DateTimeOffset.UtcNow;
                await agentDispatchesContainer
                    .ReplaceItemAsync(doc, doc.Id, new PartitionKey(toHandle),
                        new ItemRequestOptions { IfMatchEtag = read.ETag }, ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }
        throw new InvalidOperationException("Agent response completion did not converge.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredAgentDispatch>> GetPendingAgentResponsesAsync(
        int maxItems = 100,
        CancellationToken ct = default)
    {
        if (maxItems <= 0) return [];
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var limit = Math.Clamp(maxItems, 1, 100);
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.state = @state ORDER BY c.responseStagedAt ASC")
            .WithParameter("@state", AgentDispatchStates.ResponsePending);
        using var iterator = agentDispatchesContainer.GetItemQueryIterator<AgentDispatchDoc>(
            query,
            requestOptions: new QueryRequestOptions { MaxItemCount = limit });
        if (!iterator.HasMoreResults) return [];
        var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
        return page.Resource.Take(limit).Select(ToStored).ToArray();
    }

    private async Task<IReadOnlyList<AgentDispatchDoc>> QueryAgentDispatchesAsync(
        string toHandle,
        string state,
        CancellationToken ct)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var generation = await GetHandleInboxGenerationAsync(toHandle, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(generation)) return [];
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.to = @to AND c.state = @state"
                + " AND (NOT IS_DEFINED(c.admissionGeneration) OR c.admissionGeneration = @generation)"
                + " ORDER BY c.queuedAt ASC")
            .WithParameter("@to", toHandle)
            .WithParameter("@state", state)
            .WithParameter("@generation", generation);
        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(toHandle) };
        var result = new List<AgentDispatchDoc>();
        using var iterator = agentDispatchesContainer.GetItemQueryIterator<AgentDispatchDoc>(
            query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            result.AddRange(page);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<HandleRatePolicy?> GetHandleRatePolicyAsync(
        string handle, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var normalizedHandle = NormalizeHandle(handle);
        try
        {
            var response = await ratePoliciesContainer
                .ReadItemAsync<RatePolicyDoc>(
                    normalizedHandle, new PartitionKey(normalizedHandle), cancellationToken: ct)
                .ConfigureAwait(false);
            var doc = response.Resource;
            return new HandleRatePolicy(
                doc.MessagesPerMinute,
                doc.BurstCapacity,
                doc.GroupMessagesPerMinute,
                doc.GroupBurstCapacity,
                doc.MaxFanoutRecipients,
                doc.Enabled);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetHandleRatePolicyAsync(
        string handle, HandleRatePolicy policy, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var normalizedHandle = NormalizeHandle(handle);
        var doc = new RatePolicyDoc
        {
            Id = normalizedHandle,
            Handle = normalizedHandle,
            MessagesPerMinute = policy.MessagesPerMinute,
            BurstCapacity = policy.BurstCapacity,
            GroupMessagesPerMinute = policy.GroupMessagesPerMinute,
            GroupBurstCapacity = policy.GroupBurstCapacity,
            MaxFanoutRecipients = policy.MaxFanoutRecipients,
            Enabled = policy.Enabled,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await ratePoliciesContainer
            .UpsertItemAsync(doc, new PartitionKey(normalizedHandle), cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteHandleRatePolicyAsync(
        string handle, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        var normalizedHandle = NormalizeHandle(handle);
        try
        {
            await ratePoliciesContainer
                .DeleteItemAsync<RatePolicyDoc>(
                    normalizedHandle, new PartitionKey(normalizedHandle), cancellationToken: ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // ---- Capability directory + reputation ----------------------------------

    /// <inheritdoc />
    public async Task UpsertServiceAsync(StoredService svc, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            ServiceDoc? doc = null;
            string? etag = null;
            try
            {
                var read = await servicesContainer
                    .ReadItemAsync<ServiceDoc>(svc.ServiceId, new PartitionKey(svc.ServiceId), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                doc = null;
            }

            if (doc is null)
            {
                var fresh = ToDoc(svc);
                try
                {
                    await servicesContainer
                        .CreateItemAsync(fresh, new PartitionKey(fresh.ServiceId), cancellationToken: ct)
                        .ConfigureAwait(false);
                    return;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict && attempt < maxAttempts)
                {
                    continue; // A concurrent create won the race; re-read and update instead.
                }
            }
            else
            {
                // Preserve reputation (votes + attested users) across a re-publish; only refresh metadata.
                doc.Handle = svc.Handle;
                doc.Name = svc.Name;
                doc.Description = svc.Description;
                doc.Category = svc.Category;
                try
                {
                    var options = etag is null ? null : new ItemRequestOptions { IfMatchEtag = etag };
                    await servicesContainer
                        .UpsertItemAsync(doc, new PartitionKey(doc.ServiceId), options, ct)
                        .ConfigureAwait(false);
                    return;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
                {
                    continue; // Lost the optimistic concurrency check; retry the read-modify-write.
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveServiceAsync(string handle, string serviceId, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        ServiceDoc doc;
        try
        {
            var read = await servicesContainer
                .ReadItemAsync<ServiceDoc>(serviceId, new PartitionKey(serviceId), cancellationToken: ct)
                .ConfigureAwait(false);
            doc = read.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        // Only the owning handle may unpublish.
        if (!string.Equals(doc.Handle, handle, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            await servicesContainer
                .DeleteItemAsync<ServiceDoc>(serviceId, new PartitionKey(serviceId), cancellationToken: ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false; // Lost the race to a concurrent delete.
        }
    }

    /// <inheritdoc />
    public async Task<StoredService?> GetServiceAsync(string serviceId, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await servicesContainer
                .ReadItemAsync<ServiceDoc>(serviceId, new PartitionKey(serviceId), cancellationToken: ct)
                .ConfigureAwait(false);
            return ToStored(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredService>> ListServicesAsync(string? query, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        // Simple ReadAll + in-memory filter: the directory is small relative to messaging volume, so a
        // cross-partition scan is acceptable for now. A future version can push the filter into a Cosmos
        // query (CONTAINS on name/description/category) once the directory grows.
        var iterator = servicesContainer.GetItemQueryIterator<ServiceDoc>(new QueryDefinition("SELECT * FROM c"));
        var docs = new List<ServiceDoc>();
        using (iterator)
        {
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                docs.AddRange(page);
            }
        }

        IEnumerable<StoredService> all = docs.Select(ToStored);
        var q = query?.Trim();
        if (!string.IsNullOrEmpty(q))
            all = all.Where(s =>
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        return all.ToList();
    }

    /// <inheritdoc />
    public async Task RecordServiceUsageAsync(string serviceId, string userHandle, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            ServiceDoc doc;
            string etag;
            try
            {
                var read = await servicesContainer
                    .ReadItemAsync<ServiceDoc>(serviceId, new PartitionKey(serviceId), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return; // No-op if the service does not exist.
            }

            doc.Users ??= new List<string>();
            if (doc.Users.Any(u => string.Equals(u, userHandle, StringComparison.OrdinalIgnoreCase)))
                return; // Already recorded.
            doc.Users.Add(userHandle);

            try
            {
                await servicesContainer
                    .UpsertItemAsync(doc, new PartitionKey(serviceId), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasUsedServiceAsync(string serviceId, string userHandle, CancellationToken ct = default)
    {
        var svc = await GetServiceAsync(serviceId, ct).ConfigureAwait(false);
        return svc is not null && svc.Users.Contains(userHandle);
    }

    /// <inheritdoc />
    public async Task SetServiceVoteAsync(string serviceId, string voterHandle, int vote, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            ServiceDoc doc;
            string etag;
            try
            {
                var read = await servicesContainer
                    .ReadItemAsync<ServiceDoc>(serviceId, new PartitionKey(serviceId), cancellationToken: ct)
                    .ConfigureAwait(false);
                doc = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return; // No-op if the service does not exist.
            }

            doc.Votes ??= new Dictionary<string, int>();
            // Votes are keyed by normalized voter handle; clear the existing entry then re-set to keep
            // one updatable vote per voter regardless of the stored key's original casing.
            var existingKey = doc.Votes.Keys.FirstOrDefault(k => string.Equals(k, voterHandle, StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null) doc.Votes.Remove(existingKey);
            if (vote != 0) doc.Votes[voterHandle] = vote > 0 ? 1 : -1;

            try
            {
                await servicesContainer
                    .UpsertItemAsync(doc, new PartitionKey(serviceId), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <summary>Projects a persisted service document back to the public <see cref="StoredService"/> shape.</summary>
    private static StoredService ToStored(ServiceDoc doc) => new()
    {
        ServiceId = doc.ServiceId,
        Handle = doc.Handle,
        Name = doc.Name,
        Description = doc.Description,
        Category = doc.Category,
        PublishedAt = doc.PublishedAt,
        Votes = doc.Votes is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(doc.Votes, StringComparer.OrdinalIgnoreCase),
        Users = doc.Users is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(doc.Users, StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>Projects a <see cref="StoredService"/> to its persisted Cosmos document form.</summary>
    private static ServiceDoc ToDoc(StoredService svc) => new()
    {
        Id = svc.ServiceId,
        ServiceId = svc.ServiceId,
        Handle = svc.Handle,
        Name = svc.Name,
        Description = svc.Description,
        Category = svc.Category,
        PublishedAt = svc.PublishedAt,
        Votes = new Dictionary<string, int>(svc.Votes),
        Users = svc.Users.ToList()
    };

    /// <summary>Projects a persisted handle document back to the public <see cref="StoredHandle"/> shape.</summary>
    private static StoredHandle ToStored(HandleDoc doc) => new()
    {
        Handle = doc.Handle,
        DisplayName = doc.DisplayName,
        RegisteredAt = doc.RegisteredAt,
        InboxGeneration = doc.InboxGeneration ?? "",
        DevicePublicKeys = doc.DevicePublicKeys is null ? new List<string>() : new List<string>(doc.DevicePublicKeys),
        DeviceQueueGenerations = doc.DeviceQueueGenerations is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(doc.DeviceQueueGenerations, StringComparer.Ordinal),
        RecoveryPublicKey = doc.RecoveryPublicKey,
        DeviceNames = doc.DeviceNames is null ? new Dictionary<string, string>() : new Dictionary<string, string>(doc.DeviceNames),
        DevicePlatforms = doc.DevicePlatforms is null ? new Dictionary<string, string>() : new Dictionary<string, string>(doc.DevicePlatforms),
        DeviceRemoteAgentEnabled = doc.DeviceRemoteAgentEnabled is null
            ? new Dictionary<string, bool>()
            : new Dictionary<string, bool>(doc.DeviceRemoteAgentEnabled),
        DeviceAtomicAgentDispatchEnabled = doc.DeviceAtomicAgentDispatchEnabled is null
            ? new Dictionary<string, bool>()
            : new Dictionary<string, bool>(doc.DeviceAtomicAgentDispatchEnabled),
        DeviceProtocolVersions = doc.DeviceProtocolVersions is null
            ? new Dictionary<string, int>()
            : new Dictionary<string, int>(doc.DeviceProtocolVersions),
        AgentPrimaryDeviceId = doc.AgentPrimaryDeviceId,
        AgentFailoverDeviceId = doc.AgentFailoverDeviceId,
        AgentRoutingVersion = doc.AgentRoutingVersion ?? "",
        AgentPrimaryWasSelectedAutomatically = doc.AgentPrimaryWasSelectedAutomatically,
        DevicePushTokens = doc.DevicePushTokens is null
            ? new Dictionary<string, DevicePushToken>()
            : new Dictionary<string, DevicePushToken>(doc.DevicePushTokens)
    };

    private static string NormalizeHandle(string handle)
        => handle.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Cosmos document for a handle registration. Uses lowercase "handle" as the partition key.</summary>
    private sealed class HandleDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("handle")]
        public string Handle { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("registeredAt")]
        public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("devicePublicKeys")]
        public List<string> DevicePublicKeys { get; set; } = new();

        [JsonPropertyName("inboxGeneration")]
        public string? InboxGeneration { get; set; }

        [JsonPropertyName("deviceQueueGenerations")]
        public Dictionary<string, string>? DeviceQueueGenerations { get; set; }

        [JsonPropertyName("deviceQueueFences")]
        public Dictionary<string, string>? DeviceQueueFences { get; set; }

        [JsonPropertyName("queueAdmissionGeneration")]
        public string? QueueAdmissionGeneration { get; set; }

        [JsonPropertyName("queueAdmissionBlocked")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool QueueAdmissionBlocked { get; set; }

        [JsonPropertyName("pendingRevokedDeviceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PendingRevokedDeviceId { get; set; }

        [JsonPropertyName("pendingRevocationGeneration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PendingRevocationGeneration { get; set; }

        [JsonPropertyName("pendingAdmissionGeneration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PendingAdmissionGeneration { get; set; }

        [JsonPropertyName("deleting")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Deleting { get; set; }

        [JsonPropertyName("recoveryPublicKey")]
        public string? RecoveryPublicKey { get; set; }

        [JsonPropertyName("deviceNames")]
        public Dictionary<string, string>? DeviceNames { get; set; }

        [JsonPropertyName("devicePlatforms")]
        public Dictionary<string, string>? DevicePlatforms { get; set; }

        [JsonPropertyName("deviceRemoteAgentEnabled")]
        public Dictionary<string, bool>? DeviceRemoteAgentEnabled { get; set; }

        [JsonPropertyName("deviceAtomicAgentDispatchEnabled")]
        public Dictionary<string, bool>? DeviceAtomicAgentDispatchEnabled { get; set; }

        [JsonPropertyName("deviceProtocolVersions")]
        public Dictionary<string, int>? DeviceProtocolVersions { get; set; }

        [JsonPropertyName("agentPrimaryDeviceId")]
        public string? AgentPrimaryDeviceId { get; set; }

        [JsonPropertyName("agentFailoverDeviceId")]
        public string? AgentFailoverDeviceId { get; set; }

        [JsonPropertyName("agentRoutingVersion")]
        public string? AgentRoutingVersion { get; set; }

        [JsonPropertyName("agentPrimaryWasSelectedAutomatically")]
        public bool AgentPrimaryWasSelectedAutomatically { get; set; }

        [JsonPropertyName("devicePushTokens")]
        public Dictionary<string, DevicePushToken>? DevicePushTokens { get; set; }
    }

    private enum DeviceQueueEntryValidation
    {
        Valid,
        Retry,
        Stale
    }

    /// <summary>
    /// Administrative per-handle rate-policy override. This document intentionally contains
    /// policy configuration only and is not projected through the public handle model.
    /// </summary>
    private sealed class RatePolicyDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("handle")]
        public string Handle { get; set; } = "";

        [JsonPropertyName("messagesPerMinute")]
        public int MessagesPerMinute { get; set; }

        [JsonPropertyName("burstCapacity")]
        public int BurstCapacity { get; set; }

        [JsonPropertyName("groupMessagesPerMinute")]
        public int GroupMessagesPerMinute { get; set; }

        [JsonPropertyName("groupBurstCapacity")]
        public int GroupBurstCapacity { get; set; }

        [JsonPropertyName("maxFanoutRecipients")]
        public int MaxFanoutRecipients { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>Cosmos document for a link invite, carrying a per-item "ttl" for native expiry.</summary>
    private sealed class InviteDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("handle")]
        public string Handle { get; set; } = "";

        [JsonPropertyName("codeHash")]
        public string CodeHash { get; set; } = "";

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; }
    }

    private static readonly int[] InboxPriorityOrder =
    [
        RelayInboxPriority.Critical,
        RelayInboxPriority.Control,
        RelayInboxPriority.Normal,
        RelayInboxPriority.Sync,
        RelayInboxPriority.Bulk
    ];

    private static QueryDefinition InboxAvailableQuery(
        int priority,
        DateTimeOffset now,
        bool includeForeground)
    {
        var priorityFilter = priority == RelayInboxPriority.Normal
            ? "(NOT IS_DEFINED(c.priority) OR c.priority = @priority)"
            : "c.priority = @priority";
        var eligibilityFilter = includeForeground
            ? ""
            : "AND c.requiresForeground = false";
        return new QueryDefinition($"""
                SELECT * FROM c
                WHERE (NOT IS_DEFINED(c.leaseUntil) OR IS_NULL(c.leaseUntil) OR c.leaseUntil <= @now)
                  AND (NOT IS_DEFINED(c.type) OR c.type = 'inbox')
                  AND {priorityFilter}
                  {eligibilityFilter}
                ORDER BY c.queuedAt ASC
                """)
            .WithParameter("@now", now)
            .WithParameter("@priority", priority);
    }

    private async Task<(InboxDoc Doc, string ETag)?> ReadNextAvailableInboxItemAsync(
        PartitionKey partition,
        DateTimeOffset now,
        bool includeForeground,
        CancellationToken ct)
    {
        var aged = await ReadAgedAvailableInboxItemAsync(
            partition, now, includeForeground, ct).ConfigureAwait(false);
        if (aged is not null) return aged;
        foreach (var priority in InboxPriorityOrder)
        {
            var options = new QueryRequestOptions { PartitionKey = partition, MaxItemCount = 4 };
            using var iterator = inboxContainer.GetItemQueryIterator<InboxDoc>(
                InboxAvailableQuery(priority, now, includeForeground), requestOptions: options);
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                foreach (var candidate in page)
                {
                    try
                    {
                        var read = await inboxContainer.ReadItemAsync<InboxDoc>(
                            candidate.Id, partition, cancellationToken: ct).ConfigureAwait(false);
                        var doc = read.Resource;
                        if (!RefreshInboxTtl(doc, now))
                        {
                            await inboxContainer.DeleteItemAsync<InboxDoc>(
                                doc.Id,
                                partition,
                                new ItemRequestOptions { IfMatchEtag = read.ETag },
                                ct).ConfigureAwait(false);
                            continue;
                        }
                        if (doc.LeaseUntil > now) continue;
                        if (!includeForeground && doc.RequiresForeground != false) continue;
                        return (doc, read.ETag);
                    }
                    catch (CosmosException ex) when (
                        ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                    {
                    }
                }
            }
        }
        return null;
    }
    private async Task<(InboxDoc Doc, string ETag)?> ReadAgedAvailableInboxItemAsync(
        PartitionKey partition,
        DateTimeOffset now,
        bool includeForeground,
        CancellationToken ct)
    {
        var queuedBefore = now - RelayInboxPolicy.PriorityAgingThreshold;
        foreach (var priority in InboxPriorityOrder.Reverse())
        {
            var priorityFilter = priority == RelayInboxPriority.Normal
                ? "(NOT IS_DEFINED(c.priority) OR c.priority = @priority)"
                : "c.priority = @priority";
            var eligibilityFilter = includeForeground
                ? ""
                : "AND c.requiresForeground = false";
            var query = new QueryDefinition($"""
                    SELECT * FROM c
                    WHERE (NOT IS_DEFINED(c.leaseUntil) OR IS_NULL(c.leaseUntil) OR c.leaseUntil <= @now)
                      AND (NOT IS_DEFINED(c.type) OR c.type = 'inbox')
                      AND c.queuedAt <= @queuedBefore
                      AND {priorityFilter}
                      {eligibilityFilter}
                    ORDER BY c.queuedAt ASC
                    """)
                .WithParameter("@now", now)
                .WithParameter("@queuedBefore", queuedBefore)
                .WithParameter("@priority", priority);
            var options = new QueryRequestOptions { PartitionKey = partition, MaxItemCount = 4 };
            using var iterator = inboxContainer.GetItemQueryIterator<InboxDoc>(
                query, requestOptions: options);
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                foreach (var candidate in page)
                {
                    try
                    {
                        var read = await inboxContainer.ReadItemAsync<InboxDoc>(
                            candidate.Id, partition, cancellationToken: ct).ConfigureAwait(false);
                        var doc = read.Resource;
                        if (!RefreshInboxTtl(doc, now))
                        {
                            await inboxContainer.DeleteItemAsync<InboxDoc>(
                                doc.Id,
                                partition,
                                new ItemRequestOptions { IfMatchEtag = read.ETag },
                                ct).ConfigureAwait(false);
                            continue;
                        }
                        if (doc.LeaseUntil > now
                            || doc.QueuedAt > queuedBefore
                            || (!includeForeground && doc.RequiresForeground != false))
                            continue;
                        return (doc, read.ETag);
                    }
                    catch (CosmosException ex) when (
                        ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                    {
                    }
                }
            }
        }
        return null;
    }
    private async Task<int> PurgeInboxPartitionAsync(string inboxKey, CancellationToken ct)
    {
        var partition = new PartitionKey(inboxKey);
        var query = new QueryDefinition(
            "SELECT VALUE COUNT(1) FROM c WHERE NOT IS_DEFINED(c.type) OR c.type = 'inbox'");
        var options = new QueryRequestOptions { PartitionKey = partition };
        long queued = 0;
        using (var iterator = inboxContainer.GetItemQueryIterator<long>(query, requestOptions: options))
        {
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                queued += page.Resource.Sum();
            }
        }

        await DeletePartitionItemsAsync(
            inboxContainer,
            partition,
            "SELECT c.id, c._etag FROM c",
            "inbox partition purge",
            ct).ConfigureAwait(false);
        return checked((int)Math.Min(queued, int.MaxValue));
    }

    private async Task PurgeHandleAgentDispatchesAsync(string handle, CancellationToken ct)
    {
        var partition = new PartitionKey(NormalizeHandle(handle));
        await DeletePartitionItemsAsync(
            agentDispatchesContainer,
            partition,
            "SELECT c.id, c._etag FROM c",
            "handle agent-dispatch purge",
            ct).ConfigureAwait(false);
    }

    private async Task<int> PurgeDeviceQueuePartitionAsync(string queueKey, CancellationToken ct)
    {
        var partition = new PartitionKey(queueKey);
        long queued = 0;
        using (var iterator = inboxContainer.GetItemQueryIterator<long>(
                   new QueryDefinition(
                       "SELECT VALUE COUNT(1) FROM c WHERE c.type = 'device-queue'"),
                   requestOptions: new QueryRequestOptions { PartitionKey = partition }))
        {
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                queued += page.Resource.Sum();
            }
        }
        await DeletePartitionItemsAsync(
            inboxContainer,
            partition,
            "SELECT c.id, c._etag FROM c",
            "device queue partition purge",
            ct).ConfigureAwait(false);
        return checked((int)Math.Min(queued, int.MaxValue));
    }

    private static async Task DeletePartitionItemsAsync(
        Container container,
        PartitionKey partition,
        string queryText,
        string operation,
        CancellationToken ct)
    {
        var concurrencyRetries = 0;
        while (true)
        {
            IReadOnlyList<ItemEtagProjection> items;
            using (var iterator = container.GetItemQueryIterator<ItemEtagProjection>(
                       new QueryDefinition(queryText),
                       requestOptions: new QueryRequestOptions
                       {
                           PartitionKey = partition,
                           MaxItemCount = InboxPurgePageSize
                       }))
            {
                if (!iterator.HasMoreResults) return;
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                items = page.Resource.Take(InboxPurgePageSize).ToArray();
            }
            if (items.Count == 0) return;

            var batch = container.CreateTransactionalBatch(partition);
            foreach (var item in items)
                batch.DeleteItem(
                    item.Id,
                    new TransactionalBatchItemRequestOptions { IfMatchEtag = item.ETag });
            using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                concurrencyRetries = 0;
                continue;
            }
            if (BatchContainsStatus(
                    response,
                    HttpStatusCode.NotFound,
                    HttpStatusCode.PreconditionFailed))
            {
                if (++concurrencyRetries <= 20) continue;
                throw new InvalidOperationException($"{operation} did not converge under concurrent mutation.");
            }
            ThrowBatchFailure(response, operation);
        }
    }

    private async Task PurgeHandleInboxPartitionsAsync(string handle, CancellationToken ct)
    {
        var normalized = NormalizeHandle(handle);
        var prefix = normalized + "\u001f";
        var partitions = new HashSet<string>(StringComparer.Ordinal);
        using var iterator = inboxContainer.GetItemQueryIterator<string>(
            new QueryDefinition(
                    "SELECT DISTINCT VALUE c.to FROM c"
                    + " WHERE c.to = @handle OR STARTSWITH(c.to, @prefix)")
                .WithParameter("@handle", normalized)
                .WithParameter("@prefix", prefix));
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            partitions.UnionWith(page);
        }
        foreach (var partition in partitions)
            if (!partition.Contains("\u001fqueue\u001f", StringComparison.Ordinal))
                await PurgeInboxPartitionAsync(partition, ct).ConfigureAwait(false);
    }

    private static void EnsureDeviceQueueGenerations(HandleDoc doc)
    {
        if (string.IsNullOrEmpty(doc.InboxGeneration))
            doc.InboxGeneration = Guid.NewGuid().ToString("n");
        if (string.IsNullOrEmpty(doc.QueueAdmissionGeneration))
            doc.QueueAdmissionGeneration = Guid.NewGuid().ToString("n");
        doc.DeviceQueueGenerations ??= new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var publicKey in doc.DevicePublicKeys)
        {
            var deviceId = DeviceProtocol.DeviceId(publicKey);
            if (!doc.DeviceQueueGenerations.ContainsKey(deviceId))
                doc.DeviceQueueGenerations[deviceId] = Guid.NewGuid().ToString("n");
        }
    }

    private async Task<string?> GetInboxAdmissionGenerationAsync(
        string inboxKey,
        CancellationToken ct)
    {
        var (handle, deviceId) = ParseInboxKey(inboxKey);
        var doc = await ReadHandleWithStableGenerationsAsync(handle, ct).ConfigureAwait(false);
        if (doc is null)
        {
            if (await ReadInboxControlWithEtagAsync(handle, ct).ConfigureAwait(false) is
                    { Doc.Active: false, Doc.Generation: "__deleted__" })
                return "";
            return null;
        }
        if (doc.Deleting || doc.QueueAdmissionBlocked)
            return "";
        if (deviceId is null)
            return doc.InboxGeneration;
        return doc.DeviceQueueGenerations!.TryGetValue(deviceId, out var deviceGeneration)
            ? InboxAdmissionGeneration(doc.InboxGeneration!, deviceGeneration)
            : "";
    }

    private async Task CreateDeletedHandleInboxFenceAsync(string handle, CancellationToken ct)
    {
        var normalized = NormalizeHandle(handle);
        await inboxContainer.UpsertItemAsync(
            new InboxAdmissionControlDoc
            {
                To = normalized,
                Generation = "__deleted__",
                Active = false
            },
            new PartitionKey(normalized),
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<string?> GetHandleInboxGenerationAsync(
        string handle,
        CancellationToken ct)
    {
        var normalized = NormalizeHandle(handle);
        var doc = await ReadHandleWithStableGenerationsAsync(normalized, ct).ConfigureAwait(false);
        return doc is null || doc.Deleting || doc.QueueAdmissionBlocked
            ? null
            : doc.InboxGeneration;
    }

    private async Task<HandleDoc?> ReadHandleWithStableGenerationsAsync(
        string handle,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ItemResponse<HandleDoc> read;
            try
            {
                read = await handlesContainer.ReadItemAsync<HandleDoc>(
                    handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var doc = read.Resource;
            var needsMigration = string.IsNullOrEmpty(doc.InboxGeneration)
                || string.IsNullOrEmpty(doc.QueueAdmissionGeneration)
                || doc.DeviceQueueGenerations is null
                || doc.DevicePublicKeys.Any(publicKey =>
                    !doc.DeviceQueueGenerations.ContainsKey(DeviceProtocol.DeviceId(publicKey)));
            if (!needsMigration) return doc;
            EnsureDeviceQueueGenerations(doc);
            try
            {
                var replaced = await handlesContainer.ReplaceItemAsync(
                    doc,
                    doc.Id,
                    new PartitionKey(handle),
                    new ItemRequestOptions { IfMatchEtag = read.ETag },
                    ct).ConfigureAwait(false);
                return replaced.Resource;
            }
            catch (CosmosException ex) when (
                ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
            }
        }
        throw new InvalidOperationException("Handle generation migration did not converge.");
    }

    private async Task<bool> AgentDispatchMatchesCurrentHandleAsync(
        AgentDispatchDoc doc,
        CancellationToken ct)
    {
        var current = await GetHandleInboxGenerationAsync(doc.To, ct).ConfigureAwait(false);
        return current is not null
            && (string.IsNullOrEmpty(doc.AdmissionGeneration)
                || string.Equals(doc.AdmissionGeneration, current, StringComparison.Ordinal));
    }

    private async Task ActivateCurrentInboxesAsync(HandleDoc doc, CancellationToken ct)
        {
            if (doc.Deleting || doc.QueueAdmissionBlocked) return;
            await ActivateInboxAsync(doc.Handle, doc.InboxGeneration!, ct).ConfigureAwait(false);
            foreach (var (deviceId, generation) in doc.DeviceQueueGenerations!)
                await ActivateInboxAsync(
                    RelayInboxKey.Device(doc.Handle, deviceId),
                    InboxAdmissionGeneration(doc.InboxGeneration!, generation),
                    ct).ConfigureAwait(false);
        }

        private async Task ActivateInboxAsync(
            string inboxKey,
            string generation,
            CancellationToken ct)
        {
            var partition = new PartitionKey(inboxKey);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var current = await ReadInboxControlWithEtagAsync(inboxKey, ct).ConfigureAwait(false);
                var registeredGeneration = await GetInboxAdmissionGenerationAsync(
                    inboxKey, ct).ConfigureAwait(false);
                if (!string.Equals(registeredGeneration, generation, StringComparison.Ordinal))
                {
                    if (current is not null
                        && current.Value.Doc.Active
                        && string.Equals(
                            current.Value.Doc.Generation, generation, StringComparison.Ordinal))
                        await FenceInboxAdmissionAsync(inboxKey, generation, ct).ConfigureAwait(false);
                    return;
                }
                if (current is not null
                    && current.Value.Doc.Active
                    && string.Equals(current.Value.Doc.Generation, generation, StringComparison.Ordinal))
                    return;
                var active = new InboxAdmissionControlDoc
                {
                    To = inboxKey,
                    Generation = generation,
                    Active = true
                };
                try
                {
                    if (current is null)
                        await inboxContainer.CreateItemAsync(active, partition, cancellationToken: ct)
                            .ConfigureAwait(false);
                    else
                        await inboxContainer.ReplaceItemAsync(
                            active,
                            InboxAdmissionControlId,
                            partition,
                            new ItemRequestOptions { IfMatchEtag = current.Value.ETag },
                            ct).ConfigureAwait(false);
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound
                        or HttpStatusCode.PreconditionFailed)
                {
                    continue;
                }

                if (string.Equals(
                        await GetInboxAdmissionGenerationAsync(inboxKey, ct).ConfigureAwait(false),
                        generation,
                        StringComparison.Ordinal))
                    return;
                await FenceInboxAdmissionAsync(inboxKey, generation, ct).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("Inbox activation did not converge.");
        }

        private async Task FenceInboxAdmissionAsync(
            string inboxKey,
            string generation,
            CancellationToken ct)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var current = await ReadInboxControlWithEtagAsync(inboxKey, ct).ConfigureAwait(false);
                if (current is null
                    || !current.Value.Doc.Active
                    || !string.Equals(current.Value.Doc.Generation, generation, StringComparison.Ordinal))
                    return;
                current.Value.Doc.Active = false;
                try
                {
                    await inboxContainer.ReplaceItemAsync(
                        current.Value.Doc,
                        InboxAdmissionControlId,
                        new PartitionKey(inboxKey),
                        new ItemRequestOptions { IfMatchEtag = current.Value.ETag },
                        ct).ConfigureAwait(false);
                    return;
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                }
            }
            throw new InvalidOperationException("Inbox admission fence did not converge.");
        }

        private async Task FenceAndPurgeInboxAsync(
            string inboxKey,
            string generation,
            CancellationToken ct)
        {
            await FenceInboxAdmissionAsync(inboxKey, generation, ct).ConfigureAwait(false);
            await PurgeInboxPartitionAsync(inboxKey, ct).ConfigureAwait(false);
        }

        private async Task<(InboxAdmissionControlDoc Doc, string ETag)?> ReadInboxControlWithEtagAsync(
            string inboxKey,
            CancellationToken ct)
        {
            try
            {
                var read = await inboxContainer.ReadItemAsync<InboxAdmissionControlDoc>(
                    InboxAdmissionControlId,
                    new PartitionKey(inboxKey),
                    cancellationToken: ct).ConfigureAwait(false);
                return (read.Resource, read.ETag);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

            private async Task<bool> TryReplaceInboxWithAdmissionAsync(
                string inboxKey,
                InboxDoc doc,
                string etag,
                string? admissionGeneration,
                CancellationToken ct)
            {
                var partition = new PartitionKey(NormalizeInboxKey(inboxKey));
                if (admissionGeneration is null)
                {
                    try
                    {
                        await inboxContainer.ReplaceItemAsync(
                            doc,
                            doc.Id,
                            partition,
                            new ItemRequestOptions { IfMatchEtag = etag },
                            ct).ConfigureAwait(false);
                        return true;
                    }
                    catch (CosmosException ex) when (
                        ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                    {
                        return false;
                    }
                }

                var escapedGeneration = EscapeFilterValue(admissionGeneration);
                var batch = inboxContainer.CreateTransactionalBatch(partition)
                    .PatchItem(
                        InboxAdmissionControlId,
                        [PatchOperation.Set("/active", true)],
                        new TransactionalBatchPatchItemRequestOptions
                        {
                            FilterPredicate =
                                $"FROM c WHERE c.active = true AND c.generation = '{escapedGeneration}'"
                        })
                    .ReplaceItem(
                        doc.Id,
                        doc,
                        new TransactionalBatchItemRequestOptions { IfMatchEtag = etag });
                using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
                if (BatchContainsStatus(
                        response,
                        HttpStatusCode.NotFound,
                        HttpStatusCode.PreconditionFailed))
                    return false;
                ThrowBatchFailure(response, "inbox fenced replacement");
                return false;
            }

            private async Task<bool> TryDeleteInboxWithAdmissionAsync(
                string inboxKey,
                string itemId,
                string? admissionGeneration,
                CancellationToken ct)
            {
                var normalized = NormalizeInboxKey(inboxKey);
                var partition = new PartitionKey(normalized);
                if (admissionGeneration is null)
                {
                    try
                    {
                        await inboxContainer.DeleteItemAsync<InboxDoc>(
                            itemId, partition, cancellationToken: ct).ConfigureAwait(false);
                        return true;
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        return false;
                    }
                }

                var escapedGeneration = EscapeFilterValue(admissionGeneration);
                var batch = inboxContainer.CreateTransactionalBatch(partition)
                    .PatchItem(
                        InboxAdmissionControlId,
                        [PatchOperation.Set("/active", true)],
                        new TransactionalBatchPatchItemRequestOptions
                        {
                            FilterPredicate =
                                $"FROM c WHERE c.active = true AND c.generation = '{escapedGeneration}'"
                        })
                    .DeleteItem(itemId);
                using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
                if (BatchContainsStatus(
                        response,
                        HttpStatusCode.NotFound,
                        HttpStatusCode.PreconditionFailed))
                    return false;
                ThrowBatchFailure(response, "inbox fenced delete");
                return false;
            }

            private static bool InboxItemMatchesAdmission(InboxDoc doc, string? admissionGeneration)
                => admissionGeneration is null
                    || (string.IsNullOrEmpty(doc.AdmissionGeneration)
                        && doc.To.IndexOf('\u001f') < 0)
                    || string.Equals(
                        doc.AdmissionGeneration, admissionGeneration, StringComparison.Ordinal);

        private static string InboxAdmissionGeneration(string handleGeneration, string deviceGeneration)
            => handleGeneration + ":" + deviceGeneration;

        private static string NormalizeInboxKey(string inboxKey)
        {
            var (handle, deviceId) = ParseInboxKey(inboxKey);
            return deviceId is null ? handle : RelayInboxKey.Device(handle, deviceId);
        }

        private static (string Handle, string? DeviceId) ParseInboxKey(string inboxKey)
        {
            var normalized = inboxKey.Trim().TrimStart('@').ToLowerInvariant();
            var separator = normalized.IndexOf('\u001f');
            return separator < 0
                ? (normalized, null)
                : (normalized[..separator], normalized[(separator + 1)..]);
        }

        private static string EscapeFilterValue(string value)
            => value.Replace("'", "''", StringComparison.Ordinal);

    private async Task ActivateCurrentDeviceQueuesAsync(HandleDoc doc, CancellationToken ct)
    {
        if (doc.Deleting || doc.QueueAdmissionBlocked) return;
        foreach (var (deviceId, generation) in doc.DeviceQueueGenerations!)
            await ActivateDeviceQueueAsync(
                doc.Handle,
                deviceId,
                generation,
                doc.QueueAdmissionGeneration!,
                ct).ConfigureAwait(false);
    }

    private async Task CompleteDeviceQueueFenceAsync(
        string handle,
        string deviceId,
        string generation,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            ItemResponse<HandleDoc> read;
            try
            {
                read = await handlesContainer.ReadItemAsync<HandleDoc>(
                    handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            var doc = read.Resource;
            if (doc.DeviceQueueFences is null
                || !doc.DeviceQueueFences.TryGetValue(deviceId, out var pending)
                || !string.Equals(pending, generation, StringComparison.Ordinal))
                return;
            doc.DeviceQueueFences.Remove(deviceId);
            try
            {
                await handlesContainer.ReplaceItemAsync(
                    doc,
                    doc.Id,
                    new PartitionKey(handle),
                    new ItemRequestOptions { IfMatchEtag = read.ETag },
                    ct).ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (
                ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
            }
        }
        throw new InvalidOperationException("Device queue fence finalization did not converge.");
    }

    private async Task<(string SourceGeneration, string TargetGeneration, string AdmissionGeneration)?>
        ReadQueueAdmissionGenerationAsync(
        string handle,
        string sourceDeviceId,
        string targetDeviceId,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            ItemResponse<HandleDoc> read;
            try
            {
                read = await handlesContainer.ReadItemAsync<HandleDoc>(
                    handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            var doc = read.Resource;
            if (doc.Deleting
                || doc.QueueAdmissionBlocked
                || !HandleContainsDevice(doc, sourceDeviceId)
                || !HandleContainsDevice(doc, targetDeviceId))
                return null;
            if (doc.DeviceQueueGenerations?.TryGetValue(sourceDeviceId, out var sourceGeneration) == true
                && doc.DeviceQueueGenerations.TryGetValue(targetDeviceId, out var generation)
                && !string.IsNullOrEmpty(doc.QueueAdmissionGeneration))
            {
                await ActivateDeviceQueueAsync(
                    handle,
                    targetDeviceId,
                    generation,
                    doc.QueueAdmissionGeneration,
                    ct).ConfigureAwait(false);
                return (sourceGeneration!, generation!, doc.QueueAdmissionGeneration);
            }

            EnsureDeviceQueueGenerations(doc);
            try
            {
                var replaced = await handlesContainer.ReplaceItemAsync(
                    doc,
                    doc.Id,
                    new PartitionKey(handle),
                    new ItemRequestOptions { IfMatchEtag = read.ETag },
                    ct).ConfigureAwait(false);
                await ActivateCurrentDeviceQueuesAsync(replaced.Resource, ct).ConfigureAwait(false);
                return (
                    replaced.Resource.DeviceQueueGenerations![sourceDeviceId],
                    replaced.Resource.DeviceQueueGenerations![targetDeviceId],
                    replaced.Resource.QueueAdmissionGeneration!);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
            }
        }
        throw new InvalidOperationException("Device queue generation migration did not converge.");
    }

    private async Task<bool> RegistrationHasQueueGenerationAsync(
        string handle,
        string deviceId,
        string generation,
        string admissionGeneration,
        CancellationToken ct)
    {
        var current = await ReadCurrentQueueGenerationAsync(handle, deviceId, ct).ConfigureAwait(false);
        return current is not null
            && string.Equals(current.Value.TargetGeneration, generation, StringComparison.Ordinal)
            && string.Equals(
                current.Value.AdmissionGeneration, admissionGeneration, StringComparison.Ordinal);
    }

    private async Task<(string TargetGeneration, string AdmissionGeneration)?>
        ReadCurrentQueueGenerationAsync(
        string handle,
        string deviceId,
        CancellationToken ct)
    {
        try
        {
            var read = await handlesContainer.ReadItemAsync<HandleDoc>(
                handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            var doc = read.Resource;
            if (doc.Deleting
                || doc.QueueAdmissionBlocked
                || !HandleContainsDevice(doc, deviceId)
                || doc.DeviceQueueGenerations?.TryGetValue(deviceId, out var generation) != true
                || string.IsNullOrEmpty(doc.QueueAdmissionGeneration))
                return null;
            return (generation!, doc.QueueAdmissionGeneration);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<bool> IsQueueAdmissionBlockedAsync(string handle, CancellationToken ct)
    {
        try
        {
            var read = await handlesContainer.ReadItemAsync<HandleDoc>(
                handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            return read.Resource.Deleting || read.Resource.QueueAdmissionBlocked;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
    }

    private async Task<DeviceQueueEntryValidation> ValidateDeviceQueueEntryAsync(
        string handle,
        string sourceDeviceId,
        string targetDeviceId,
        string sourceGeneration,
        string targetGeneration,
        CancellationToken ct)
    {
        try
        {
            var read = await handlesContainer.ReadItemAsync<HandleDoc>(
                handle, new PartitionKey(handle), cancellationToken: ct).ConfigureAwait(false);
            var doc = read.Resource;
            if (doc.QueueAdmissionBlocked)
                return DeviceQueueEntryValidation.Retry;
            if (doc.Deleting
                || !HandleContainsDevice(doc, sourceDeviceId)
                || !HandleContainsDevice(doc, targetDeviceId)
                || doc.DeviceQueueGenerations?.TryGetValue(
                    sourceDeviceId, out var currentSourceGeneration) != true
                || doc.DeviceQueueGenerations.TryGetValue(
                    targetDeviceId, out var currentTargetGeneration) != true
                || !string.Equals(
                    currentSourceGeneration, sourceGeneration, StringComparison.Ordinal)
                || !string.Equals(
                    currentTargetGeneration, targetGeneration, StringComparison.Ordinal))
            {
                return DeviceQueueEntryValidation.Stale;
            }
            return DeviceQueueEntryValidation.Valid;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return DeviceQueueEntryValidation.Stale;
        }
    }

    private async Task RemoveStaleDeviceQueueEntryAsync(
        string queueKey,
        string entryId,
        string entryEtag,
        CancellationToken ct)
    {
        var batch = inboxContainer.CreateTransactionalBatch(new PartitionKey(queueKey))
            .DeleteItem(
                entryId,
                new TransactionalBatchItemRequestOptions { IfMatchEtag = entryEtag })
            .PatchItem(
                DeviceQueueControlId,
                [PatchOperation.Increment("/count", -1)],
                new TransactionalBatchPatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.count > 0"
                });
        using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode
            || BatchContainsStatus(
                response,
                HttpStatusCode.NotFound,
                HttpStatusCode.PreconditionFailed,
                HttpStatusCode.Conflict))
            return;
        ThrowBatchFailure(response, "stale device queue removal");
    }

    private static bool HandleContainsDevice(HandleDoc doc, string deviceId)
        => doc.DevicePublicKeys.Any(publicKey =>
            string.Equals(DeviceProtocol.DeviceId(publicKey), deviceId, StringComparison.Ordinal));

    private async Task ActivateDeviceQueueAsync(
        string handle,
        string deviceId,
        string generation,
        string admissionGeneration,
        CancellationToken ct)
    {
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var registration = await ReadCurrentQueueGenerationAsync(
                handle, deviceId, ct).ConfigureAwait(false);
            if (registration is null)
                return;
            generation = registration.Value.TargetGeneration;
            admissionGeneration = registration.Value.AdmissionGeneration;

            var current = await ReadDeviceQueueControlWithEtagAsync(queueKey, ct).ConfigureAwait(false);
            if (current is not null
                && current.Value.Doc.Active
                && string.Equals(current.Value.Doc.Generation, generation, StringComparison.Ordinal)
                && string.Equals(
                    current.Value.Doc.AdmissionGeneration,
                    admissionGeneration,
                    StringComparison.Ordinal))
                return;

            if (!await RegistrationHasQueueGenerationAsync(
                    handle,
                    deviceId,
                    generation,
                    admissionGeneration,
                    ct).ConfigureAwait(false))
            {
                continue;
            }

            if (current is not null && string.IsNullOrEmpty(current.Value.Doc.Generation))
            {
                var migrated = current.Value.Doc;
                migrated.Generation = generation;
                migrated.AdmissionGeneration = admissionGeneration;
                migrated.Active = true;
                try
                {
                    await inboxContainer.ReplaceItemAsync(
                        migrated,
                        DeviceQueueControlId,
                        new PartitionKey(queueKey),
                        new ItemRequestOptions { IfMatchEtag = current.Value.ETag },
                        ct).ConfigureAwait(false);
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                    continue;
                }
            }
            else if (current is not null
                     && string.Equals(
                         current.Value.Doc.Generation, generation, StringComparison.Ordinal))
            {
                var refreshed = current.Value.Doc;
                refreshed.AdmissionGeneration = admissionGeneration;
                refreshed.Active = true;
                try
                {
                    await inboxContainer.ReplaceItemAsync(
                        refreshed,
                        DeviceQueueControlId,
                        new PartitionKey(queueKey),
                        new ItemRequestOptions { IfMatchEtag = current.Value.ETag },
                        ct).ConfigureAwait(false);
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                    continue;
                }
            }
            else
            {
                if (current is not null)
                    await FenceAndPurgeDeviceQueueAsync(
                        queueKey, current.Value.Doc.Generation, ct).ConfigureAwait(false);

                if (!await RegistrationHasQueueGenerationAsync(
                        handle,
                        deviceId,
                        generation,
                        admissionGeneration,
                        ct).ConfigureAwait(false))
                {
                    continue;
                }

                var active = new DeviceQueueControlDoc
                {
                    To = queueKey,
                    Handle = NormalizeHandle(handle),
                    Generation = generation,
                    AdmissionGeneration = admissionGeneration,
                    Active = true,
                    Count = 0
                };
                current = await ReadDeviceQueueControlWithEtagAsync(queueKey, ct).ConfigureAwait(false);
                try
                {
                    if (current is null)
                        await inboxContainer.CreateItemAsync(
                            active, new PartitionKey(queueKey), cancellationToken: ct).ConfigureAwait(false);
                    else if (!current.Value.Doc.Active)
                        await inboxContainer.ReplaceItemAsync(
                            active,
                            DeviceQueueControlId,
                            new PartitionKey(queueKey),
                            new ItemRequestOptions { IfMatchEtag = current.Value.ETag },
                            ct).ConfigureAwait(false);
                    else
                        continue;
                }
                catch (CosmosException ex) when (
                    ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound
                        or HttpStatusCode.PreconditionFailed)
                {
                    continue;
                }
            }

            if (await RegistrationHasQueueGenerationAsync(
                    handle, deviceId, generation, admissionGeneration, ct).ConfigureAwait(false))
                return;
            await FenceDeviceQueueAdmissionAsync(
                queueKey, generation, admissionGeneration, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Device queue activation did not converge.");
    }

    private async Task FenceDeviceQueueAdmissionAsync(
        string queueKey,
        string targetGeneration,
        string admissionGeneration,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var current = await ReadDeviceQueueControlWithEtagAsync(queueKey, ct).ConfigureAwait(false);
            if (current is null)
                return;
            if (!string.Equals(
                    current.Value.Doc.Generation, targetGeneration, StringComparison.Ordinal))
                return;
            if (!current.Value.Doc.Active)
                return;
            if (!string.Equals(
                    current.Value.Doc.AdmissionGeneration,
                    admissionGeneration,
                    StringComparison.Ordinal))
                return;
            current.Value.Doc.Active = false;
            try
            {
                await inboxContainer.ReplaceItemAsync(
                    current.Value.Doc,
                    DeviceQueueControlId,
                    new PartitionKey(queueKey),
                    new ItemRequestOptions { IfMatchEtag = current.Value.ETag },
                    ct).ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (
                ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
            }
        }
        throw new InvalidOperationException("Device queue admission fence did not converge.");
    }

    private async Task<int> FenceAndPurgeDeviceQueueAsync(
        string queueKey,
        string expectedGeneration,
        CancellationToken ct)
    {
        var fenceAcquired = false;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var current = await ReadDeviceQueueControlWithEtagAsync(queueKey, ct).ConfigureAwait(false);
            if (current is null)
            {
                try
                {
                    await inboxContainer.CreateItemAsync(
                        new DeviceQueueControlDoc
                        {
                            To = queueKey,
                            Handle = queueKey[..queueKey.IndexOf('\u001f')],
                            Generation = expectedGeneration,
                            Active = false,
                            Count = 0
                        },
                        new PartitionKey(queueKey),
                        cancellationToken: ct).ConfigureAwait(false);
                    fenceAcquired = true;
                    break;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    continue;
                }
            }
            if (!string.IsNullOrEmpty(current.Value.Doc.Generation)
                && !string.Equals(
                    current.Value.Doc.Generation, expectedGeneration, StringComparison.Ordinal))
                return 0;
            if (!current.Value.Doc.Active)
            {
                fenceAcquired = true;
                break;
            }

            var fenced = current.Value.Doc;
            fenced.Generation = expectedGeneration;
            fenced.Active = false;
            try
            {
                await inboxContainer.ReplaceItemAsync(
                    fenced,
                    DeviceQueueControlId,
                    new PartitionKey(queueKey),
                    new ItemRequestOptions { IfMatchEtag = current.Value.ETag },
                    ct).ConfigureAwait(false);
                fenceAcquired = true;
                break;
            }
            catch (CosmosException ex) when (
                ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
            }
        }
        if (!fenceAcquired)
            throw new InvalidOperationException("Device queue fence did not converge.");

        var purged = 0;
        var partition = new PartitionKey(queueKey);
        for (var attempt = 0; attempt < 64;)
        {
            IReadOnlyList<string> ids;
            using (var iterator = inboxContainer.GetItemQueryIterator<DeviceQueueIdProjection>(
                       new QueryDefinition("SELECT c.id FROM c WHERE c.type = 'device-queue'"),
                       requestOptions: new QueryRequestOptions
                       {
                           PartitionKey = partition,
                           MaxItemCount = DeviceQueuePurgePageSize
                       }))
            {
                if (!iterator.HasMoreResults) break;
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                ids = page.Resource.Take(DeviceQueuePurgePageSize).Select(item => item.Id).ToArray();
            }
            if (ids.Count == 0) break;
            var escapedGeneration = expectedGeneration.Replace("'", "''", StringComparison.Ordinal);
            var batch = inboxContainer.CreateTransactionalBatch(partition)
                .PatchItem(
                    DeviceQueueControlId,
                    [PatchOperation.Set("/active", false)],
                    new TransactionalBatchPatchItemRequestOptions
                    {
                        FilterPredicate =
                            $"FROM c WHERE c.active = false AND c.generation = '{escapedGeneration}'"
                    });
            foreach (var id in ids)
                batch.DeleteItem(id);
            using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                purged += ids.Count;
                attempt++;
                continue;
            }
            if (BatchContainsStatus(
                    response, HttpStatusCode.NotFound, HttpStatusCode.PreconditionFailed))
            {
                var control = await ReadDeviceQueueControlAsync(queueKey, ct).ConfigureAwait(false);
                if (control is null
                    || control.Active
                    || !string.Equals(
                        control.Generation, expectedGeneration, StringComparison.Ordinal))
                    return purged;
                continue;
            }
            else
                ThrowBatchFailure(response, "device queue fence purge");
        }

        if (await DeviceQueueHasEntriesAsync(queueKey, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Device queue fence purge did not converge.");

        var final = await ReadDeviceQueueControlWithEtagAsync(queueKey, ct).ConfigureAwait(false);
        if (final is not null
            && !final.Value.Doc.Active
            && string.Equals(final.Value.Doc.Generation, expectedGeneration, StringComparison.Ordinal))
        {
            final.Value.Doc.Count = 0;
            try
            {
                await inboxContainer.ReplaceItemAsync(
                    final.Value.Doc,
                    DeviceQueueControlId,
                    partition,
                    new ItemRequestOptions { IfMatchEtag = final.Value.ETag },
                    ct).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (
                ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
            }
        }
        return purged;
    }

    private async Task<bool> DeviceQueueHasEntriesAsync(string queueKey, CancellationToken ct)
    {
        using var iterator = inboxContainer.GetItemQueryIterator<long>(
            new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.type = 'device-queue'"),
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(queueKey),
                MaxItemCount = 1
            });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            if (page.Resource.Sum() != 0) return true;
        }
        return false;
    }

    private async Task<DeviceQueueControlDoc?> ReadDeviceQueueControlAsync(
        string queueKey,
        CancellationToken ct)
    {
        var result = await ReadDeviceQueueControlWithEtagAsync(queueKey, ct).ConfigureAwait(false);
        return result?.Doc;
    }

    private async Task<(DeviceQueueControlDoc Doc, string ETag)?> ReadDeviceQueueControlWithEtagAsync(
        string queueKey,
        CancellationToken ct)
    {
        try
        {
            var read = await inboxContainer.ReadItemAsync<DeviceQueueControlDoc>(
                DeviceQueueControlId,
                new PartitionKey(queueKey),
                cancellationToken: ct).ConfigureAwait(false);
            return (read.Resource, read.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<bool> DeviceQueueEntryExistsAsync(
        string queueKey,
        string entryId,
        CancellationToken ct)
        => await ReadDeviceQueueEntryWithEtagAsync(queueKey, entryId, ct).ConfigureAwait(false)
           is not null;

    private async Task<(DeviceQueueDoc Doc, string ETag)?> ReadDeviceQueueEntryWithEtagAsync(
        string queueKey,
        string entryId,
        CancellationToken ct)
    {
        try
        {
            var read = await inboxContainer.ReadItemAsync<DeviceQueueDoc>(
                entryId,
                new PartitionKey(queueKey),
                cancellationToken: ct).ConfigureAwait(false);
            return (read.Resource, read.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task PurgeExpiredDeviceQueueAsync(
        string queueKey,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var partition = new PartitionKey(queueKey);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var control = await ReadDeviceQueueControlAsync(queueKey, ct).ConfigureAwait(false);
            if (control is not { Active: true } || string.IsNullOrEmpty(control.Generation))
                return;
            var escapedGeneration = control.Generation.Replace("'", "''", StringComparison.Ordinal);
            var escapedAdmissionGeneration =
                control.AdmissionGeneration.Replace("'", "''", StringComparison.Ordinal);
            IReadOnlyList<string> expiredIds;
            using (var iterator = inboxContainer.GetItemQueryIterator<DeviceQueueIdProjection>(
                       new QueryDefinition(
                               "SELECT c.id FROM c"
                               + " WHERE c.type = 'device-queue' AND c.expiresAt <= @now")
                           .WithParameter("@now", now),
                       requestOptions: new QueryRequestOptions
                       {
                           PartitionKey = partition,
                           MaxItemCount = DeviceQueuePurgePageSize
                       }))
            {
                if (!iterator.HasMoreResults)
                    return;
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                expiredIds = page.Resource
                    .Take(DeviceQueuePurgePageSize)
                    .Select(item => item.Id)
                    .ToArray();
            }
            if (expiredIds.Count == 0)
                return;

            var batch = inboxContainer.CreateTransactionalBatch(partition)
                .PatchItem(
                    DeviceQueueControlId,
                    [PatchOperation.Increment("/count", -expiredIds.Count)],
                    new TransactionalBatchPatchItemRequestOptions
                    {
                        FilterPredicate =
                            $"FROM c WHERE c.active = true AND c.generation = '{escapedGeneration}'"
                            + $" AND c.admissionGeneration = '{escapedAdmissionGeneration}'"
                            + $" AND c.count >= {expiredIds.Count}"
                    });
            foreach (var entryId in expiredIds)
                batch.DeleteItem(entryId);

            using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                continue;
            if (BatchContainsStatus(
                    response,
                    HttpStatusCode.Conflict,
                    HttpStatusCode.NotFound,
                    HttpStatusCode.PreconditionFailed))
                continue;
            ThrowBatchFailure(response, "expired device queue purge");
        }
        throw new InvalidOperationException("Expired device queue purge did not converge.");
    }

    private static void ThrowBatchFailure(
        TransactionalBatchResponse response,
        string operation)
        => throw new InvalidOperationException(
            $"Cosmos {operation} batch failed with HTTP {(int)response.StatusCode}:"
            + $" {response.ErrorMessage}");

    private static bool BatchContainsStatus(
        TransactionalBatchResponse response,
        params HttpStatusCode[] statuses)
    {
        var expected = statuses.ToHashSet();
        for (var index = 0; index < response.Count; index++)
            if (expected.Contains(response[index].StatusCode))
                return true;
        return false;
    }
    private static bool RefreshInboxTtl(InboxDoc doc, DateTimeOffset now)
    {
        if (RelayInboxPolicy.NeverExpires(doc.To))
        {
            doc.ExpiresAt = null;
            doc.Ttl = -1;
            return true;
        }

        var expiresAt = doc.ExpiresAt ?? doc.QueuedAt + RelayInboxPolicy.Retention;
        doc.ExpiresAt = expiresAt;
        var remaining = expiresAt - now;
        if (remaining <= TimeSpan.Zero) return false;
        doc.Ttl = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return true;
    }

    private static StoredEnvelope ToStoredEnvelope(InboxDoc doc) => new()
    {
        Id = doc.Id,
        EnvelopeId = doc.EnvelopeId,
        From = doc.From,
        To = doc.To,
        Json = doc.Json,
        QueuedAt = doc.QueuedAt,
        ExpiresAt = doc.ExpiresAt,
        LeaseOwner = doc.LeaseOwner,
        LeaseUntil = doc.LeaseUntil,
        DeliveryAttempts = doc.DeliveryAttempts,
        Priority = doc.Priority ?? RelayInboxPriority.Normal,
        RequiresForeground = doc.RequiresForeground ?? true
    };

    private static QueueEntry ToQueueEntry(DeviceQueueDoc doc)
        => new(
            doc.Id,
            doc.SourceDeviceId,
            doc.TargetDeviceId,
            doc.Payload,
            doc.EnqueuedAt,
            doc.ExpiresAt);

    /// <summary>Cosmos document for a queued envelope. Uses lowercase "to" as the partition key.</summary>
    private sealed class InboxQueuedAtProjection
    {
        [JsonPropertyName("queuedAt")]
        public DateTimeOffset QueuedAt { get; set; }
    }

    private sealed class DeviceQueueEnqueuedAtProjection
    {
        [JsonPropertyName("enqueuedAt")]
        public DateTimeOffset EnqueuedAt { get; set; }
    }

    private sealed class DeviceQueueIdProjection
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }

    private sealed class ItemEtagProjection
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("_etag")]
        public string ETag { get; set; } = "";
    }

    private sealed class InboxDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "inbox";

        [JsonPropertyName("envelopeId")]
        public string EnvelopeId { get; set; } = "";

        [JsonPropertyName("from")]
        public string From { get; set; } = "";

        [JsonPropertyName("to")]
        public string To { get; set; } = "";

        [JsonPropertyName("json")]
        public string Json { get; set; } = "";

        [JsonPropertyName("queuedAt")]
        public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("expiresAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonPropertyName("leaseOwner")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LeaseOwner { get; set; }

        [JsonPropertyName("leaseUntil")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? LeaseUntil { get; set; }

        [JsonPropertyName("deliveryAttempts")]
        public int DeliveryAttempts { get; set; }

        [JsonPropertyName("priority")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Priority { get; set; }

        [JsonPropertyName("requiresForeground")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? RequiresForeground { get; set; }

        [JsonPropertyName("admissionGeneration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AdmissionGeneration { get; set; }

        // The remaining seconds are rewritten after lease mutations so Cosmos TTL still expires
        // the item 14 days after QueuedAt rather than 14 days after the latest delivery attempt.
        [JsonPropertyName("ttl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Ttl { get; set; }
    }

    private sealed class InboxAdmissionControlDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = InboxAdmissionControlId;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "inbox-admission-control";

        [JsonPropertyName("to")]
        public string To { get; set; } = "";

        [JsonPropertyName("generation")]
        public string Generation { get; set; } = "";

        [JsonPropertyName("active")]
        public bool Active { get; set; }

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; } = -1;
    }

    private sealed class DeviceQueueDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "device-queue";

        [JsonPropertyName("to")]
        public string To { get; set; } = "";

        [JsonPropertyName("handle")]
        public string Handle { get; set; } = "";

        [JsonPropertyName("sourceDeviceId")]
        public string SourceDeviceId { get; set; } = "";

        [JsonPropertyName("sourceGeneration")]
        public string SourceGeneration { get; set; } = "";

        [JsonPropertyName("targetDeviceId")]
        public string TargetDeviceId { get; set; } = "";

        [JsonPropertyName("operationId")]
        public string OperationId { get; set; } = "";

        [JsonPropertyName("payload")]
        public string Payload { get; set; } = "";

        [JsonPropertyName("enqueuedAt")]
        public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("leaseOwner")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LeaseOwner { get; set; }

        [JsonPropertyName("leaseUntil")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? LeaseUntil { get; set; }

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; }
    }

    private sealed class DeviceQueueControlDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = DeviceQueueControlId;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "device-queue-control";

        [JsonPropertyName("to")]
        public string To { get; set; } = "";

        [JsonPropertyName("handle")]
        public string Handle { get; set; } = "";

        [JsonPropertyName("generation")]
        public string Generation { get; set; } = "";

        [JsonPropertyName("admissionGeneration")]
        public string AdmissionGeneration { get; set; } = "";

        [JsonPropertyName("active")]
        public bool Active { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; } = -1;
    }

    private static StoredAgentDispatch ToStored(AgentDispatchDoc doc) => new()
    {
        Id = doc.Id,
        RequestId = doc.RequestId,
        From = doc.From,
        To = doc.To,
        EnvelopeJson = doc.EnvelopeJson,
        EnvelopeHash = doc.EnvelopeHash,
        RecipientDeviceIds = doc.RecipientDeviceIds?.ToList() ?? new List<string>(),
        DispatchToken = doc.DispatchToken,
        State = doc.State,
        AssignedDeviceId = doc.AssignedDeviceId,
        DeliveryLeaseOwner = doc.DeliveryLeaseOwner,
        DeliveryLeaseUntil = doc.DeliveryLeaseUntil,
        QueuedAt = doc.QueuedAt,
        AssignedAt = doc.AssignedAt,
        DeliveredAt = doc.DeliveredAt,
        ResponseId = doc.ResponseId,
        ResponseJson = doc.ResponseJson,
        ResponseHash = doc.ResponseHash,
        ResponseStagedAt = doc.ResponseStagedAt,
        CompletedAt = doc.CompletedAt
    };

    private static AgentDispatchDoc ToDoc(StoredAgentDispatch dispatch) => new()
    {
        Id = dispatch.Id,
        RequestId = dispatch.RequestId,
        From = dispatch.From,
        To = dispatch.To,
        EnvelopeJson = dispatch.EnvelopeJson,
        EnvelopeHash = dispatch.EnvelopeHash,
        RecipientDeviceIds = dispatch.RecipientDeviceIds.ToList(),
        DispatchToken = dispatch.DispatchToken,
        State = dispatch.State,
        AssignedDeviceId = dispatch.AssignedDeviceId,
        DeliveryLeaseOwner = dispatch.DeliveryLeaseOwner,
        DeliveryLeaseUntil = dispatch.DeliveryLeaseUntil,
        QueuedAt = dispatch.QueuedAt,
        AssignedAt = dispatch.AssignedAt,
        DeliveredAt = dispatch.DeliveredAt,
        ResponseId = dispatch.ResponseId,
        ResponseJson = dispatch.ResponseJson,
        ResponseHash = dispatch.ResponseHash,
        ResponseStagedAt = dispatch.ResponseStagedAt,
        CompletedAt = dispatch.CompletedAt
    };

    private sealed class AgentDispatchDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("requestId")]
        public string RequestId { get; set; } = "";

        [JsonPropertyName("from")]
        public string From { get; set; } = "";

        [JsonPropertyName("to")]
        public string To { get; set; } = "";

        [JsonPropertyName("admissionGeneration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AdmissionGeneration { get; set; }

        [JsonPropertyName("envelopeJson")]
        public string EnvelopeJson { get; set; } = "";

        [JsonPropertyName("envelopeHash")]
        public string EnvelopeHash { get; set; } = "";

        [JsonPropertyName("recipientDeviceIds")]
        public List<string>? RecipientDeviceIds { get; set; }

        [JsonPropertyName("dispatchToken")]
        public string DispatchToken { get; set; } = "";

        [JsonPropertyName("state")]
        public string State { get; set; } = AgentDispatchStates.Pending;

        [JsonPropertyName("assignedDeviceId")]
        public string? AssignedDeviceId { get; set; }

        [JsonPropertyName("deliveryLeaseOwner")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DeliveryLeaseOwner { get; set; }

        [JsonPropertyName("deliveryLeaseUntil")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? DeliveryLeaseUntil { get; set; }

        [JsonPropertyName("queuedAt")]
        public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("assignedAt")]
        public DateTimeOffset? AssignedAt { get; set; }

        [JsonPropertyName("deliveredAt")]
        public DateTimeOffset? DeliveredAt { get; set; }

        [JsonPropertyName("responseId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResponseId { get; set; }

        [JsonPropertyName("responseJson")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResponseJson { get; set; }

        [JsonPropertyName("responseHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResponseHash { get; set; }

        [JsonPropertyName("responseStagedAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? ResponseStagedAt { get; set; }

        [JsonPropertyName("completedAt")]
        public DateTimeOffset? CompletedAt { get; set; }
    }

    /// <summary>
    /// Cosmos document for a published service and its reputation.Uses "serviceId" as the partition
    /// key so a vote/usage mutation is a single-partition read-modify-write. Users are stored as a list
    /// (Cosmos has no native set type) and de-duplicated on read into a <see cref="HashSet{T}"/>.
    /// </summary>
    private sealed class ServiceDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("serviceId")]
        public string ServiceId { get; set; } = "";

        [JsonPropertyName("handle")]
        public string Handle { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("publishedAt")]
        public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("votes")]
        public Dictionary<string, int> Votes { get; set; } = new();

        [JsonPropertyName("users")]
        public List<string> Users { get; set; } = new();
    }

    /// <summary>
    /// A <see cref="CosmosSerializer"/> that uses System.Text.Json instead of Newtonsoft.Json,
    /// so this store carries no compile-time dependency on Newtonsoft. Document property names
    /// are controlled with <see cref="JsonPropertyNameAttribute"/> to match the container
    /// partition key paths ("/handle", "/to") exactly.
    /// </summary>
    private sealed class SystemTextJsonCosmosSerializer : CosmosSerializer
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

        public override T FromStream<T>(Stream stream)
        {
            using (stream)
            {
                if (typeof(Stream).IsAssignableFrom(typeof(T)))
                    return (T)(object)stream;

                if (stream.CanSeek && stream.Length == 0)
                    return default!;

                return JsonSerializer.Deserialize<T>(stream, Options)!;
            }
        }

        public override Stream ToStream<T>(T input)
        {
            var stream = new MemoryStream();
            JsonSerializer.Serialize(stream, input, Options);
            stream.Position = 0;
            return stream;
        }
    }
}
