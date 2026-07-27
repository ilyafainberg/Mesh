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
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> invites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<StoredEnvelope>> inboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredAgentDispatch> agentDispatches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StoredService> services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HandleRatePolicy> ratePolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider timeProvider;

    public InMemoryRelayStore(TimeProvider? timeProvider = null)
        => this.timeProvider = timeProvider ?? TimeProvider.System;

    private DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public Task<StoredHandle?> GetHandleAsync(string handle, CancellationToken ct = default)
        => Task.FromResult(handles.TryGetValue(handle, out var rec) ? Clone(rec) : null);

    public Task<(StoredHandle record, bool deviceAuthorized)> UpsertHandleAsync(
        string handle, string devicePublicKey, string? displayName, bool allowNewDevice, CancellationToken ct = default)
    {
        var rec = handles.AddOrUpdate(handle,
            _ =>
            {
                var fresh = new StoredHandle { Handle = handle, DisplayName = displayName, RegisteredAt = DateTimeOffset.UtcNow };
                fresh.DevicePublicKeys.Add(devicePublicKey);
                return fresh;
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    if (displayName is not null) existing.DisplayName = displayName;
                    if (!existing.DevicePublicKeys.Contains(devicePublicKey) && allowNewDevice)
                        existing.DevicePublicKeys.Add(devicePublicKey);
                }
                return existing;
            });

        bool authorized;
        lock (rec) authorized = rec.DevicePublicKeys.Contains(devicePublicKey);
        return Task.FromResult((Clone(rec), authorized));
    }

    public Task<bool> DeleteHandleAsync(string handle, CancellationToken ct = default)
    {
        var removed = handles.TryRemove(handle, out _);
        invites.TryRemove(handle, out _);
        foreach (var inboxKey in inboxes.Keys.Where(key =>
                     string.Equals(key, handle, StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith(handle + "\u001f", StringComparison.OrdinalIgnoreCase)))
            inboxes.TryRemove(inboxKey, out _);
        foreach (var item in agentDispatches)
            if (string.Equals(item.Value.To, handle, StringComparison.OrdinalIgnoreCase))
                agentDispatches.TryRemove(item.Key, out _);
        ratePolicies.TryRemove(NormalizeHandle(handle), out _);
        return Task.FromResult(removed);
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

    public Task SetDevicePushTokenAsync(string handle, string deviceId, string platform, string token, CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec)
                rec.DevicePushTokens[deviceId] = new DevicePushToken { Platform = platform, Token = token, UpdatedAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
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

    public Task<InboxEnqueueResult> EnqueueAsync(
        string toHandle,
        string envelopeId,
        string fromHandle,
        string envelopeJson,
        CancellationToken ct = default)
    {
        var deliveryId = InboxDeliveryId.Create(fromHandle, envelopeId);
        var inbox = inboxes.GetOrAdd(toHandle, _ => new List<StoredEnvelope>());
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
                    To = toHandle,
                    Json = envelopeJson,
                    QueuedAt = now,
                    ExpiresAt = RelayInboxPolicy.NeverExpires(toHandle)
                        ? null
                        : now + RelayInboxPolicy.Retention
                });
                created = true;
            }
        }
        return Task.FromResult(new InboxEnqueueResult(deliveryId, created));
    }

    public Task<IReadOnlyList<StoredEnvelope>> LeaseInboxAsync(
        string toHandle,
        string leaseOwner,
        int maxItems = RelayInboxPolicy.DeliveryWindow,
        TimeSpan? leaseDuration = null,
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
            var result = inbox
                .Where(item => item.LeaseUntil is null || item.LeaseUntil <= now)
                .OrderBy(item => item.QueuedAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
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
        CancellationToken ct = default)
    {
        if (!inboxes.TryGetValue(toHandle, out var inbox))
            return Task.FromResult<StoredEnvelope?>(null);
        lock (inbox)
        {
            PurgeExpiredInbox(inbox, UtcNow);
            var index = inbox.FindIndex(item =>
                string.Equals(item.Id, deliveryId, StringComparison.Ordinal));
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
            var item = inbox.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, deliveryId, StringComparison.Ordinal));
            if (item is null || item.LeaseUntil > now) return Task.FromResult(false);
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
            var removed = inbox.RemoveAll(item =>
                string.Equals(item.Id, deliveryId, StringComparison.Ordinal)
                && string.Equals(item.From, normalizedFrom, StringComparison.Ordinal)) > 0;
            return Task.FromResult(removed);
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
        return Task.FromResult(new RelayInboxStats(count, oldest));
    }

    public Task<AgentDispatchCreateResult> CreateAgentDispatchAsync(
        StoredAgentDispatch dispatch,
        CancellationToken ct = default)
    {
        var key = DispatchDictionaryKey(dispatch.To, dispatch.Id);
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
        CancellationToken ct = default)
    {
        var result = new List<StoredAgentDispatch>();
        foreach (var dispatch in AgentDispatchesFor(toHandle))
            lock (dispatch)
            {
                if (result.Count > 0) break;
                if (!string.Equals(dispatch.State, AgentDispatchStates.Assigned, StringComparison.Ordinal)
                    || !string.Equals(dispatch.AssignedDeviceId, deviceId, StringComparison.Ordinal))
                    continue;
                dispatch.State = AgentDispatchStates.Delivered;
                dispatch.DeliveredAt = DateTimeOffset.UtcNow;
                result.Add(CloneDispatch(dispatch));
            }
        return Task.FromResult<IReadOnlyList<StoredAgentDispatch>>(result);
    }

    public Task<bool> ReleaseAgentDispatchAsync(
        string toHandle,
        string dispatchId,
        string deviceId,
        string? nextDeviceId = null,
        CancellationToken ct = default)
    {
        var key = DispatchDictionaryKey(toHandle, dispatchId);
        if (!agentDispatches.TryGetValue(key, out var dispatch)) return Task.FromResult(false);
        lock (dispatch)
        {
            if (!string.Equals(dispatch.State, AgentDispatchStates.Delivered, StringComparison.Ordinal)
                || !string.Equals(dispatch.AssignedDeviceId, deviceId, StringComparison.Ordinal))
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
            return Task.FromResult(true);
        }
    }

    public Task<bool> CompleteAgentDispatchAsync(
        string toHandle,
        string dispatchId,
        string fromHandle,
        string dispatchToken,
        string respondingDeviceId,
        CancellationToken ct = default)
    {
        var key = DispatchDictionaryKey(toHandle, dispatchId);
        if (!agentDispatches.TryGetValue(key, out var dispatch)) return Task.FromResult(false);
        lock (dispatch)
        {
            if (!string.Equals(dispatch.State, AgentDispatchStates.Delivered, StringComparison.Ordinal)
                || !string.Equals(dispatch.From, fromHandle, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(dispatch.DispatchToken, dispatchToken, StringComparison.Ordinal)
                || !string.Equals(dispatch.AssignedDeviceId, respondingDeviceId, StringComparison.Ordinal))
                return Task.FromResult(false);
            dispatch.State = AgentDispatchStates.Completed;
            dispatch.CompletedAt = DateTimeOffset.UtcNow;
            dispatch.EnvelopeJson = "";
            dispatch.RecipientDeviceIds.Clear();
            return Task.FromResult(true);
        }
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
                DevicePublicKeys = r.DevicePublicKeys.ToList(),
                RecoveryPublicKey = r.RecoveryPublicKey,
                DeviceNames = new Dictionary<string, string>(r.DeviceNames),
                DevicePlatforms = new Dictionary<string, string>(r.DevicePlatforms),
                DeviceRemoteAgentEnabled = new Dictionary<string, bool>(r.DeviceRemoteAgentEnabled),
                DeviceAtomicAgentDispatchEnabled = new Dictionary<string, bool>(r.DeviceAtomicAgentDispatchEnabled),
                AgentPrimaryDeviceId = r.AgentPrimaryDeviceId,
                AgentFailoverDeviceId = r.AgentFailoverDeviceId,
                AgentRoutingVersion = r.AgentRoutingVersion,
                AgentPrimaryWasSelectedAutomatically = r.AgentPrimaryWasSelectedAutomatically,
                DevicePushTokens = r.DevicePushTokens.ToDictionary(
                    kv => kv.Key,
                    kv => new DevicePushToken { Platform = kv.Value.Platform, Token = kv.Value.Token, UpdatedAt = kv.Value.UpdatedAt })
            };
    }

    private static void PurgeExpiredInbox(List<StoredEnvelope> inbox, DateTimeOffset now)
        => inbox.RemoveAll(item => item.ExpiresAt is { } expiresAt && expiresAt <= now);

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
        DeliveryAttempts = envelope.DeliveryAttempts
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
        QueuedAt = dispatch.QueuedAt,
        AssignedAt = dispatch.AssignedAt,
        DeliveredAt = dispatch.DeliveredAt,
        CompletedAt = dispatch.CompletedAt
    };
}
