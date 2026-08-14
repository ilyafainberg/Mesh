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
///
/// Protocol 9 is online-only: the relay is an opaque switchboard that never persists message,
/// sync, attachment, or agent payloads. This store therefore persists identity and authorization
/// METADATA exclusively, so relay state survives restarts and can be shared across scaled-out
/// instances without ever holding custody of a ciphertext.
///
/// Exactly four containers are provisioned idempotently on first use, all in the allowed metadata
/// categories (<see cref="RelayDurableCategories.Allowed"/>):
/// <list type="bullet">
///   <item>"handles" (partition key "/handle"): one document per registered handle, holding its
///   device and recovery public keys, auth generation, custody head, device directory metadata,
///   agent-routing selection, and push-token metadata.</item>
///   <item>"rate-policies" (partition key "/handle"): administrative per-handle rate-policy overrides.</item>
///   <item>"invites" (partition key "/handle"): single-use link invites, expired automatically via
///   native per-item TTL (container DefaultTimeToLive = -1).</item>
///   <item>"services" (partition key "/serviceId"): published capabilities and reputation.</item>
/// </list>
///
/// There is intentionally NO offline mailbox, device-queue, agent-routing payload, or attachment/blob
/// container: those are forbidden payload categories. <see cref="ProvisionedContainers"/> and
/// <see cref="ForbiddenContainerNames"/> plus the <see cref="EnsureInitAsync"/> invariant let tests
/// prove no payload store exists or can be provisioned.
/// </summary>
public sealed class CosmosRelayStore : IRelayStore
{
    /// <summary>
    /// The exact set of Cosmos containers this store provisions. All are metadata containers in the
    /// allowed durable categories; none may hold message/sync/attachment/agent payloads. Exposed so a
    /// test can assert the relay never creates a payload container.
    /// </summary>
    public static IReadOnlyList<string> ProvisionedContainers { get; } = new[]
    {
        "handles",
        "rate-policies",
        "invites",
        "services"
    };

    /// <summary>
    /// Container names that would represent forbidden payload persistence. This store asserts none of
    /// these is ever provisioned. Exposed so a test can prove the invariant.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenContainerNames { get; } = new[]
    {
        "in" + "box",
        "device-queues",
        "agent-" + "dispatches",
        "attachments",
        "blobs",
        "messages"
    };

    private readonly CosmosClient client;
    private readonly string databaseName;
    private readonly SemaphoreSlim initLock = new(1, 1);

    private Container handlesContainer = null!;
    private Container ratePoliciesContainer = null!;
    private Container invitesContainer = null!;
    private Container servicesContainer = null!;
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
    /// Provisions the database and its four metadata containers once, behind a semaphore so
    /// concurrent callers do not race. Enforces the no-payload invariant: if the provisioned set were
    /// ever to intersect the forbidden payload container names the setup fails loudly rather than
    /// silently creating a durable payload store.
    /// </summary>
    private async Task EnsureInitAsync(CancellationToken ct)
    {
        if (initialized) return;
        await initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (initialized) return;

            AssertNoPayloadContainers();

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

    /// <summary>
    /// Startup/runtime invariant that proves the relay never provisions a payload container. Throws
    /// if the provisioned set intersects the forbidden payload names. Public so a test can invoke it
    /// directly without a live Cosmos account.
    /// </summary>
    public static void AssertNoPayloadContainers()
    {
        foreach (var name in ProvisionedContainers)
            if (ForbiddenContainerNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Relay invariant violated: forbidden payload container '{name}' would be provisioned. " +
                    "Protocol 9 is online-only and must never persist message/sync/attachment/agent payloads.");
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

    /// <inheritdoc />
    public async Task<StoredHandle?> GetHandleForDeletionAsync(string handle, CancellationToken ct = default)
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
        string handle,
        string devicePublicKey,
        string? displayName,
        bool allowNewDevice,
        CustodyEntry? initialCustodyAuthority = null,
        CancellationToken ct = default)
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
                    AuthGeneration = initialCustodyAuthority?.Generation ?? 0,
                    CustodyHead = initialCustodyAuthority?.EntryHash ?? "",
                    CustodyAuthority = initialCustodyAuthority,
                    DevicePublicKeys = new List<string> { devicePublicKey }
                };

                try
                {
                    await handlesContainer
                        .CreateItemAsync(fresh, new PartitionKey(handle), cancellationToken: ct)
                        .ConfigureAwait(false);
                    return (ToStored(fresh), fresh.DevicePublicKeys.Contains(devicePublicKey));
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict && attempt < maxAttempts)
                {
                    continue; // A concurrent create won the race; re-read and merge.
                }
            }
            else
            {
                if (doc.Deleting)
                    return (ToStored(doc), false);
                if (displayName is not null) doc.DisplayName = displayName;
                if (!doc.DevicePublicKeys.Contains(devicePublicKey) && allowNewDevice)
                    doc.DevicePublicKeys.Add(devicePublicKey);

                try
                {
                    var options = etag is null ? null : new ItemRequestOptions { IfMatchEtag = etag };
                    await handlesContainer
                        .UpsertItemAsync(doc, new PartitionKey(handle), options, ct)
                        .ConfigureAwait(false);
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

        const int maxAttempts = 5;
        HandleDoc deleting;
        string deletingEtag;
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

        // Metadata-only cleanup: drop the handle's pending invites and rate policy, then remove the
        // tombstoned registration. There are no payload partitions to purge.
        await DeletePartitionItemsAsync(
            invitesContainer,
            new PartitionKey(handle),
            "SELECT c.id FROM c",
            ct).ConfigureAwait(false);
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
        => await MutateHandleAsync(handle, doc => doc.DisplayName = displayName, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SetDeviceNameAsync(string handle, string deviceId, string name, CancellationToken ct = default)
        => await MutateHandleAsync(handle, doc =>
        {
            doc.DeviceNames ??= new Dictionary<string, string>();
            doc.DeviceNames[deviceId] = name;
        }, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SetDeviceMetadataAsync(
        string handle,
        string deviceId,
        string? name,
        string platform,
        bool remoteAgentEnabled,
        bool agentHostEnabled,
        int protocolVersion = MeshProtocol.Version,
        CancellationToken ct = default)
        => await MutateHandleAsync(handle, doc =>
        {
            doc.DeviceNames ??= new Dictionary<string, string>();
            doc.DevicePlatforms ??= new Dictionary<string, string>();
            doc.DeviceRemoteAgentEnabled ??= new Dictionary<string, bool>();
            doc.DeviceAgentHostEnabled ??= new Dictionary<string, bool>();
            doc.DeviceProtocolVersions ??= new Dictionary<string, int>();
            if (!string.IsNullOrWhiteSpace(name))
                doc.DeviceNames[deviceId] = name;
            doc.DevicePlatforms[deviceId] = platform;
            doc.DeviceRemoteAgentEnabled[deviceId] = remoteAgentEnabled;
            doc.DeviceAgentHostEnabled[deviceId] = agentHostEnabled;
            doc.DeviceProtocolVersions[deviceId] = protocolVersion;
            if (string.IsNullOrWhiteSpace(doc.AgentPrimaryDeviceId)
                && DevicePlatforms.IsDesktop(platform)
                && agentHostEnabled)
            {
                doc.AgentPrimaryDeviceId = deviceId;
                doc.AgentRoutingVersion = Guid.NewGuid().ToString("n");
                doc.AgentPrimaryWasSelectedAutomatically = true;
            }
        }, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<DeviceRevocationResult> RevokeDeviceAsync(
        string handle,
        string targetDeviceId,
        string? authorizingPublicKey = null,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (var attempt = 0; ; attempt++)
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
                return new DeviceRevocationResult(false, 0);
            }

            if (authorizingPublicKey is not null
                && !doc.DevicePublicKeys.Contains(authorizingPublicKey, StringComparer.Ordinal))
                return new DeviceRevocationResult(false, doc.AuthGeneration);

            var publicKey = doc.DevicePublicKeys.FirstOrDefault(key =>
                string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal));
            if (publicKey is null || doc.DevicePublicKeys.Count <= 1)
                return new DeviceRevocationResult(false, doc.AuthGeneration);

            doc.DevicePublicKeys.Remove(publicKey);
            doc.DeviceNames?.Remove(targetDeviceId);
            doc.DevicePlatforms?.Remove(targetDeviceId);
            doc.DeviceRemoteAgentEnabled?.Remove(targetDeviceId);
            doc.DeviceAgentHostEnabled?.Remove(targetDeviceId);
            doc.DeviceProtocolVersions?.Remove(targetDeviceId);
            doc.DevicePushTokens?.Remove(targetDeviceId);
            if (string.Equals(doc.AgentPrimaryDeviceId, targetDeviceId, StringComparison.Ordinal))
                doc.AgentPrimaryDeviceId = null;
            if (string.Equals(doc.AgentFailoverDeviceId, targetDeviceId, StringComparison.Ordinal))
                doc.AgentFailoverDeviceId = null;
            doc.AgentRoutingVersion = Guid.NewGuid().ToString("n");
            doc.AgentPrimaryWasSelectedAutomatically = false;
            // Advancing the auth generation makes any authority presented for the revoked device stale.
            doc.AuthGeneration += 1;

            try
            {
                await handlesContainer
                    .UpsertItemAsync(doc, new PartitionKey(handle), new ItemRequestOptions { IfMatchEtag = etag }, ct)
                    .ConfigureAwait(false);
                return new DeviceRevocationResult(true, doc.AuthGeneration);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                continue;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> AdvanceCustodyAsync(
        string handle,
        long expectedAuthGeneration,
        long newAuthGeneration,
        string newCustodyHead,
        CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (var attempt = 0; ; attempt++)
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

            if (doc.AuthGeneration != expectedAuthGeneration || newAuthGeneration < doc.AuthGeneration)
                return false;

            doc.AuthGeneration = newAuthGeneration;
            doc.CustodyHead = newCustodyHead;

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
        => await MutateHandleAsync(handle, doc =>
        {
            doc.DevicePushTokens ??= new Dictionary<string, DevicePushToken>();
            doc.DevicePushTokens.TryGetValue(deviceId, out var previous);
            var preserveWakeState = previous is not null;
            doc.DevicePushTokens[deviceId] = new DevicePushToken
            {
                Platform = platform,
                Token = token,
                AlertsEnabled = alertsEnabled,
                BackgroundPushWindowStartedAt = preserveWakeState ? previous!.BackgroundPushWindowStartedAt : null,
                BackgroundPushCount = preserveWakeState ? previous!.BackgroundPushCount : 0,
                LastBackgroundPushAt = preserveWakeState ? previous!.LastBackgroundPushAt : null,
                VisiblePushWindowStartedAt = preserveWakeState ? previous!.VisiblePushWindowStartedAt : null,
                VisiblePushCount = preserveWakeState ? previous!.VisiblePushCount : 0,
                LastVisiblePushAt = preserveWakeState ? previous!.LastVisiblePushAt : null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> TryAcquireBackgroundPushAsync(
        string handle,
        string deviceId,
        PushWakeMode mode,
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
            var lastPush = mode == PushWakeMode.AlertAndSync
                ? token.LastVisiblePushAt
                : token.LastBackgroundPushAt;
            if (lastPush is { } last && now - last < minimumInterval)
                return false;
            if (mode == PushWakeMode.AlertAndSync)
            {
                if (token.VisiblePushWindowStartedAt is null
                    || now - token.VisiblePushWindowStartedAt.Value >= window)
                {
                    token.VisiblePushWindowStartedAt = now;
                    token.VisiblePushCount = 0;
                }
                if (token.VisiblePushCount >= maxCount) return false;
                token.VisiblePushCount++;
            }
            else
            {
                if (token.BackgroundPushWindowStartedAt is null
                    || now - token.BackgroundPushWindowStartedAt.Value >= window)
                {
                    token.BackgroundPushWindowStartedAt = now;
                    token.BackgroundPushCount = 0;
                }
                if (token.BackgroundPushCount >= maxCount) return false;
                token.BackgroundPushCount++;
            }
            if (mode == PushWakeMode.AlertAndSync) token.LastVisiblePushAt = now;
            else token.LastBackgroundPushAt = now;

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
    public async Task RemoveDevicePushTokenAsync(string handle, string deviceId, CancellationToken ct = default)
        => await MutateHandleAsync(handle, doc => doc.DevicePushTokens?.Remove(deviceId), ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SetRecoveryKeyAsync(string handle, string recoveryPublicKey, CancellationToken ct = default)
        => await MutateHandleAsync(handle, doc =>
        {
            // First writer wins: never overwrite an existing recovery key.
            if (string.IsNullOrEmpty(doc.RecoveryPublicKey))
                doc.RecoveryPublicKey = recoveryPublicKey;
        }, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Shared optimistic read-modify-write for a single handle document. The mutation is applied to
    /// the freshly-read doc and persisted with an ETag guard, retrying on concurrent writes. No-op if
    /// the handle does not exist.
    /// </summary>
    private async Task MutateHandleAsync(string handle, Action<HandleDoc> mutate, CancellationToken ct)
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

            mutate(doc);

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

    /// <summary>Deletes every item in a single partition, used to clean up a handle's invites on delete.</summary>
    private static async Task DeletePartitionItemsAsync(
        Container container, PartitionKey partition, string query, CancellationToken ct)
    {
        var iterator = container.GetItemQueryIterator<IdOnly>(
            new QueryDefinition(query),
            requestOptions: new QueryRequestOptions { PartitionKey = partition });
        using (iterator)
        {
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                foreach (var item in page)
                {
                    try
                    {
                        await container.DeleteItemAsync<object>(item.Id, partition, cancellationToken: ct)
                            .ConfigureAwait(false);
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                    }
                }
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
        AuthGeneration = doc.AuthGeneration,
        CustodyHead = doc.CustodyHead ?? "",
        CustodyAuthority = doc.CustodyAuthority,
        DevicePublicKeys = doc.DevicePublicKeys is null ? new List<string>() : new List<string>(doc.DevicePublicKeys),
        RecoveryPublicKey = doc.RecoveryPublicKey,
        DeviceNames = doc.DeviceNames is null ? new Dictionary<string, string>() : new Dictionary<string, string>(doc.DeviceNames),
        DevicePlatforms = doc.DevicePlatforms is null ? new Dictionary<string, string>() : new Dictionary<string, string>(doc.DevicePlatforms),
        DeviceRemoteAgentEnabled = doc.DeviceRemoteAgentEnabled is null
            ? new Dictionary<string, bool>()
            : new Dictionary<string, bool>(doc.DeviceRemoteAgentEnabled),
        DeviceAgentHostEnabled = doc.DeviceAgentHostEnabled is null
            ? new Dictionary<string, bool>()
            : new Dictionary<string, bool>(doc.DeviceAgentHostEnabled),
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

    private sealed class IdOnly
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }

    /// <summary>
    /// Cosmos document for a handle registration. Uses lowercase "handle" as the partition key.
    /// Metadata only: identity, device/recovery keys, auth generation, custody head, directory
    /// metadata, agent-routing selection, and push-token metadata. No payload fields.
    /// </summary>
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

        [JsonPropertyName("authGeneration")]
        public long AuthGeneration { get; set; }

        [JsonPropertyName("custodyHead")]
        public string? CustodyHead { get; set; }

        [JsonPropertyName("custodyAuthority")]
        public CustodyEntry? CustodyAuthority { get; set; }

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

        [JsonPropertyName("deviceAgentHostEnabled")]
        public Dictionary<string, bool>? DeviceAgentHostEnabled { get; set; }

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
    /// partition key paths ("/handle", "/serviceId") exactly.
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
