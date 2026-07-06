using System.Collections.Concurrent;

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
        return Task.FromResult(removed);
    }

    public Task SetDisplayNameAsync(string handle, string displayName, CancellationToken ct = default)
    {
        if (handles.TryGetValue(handle, out var rec))
            lock (rec) rec.DisplayName = displayName;
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

    private static void Purge(ConcurrentDictionary<string, DateTimeOffset> map)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in map)
            if (kv.Value <= now) map.TryRemove(kv.Key, out _);
    }

    private static StoredHandle Clone(StoredHandle r)
    {
        lock (r)
            return new StoredHandle
            {
                Handle = r.Handle,
                DisplayName = r.DisplayName,
                RegisteredAt = r.RegisteredAt,
                DevicePublicKeys = r.DevicePublicKeys.ToList(),
                RecoveryPublicKey = r.RecoveryPublicKey
            };
    }
}
