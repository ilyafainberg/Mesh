using System.Collections.Concurrent;
using Mesh.Relay.RateLimiting;

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
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> inboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredService> services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HandleRatePolicy> ratePolicies = new(StringComparer.OrdinalIgnoreCase);

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
        inboxes.TryRemove(handle, out _);
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
        CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    rec.DeviceNames[deviceId] = name;
                rec.DevicePlatforms[deviceId] = platform;
                rec.DeviceRemoteAgentEnabled[deviceId] = remoteAgentEnabled;
            }
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

    public Task EnqueueAsync(string toHandle, string envelopeJson, CancellationToken ct = default)
    {
        inboxes.GetOrAdd(toHandle, _ => new()).Enqueue(envelopeJson);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> DrainInboxAsync(string toHandle, CancellationToken ct = default)
    {
        var result = new List<string>();
        if (inboxes.TryGetValue(toHandle, out var q))
            while (q.TryDequeue(out var item)) result.Add(item);
        return Task.FromResult<IReadOnlyList<string>>(result);
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
                DeviceRemoteAgentEnabled = new Dictionary<string, bool>(r.DeviceRemoteAgentEnabled)
            };
    }
}
