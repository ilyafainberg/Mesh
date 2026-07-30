using System.Collections.Concurrent;
using Mesh.Relay.RateLimiting;
using Mesh.Shared;

namespace Mesh.Relay.Storage;

/// <summary>
/// Default in-memory implementation of <see cref="IRelayStore"/>. Preserves the relay's
/// original prototype behavior and is used whenever no Cosmos connection is configured
/// (local dev, single instance). State is lost on restart, which is exactly why the
/// Cosmos-backed store exists for production.
/// </summary>
public sealed class InMemoryRelayStore : IRelayStore
{
    private readonly ConcurrentDictionary<string, StoredHandle> handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredHandle> deletingHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> invites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<StoredEnvelope>> inboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<StoredDeviceQueueItem>> deviceQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredAgentDispatch> agentDispatches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StoredService> services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HandleRatePolicy> ratePolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> handleQueueGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> registeredHandleLifetimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider timeProvider;
    private readonly Func<Task>? beforeQueueAdmission;
    private readonly Func<Task>? beforeInboxAdmission;
    private readonly Func<Task>? beforeHandleDeleteCompletion;
    private readonly bool enforceQueueRegistration;

    public InMemoryRelayStore(TimeProvider? timeProvider = null)
        : this(timeProvider, null, true, null, null)
    {
    }

    internal InMemoryRelayStore(
        TimeProvider? timeProvider,
        Func<Task>? beforeQueueAdmission,
        bool enforceQueueRegistration = true,
        Func<Task>? beforeHandleDeleteCompletion = null,
        Func<Task>? beforeInboxAdmission = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.beforeQueueAdmission = beforeQueueAdmission;
        this.enforceQueueRegistration = enforceQueueRegistration;
        this.beforeHandleDeleteCompletion = beforeHandleDeleteCompletion;
        this.beforeInboxAdmission = beforeInboxAdmission;
    }

    private DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    private object HandleQueueGate(string handle)
        => handleQueueGates.GetOrAdd(NormalizeHandle(handle), static _ => new object());

    private static bool RegistrationContainsDevice(StoredHandle registration, string deviceId)
        => registration.DevicePublicKeys.Any(publicKey =>
            string.Equals(DeviceProtocol.DeviceId(publicKey), deviceId, StringComparison.Ordinal));

    public Task<StoredHandle?> GetHandleAsync(string handle, CancellationToken ct = default)
        => Task.FromResult(handles.TryGetValue(handle, out var rec) ? Clone(rec) : null);

    public Task<StoredHandle?> GetHandleForDeletionAsync(string handle, CancellationToken ct = default)
        => Task.FromResult(
            handles.TryGetValue(handle, out var active)
                ? Clone(active)
                : deletingHandles.TryGetValue(handle, out var deleting) ? Clone(deleting) : null);

    public Task<(StoredHandle record, bool deviceAuthorized)> UpsertHandleAsync(
        string handle, string devicePublicKey, string? displayName, bool allowNewDevice, CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(handle);
        lock (HandleQueueGate(normalized))
        {
            if (deletingHandles.TryGetValue(normalized, out var deleting))
                return Task.FromResult((Clone(deleting), false));
            if (!handles.TryGetValue(normalized, out var rec))
            {
                rec = new StoredHandle
                {
                    Handle = normalized,
                    DisplayName = displayName,
                    RegisteredAt = DateTimeOffset.UtcNow,
                    InboxGeneration = Guid.NewGuid().ToString("n")
                };
                rec.DevicePublicKeys.Add(devicePublicKey);
                rec.DeviceQueueGenerations[DeviceProtocol.DeviceId(devicePublicKey)] =
                    Guid.NewGuid().ToString("n");
                handles[normalized] = rec;
            }
            else
            {
                if (displayName is not null) rec.DisplayName = displayName;
                if (!rec.DevicePublicKeys.Contains(devicePublicKey) && allowNewDevice)
                {
                    rec.DevicePublicKeys.Add(devicePublicKey);
                    var deviceId = DeviceProtocol.DeviceId(devicePublicKey);
                    rec.DeviceQueueGenerations[deviceId] = Guid.NewGuid().ToString("n");
                    deviceQueues.TryRemove(RelayDeviceQueueKey.Create(normalized, deviceId), out _);
                }
            }

            var authorized = rec.DevicePublicKeys.Contains(devicePublicKey);
            registeredHandleLifetimes[normalized] = 0;
            return Task.FromResult((Clone(rec), authorized));
        }
    }

    public async Task<bool> DeleteHandleAsync(string handle, CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(handle);
        lock (HandleQueueGate(normalized))
        {
            if (!deletingHandles.ContainsKey(normalized))
            {
                if (!handles.TryRemove(normalized, out var record))
                    return false;
                deletingHandles[normalized] = record;
            }
        }

        if (beforeHandleDeleteCompletion is not null)
            await beforeHandleDeleteCompletion().ConfigureAwait(false);

        lock (HandleQueueGate(normalized))
        {
            invites.TryRemove(normalized, out _);
            foreach (var inboxKey in inboxes.Keys.Where(key =>
                         string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith(normalized + "\u001f", StringComparison.OrdinalIgnoreCase)))
                inboxes.TryRemove(inboxKey, out _);
            foreach (var item in agentDispatches)
                if (string.Equals(item.Value.To, normalized, StringComparison.OrdinalIgnoreCase))
                    agentDispatches.TryRemove(item.Key, out _);
            foreach (var queueKey in deviceQueues.Keys.Where(key =>
                         key.StartsWith(normalized + "\u001fqueue\u001f", StringComparison.OrdinalIgnoreCase)))
                deviceQueues.TryRemove(queueKey, out _);
            ratePolicies.TryRemove(normalized, out _);
            deletingHandles.TryRemove(normalized, out _);
            return true;
        }
    }

    public Task SetDisplayNameAsync(string handle, string displayName, CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec) rec.DisplayName = displayName;
        return Task.CompletedTask;
    }

    public Task SetDeviceNameAsync(string handle, string deviceId, string name, CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec) rec.DeviceNames[deviceId] = name;
        return Task.CompletedTask;
    }

    public Task SetDeviceMetadataAsync(
        string handle,
        string deviceId,
        string? name,
        string platform,
        bool remoteAgentEnabled,
        bool atomicAgentDispatchEnabled,
        int protocolVersion = MeshProtocol.Version,
        CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    rec.DeviceNames[deviceId] = name;
                rec.DevicePlatforms[deviceId] = platform;
                rec.DeviceRemoteAgentEnabled[deviceId] = remoteAgentEnabled;
                rec.DeviceAtomicAgentDispatchEnabled[deviceId] = atomicAgentDispatchEnabled;
                rec.DeviceProtocolVersions[deviceId] = protocolVersion;
                if (string.IsNullOrWhiteSpace(rec.AgentPrimaryDeviceId)
                    && DevicePlatforms.IsDesktop(platform)
                    && atomicAgentDispatchEnabled)
                {
                    rec.AgentPrimaryDeviceId = deviceId;
                    rec.AgentRoutingVersion = Guid.NewGuid().ToString("n");
                    rec.AgentPrimaryWasSelectedAutomatically = true;
                }
            }

        return Task.CompletedTask;
    }

    public Task<DeviceRevocationResult> RevokeDeviceAsync(
        string handle,
        string targetDeviceId,
        string? authorizingPublicKey = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(handle);
        lock (HandleQueueGate(normalized))
        {
            var revoked = false;
            if (handles.TryGetValue(normalized, out var rec))
            {
                if (authorizingPublicKey is not null
                    && !rec.DevicePublicKeys.Contains(authorizingPublicKey, StringComparer.Ordinal))
                    return Task.FromResult(new DeviceRevocationResult(false, 0));
                var publicKey = rec.DevicePublicKeys.FirstOrDefault(key =>
                    string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal));
                if (publicKey is not null && rec.DevicePublicKeys.Count > 1)
                {
                    rec.DevicePublicKeys.Remove(publicKey);
                    rec.DeviceNames.Remove(targetDeviceId);
                    rec.DevicePlatforms.Remove(targetDeviceId);
                    rec.DeviceRemoteAgentEnabled.Remove(targetDeviceId);
                    rec.DeviceAtomicAgentDispatchEnabled.Remove(targetDeviceId);
                    rec.DeviceProtocolVersions.Remove(targetDeviceId);
                    rec.DevicePushTokens.Remove(targetDeviceId);
                    rec.DeviceQueueGenerations.Remove(targetDeviceId);
                    if (string.Equals(rec.AgentPrimaryDeviceId, targetDeviceId, StringComparison.Ordinal))
                        rec.AgentPrimaryDeviceId = null;
                    if (string.Equals(rec.AgentFailoverDeviceId, targetDeviceId, StringComparison.Ordinal))
                        rec.AgentFailoverDeviceId = null;
                    rec.AgentRoutingVersion = Guid.NewGuid().ToString("n");
                    rec.AgentPrimaryWasSelectedAutomatically = false;
                    revoked = true;
                }
            }

            var inboxKey = RelayInboxKey.Device(normalized, targetDeviceId);
            var purged = 0;
            if (inboxes.TryRemove(inboxKey, out var inbox))
            {
                lock (inbox) purged = inbox.Count;
            }
            if (deviceQueues.TryRemove(RelayDeviceQueueKey.Create(normalized, targetDeviceId), out var queue))
            {
                lock (queue) purged += queue.Count;
            }
            return Task.FromResult(new DeviceRevocationResult(revoked, purged));
        }
    }

    public Task<bool> SetAgentRoutingAsync(
        string handle,
        string primaryDeviceId,
        string? failoverDeviceId,
        string expectedVersion,
        CancellationToken ct = default)
    {
        if (!handles.TryGetValue(handle, out var rec)) return Task.FromResult(false);
        lock (rec)
        {
            if (!string.Equals(rec.AgentRoutingVersion, expectedVersion, StringComparison.Ordinal))
                return Task.FromResult(false);
            rec.AgentPrimaryDeviceId = primaryDeviceId;
            rec.AgentFailoverDeviceId = failoverDeviceId;
            rec.AgentRoutingVersion = Guid.NewGuid().ToString("n");
            rec.AgentPrimaryWasSelectedAutomatically = false;
            return Task.FromResult(true);
        }
    }

    public Task SetDevicePushTokenAsync(
        string handle, string deviceId, string platform, string token, bool alertsEnabled,
        CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec)
            {
                rec.DevicePushTokens.TryGetValue(deviceId, out var previous);
                var preserveWakeState = previous is not null
                    && string.Equals(previous.Platform, platform, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(previous.Token, token, StringComparison.Ordinal);
                rec.DevicePushTokens[deviceId] = new DevicePushToken
                {
                    Platform = platform,
                    Token = token,
                    AlertsEnabled = alertsEnabled,
                    BackgroundPushWindowStartedAt = preserveWakeState ? previous!.BackgroundPushWindowStartedAt : null,
                    BackgroundPushCount = preserveWakeState ? previous!.BackgroundPushCount : 0,
                    LastBackgroundPushAt = preserveWakeState ? previous!.LastBackgroundPushAt : null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireBackgroundPushAsync(
        string handle,
        string deviceId,
        DateTimeOffset now,
        TimeSpan minimumInterval,
        TimeSpan window,
        int maxCount,
        CancellationToken ct = default)
    {
        if (!handles.TryGetValue(handle, out var rec)) return Task.FromResult(false);
        lock (rec)
        {
            if (!rec.DevicePushTokens.TryGetValue(deviceId, out var token)) return Task.FromResult(false);
            if (token.LastBackgroundPushAt is { } last && now - last < minimumInterval)
                return Task.FromResult(false);
            if (token.BackgroundPushWindowStartedAt is null
                || now - token.BackgroundPushWindowStartedAt.Value >= window)
            {
                token.BackgroundPushWindowStartedAt = now;
                token.BackgroundPushCount = 0;
            }
            if (token.BackgroundPushCount >= maxCount) return Task.FromResult(false);
            token.BackgroundPushCount++;
            token.LastBackgroundPushAt = now;
            return Task.FromResult(true);
        }
    }

    public Task RemoveDevicePushTokenAsync(string handle, string deviceId, CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec) rec.DevicePushTokens.Remove(deviceId);
        return Task.CompletedTask;
    }

    public Task SetRecoveryKeyAsync(string handle, string recoveryPublicKey, CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec)
                // First writer wins: never overwrite an existing recovery key.
                rec.RecoveryPublicKey ??= recoveryPublicKey;
        return Task.CompletedTask;
    }

    public Task AddInviteAsync(StoredInvite invite, CancellationToken ct = default)
    {
        var map = invites.GetOrAdd(invite.Handle, _ => new(StringComparer.Ordinal));
        Purge(map);
        map[invite.CodeHash] = invite.ExpiresAt;
        return Task.CompletedTask;
    }

    public Task<bool> ConsumeInviteAsync(string handle, string codeHash, CancellationToken ct = default)
    {
        if (!invites.TryGetValue(handle, out var map)) return Task.FromResult(false);
        Purge(map);
        var ok = map.TryRemove(codeHash, out var exp) && exp > DateTimeOffset.UtcNow;
        return Task.FromResult(ok);
    }

    public async Task<InboxEnqueueResult> EnqueueAsync(
        string toHandle,
        string envelopeId,
        string fromHandle,
        string envelopeJson,
        int priority = RelayInboxPriority.Normal,
        bool requiresForeground = false,
        CancellationToken ct = default)
    {
        var normalizedInbox = NormalizeHandle(toHandle);
        var deliveryId = InboxDeliveryId.Create(fromHandle, envelopeId);
        string? admittedGeneration;
        lock (HandleQueueGate(InboxHandle(normalizedInbox)))
            admittedGeneration = GetInboxAdmissionGeneration(normalizedInbox);
        if (admittedGeneration is "")
            return new InboxEnqueueResult(deliveryId, Accepted: false, Created: false, "inbox_admission_rejected");

        if (beforeInboxAdmission is not null)
            await beforeInboxAdmission().ConfigureAwait(false);

        lock (HandleQueueGate(InboxHandle(normalizedInbox)))
        {
            if (admittedGeneration is not null
                && !string.Equals(
                    admittedGeneration,
                    GetInboxAdmissionGeneration(normalizedInbox),
                    StringComparison.Ordinal))
                return new InboxEnqueueResult(deliveryId, Accepted: false, Created: false, "inbox_admission_rejected");

            var inbox = inboxes.GetOrAdd(normalizedInbox, _ => new List<StoredEnvelope>());
            var now = UtcNow;
            var created = false;
            lock (inbox)
            {
                PurgeExpiredInbox(inbox, now);
                if (inbox.All(item => !string.Equals(item.Id, deliveryId, StringComparison.Ordinal)))
                {
                    inbox.Add(new StoredEnvelope
                    {
                        Id = deliveryId,
                        EnvelopeId = envelopeId,
                        From = LinkProtocol.Normalize(fromHandle),
                        To = normalizedInbox,
                        Json = envelopeJson,
                        QueuedAt = now,
                        ExpiresAt = RelayInboxPolicy.NeverExpires(normalizedInbox)
                            ? null
                            : now + RelayInboxPolicy.Retention,
                        Priority = priority,
                        RequiresForeground = requiresForeground
                    });
                    created = true;
                }
            }
            return new InboxEnqueueResult(deliveryId, Accepted: true, created);
        }
    }

    public Task<IReadOnlyList<StoredEnvelope>> LeaseInboxAsync(
        string toHandle,
        string leaseOwner,
        int maxItems = RelayInboxPolicy.DeliveryWindow,
        TimeSpan? leaseDuration = null,
        bool includeForeground = true,
        CancellationToken ct = default)
    {
        if (maxItems <= 0) return Task.FromResult<IReadOnlyList<StoredEnvelope>>([]);
        if (!inboxes.TryGetValue(toHandle, out var inbox))
            return Task.FromResult<IReadOnlyList<StoredEnvelope>>([]);
        var now = UtcNow;
        var until = now + (leaseDuration ?? RelayInboxPolicy.LeaseDuration);
        lock (inbox)
        {
            PurgeExpiredInbox(inbox, now);
            var outstanding = inbox.Count(item =>
                string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal)
                && item.LeaseUntil > now);
            var capacity = Math.Max(0, maxItems - outstanding);
            var eligible = includeForeground
                ? inbox
                : inbox.Where(item => !item.RequiresForeground);
            var result = OrderInboxCandidates(eligible, now)
                .Take(capacity)
                .ToList();
            foreach (var item in result)
            {
                item.LeaseOwner = leaseOwner;
                item.LeaseUntil = until;
                item.DeliveryAttempts++;
            }
            return Task.FromResult<IReadOnlyList<StoredEnvelope>>(
                result.Select(CloneEnvelope).ToList());
        }
    }

    public Task<StoredEnvelope?> AcknowledgeInboxAsync(
        string toHandle,
        string deliveryId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        if (!inboxes.TryGetValue(toHandle, out var inbox))
            return Task.FromResult<StoredEnvelope?>(null);
        lock (inbox)
        {
            var now = UtcNow;
            PurgeExpiredInbox(inbox, now);
            var index = inbox.FindIndex(item =>
                string.Equals(item.Id, deliveryId, StringComparison.Ordinal)
                && string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal)
                && item.LeaseUntil > now);
            if (index < 0) return Task.FromResult<StoredEnvelope?>(null);
            var acknowledged = CloneEnvelope(inbox[index]);
            inbox.RemoveAt(index);
            return Task.FromResult<StoredEnvelope?>(acknowledged);
        }
    }

    public Task<bool> TryLeaseInboxItemAsync(
        string toHandle,
        string deliveryId,
        string leaseOwner,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default)
    {
        if (!inboxes.TryGetValue(toHandle, out var inbox)) return Task.FromResult(false);
        var now = UtcNow;
        lock (inbox)
        {
            PurgeExpiredInbox(inbox, now);
            var item = OrderInboxCandidates(inbox, now).FirstOrDefault();
            if (item is null || !string.Equals(item.Id, deliveryId, StringComparison.Ordinal))
                return Task.FromResult(false);
            item.LeaseOwner = leaseOwner;
            item.LeaseUntil = now + (leaseDuration ?? RelayInboxPolicy.LeaseDuration);
            item.DeliveryAttempts++;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseInboxLeaseAsync(
        string toHandle,
        string deliveryId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        if (!inboxes.TryGetValue(toHandle, out var inbox)) return Task.CompletedTask;
        lock (inbox)
        {
            PurgeExpiredInbox(inbox, UtcNow);
            var item = inbox.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, deliveryId, StringComparison.Ordinal)
                && string.Equals(candidate.LeaseOwner, leaseOwner, StringComparison.Ordinal));
            if (item is not null)
            {
                item.LeaseOwner = null;
                item.LeaseUntil = null;
                item.DeliveryAttempts = Math.Max(0, item.DeliveryAttempts - 1);
            }
        }
        return Task.CompletedTask;
    }
    public Task<bool> CancelInboxAsync(
        string toHandle,
        string deliveryId,
        string fromHandle,
        CancellationToken ct = default)
    {
        if (!inboxes.TryGetValue(toHandle, out var inbox)) return Task.FromResult(false);
        var normalizedFrom = LinkProtocol.Normalize(fromHandle);
        lock (inbox)
        {
            PurgeExpiredInbox(inbox, UtcNow);
            var item = inbox.FirstOrDefault(item =>
                string.Equals(item.Id, deliveryId, StringComparison.Ordinal)
                && string.Equals(item.From, normalizedFrom, StringComparison.Ordinal));
            if (item is null || item.DeliveryAttempts != 0 || item.LeaseUntil is not null)
                return Task.FromResult(false);
            return Task.FromResult(inbox.Remove(item));
        }
    }

    public Task ReleaseInboxLeasesAsync(
        string toHandle,
        string leaseOwner,
        CancellationToken ct = default)
    {
        if (!inboxes.TryGetValue(toHandle, out var inbox)) return Task.CompletedTask;
        lock (inbox)
        {
            PurgeExpiredInbox(inbox, UtcNow);
            foreach (var item in inbox.Where(item =>
                         string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal)))
            {
                item.LeaseOwner = null;
                item.LeaseUntil = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> DrainInboxAsync(string toHandle, CancellationToken ct = default)
    {
        if (!inboxes.TryGetValue(toHandle, out var inbox))
            return Task.FromResult<IReadOnlyList<string>>([]);
        lock (inbox)
        {
            PurgeExpiredInbox(inbox, UtcNow);
            var result = inbox.OrderBy(item => item.QueuedAt).Select(item => item.Json).ToList();
            inbox.Clear();
            return Task.FromResult<IReadOnlyList<string>>(result);
        }
    }
    public Task<RelayInboxStats> GetInboxStatsAsync(CancellationToken ct = default)
    {
        long count = 0;
        DateTimeOffset? oldest = null;
        var now = UtcNow;
        foreach (var inbox in inboxes.Values)
        {
            lock (inbox)
            {
                PurgeExpiredInbox(inbox, now);
                count += inbox.Count;
                if (inbox.Count == 0) continue;
                var candidate = inbox.Min(item => item.QueuedAt);
                if (oldest is null || candidate < oldest) oldest = candidate;
            }
        }
        foreach (var queue in deviceQueues.Values)
        {
            lock (queue)
            {
                PurgeExpiredDeviceQueue(queue, now);
                count += queue.Count;
                if (queue.Count == 0) continue;
                var candidate = queue.Min(item => item.EnqueuedAt);
                if (oldest is null || candidate < oldest) oldest = candidate;
            }
        }
        return Task.FromResult(new RelayInboxStats(count, oldest));
    }

    public async Task<QueueEnqueueResult> EnqueueDeviceQueueAsync(
        string handle,
        QueueEnqueue request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeHandle(handle);
        string? sourceGeneration = null;
        string? targetGeneration = null;
        if (enforceQueueRegistration)
        {
            lock (HandleQueueGate(normalized))
            {
                if (!handles.TryGetValue(normalized, out var admitted)
                    || !RegistrationContainsDevice(admitted, request.SourceDeviceId)
                    || !RegistrationContainsDevice(admitted, request.TargetDeviceId)
                    || !admitted.DeviceQueueGenerations.TryGetValue(
                        request.SourceDeviceId, out sourceGeneration)
                    || !admitted.DeviceQueueGenerations.TryGetValue(
                        request.TargetDeviceId, out targetGeneration))
                    return new QueueEnqueueResult(
                        false,
                        DeviceQueueEntryIdProtocol.Create(
                            request.SourceDeviceId, request.TargetDeviceId, request.OperationId),
                        "sync_target_unknown");
            }
        }
        if (beforeQueueAdmission is not null)
            await beforeQueueAdmission().ConfigureAwait(false);
        var admittedSourceGeneration = sourceGeneration ?? "";
        var entryId = DeviceQueueEntryIdProtocol.Create(
            request.SourceDeviceId,
            request.TargetDeviceId,
            request.OperationId);
        lock (HandleQueueGate(normalized))
        {
            StoredHandle? registration = null;
            if (enforceQueueRegistration
                && (!handles.TryGetValue(normalized, out registration)
                    || !RegistrationContainsDevice(registration, request.TargetDeviceId)
                    || !registration.DeviceQueueGenerations.TryGetValue(
                        request.TargetDeviceId, out var currentTargetGeneration)
                    || !string.Equals(
                        currentTargetGeneration, targetGeneration, StringComparison.Ordinal)))
                return new QueueEnqueueResult(false, entryId, "sync_target_unknown");

            var queueKey = RelayDeviceQueueKey.Create(normalized, request.TargetDeviceId);
            var queue = deviceQueues.GetOrAdd(queueKey, _ => new List<StoredDeviceQueueItem>());
            var now = UtcNow;
            lock (queue)
            {
                PurgeExpiredDeviceQueue(queue, now);
                var existing = queue.FirstOrDefault(item =>
                    string.Equals(item.EntryId, entryId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (string.Equals(
                            existing.SourceGeneration,
                            admittedSourceGeneration,
                            StringComparison.Ordinal))
                        return new QueueEnqueueResult(true, existing.EntryId, Created: false);
                    queue.Remove(existing);
                }
                if (queue.Count >= DeviceQueueProtocol.MaxEntries)
                    return new QueueEnqueueResult(false, entryId, DeviceQueueProtocol.BoundedQueueFull);
                queue.Add(new StoredDeviceQueueItem
                {
                    EntryId = entryId,
                    Handle = normalized,
                    SourceDeviceId = request.SourceDeviceId,
                    SourceGeneration = admittedSourceGeneration,
                    TargetDeviceId = request.TargetDeviceId,
                    OperationId = request.OperationId,
                    Payload = request.Payload,
                    EnqueuedAt = now,
                    ExpiresAt = now + DeviceQueueProtocol.EntryTtl
                });
                return new QueueEnqueueResult(true, entryId, Created: true);
            }
        }
    }

    public Task<QueueDrainResponse> DrainDeviceQueueAsync(
        string handle,
        string deviceId,
        string leaseOwner,
        int maxEntries = DeviceQueueProtocol.DeliveryWindow,
        CancellationToken ct = default)
    {
        if (maxEntries <= 0)
            return Task.FromResult(new QueueDrainResponse([]));
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        var normalized = NormalizeHandle(handle);
        lock (HandleQueueGate(normalized))
        {
            if (!deviceQueues.TryGetValue(queueKey, out var queue))
                return Task.FromResult(new QueueDrainResponse([]));
            var now = UtcNow;
            var until = now + DeviceQueueProtocol.LeaseDuration;
            lock (queue)
            {
                PurgeExpiredDeviceQueue(queue, now);
                if (enforceQueueRegistration)
                {
                    handles.TryGetValue(normalized, out var registration);
                    queue.RemoveAll(item =>
                        registration is null
                        || !registration.DeviceQueueGenerations.TryGetValue(
                            item.SourceDeviceId, out var sourceGeneration)
                        || !string.Equals(
                            sourceGeneration, item.SourceGeneration, StringComparison.Ordinal));
                }
                var entries = queue
                    .Where(item => item.LeaseUntil is null || item.LeaseUntil <= now)
                    .OrderBy(item => item.EnqueuedAt)
                    .Take(Math.Min(maxEntries, DeviceQueueProtocol.DeliveryWindow))
                    .ToArray();
                foreach (var item in entries)
                {
                    item.LeaseOwner = leaseOwner;
                    item.LeaseUntil = until;
                }
                return Task.FromResult(new QueueDrainResponse(entries.Select(ToQueueEntry).ToArray()));
            }
        }
    }

    public Task<bool> AcknowledgeDeviceQueueAsync(
        string handle,
        string deviceId,
        string entryId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        if (!deviceQueues.TryGetValue(queueKey, out var queue))
            return Task.FromResult(false);
        lock (queue)
        {
            var now = UtcNow;
            PurgeExpiredDeviceQueue(queue, now);
            var index = queue.FindIndex(item =>
                string.Equals(item.EntryId, entryId, StringComparison.Ordinal)
                && string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal)
                && item.LeaseUntil > now);
            if (index < 0)
                return Task.FromResult(false);
            queue.RemoveAt(index);
            return Task.FromResult(true);
        }
    }

    public Task ReleaseDeviceQueueLeasesAsync(
        string handle,
        string deviceId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        if (!deviceQueues.TryGetValue(queueKey, out var queue))
            return Task.CompletedTask;
        lock (queue)
        {
            PurgeExpiredDeviceQueue(queue, UtcNow);
            foreach (var item in queue.Where(item =>
                         string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal)))
            {
                item.LeaseOwner = null;
                item.LeaseUntil = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task<int> GetDeviceQueueSizeAsync(
        string handle,
        string deviceId,
        CancellationToken ct = default)
    {
        var queueKey = RelayDeviceQueueKey.Create(handle, deviceId);
        if (!deviceQueues.TryGetValue(queueKey, out var queue))
            return Task.FromResult(0);
        lock (queue)
        {
            PurgeExpiredDeviceQueue(queue, UtcNow);
            return Task.FromResult(queue.Count);
        }
    }

    public Task<AgentDispatchCreateResult> CreateAgentDispatchAsync(
        StoredAgentDispatch dispatch,
        CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(dispatch.To);
        lock (HandleQueueGate(normalized))
        {
            if (deletingHandles.ContainsKey(normalized)
                || registeredHandleLifetimes.ContainsKey(normalized) && !handles.ContainsKey(normalized))
                return Task.FromResult(new AgentDispatchCreateResult(
                    AgentDispatchCreateStatus.Conflict, "", null));

            var key = DispatchDictionaryKey(normalized, dispatch.Id);
            var stored = CloneDispatch(dispatch);
            if (agentDispatches.TryAdd(key, stored))
                return Task.FromResult(new AgentDispatchCreateResult(
                    AgentDispatchCreateStatus.Created, stored.State, stored.AssignedDeviceId));

            var existing = agentDispatches[key];
            lock (existing)
            {
                var duplicate = string.Equals(existing.RequestId, dispatch.RequestId, StringComparison.Ordinal)
                    && string.Equals(existing.From, dispatch.From, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.To, dispatch.To, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.EnvelopeHash, dispatch.EnvelopeHash, StringComparison.Ordinal);
                return Task.FromResult(new AgentDispatchCreateResult(
                    duplicate ? AgentDispatchCreateStatus.Duplicate : AgentDispatchCreateStatus.Conflict,
                    existing.State,
                    existing.AssignedDeviceId));
            }
        }
    }

    public Task<StoredAgentDispatch?> GetAgentDispatchAsync(
        string toHandle,
        string dispatchId,
        CancellationToken ct = default)
    {
        var key = DispatchDictionaryKey(toHandle, dispatchId);
        if (!agentDispatches.TryGetValue(key, out var dispatch))
            return Task.FromResult<StoredAgentDispatch?>(null);
        lock (dispatch) return Task.FromResult<StoredAgentDispatch?>(CloneDispatch(dispatch));
    }

    public Task AssignPendingAgentDispatchesAsync(
        string toHandle,
        IReadOnlyList<string> candidateDeviceIds,
        CancellationToken ct = default)
    {
        foreach (var dispatch in AgentDispatchesFor(toHandle))
            lock (dispatch)
            {
                if (dispatch.State == AgentDispatchStates.Delivering
                    && dispatch.DeliveryLeaseUntil <= UtcNow)
                {
                    dispatch.State = AgentDispatchStates.Assigned;
                    dispatch.DeliveryLeaseOwner = null;
                    dispatch.DeliveryLeaseUntil = null;
                }
                if (dispatch.State is not (AgentDispatchStates.Pending or AgentDispatchStates.Assigned)) continue;
                var deviceId = AgentDispatchRecipientPolicy.ChooseDevice(
                    dispatch.RecipientDeviceIds, candidateDeviceIds);
                if (deviceId is null)
                {
                    if (dispatch.State == AgentDispatchStates.Assigned)
                    {
                        dispatch.State = AgentDispatchStates.Pending;
                        dispatch.AssignedDeviceId = null;
                        dispatch.AssignedAt = null;
                    }
                    continue;
                }
                if (dispatch.State == AgentDispatchStates.Assigned
                    && string.Equals(dispatch.AssignedDeviceId, deviceId, StringComparison.Ordinal))
                    continue;
                dispatch.State = AgentDispatchStates.Assigned;
                dispatch.AssignedDeviceId = deviceId;
                dispatch.AssignedAt = DateTimeOffset.UtcNow;
            }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredAgentDispatch>> TakeAssignedAgentDispatchesAsync(
        string toHandle,
        string deviceId,
        string leaseOwner,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default)
    {
        var result = new List<StoredAgentDispatch>();
        var now = UtcNow;
        foreach (var dispatch in AgentDispatchesFor(toHandle))
            lock (dispatch)
            {
                if (result.Count > 0) break;
                var claimable = dispatch.State == AgentDispatchStates.Assigned
                    || (dispatch.State == AgentDispatchStates.Delivering
                        && dispatch.DeliveryLeaseUntil <= now);
                if (!claimable
                    || !string.Equals(dispatch.AssignedDeviceId, deviceId, StringComparison.Ordinal))
                    continue;
                dispatch.State = AgentDispatchStates.Delivering;
                dispatch.DeliveryLeaseOwner = leaseOwner;
                dispatch.DeliveryLeaseUntil = now + (leaseDuration ?? RelayInboxPolicy.LeaseDuration);
                result.Add(CloneDispatch(dispatch));
            }
        return Task.FromResult<IReadOnlyList<StoredAgentDispatch>>(result);
    }

    public Task<bool> MarkAgentDispatchDeliveredAsync(
        string toHandle,
        string dispatchId,
        string deviceId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        var key = DispatchDictionaryKey(toHandle, dispatchId);
        if (!agentDispatches.TryGetValue(key, out var dispatch)) return Task.FromResult(false);
        lock (dispatch)
        {
            if (!OwnsLiveDeliveryLease(dispatch, deviceId, leaseOwner, UtcNow))
                return Task.FromResult(false);
            dispatch.State = AgentDispatchStates.Delivered;
            dispatch.DeliveredAt = UtcNow;
            dispatch.DeliveryLeaseOwner = null;
            dispatch.DeliveryLeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseAgentDispatchAsync(
        string toHandle,
        string dispatchId,
        string deviceId,
        string leaseOwner,
        string? nextDeviceId = null,
        CancellationToken ct = default)
    {
        var key = DispatchDictionaryKey(toHandle, dispatchId);
        if (!agentDispatches.TryGetValue(key, out var dispatch)) return Task.FromResult(false);
        lock (dispatch)
        {
            if (!OwnsLiveDeliveryLease(dispatch, deviceId, leaseOwner, UtcNow))
                return Task.FromResult(false);
            if (string.IsNullOrWhiteSpace(nextDeviceId))
            {
                dispatch.State = AgentDispatchStates.Pending;
                dispatch.AssignedDeviceId = null;
                dispatch.AssignedAt = null;
            }
            else
            {
                dispatch.State = AgentDispatchStates.Assigned;
                dispatch.AssignedDeviceId = nextDeviceId;
                dispatch.AssignedAt = DateTimeOffset.UtcNow;
            }
            dispatch.DeliveredAt = null;
            dispatch.DeliveryLeaseOwner = null;
            dispatch.DeliveryLeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    private static bool OwnsLiveDeliveryLease(
        StoredAgentDispatch dispatch,
        string deviceId,
        string leaseOwner,
        DateTimeOffset now)
        => dispatch.State == AgentDispatchStates.Delivering
           && string.Equals(dispatch.AssignedDeviceId, deviceId, StringComparison.Ordinal)
           && string.Equals(dispatch.DeliveryLeaseOwner, leaseOwner, StringComparison.Ordinal)
           && dispatch.DeliveryLeaseUntil > now;

    public Task<AgentDispatchResponseStageResult> StageAgentDispatchResponseAsync(
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
        var key = DispatchDictionaryKey(toHandle, dispatchId);
        if (!agentDispatches.TryGetValue(key, out var dispatch))
            return Task.FromResult(new AgentDispatchResponseStageResult(false, false, false, null));
        lock (dispatch)
        {
            if (!string.Equals(dispatch.From, fromHandle, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(dispatch.DispatchToken, dispatchToken, StringComparison.Ordinal)
                || !string.Equals(dispatch.AssignedDeviceId, respondingDeviceId, StringComparison.Ordinal))
                return Task.FromResult(new AgentDispatchResponseStageResult(false, false, false, null));

            if (dispatch.State is AgentDispatchStates.ResponsePending or AgentDispatchStates.Completed)
            {
                var duplicate = string.Equals(dispatch.ResponseId, responseId, StringComparison.Ordinal)
                    && string.Equals(dispatch.ResponseHash, responseHash, StringComparison.Ordinal);
                return Task.FromResult(new AgentDispatchResponseStageResult(
                    duplicate,
                    false,
                    duplicate && dispatch.State == AgentDispatchStates.Completed,
                    duplicate ? dispatch.ResponseJson : null));
            }
            if (!string.Equals(dispatch.State, AgentDispatchStates.Delivered, StringComparison.Ordinal))
                return Task.FromResult(new AgentDispatchResponseStageResult(false, false, false, null));

            dispatch.State = AgentDispatchStates.ResponsePending;
            dispatch.ResponseId = responseId;
            dispatch.ResponseJson = responseJson;
            dispatch.ResponseHash = responseHash;
            dispatch.ResponseStagedAt = UtcNow;
            dispatch.EnvelopeJson = "";
            dispatch.RecipientDeviceIds.Clear();
            return Task.FromResult(new AgentDispatchResponseStageResult(true, true, false, responseJson));
        }
    }

    public Task<bool> CompleteAgentDispatchResponseAsync(
        string toHandle,
        string dispatchId,
        string responseId,
        CancellationToken ct = default)
    {
        var key = DispatchDictionaryKey(toHandle, dispatchId);
        if (!agentDispatches.TryGetValue(key, out var dispatch)) return Task.FromResult(false);
        lock (dispatch)
        {
            if (string.Equals(dispatch.State, AgentDispatchStates.Completed, StringComparison.Ordinal)
                && string.Equals(dispatch.ResponseId, responseId, StringComparison.Ordinal))
                return Task.FromResult(true);
            if (!string.Equals(dispatch.State, AgentDispatchStates.ResponsePending, StringComparison.Ordinal)
                || !string.Equals(dispatch.ResponseId, responseId, StringComparison.Ordinal))
                return Task.FromResult(false);
            dispatch.State = AgentDispatchStates.Completed;
            dispatch.CompletedAt = UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<StoredAgentDispatch>> GetPendingAgentResponsesAsync(
        int maxItems = 100,
        CancellationToken ct = default)
    {
        if (maxItems <= 0) return Task.FromResult<IReadOnlyList<StoredAgentDispatch>>([]);
        var pending = agentDispatches.Values
            .Where(dispatch => string.Equals(
                dispatch.State, AgentDispatchStates.ResponsePending, StringComparison.Ordinal))
            .OrderBy(dispatch => dispatch.ResponseStagedAt)
            .ThenBy(dispatch => dispatch.Id, StringComparer.Ordinal)
            .Take(maxItems)
            .Select(dispatch =>
            {
                lock (dispatch) return CloneDispatch(dispatch);
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<StoredAgentDispatch>>(pending);
    }

    public Task<HandleRatePolicy?> GetHandleRatePolicyAsync(string handle, CancellationToken ct = default)
        => Task.FromResult(ratePolicies.TryGetValue(NormalizeHandle(handle), out var policy)
            ? policy with { }
            : null);

    public Task SetHandleRatePolicyAsync(
        string handle, HandleRatePolicy policy, CancellationToken ct = default)
    {
        ratePolicies[NormalizeHandle(handle)] = policy with { };
        return Task.CompletedTask;
    }

    public Task<bool> DeleteHandleRatePolicyAsync(string handle, CancellationToken ct = default)
        => Task.FromResult(ratePolicies.TryRemove(NormalizeHandle(handle), out _));

    // ---- Capability directory + reputation ----------------------------------

    public Task UpsertServiceAsync(StoredService svc, CancellationToken ct = default)
    {
        services.AddOrUpdate(svc.ServiceId,
            _ => CloneService(svc),
            (_, existing) =>
            {
                // Preserve reputation (votes + attested users) across a re-publish; only refresh metadata.
                lock (existing)
                {
                    existing.Handle = svc.Handle;
                    existing.Name = svc.Name;
                    existing.Description = svc.Description;
                    existing.Category = svc.Category;
                }
                return existing;
            });
        return Task.CompletedTask;
    }

    public Task<bool> RemoveServiceAsync(string handle, string serviceId, CancellationToken ct = default)
    {
        if (!services.TryGetValue(serviceId, out var svc))
            return Task.FromResult(false);

        bool owned;
        lock (svc) owned = string.Equals(svc.Handle, handle, StringComparison.OrdinalIgnoreCase);
        if (!owned) return Task.FromResult(false);

        return Task.FromResult(services.TryRemove(serviceId, out _));
    }

    public Task<StoredService?> GetServiceAsync(string serviceId, CancellationToken ct = default)
        => Task.FromResult(services.TryGetValue(serviceId, out var svc) ? CloneService(svc) : null);

    public Task<IReadOnlyList<StoredService>> ListServicesAsync(string? query, CancellationToken ct = default)
    {
        var q = query?.Trim();
        var all = services.Values.Select(CloneService);
        if (!string.IsNullOrEmpty(q))
            all = all.Where(s =>
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<StoredService>>(all.ToList());
    }

    public Task RecordServiceUsageAsync(string serviceId, string userHandle, CancellationToken ct = default)
    {
        if (services.TryGetValue(serviceId, out var svc))
            lock (svc) svc.Users.Add(userHandle);
        return Task.CompletedTask;
    }

    public Task<bool> HasUsedServiceAsync(string serviceId, string userHandle, CancellationToken ct = default)
    {
        if (!services.TryGetValue(serviceId, out var svc)) return Task.FromResult(false);
        bool used;
        lock (svc) used = svc.Users.Contains(userHandle);
        return Task.FromResult(used);
    }

    public Task SetServiceVoteAsync(string serviceId, string voterHandle, int vote, CancellationToken ct = default)
    {
        if (services.TryGetValue(serviceId, out var svc))
            lock (svc)
            {
                if (vote == 0) svc.Votes.Remove(voterHandle);
                else svc.Votes[voterHandle] = vote > 0 ? 1 : -1; // one updatable vote per voter
            }
        return Task.CompletedTask;
    }

    private static StoredService CloneService(StoredService s)
    {
        lock (s)
            return new StoredService
            {
                ServiceId = s.ServiceId,
                Handle = s.Handle,
                Name = s.Name,
                Description = s.Description,
                Category = s.Category,
                PublishedAt = s.PublishedAt,
                Votes = new Dictionary<string, int>(s.Votes, StringComparer.OrdinalIgnoreCase),
                Users = new HashSet<string>(s.Users, StringComparer.OrdinalIgnoreCase)
            };
    }

    private static void Purge(ConcurrentDictionary<string, DateTimeOffset> map)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in map)
            if (kv.Value <= now) map.TryRemove(kv.Key, out _);
    }

    private static string NormalizeHandle(string handle)
        => handle.Trim().TrimStart('@').ToLowerInvariant();

    private static string InboxHandle(string inboxKey)
    {
        var separator = inboxKey.IndexOf('\u001f');
        return separator < 0 ? inboxKey : inboxKey[..separator];
    }

    private string? GetInboxAdmissionGeneration(string inboxKey)
    {
        var handle = InboxHandle(inboxKey);
        if (!handles.TryGetValue(handle, out var registration))
            return deletingHandles.ContainsKey(handle) || registeredHandleLifetimes.ContainsKey(handle)
                ? ""
                : null;
        var separator = inboxKey.IndexOf('\u001f');
        if (separator < 0)
            return registration.InboxGeneration;
        var deviceId = inboxKey[(separator + 1)..];
        return registration.DeviceQueueGenerations.TryGetValue(deviceId, out var deviceGeneration)
            ? registration.InboxGeneration + ":" + deviceGeneration
            : "";
    }

    private static QueueEntry ToQueueEntry(StoredDeviceQueueItem item)
        => new(
            item.EntryId,
            item.SourceDeviceId,
            item.TargetDeviceId,
            item.Payload,
            item.EnqueuedAt,
            item.ExpiresAt);

    private IEnumerable<StoredAgentDispatch> AgentDispatchesFor(string toHandle)
        => agentDispatches.Values
            .Where(dispatch => string.Equals(dispatch.To, toHandle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(dispatch => dispatch.QueuedAt)
            .ThenBy(dispatch => dispatch.Id, StringComparer.Ordinal);

    private static string DispatchDictionaryKey(string toHandle, string dispatchId)
        => $"{NormalizeHandle(toHandle)}\u001f{dispatchId}";

    private static StoredHandle Clone(StoredHandle r)
    {
        lock (r)
            return new StoredHandle
            {
                Handle = r.Handle,
                DisplayName = r.DisplayName,
                RegisteredAt = r.RegisteredAt,
                InboxGeneration = r.InboxGeneration,
                DevicePublicKeys = r.DevicePublicKeys.ToList(),
                DeviceQueueGenerations = new Dictionary<string, string>(
                    r.DeviceQueueGenerations, StringComparer.Ordinal),
                RecoveryPublicKey = r.RecoveryPublicKey,
                DeviceNames = new Dictionary<string, string>(r.DeviceNames),
                DevicePlatforms = new Dictionary<string, string>(r.DevicePlatforms),
                DeviceRemoteAgentEnabled = new Dictionary<string, bool>(r.DeviceRemoteAgentEnabled),
                DeviceAtomicAgentDispatchEnabled = new Dictionary<string, bool>(r.DeviceAtomicAgentDispatchEnabled),
                DeviceProtocolVersions = new Dictionary<string, int>(r.DeviceProtocolVersions),
                AgentPrimaryDeviceId = r.AgentPrimaryDeviceId,
                AgentFailoverDeviceId = r.AgentFailoverDeviceId,
                AgentRoutingVersion = r.AgentRoutingVersion,
                AgentPrimaryWasSelectedAutomatically = r.AgentPrimaryWasSelectedAutomatically,
                DevicePushTokens = r.DevicePushTokens.ToDictionary(
                    kv => kv.Key,
                    kv => new DevicePushToken
                    {
                        Platform = kv.Value.Platform,
                        Token = kv.Value.Token,
                        AlertsEnabled = kv.Value.AlertsEnabled,
                        BackgroundPushWindowStartedAt = kv.Value.BackgroundPushWindowStartedAt,
                        BackgroundPushCount = kv.Value.BackgroundPushCount,
                        LastBackgroundPushAt = kv.Value.LastBackgroundPushAt,
                        UpdatedAt = kv.Value.UpdatedAt
                    })
            };
    }

    private static IReadOnlyList<StoredEnvelope> OrderInboxCandidates(
        IEnumerable<StoredEnvelope> inbox,
        DateTimeOffset now)
    {
        var available = inbox
            .Where(item => item.LeaseUntil is null || item.LeaseUntil <= now)
            .ToList();
        var aged = available
            .Where(item => item.QueuedAt <= now - RelayInboxPolicy.PriorityAgingThreshold)
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.QueuedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        var prioritized = available
            .Where(item => !ReferenceEquals(item, aged))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.QueuedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal);
        return aged is null
            ? prioritized.ToArray()
            : new[] { aged }.Concat(prioritized).ToArray();
    }
    private static void PurgeExpiredInbox(List<StoredEnvelope> inbox, DateTimeOffset now)
        => inbox.RemoveAll(item => item.ExpiresAt is { } expiresAt && expiresAt <= now);

    private static void PurgeExpiredDeviceQueue(
        List<StoredDeviceQueueItem> queue,
        DateTimeOffset now)
        => queue.RemoveAll(item => item.ExpiresAt <= now);

    private static StoredEnvelope CloneEnvelope(StoredEnvelope envelope) => new()
    {
        Id = envelope.Id,
        EnvelopeId = envelope.EnvelopeId,
        From = envelope.From,
        To = envelope.To,
        Json = envelope.Json,
        QueuedAt = envelope.QueuedAt,
        ExpiresAt = envelope.ExpiresAt,
        LeaseOwner = envelope.LeaseOwner,
        LeaseUntil = envelope.LeaseUntil,
        DeliveryAttempts = envelope.DeliveryAttempts,
        Priority = envelope.Priority,
        RequiresForeground = envelope.RequiresForeground
    };

    private static StoredAgentDispatch CloneDispatch(StoredAgentDispatch dispatch) => new()
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
}
