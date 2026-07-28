using System.Text.Json;
using Mesh.Shared;

namespace Mesh.App.Services;

public enum BackgroundSyncOutcome
{
    NewData,
    NoData,
    Failed
}

public sealed record BackgroundSyncResult(
    BackgroundSyncOutcome Outcome,
    int ProcessedEnvelopes = 0,
    int DeferredEnvelopes = 0,
    string? Error = null)
{
    public static BackgroundSyncResult NewData(int processed, int deferred = 0)
        => new(BackgroundSyncOutcome.NewData, processed, deferred);

    public static BackgroundSyncResult NoData(int deferred = 0)
        => new(BackgroundSyncOutcome.NoData, 0, deferred);

    public static BackgroundSyncResult Failed(string error, int processed = 0, int deferred = 0)
        => new(BackgroundSyncOutcome.Failed, processed, deferred, error);
}

public interface IBackgroundSyncTransport
{
    Task<BackgroundSyncResult> SynchronizePendingAsync(CancellationToken ct = default);
}

internal static class BackgroundSyncCapabilityPolicy
{
    public static bool IsSupported(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object)
            return false;

        return IsEnabled(capabilities, "durableDelivery")
            && IsEnabled(capabilities, "backgroundSync");
    }

    private static bool IsEnabled(JsonElement capabilities, string name)
        => capabilities.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;
}

/// <summary>Coalesces native wake sources onto one bounded relay synchronization session.</summary>
public sealed class BackgroundSyncCoordinator(IBackgroundSyncTransport transport)
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(25);

    private readonly object gate = new();
    private Task<BackgroundSyncResult>? active;

    public Task<BackgroundSyncResult> SynchronizePendingAsync(
        TimeSpan? budget = null,
        CancellationToken ct = default)
    {
        Task<BackgroundSyncResult> session;
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

    private async Task<BackgroundSyncResult> RunAsync(TimeSpan budget, CancellationToken ct)
    {
        if (budget <= TimeSpan.Zero)
            return BackgroundSyncResult.Failed("invalid_budget");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            return await transport.SynchronizePendingAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return BackgroundSyncResult.Failed(ct.IsCancellationRequested ? "cancelled" : "timeout");
        }
        catch (Exception ex)
        {
            return BackgroundSyncResult.Failed(ex.GetType().Name);
        }
    }
}

/// <summary>Native callbacks cannot use constructor injection, so they enter through this registered bridge.</summary>
public static class BackgroundSyncBridge
{
    private static BackgroundSyncCoordinator? coordinator;

    public static void Register(BackgroundSyncCoordinator value)
        => Volatile.Write(ref coordinator, value ?? throw new ArgumentNullException(nameof(value)));

    public static Task<BackgroundSyncResult> SynchronizePendingAsync(
        TimeSpan? budget = null,
        CancellationToken ct = default)
        => Volatile.Read(ref coordinator)?.SynchronizePendingAsync(budget, ct)
           ?? Task.FromResult(BackgroundSyncResult.Failed("coordinator_unavailable"));
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

internal static class InboundAttachmentFailurePolicy
{
    public static bool ShouldRetry(string error)
        => error is "attachment inbox is disposed"
            or "attachment inbox is full"
            or "attachment storage is unavailable"
            or "duplicate or conflicting attachment storage";
}

public static class BackgroundInboundPolicy
{
    public static bool RequiresForeground(string kind)
        => BackgroundSyncProtocol.RequiresForeground(kind);
}

public static class DeviceSyncUnreadPolicy
{
    public static bool ShouldMarkConversationUnread(string? role)
        => string.Equals(role, "user", StringComparison.Ordinal);
}
