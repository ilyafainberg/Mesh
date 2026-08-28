using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

internal static class NotificationContentPolicy
{
    private const int MaxPreviewChars = 160;

    public static LocalNotification Build(
        CommittedActivity activity,
        bool playSound)
    {
        return new LocalNotification(
            activity.StableId,
            activity.Kind,
            activity.Title,
            NormalizeBody(activity.Body),
            activity.Route,
            playSound);
    }

    private static string NormalizeBody(string value)
    {
        var normalized = string.Join(' ', value.Split(
            ['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= MaxPreviewChars ? normalized : normalized[..MaxPreviewChars] + "...";
    }
}

internal static class NotificationDecisionPolicy
{
    public static bool ShouldShowBanner(
        CommittedActivity activity,
        bool doNotDisturb,
        bool muted,
        bool entityVisible)
        => activity.NotifyRequested
           && !activity.IsHistorical
           && !doNotDisturb
           && !muted
           && !entityVisible;
}

internal static class RemoteWakeNotificationPolicy
{
    public static bool ShouldShowGenericAlert(bool requested, bool appForeground)
        => requested && !appForeground;
}

internal static class TopicRunBackgroundPolicy
{
    public static bool ShouldDefer(TopicRunUpdatePayload update)
        => !string.IsNullOrEmpty(update.Delta)
           && update.Phase is not TopicRunPhase.Completed
               and not TopicRunPhase.Failed
               and not TopicRunPhase.Cancelled;
}

internal sealed class NotificationWakeDeduplicator(TimeSpan retention, int capacity = 512)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> seen =
        new(StringComparer.Ordinal);

    public bool TryAccept(string? wakeId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(wakeId)) return true;
        while (true)
        {
            if (seen.TryGetValue(wakeId, out var previous))
            {
                if (now - previous < retention) return false;
                if (seen.TryUpdate(wakeId, now, previous)) return true;
                continue;
            }
            if (seen.Count >= capacity) Prune(now);
            if (seen.TryAdd(wakeId, now)) return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var item in seen)
            if (now - item.Value >= retention) seen.TryRemove(item.Key, out _);
        if (seen.Count < capacity) return;
        foreach (var item in seen.OrderBy(item => item.Value).Take(Math.Max(1, seen.Count - capacity + 1)))
            seen.TryRemove(item.Key, out _);
    }
}

/// <summary>Tracks the entities currently visible in the foreground across desktop and mobile pages.</summary>
public sealed class NotificationViewState(IAppLifecycleState lifecycle)
{
    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<string>> scopes = new(StringComparer.Ordinal);

    public bool IsForeground => lifecycle.IsForeground;

    public void SetVisibleEntities(string scope, IEnumerable<string> entityIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (gate)
        {
            var values = entityIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (values.Count == 0) scopes.Remove(scope);
            else scopes[scope] = values;
        }
    }

    public void Clear(string scope)
    {
        lock (gate) scopes.Remove(scope);
    }

    public bool IsEntityVisible(string entityId)
    {
        if (!lifecycle.IsForeground || string.IsNullOrWhiteSpace(entityId)) return false;
        lock (gate) return scopes.Values.Any(values => values.Contains(entityId));
    }
}

/// <summary>Coalesces native wake callbacks and records whether the OS already showed a generic alert.</summary>
public sealed class NotificationWakeSession
{
    private readonly object gate = new();
    private readonly Dictionary<string, WakeState> active = new(StringComparer.Ordinal);

    public bool HasVisibleRemoteAlert
    {
        get { lock (gate) return active.Values.Any(static state => state.VisibleLeases > 0); }
    }

    public IDisposable Begin(string? wakeId, bool visibleRemoteAlert)
    {
        var id = string.IsNullOrWhiteSpace(wakeId) ? Guid.NewGuid().ToString("n") : wakeId;
        lock (gate)
        {
            active[id] = active.TryGetValue(id, out var current)
                ? new WakeState(current.Leases + 1, current.VisibleLeases + (visibleRemoteAlert ? 1 : 0))
                : new WakeState(1, visibleRemoteAlert ? 1 : 0);
        }
        return new WakeLease(this, id, visibleRemoteAlert);
    }

    private void End(string id, bool visibleRemoteAlert)
    {
        lock (gate)
        {
            if (!active.TryGetValue(id, out var current)) return;
            if (current.Leases == 1) active.Remove(id);
            else active[id] = new WakeState(
                current.Leases - 1,
                current.VisibleLeases - (visibleRemoteAlert ? 1 : 0));
        }
    }

    private readonly record struct WakeState(int Leases, int VisibleLeases);

    private sealed class WakeLease(NotificationWakeSession owner, string id, bool visibleRemoteAlert) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.End(id, visibleRemoteAlert);
        }
    }
}
internal sealed class NotificationOperationGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => operation(ct), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => operation(ct), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
