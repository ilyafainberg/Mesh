using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mesh.Shared;

namespace Mesh.App.Services;

internal enum ConnectionPurpose
{
    Foreground,
    BackgroundWake
}

internal static class ConnectionPurposePolicy
{
    public static bool AllowsConnection(ConnectionPurpose purpose, bool isForeground)
        => purpose == ConnectionPurpose.BackgroundWake || isForeground;
}

internal static class WakeQuiescencePolicy
{
    public static bool IsComplete(
        DateTimeOffset now,
        DateTimeOffset? lastActivity,
        DateTimeOffset sessionStartedAt,
        TimeSpan idlePeriod,
        bool hasImmediatelyDeliverableWork)
    {
        if (idlePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idlePeriod));
        if (hasImmediatelyDeliverableWork) return false;

        var activityAt = lastActivity ?? sessionStartedAt;
        return now - activityAt >= idlePeriod;
    }
}

internal sealed class ReplicationConnectionLease : IAsyncDisposable
{
    private Func<ValueTask>? release;

    internal ReplicationConnectionLease(
        ConnectionPurpose purpose,
        bool isConnected,
        Func<ValueTask>? release = null)
    {
        Purpose = purpose;
        IsConnected = isConnected;
        this.release = release;
    }

    public ConnectionPurpose Purpose { get; }
    public bool IsConnected { get; }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref release, null)?.Invoke() ?? ValueTask.CompletedTask;
}

public enum ReplicationPhase
{
    UpToDate,
    WaitingForPeer,
    Connecting,
    Synchronizing,
    Bootstrapping,
    DeferredByOperatingSystem,
    AuthenticationFailed,
    Failed
}

public sealed record ReplicationStatus(
    ReplicationPhase Phase,
    int PendingEvents,
    string? PeerDeviceId,
    DateTimeOffset? LastSuccessfulSync,
    string? Reason);

public static class ReplicationStatusDisplayPolicy
{
    public static bool ShouldShow(ReplicationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Phase is ReplicationPhase.AuthenticationFailed or ReplicationPhase.Failed;
    }
}

public static class ReplicationStatusFormatter
{
    public static string Format(ReplicationStatus status, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Phase switch
        {
            ReplicationPhase.AuthenticationFailed => "Synchronization authentication failed",
            ReplicationPhase.Failed => status.LastSuccessfulSync is { } last
                ? $"Last synced {Relative(last, now ?? DateTimeOffset.Now)}"
                : "Synchronization failed",
            _ => string.Empty
        };
    }

    private static string Relative(DateTimeOffset value, DateTimeOffset now)
    {
        var elapsed = now - value.ToLocalTime();
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} minutes ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} hours ago";
        return value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    }
}

internal enum AndroidReplicationWakePayloadKind
{
    None,
    Sync,
    UnsupportedMeshPayload
}

internal sealed record AndroidReplicationWakePayload(string? WakeId, bool ShowAlert);

internal static class AndroidReplicationWakePolicy
{
    public const string UniqueWorkName = "mesh-protocol9-sync";

    public static AndroidReplicationWakePayloadKind Classify(
        IEnumerable<KeyValuePair<string, string>>? data)
        => TryParse(data, out _) ? AndroidReplicationWakePayloadKind.Sync : ClassifyInvalid(data);

    public static bool TryParse(
        IEnumerable<KeyValuePair<string, string>>? data,
        out AndroidReplicationWakePayload payload)
    {
        payload = new AndroidReplicationWakePayload(null, false);
        if (data is null) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in data) values[item.Key] = item.Value;

        if (values.TryGetValue("mesh_type", out var type))
        {
            if (!string.Equals(type, "sync", StringComparison.Ordinal)
                || !(HasCurrentVersion(values, "mesh_version")
                     || HasCurrentVersion(values, "mesh_v")))
                return false;
            values.TryGetValue("wake_id", out var wakeId);
            payload = new AndroidReplicationWakePayload(
                string.IsNullOrWhiteSpace(wakeId) ? null : wakeId,
                values.TryGetValue("show_alert", out var show)
                && (show == "1" || bool.TryParse(show, out var parsed) && parsed));
            return true;
        }

        if (values.TryGetValue("mesh.type", out var flatType))
        {
            if (!string.Equals(flatType, "sync", StringComparison.Ordinal)
                || !HasCurrentVersion(values, "mesh.v"))
                return false;
            values.TryGetValue("wake_id", out var wakeId);
            payload = new AndroidReplicationWakePayload(wakeId, false);
            return true;
        }

        if (!values.TryGetValue("mesh", out var raw) || string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var nestedType)
                || !string.Equals(nestedType.GetString(), "sync", StringComparison.Ordinal)
                || !root.TryGetProperty("v", out var version)
                || !version.TryGetInt32(out var parsedVersion)
                || parsedVersion != MeshProtocol.Version)
                return false;
            payload = new AndroidReplicationWakePayload(null, false);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsSyncPayload(IEnumerable<KeyValuePair<string, string>>? data)
        => TryParse(data, out _);

    private static AndroidReplicationWakePayloadKind ClassifyInvalid(
        IEnumerable<KeyValuePair<string, string>>? data)
    {
        if (data is null) return AndroidReplicationWakePayloadKind.None;
        var keys = data.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        return keys.Contains("mesh_type")
               || keys.Contains("mesh.type")
               || keys.Contains("mesh")
            ? AndroidReplicationWakePayloadKind.UnsupportedMeshPayload
            : AndroidReplicationWakePayloadKind.None;
    }

    private static bool HasCurrentVersion(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var raw)
           && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
           && version == MeshProtocol.Version;
}

internal static class ReplicationDiagnostics{
    public static void Record(string eventName, params (string Key, object? Value)[] fields)
    {
        var message = new StringBuilder(eventName);
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null) continue;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 160) text = text[..160];
            message.Append(';').Append(key).Append('=').Append(text);
        }
        RuntimeDiagnostics.Current?.RecordEvent("replication", message.ToString());
    }
}

public enum OnlineReplicationWakeOutcome
{
    NewData,
    NoData,
    Failed
}

public sealed record OnlineReplicationWakeResult(
    OnlineReplicationWakeOutcome Outcome,
    int ProcessedEnvelopes = 0,
    int DeferredEnvelopes = 0,
    string? Error = null)
{
    public static OnlineReplicationWakeResult NewData(int processed, int deferred = 0)
        => new(OnlineReplicationWakeOutcome.NewData, processed, deferred);

    public static OnlineReplicationWakeResult NoData(int deferred = 0)
        => new(OnlineReplicationWakeOutcome.NoData, 0, deferred);

    public static OnlineReplicationWakeResult Failed(string error, int processed = 0, int deferred = 0)
        => new(OnlineReplicationWakeOutcome.Failed, processed, deferred, error);
}

internal static class OnlineReplicationWakeResultPolicy
{
    public static OnlineReplicationWakeResult FromProgress(
        long committedBefore,
        long committedAfter,
        int deferred)
    {
        if (committedBefore < 0) throw new ArgumentOutOfRangeException(nameof(committedBefore));
        if (committedAfter < 0) throw new ArgumentOutOfRangeException(nameof(committedAfter));
        if (deferred < 0) throw new ArgumentOutOfRangeException(nameof(deferred));

        var delta = committedAfter > committedBefore
            ? committedAfter - committedBefore
            : 0;
        var processed = (int)Math.Min(int.MaxValue, delta);
        return processed > 0
            ? OnlineReplicationWakeResult.NewData(processed, deferred)
            : OnlineReplicationWakeResult.NoData(deferred);
    }
}

public interface IOnlineReplicationWakeTransport
{
    Task<OnlineReplicationWakeResult> SynchronizePendingAsync(CancellationToken ct = default);
}

internal static class OnlineReplicationWakeCapabilityPolicy
{
    public static bool IsSupported(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object)
            return false;

        if (!capabilities.TryGetProperty("protocolVersion", out var protocolVersion)
            || !protocolVersion.TryGetInt32(out var version)
            || version != MeshProtocol.Version)
            return false;

        return IsEnabled(capabilities, "onlineReplication")
            && IsEnabled(capabilities, "onlineWake")
            && IsEnabled(capabilities, "contentlessPush");
    }

    private static bool IsEnabled(JsonElement capabilities, string name)
        => capabilities.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;
}

internal static class ReplicationHandshakeCoordinator
{
    public static async Task<ReplicationHandshakeResult> RunAsync(
        Func<Task<ReplicationPollOutcome>> poll,
        Func<Task> discoverSnapshots,
        Func<bool> shouldContinue,
        Action<Exception> reportFailure,
        int discoveryAttempts = 3,
        Func<int, Task>? retryDelay = null)
    {
        ReplicationPollOutcome pollResult;
        try
        {
            pollResult = await poll().ConfigureAwait(false);
        }

        catch (Exception ex)
        {
            reportFailure(ex);
            return ReplicationHandshakeResult.Failed("online_poll_exception", ex);
        }

        if (!pollResult.Succeeded)
        {
            var failure = new InvalidOperationException(
                pollResult.Error ?? "online_poll_rejected");
            reportFailure(failure);
            return ReplicationHandshakeResult.Failed(
                pollResult.Error ?? "online_poll_rejected",
                failure);
        }

        for (var attempt = 0; attempt < discoveryAttempts && shouldContinue(); attempt++)
        {
            try
            {
                await discoverSnapshots().ConfigureAwait(false);
                return ReplicationHandshakeResult.Completed();
            }
            catch (Exception ex)
            {
                reportFailure(ex);
                if (attempt + 1 >= discoveryAttempts || !shouldContinue())
                    return ReplicationHandshakeResult.Failed("presence_poll_failed", ex);
                if (retryDelay is not null)
                    await retryDelay(attempt + 1).ConfigureAwait(false);
                else
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt)).ConfigureAwait(false);
            }
        }
        return ReplicationHandshakeResult.Completed();
    }
}

internal static class ReplicationPresenceResponsePolicy
{
    public static Task Start(Func<Task> response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Task.Run(response, CancellationToken.None);
    }
}

internal sealed record ReplicationPollOutcome(
    bool Succeeded,
    int ProcessedEnvelopes,
    string? Error = null)
{
    public static ReplicationPollOutcome Completed(int processed)
        => new(true, processed);

    public static ReplicationPollOutcome Failed(string error, int processed = 0)
        => new(false, processed, error);
}

internal sealed record ReplicationHandshakeResult(
    bool Succeeded,
    string? Error = null,
    Exception? Exception = null)
{
    public static ReplicationHandshakeResult Completed() => new(true);

    public static ReplicationHandshakeResult Failed(string error, Exception exception)
        => new(false, error, exception);
}

/// <summary>Coalesces native wake sources onto one bounded relay synchronization session.</summary>
public sealed class OnlineReplicationWakeCoordinator(IOnlineReplicationWakeTransport transport)
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(25);

    private readonly object gate = new();
    private Task<OnlineReplicationWakeResult>? active;

    public Task<OnlineReplicationWakeResult> SynchronizePendingAsync(
        TimeSpan? budget = null,
        CancellationToken ct = default)
    {
        Task<OnlineReplicationWakeResult> session;
        ReplicationDiagnostics.Record("wake.received", ("budget_ms", (long)(budget ?? DefaultBudget).TotalMilliseconds));
        lock (gate)
        {
            if (active is { IsCompleted: false })
            {
                session = active;
            }
            else
            {
                session = RunAsync(budget ?? DefaultBudget, ct);
                active = session;
                _ = session.ContinueWith(
                    completed =>
                    {
                        lock (gate)
                            if (ReferenceEquals(active, completed)) active = null;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        return session;
    }

    private async Task<OnlineReplicationWakeResult> RunAsync(TimeSpan budget, CancellationToken ct)
    {
        if (budget <= TimeSpan.Zero)
            return OnlineReplicationWakeResult.Failed("invalid_budget");

        var started = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            var result = await transport.SynchronizePendingAsync(timeout.Token).ConfigureAwait(false);
            ReplicationDiagnostics.Record(
                "wake.completed",
                ("duration_ms", started.ElapsedMilliseconds),
                ("processed", result.ProcessedEnvelopes),
                ("deferred", result.DeferredEnvelopes),
                ("outcome", result.Outcome));
            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var error = ct.IsCancellationRequested ? "cancelled" : "timeout";
            ReplicationDiagnostics.Record(
                "wake.timed_out",
                ("duration_ms", started.ElapsedMilliseconds),
                ("error_code", error));
            return OnlineReplicationWakeResult.Failed(error);
        }
        catch (Exception ex)
        {
            var error = ex.GetType().Name;
            ReplicationDiagnostics.Record(
                "wake.completed",
                ("duration_ms", started.ElapsedMilliseconds),
                ("error_code", error));
            return OnlineReplicationWakeResult.Failed(error);
        }
    }
}

/// <summary>Native callbacks cannot use constructor injection, so they enter through this registered bridge.</summary>
public static class OnlineReplicationWakeBridge
{
    private static OnlineReplicationWakeCoordinator? coordinator;

    public static void Register(OnlineReplicationWakeCoordinator value)
        => Volatile.Write(ref coordinator, value ?? throw new ArgumentNullException(nameof(value)));

    public static Task<OnlineReplicationWakeResult> SynchronizePendingAsync(
        TimeSpan? budget = null,
        CancellationToken ct = default)
        => Volatile.Read(ref coordinator)?.SynchronizePendingAsync(budget, ct)
           ?? Task.FromResult(OnlineReplicationWakeResult.Failed("coordinator_unavailable"));
}

public enum InboundProcessingMode
{
    Foreground,
    Background
}

internal enum InboundDisposition
{
    Processed,
    Retry,
    PermanentReject,
    Defer
}

internal static class InboundAcknowledgementPolicy
{
    public static bool ShouldAcknowledge(InboundDisposition disposition)
        => disposition is InboundDisposition.Processed or InboundDisposition.PermanentReject;
}

internal static class ReplicationPollDispositionPolicy
{
    public static ReplicationPollOutcome StopResult(
        InboundDisposition disposition,
        int processed)
        => disposition switch
        {
            InboundDisposition.Retry => ReplicationPollOutcome.Failed(
                "processing_retry",
                processed),
            InboundDisposition.Defer => ReplicationPollOutcome.Completed(processed),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition))
        };
}

internal static class InboundAttachmentFailurePolicy
{
    public static bool ShouldRetry(string error)
        => error is "attachment assembler is disposed"
            or "attachment assembler is full"
            or "attachment storage is unavailable"
            or "duplicate or conflicting attachment storage";
}

public static class OnlineReplicationWakeInboundPolicy
{
    public static bool RequiresForeground(string kind) => kind is
        MeshKinds.Chat
        or MeshKinds.AgentRequest
        or MeshKinds.AtomicAgentRequest
        or MeshKinds.ServiceRequest
        or MeshKinds.TopicRunRequest
        or MeshKinds.TopicRunCancel
        or MeshKinds.AttachmentChunk
        or MeshKinds.TopicAttachmentChunk;
}

public static class ReplicationUnreadPolicy
{
    public static bool ShouldMarkConversationUnread(string? role)
        => string.Equals(role, "user", StringComparison.Ordinal);
}
