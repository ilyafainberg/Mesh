using System.Text.Json;
using Mesh.Shared;

namespace Mesh.App.Services;

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
            && IsEnabled(capabilities, "onlineWake");
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            return await transport.SynchronizePendingAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return OnlineReplicationWakeResult.Failed(ct.IsCancellationRequested ? "cancelled" : "timeout");
        }
        catch (Exception ex)
        {
            return OnlineReplicationWakeResult.Failed(ex.GetType().Name);
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
