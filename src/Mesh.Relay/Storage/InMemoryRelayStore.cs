using System.Collections.Concurrent;
using Mesh.Relay.RateLimiting;
using Mesh.Shared;

namespace Mesh.Relay.Storage;

/// <summary>
/// Default in-memory implementation of <see cref="IRelayStore"/>. Used whenever no Cosmos
/// connection is configured (local dev, single instance). State is lost on restart, which is
/// acceptable because Protocol 9 persists metadata only and never message payloads.
///
/// Every mutation is metadata: handles, device/recovery keys, auth generation, custody head,
/// push token metadata, invites, service directory, and administrative rate policy. There are no
/// mailbox, device-queue, or agent-routing payload structures here, by design.
/// </summary>
public sealed class InMemoryRelayStore : IRelayStore
{
    private readonly ConcurrentDictionary<string, StoredHandle> handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredHandle> deletingHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> invites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredService> services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HandleRatePolicy> ratePolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> handleGates = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRelayStore(TimeProvider? timeProvider = null)
    {
        // TimeProvider retained for parity with the Cosmos store's constructor shape; the in-memory
        // store reads wall-clock only for opportunistic invite expiry.
        _ = timeProvider;
    }

    private object HandleGate(string handle)
        => handleGates.GetOrAdd(NormalizeHandle(handle), static _ => new object());

    public Task<StoredHandle?> GetHandleAsync(string handle, CancellationToken ct = default)
        => Task.FromResult(handles.TryGetValue(NormalizeHandle(handle), out var rec) ? Clone(rec) : null);

    public Task<StoredHandle?> GetHandleForDeletionAsync(string handle, CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(handle);
        return Task.FromResult(
            handles.TryGetValue(normalized, out var active)
                ? Clone(active)
                : deletingHandles.TryGetValue(normalized, out var deleting) ? Clone(deleting) : null);
    }

    public Task<(StoredHandle record, bool deviceAuthorized)> UpsertHandleAsync(
        string handle, string devicePublicKey, string? displayName, bool allowNewDevice, CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(handle);
        lock (HandleGate(normalized))
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
                    AuthGeneration = 1,
                    CustodyHead = ""
                };
                rec.DevicePublicKeys.Add(devicePublicKey);
                handles[normalized] = rec;
            }
            else
            {
                if (displayName is not null) rec.DisplayName = displayName;
                if (!rec.DevicePublicKeys.Contains(devicePublicKey) && allowNewDevice)
                    rec.DevicePublicKeys.Add(devicePublicKey);
            }

            var authorized = rec.DevicePublicKeys.Contains(devicePublicKey);
            return Task.FromResult((Clone(rec), authorized));
        }
    }

    public Task<bool> DeleteHandleAsync(string handle, CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(handle);
        lock (HandleGate(normalized))
        {
            if (!handles.TryRemove(normalized, out _) && !deletingHandles.ContainsKey(normalized))
                return Task.FromResult(false);
            invites.TryRemove(normalized, out _);
            ratePolicies.TryRemove(normalized, out _);
            deletingHandles.TryRemove(normalized, out _);
            return Task.FromResult(true);
        }
    }

    public Task SetDisplayNameAsync(string handle, string displayName, CancellationToken ct = default)
    {
        if (handles.TryGetValue(NormalizeHandle(handle), out var rec))
            lock (rec) rec.DisplayName = displayName;
        return Task.CompletedTask;
    }

    public Task SetDeviceNameAsync(string handle, string deviceId, string name, CancellationToken ct = default)
    {
        if (handles.TryGetValue(NormalizeHandle(handle), out var rec))
            lock (rec) rec.DeviceNames[deviceId] = name;
        return Task.CompletedTask;
    }

    public Task SetDeviceMetadataAsync(
        string handle,
        string deviceId,
        string? name,
        string platform,
        bool remoteAgentEnabled,
        bool agentHostEnabled,
        int protocolVersion = MeshProtocol.Version,
        CancellationToken ct = default)
    {
        if (handles.TryGetValue(NormalizeHandle(handle), out var rec))
            lock (rec)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    rec.DeviceNames[deviceId] = name;
                rec.DevicePlatforms[deviceId] = platform;
                rec.DeviceRemoteAgentEnabled[deviceId] = remoteAgentEnabled;
                rec.DeviceAgentHostEnabled[deviceId] = agentHostEnabled;
                rec.DeviceProtocolVersions[deviceId] = protocolVersion;
                if (string.IsNullOrWhiteSpace(rec.AgentPrimaryDeviceId)
                    && DevicePlatforms.IsDesktop(platform)
                    && agentHostEnabled)
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
        lock (HandleGate(normalized))
        {
            if (!handles.TryGetValue(normalized, out var rec))
                return Task.FromResult(new DeviceRevocationResult(false, 0));

            if (authorizingPublicKey is not null
                && !rec.DevicePublicKeys.Contains(authorizingPublicKey, StringComparer.Ordinal))
                return Task.FromResult(new DeviceRevocationResult(false, rec.AuthGeneration));

            var publicKey = rec.DevicePublicKeys.FirstOrDefault(key =>
                string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal));
            if (publicKey is null || rec.DevicePublicKeys.Count <= 1)
                return Task.FromResult(new DeviceRevocationResult(false, rec.AuthGeneration));

            rec.DevicePublicKeys.Remove(publicKey);
            rec.DeviceNames.Remove(targetDeviceId);
            rec.DevicePlatforms.Remove(targetDeviceId);
            rec.DeviceRemoteAgentEnabled.Remove(targetDeviceId);
            rec.DeviceAgentHostEnabled.Remove(targetDeviceId);
            rec.DeviceProtocolVersions.Remove(targetDeviceId);
            rec.DevicePushTokens.Remove(targetDeviceId);
            if (string.Equals(rec.AgentPrimaryDeviceId, targetDeviceId, StringComparison.Ordinal))
                rec.AgentPrimaryDeviceId = null;
            if (string.Equals(rec.AgentFailoverDeviceId, targetDeviceId, StringComparison.Ordinal))
                rec.AgentFailoverDeviceId = null;
            rec.AgentRoutingVersion = Guid.NewGuid().ToString("n");
            rec.AgentPrimaryWasSelectedAutomatically = false;
            // Advancing the auth generation makes any authority presented for the revoked device stale.
            rec.AuthGeneration++;
            return Task.FromResult(new DeviceRevocationResult(true, rec.AuthGeneration));
        }
    }

    public Task<bool> AdvanceCustodyAsync(
        string handle,
        long expectedAuthGeneration,
        long newAuthGeneration,
        string newCustodyHead,
        CancellationToken ct = default)
    {
        var normalized = NormalizeHandle(handle);
        lock (HandleGate(normalized))
        {
            if (!handles.TryGetValue(normalized, out var rec))
                return Task.FromResult(false);
            lock (rec)
            {
                if (rec.AuthGeneration != expectedAuthGeneration || newAuthGeneration < rec.AuthGeneration)
                    return Task.FromResult(false);
                rec.AuthGeneration = newAuthGeneration;
                rec.CustodyHead = newCustodyHead;
                return Task.FromResult(true);
            }
        }
    }

    public Task<bool> SetAgentRoutingAsync(
        string handle,
        string primaryDeviceId,
        string? failoverDeviceId,
        string expectedVersion,
        CancellationToken ct = default)
    {
        if (!handles.TryGetValue(NormalizeHandle(handle), out var rec)) return Task.FromResult(false);
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
        if (handles.TryGetValue(NormalizeHandle(handle), out var rec))
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
        if (!handles.TryGetValue(NormalizeHandle(handle), out var rec)) return Task.FromResult(false);
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
        if (handles.TryGetValue(NormalizeHandle(handle), out var rec))
            lock (rec) rec.DevicePushTokens.Remove(deviceId);
        return Task.CompletedTask;
    }

    public Task SetRecoveryKeyAsync(string handle, string recoveryPublicKey, CancellationToken ct = default)
    {
        if (handles.TryGetValue(NormalizeHandle(handle), out var rec))
            lock (rec)
                // First writer wins: never overwrite an existing recovery key.
                rec.RecoveryPublicKey ??= recoveryPublicKey;
        return Task.CompletedTask;
    }

    public Task AddInviteAsync(StoredInvite invite, CancellationToken ct = default)
    {
        var map = invites.GetOrAdd(NormalizeHandle(invite.Handle), _ => new(StringComparer.Ordinal));
        Purge(map);
        map[invite.CodeHash] = invite.ExpiresAt;
        return Task.CompletedTask;
    }

    public Task<bool> ConsumeInviteAsync(string handle, string codeHash, CancellationToken ct = default)
    {
        if (!invites.TryGetValue(NormalizeHandle(handle), out var map)) return Task.FromResult(false);
        Purge(map);
        var ok = map.TryRemove(codeHash, out var exp) && exp > DateTimeOffset.UtcNow;
        return Task.FromResult(ok);
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
                AuthGeneration = r.AuthGeneration,
                CustodyHead = r.CustodyHead,
                DevicePublicKeys = r.DevicePublicKeys.ToList(),
                RecoveryPublicKey = r.RecoveryPublicKey,
                DeviceNames = new Dictionary<string, string>(r.DeviceNames),
                DevicePlatforms = new Dictionary<string, string>(r.DevicePlatforms),
                DeviceRemoteAgentEnabled = new Dictionary<string, bool>(r.DeviceRemoteAgentEnabled),
                DeviceAgentHostEnabled = new Dictionary<string, bool>(r.DeviceAgentHostEnabled),
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
}
