using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mesh.Relay.RateLimiting;
using Microsoft.Azure.Cosmos;

namespace Mesh.Relay.Storage;

/// <summary>
/// Azure Cosmos DB (serverless) backed implementation of <see cref="IRelayStore"/>.
/// Persists the handle registry, pending device-link invites, offline inbox, capability
/// directory, and administrative rate-policy overrides so relay state survives restarts
/// and can be shared across scaled-out instances.
///
/// Five containers are provisioned idempotently on first use:
/// <list type="bullet">
///   <item>"handles" (partition key "/handle"): one document per registered handle.</item>
///   <item>"rate-policies" (partition key "/handle"): administrative per-handle rate-policy
///   overrides, stored separately from public handle registrations.</item>
///   <item>"invites" (partition key "/handle"): single-use link invites, expired automatically
///   via native per-item TTL (container DefaultTimeToLive = -1).</item>
///   <item>"inbox" (partition key "/to"): queued envelopes for offline recipients, expired
///   after 14 days via a container DefaultTimeToLive of 1209600 seconds.</item>
///   <item>"services" (partition key "/serviceId"): published capabilities and reputation.</item>
/// </list>
/// </summary>
public sealed class CosmosRelayStore : IRelayStore
{
    private const int InboxTtlSeconds = 1209600; // 14 days

    private readonly CosmosClient client;
    private readonly string databaseName;
    private readonly SemaphoreSlim initLock = new(1, 1);

    private Container handlesContainer = null!;
    private Container invitesContainer = null!;
    private Container inboxContainer = null!;
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
        try
        {
            await handlesContainer
                .DeleteItemAsync<HandleDoc>(handle, new PartitionKey(handle), cancellationToken: ct)
                .ConfigureAwait(false);
            // Remove the override only after the identity is gone. If handle deletion fails, a
            // restrictive policy must remain in force rather than silently falling back to defaults.
            await DeleteHandleRatePolicyAsync(handle, ct).ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
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
            if (!string.IsNullOrWhiteSpace(name))
                doc.DeviceNames[deviceId] = name;
            doc.DevicePlatforms[deviceId] = platform;
            doc.DeviceRemoteAgentEnabled[deviceId] = remoteAgentEnabled;

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
    public async Task EnqueueAsync(string toHandle, string envelopeJson, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        var doc = new InboxDoc
        {
            Id = Guid.NewGuid().ToString("N"),
            To = toHandle,
            Json = envelopeJson,
            QueuedAt = DateTimeOffset.UtcNow
        };

        // Messages queued for a reserved system handle (for example "meshreport") never expire:
        // a per-item ttl of -1 overrides the container's 14-day DefaultTimeToLive. Normal recipients
        // leave Ttl null so the existing 14-day default still applies.
        if (Mesh.Shared.ReservedHandles.IsReserved(toHandle))
            doc.Ttl = -1;

        await inboxContainer
            .CreateItemAsync(doc, new PartitionKey(toHandle), cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DrainInboxAsync(string toHandle, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct).ConfigureAwait(false);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.to = @to ORDER BY c.queuedAt ASC")
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
            result.Add(doc.Json);
            try
            {
                await inboxContainer
                    .DeleteItemAsync<InboxDoc>(doc.Id, new PartitionKey(toHandle), cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Already removed (drained concurrently or expired); nothing to do.
            }
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
        DevicePublicKeys = doc.DevicePublicKeys is null ? new List<string>() : new List<string>(doc.DevicePublicKeys),
        RecoveryPublicKey = doc.RecoveryPublicKey,
        DeviceNames = doc.DeviceNames is null ? new Dictionary<string, string>() : new Dictionary<string, string>(doc.DeviceNames),
        DevicePlatforms = doc.DevicePlatforms is null ? new Dictionary<string, string>() : new Dictionary<string, string>(doc.DevicePlatforms),
        DeviceRemoteAgentEnabled = doc.DeviceRemoteAgentEnabled is null
            ? new Dictionary<string, bool>()
            : new Dictionary<string, bool>(doc.DeviceRemoteAgentEnabled)
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

        [JsonPropertyName("recoveryPublicKey")]
        public string? RecoveryPublicKey { get; set; }

        [JsonPropertyName("deviceNames")]
        public Dictionary<string, string>? DeviceNames { get; set; }

        [JsonPropertyName("devicePlatforms")]
        public Dictionary<string, string>? DevicePlatforms { get; set; }

        [JsonPropertyName("deviceRemoteAgentEnabled")]
        public Dictionary<string, bool>? DeviceRemoteAgentEnabled { get; set; }
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

    /// <summary>Cosmos document for a queued envelope. Uses lowercase "to" as the partition key.</summary>
    private sealed class InboxDoc
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("to")]
        public string To { get; set; } = "";

        [JsonPropertyName("json")]
        public string Json { get; set; } = "";

        [JsonPropertyName("queuedAt")]
        public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;

        // Per-item time-to-live in seconds. When null it is omitted from the document so the
        // container DefaultTimeToLive (14 days) applies. A value of -1 makes the item never
        // expire, overriding the container default for reserved-handle queued messages.
        [JsonPropertyName("ttl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Ttl { get; set; }
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
