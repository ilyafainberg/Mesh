using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed class OnlineDeliveryTargetScope
{
    private readonly HashSet<string>? targets;

    public OnlineDeliveryTargetScope(IReadOnlyCollection<string>? targetDeviceIds)
        => targets = targetDeviceIds is null
            ? null
            : new HashSet<string>(
                targetDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);

    public bool Includes(string targetDeviceId)
        => targets is null || targets.Contains(targetDeviceId);
}

/// <summary>Transport-neutral input for one local or device-bound topic turn.</summary>
public sealed record TopicTurnDraft(
    string RunId,
    string ThreadId,
    string TriggerLineId,
    string TriggerHandle,
    string Prompt,
    DateTimeOffset TriggerAt,
    TopicTurnMode TurnMode,
    string? TargetDeviceId = null,
    string? WidgetId = null,
    string? WidgetContext = null,
    IReadOnlyList<ChatAttachment>? Attachments = null,
    string? TriggerOperationId = null);

/// <summary>Result of accepting or rejecting a topic dispatch.</summary>
public sealed record TopicDispatchResult(
    bool Accepted,
    string RunId,
    string Code,
    string? Error = null,
    bool Durable = false)
{
    public static TopicDispatchResult Ok(
        string runId,
        string code = "accepted",
        bool durable = false)
        => new(true, runId, code, Durable: durable);

    public static TopicDispatchResult Reject(
        string code,
        string runId = "",
        string? error = null,
        bool durable = false)
        => new(false, runId, code, error, durable);
}

public enum TopicRunBeginMode
{
    Local,
    Remote
}

internal enum TopicProjectionCheckpoint
{
    AfterCommitBeforeProjection,
    AfterProjection
}

public sealed record TopicRunBeginCommand(
    TopicTurnDraft Draft,
    ExecutionDevice Target,
    TopicRunBeginMode Mode,
    TopicRunUpdatePayload InitialProjection,
    TopicRunRequestPayload? Request = null,
    IReadOnlyList<ChatAttachment>? Attachments = null);

public sealed record TopicRunBeginResult(
    bool DurableCommitted,
    bool Created,
    string Code,
    MeshDb.TopicOutboxItem? Outbox = null,
    string? AuthoritativeRunId = null,
    string? TriggerId = null,
    TopicTurnDraft? AuthoritativeDraft = null,
    bool ProjectionApplied = false,
    string? ProjectionError = null)
{
    public bool Committed => DurableCommitted;
    public bool ProjectionDeferred => DurableCommitted && !ProjectionApplied;
}

public enum TopicRunTriggerLookupKind
{
    Found,
    NotFound,
    Conflict,
    Corrupt,
    Unavailable,
    QueryFailed
}

public sealed record TopicRunTriggerLookupResult(
    TopicRunTriggerLookupKind Kind,
    string? RunId = null,
    string? TriggerLineId = null,
    string? OutboxId = null,
    bool Terminal = false,
    string? Detail = null,
    string? Reason = null,
    string? AccountId = null,
    string? DatabaseIdentity = null,
    long DatabaseGeneration = 0,
    long ObservationVersion = 0,
    DateTimeOffset ObservedAt = default);

public static class TopicRunTriggerIdentity
{
    public static string For(
        string threadId,
        string triggerLineId,
        string? triggerOperationId = null)
        => "topic.trigger:" + StableHash(
            string.IsNullOrWhiteSpace(triggerOperationId)
                ? string.Join("\0", "topic-trigger-v1", threadId, triggerLineId)
                : string.Join("\0", "topic-operation-v1", triggerOperationId));

    public static string PayloadHash(TopicRunBeginCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, command.Mode.ToString());
        Append(hash, command.Draft.ThreadId);
        Append(hash, command.Draft.TriggerLineId);
        Append(hash, command.Draft.TriggerHandle);
        Append(hash, command.Draft.Prompt);
        Append(hash, command.Draft.TriggerAt.ToUniversalTime().ToString("O"));
        Append(hash, ((int)command.Draft.TurnMode).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, command.Target.DeviceId);
        Append(hash, command.Draft.WidgetId);
        Append(hash, command.Draft.WidgetContext);
        foreach (var attachment in command.Attachments ?? command.Draft.Attachments ?? [])
        {
            Append(hash, attachment.Name);
            Append(hash, attachment.MimeType);
            hash.AppendData(BitConverter.GetBytes(attachment.Data.LongLength));
            hash.AppendData(attachment.Data);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string StableHash(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16))
            .ToLowerInvariant();

    private static void Append(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}

public interface ITopicDurabilityStore : ITopicControlOutboxStore
{
    MeshDb.InboundTopicRunItem? GetInboundTopicRun(string runId);
    bool TryAcceptInboundTopicRunAndQueueAcceptance(
        MeshDb.InboundTopicRunItem item,
        MeshDb.DeviceEnvelopeOutboxItem acceptance);
    MeshDb.ReceivedTopicControlItem? GetReceivedTopicControl(string envelopeId);
    RemoteTopicUpdatePersistenceResult TryApplyReceivedTopicControl(
        TopicRunUpdatePayload update,
        string sourceDeviceId,
        MeshDb.ReceivedTopicControlItem control);
    RemoteTopicUpdatePersistenceResult ApplyRemoteTopicUpdate(
        TopicRunUpdatePayload update,
        string sourceDeviceId);
    TopicControlReceiptPersistenceResult ApplyTopicControlReceipt(
        TopicRunUpdatePayload receipt,
        string sourceDeviceId,
        string acknowledgedEnvelopeId);
    bool SetInboundTopicRunTerminalAndQueue(
        string runId,
        string runState,
        TopicRunUpdatePayload terminalUpdate,
        MeshDb.DeviceEnvelopeOutboxItem outbox);
}

public interface ITopicRequestOutboxStore
{
    MeshDb.TopicOutboxItem? GetTopicOutbox(string runId);
    bool SaveTopicOutbox(MeshDb.TopicOutboxItem item);
    TopicSendOutcomePersistenceResult ApplyTopicRequestSendOutcome(
        string runId,
        string outboxState,
        string? error = null);
}

public interface ITopicCorrelationMaintenanceStore
{
    int PruneTopicRunCorrelations(DateTimeOffset localNow);
}

/// <summary>
/// Single-flight delayed retry loop used by the connected client to drain durable topic work.
/// Repeated wake requests coalesce into one worker, preventing retry amplification.
/// </summary>
public sealed class TopicDeliveryRetryLoop(
    TimeProvider timeProvider,
    TimeSpan interval,
    Func<CancellationToken, Task> attempt,
    Func<bool> shouldContinue,
    TimeSpan? maximumInterval = null)
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly TimeSpan maxInterval = maximumInterval
                                            ?? TimeSpan.FromSeconds(30);
    private int scheduled;
    private int attempts;
    private int workerStarts;

    public int AttemptCount => Volatile.Read(ref attempts);
    public int WorkerStartCount => Volatile.Read(ref workerStarts);

    public void Schedule()
    {
        if (lifetime.IsCancellationRequested
            || Interlocked.Exchange(ref scheduled, 1) == 1)
            return;
        _ = Task.Run(RunAsync);
    }

    public void Wake()
    {
        if (lifetime.IsCancellationRequested) return;
        Schedule();
        if (wake.CurrentCount == 0)
            try { wake.Release(); }
            catch (SemaphoreFullException) { }
    }

    public void Stop() => lifetime.Cancel();

    private async Task RunAsync()
    {
        Interlocked.Increment(ref workerStarts);
        var delay = interval;
        try
        {
            while (!lifetime.IsCancellationRequested && shouldContinue())
            {
                using (var delayCancellation =
                       CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    var timer = Task.Delay(delay, timeProvider, delayCancellation.Token);
                    var signaled = wake.WaitAsync(delayCancellation.Token);
                    var completed = await Task.WhenAny(timer, signaled).ConfigureAwait(false);
                    delayCancellation.Cancel();
                    try { await completed.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                }
                if (!shouldContinue()) break;
                Interlocked.Increment(ref attempts);
                try
                {
                    await attempt(lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Durable work remains eligible. A transient send failure only advances the
                    // bounded backoff; a later wake or timer retries the same persisted envelope.
                }
                delay = TimeSpan.FromTicks(Math.Min(
                    maxInterval.Ticks,
                    Math.Max(interval.Ticks, delay.Ticks * 2)));
            }
        }
        finally
        {
            Interlocked.Exchange(ref scheduled, 0);
            if (!lifetime.IsCancellationRequested && shouldContinue())
                Schedule();
        }
    }
}

public sealed class TopicCorrelationMaintenance
{
    private readonly ITopicCorrelationMaintenanceStore store;
    private readonly TimeProvider timeProvider;

    public TopicCorrelationMaintenance(
        ITopicCorrelationMaintenanceStore store,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public int PruneTerminalCorrelations()
        => store.PruneTopicRunCorrelations(timeProvider.GetUtcNow());
}

public sealed class TopicRequestOutboxHandler
{
    private readonly ITopicRequestOutboxStore store;
    private readonly TimeProvider timeProvider;

    public TopicRequestOutboxHandler(
        ITopicRequestOutboxStore store,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public MeshDb.TopicOutboxItem Queue(
        string targetDeviceId,
        TopicRunRequestPayload request,
        IReadOnlyList<ChatAttachment> attachments)
    {
        var existing = store.GetTopicOutbox(request.RunId);
        if (existing is not null)
        {
            if (!string.Equals(existing.ThreadId, request.ThreadId, StringComparison.Ordinal)
                || !string.Equals(
                    existing.TriggerLineId, request.TriggerLineId, StringComparison.Ordinal)
                || !string.Equals(
                    existing.TargetDeviceId, targetDeviceId, StringComparison.Ordinal))
                throw new InvalidOperationException("run_id_conflict");
            return existing;
        }

        var now = timeProvider.GetUtcNow();
        var item = new MeshDb.TopicOutboxItem(
            request.RunId,
            request.ThreadId,
            request.TriggerLineId,
            targetDeviceId,
            request,
            attachments.Select(CloneAttachment).ToArray(),
            TopicOutboxStates.Pending,
            now,
            now);
        if (!store.SaveTopicOutbox(item))
            throw new InvalidOperationException("local_persistence_failed");
        return store.GetTopicOutbox(request.RunId)
               ?? throw new InvalidOperationException("local_persistence_failed");
    }

    public TopicSendOutcomePersistenceResult ApplySendOutcome(
        string runId,
        string state,
        string? error = null)
        => store.ApplyTopicRequestSendOutcome(runId, state, error);

    private static ChatAttachment CloneAttachment(ChatAttachment attachment)
        => new(attachment.Name, attachment.MimeType, attachment.Data.ToArray());
}

public sealed record TopicRequestDeliveryResult(
    MeshSendResult? TransportResult,
    TopicSendOutcomePersistenceResult? PersistenceResult);

public sealed class TopicRequestOutboxDelivery
{
    private readonly TopicRequestOutboxHandler outbox;
    private readonly ITopicEnvelopeTransport transport;
    private readonly TimeProvider timeProvider;

    public TopicRequestOutboxDelivery(
        TopicRequestOutboxHandler outbox,
        ITopicEnvelopeTransport transport,
        TimeProvider timeProvider)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<TopicRequestDeliveryResult> TrySendAsync(
        MeshDb.TopicOutboxItem item,
        CancellationToken cancellationToken)
    {
        if (!TopicTransportPolicy.ShouldAttemptRequestDelivery(
                item.State, item.UpdatedAt, timeProvider.GetUtcNow()))
            return new TopicRequestDeliveryResult(null, null);
        var result = await transport.SendAsync(
            item.TargetDeviceId,
            MeshKinds.TopicRunRequest,
            TopicRunProtocol.RequestBody(item.Request),
            item.RunId,
            null,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
            return new TopicRequestDeliveryResult(null, null);
        var state = result.Accepted
            ? TopicOutboxStates.RelayQueued
            : TopicTransportPolicy.IsPermanentRejection(result.Code)
                ? TopicOutboxStates.Failed
                : TopicOutboxStates.Pending;
        return new TopicRequestDeliveryResult(
            result,
            outbox.ApplySendOutcome(item.RunId, state, result.Accepted ? null : result.Code));
    }
}

public sealed class TopicDurabilityHandler
{
    private readonly ITopicDurabilityStore store;
    private readonly TimeProvider timeProvider;

    public TopicDurabilityHandler(ITopicDurabilityStore store, TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public MeshDb.InboundTopicRunItem AcceptRequest(
        TopicRunRequestPayload request,
        string sourceDeviceId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDeviceId);
        var record = store.GetInboundTopicRun(request.RunId);
        if (record is null)
        {
            var now = timeProvider.GetUtcNow();
            var candidate = new MeshDb.InboundTopicRunItem(
                request.RunId,
                sourceDeviceId,
                request,
                InboundTopicRunStates.Accepted,
                now,
                now);
            var accepted = TopicAcceptancePolicy.Create(request, now);
            var body = TopicRunProtocol.UpdateBody(accepted);
            var outbox = new MeshDb.DeviceEnvelopeOutboxItem(
                TopicControlProtocol.EnvelopeId(
                    TopicControlProtocol.ControlPurpose(accepted), request.RunId),
                sourceDeviceId,
                MeshKinds.TopicRunUpdate,
                body,
                null,
                now);
            record = store.TryAcceptInboundTopicRunAndQueueAcceptance(candidate, outbox)
                ? candidate
                : store.GetInboundTopicRun(request.RunId)
                  ?? throw new InvalidOperationException(
                      "The inbound topic request could not be persisted.");
        }
        if (!string.Equals(record.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
            || !string.Equals(
                TopicRunProtocol.RequestBody(record.Request),
                TopicRunProtocol.RequestBody(request),
                StringComparison.Ordinal))
            throw new InvalidOperationException("topic_request_identity_conflict");
        return record;
    }

    public RemoteTopicUpdatePersistenceResult HandleControl(
        TopicRunUpdatePayload update,
        string sourceDeviceId,
        string envelopeId)
    {
        ArgumentNullException.ThrowIfNull(update);
        var plaintext = TopicRunProtocol.UpdateBody(update);
        var existing = store.GetReceivedTopicControl(envelopeId);
        if (existing is not null)
            return ControlMatches(existing, sourceDeviceId, update, plaintext)
                ? RemoteTopicUpdatePersistenceResult.Duplicate
                : RemoteTopicUpdatePersistenceResult.IdentityConflict;
        var control = new MeshDb.ReceivedTopicControlItem(
            envelopeId,
            sourceDeviceId,
            update.RunId,
            update.ThreadId,
            TopicControlProtocol.ControlPurpose(update),
            plaintext,
            timeProvider.GetUtcNow());
        return store.TryApplyReceivedTopicControl(update, sourceDeviceId, control);
    }

    public RemoteTopicUpdatePersistenceResult HandleUpdate(
        TopicRunUpdatePayload update,
        string sourceDeviceId,
        string envelopeId)
        => TopicControlProtocol.RequiresPersistenceReceipt(update)
            ? HandleControl(update, sourceDeviceId, envelopeId)
            : store.ApplyRemoteTopicUpdate(update, sourceDeviceId);

    public TopicControlReceiptPersistenceResult HandleReceipt(
        TopicRunUpdatePayload receipt,
        string sourceDeviceId)
        => store.ApplyTopicControlReceipt(
            receipt,
            sourceDeviceId,
            TopicControlProtocol.EnvelopeId(
                TopicControlProtocol.AcknowledgedPurpose(receipt), receipt.RunId));

    public TopicRunUpdatePayload CompleteRun(
        string runId,
        string runState,
        TopicRunUpdatePayload terminalUpdate,
        string targetDeviceId)
    {
        var body = TopicRunProtocol.UpdateBody(terminalUpdate);
        var outbox = new MeshDb.DeviceEnvelopeOutboxItem(
            TopicControlProtocol.EnvelopeId(
                TopicControlProtocol.ControlPurpose(terminalUpdate), runId),
            targetDeviceId,
            MeshKinds.TopicRunUpdate,
            body,
            PushHintProtocol.ForTopicRunPhase(terminalUpdate.Phase),
            timeProvider.GetUtcNow());
        if (!store.SetInboundTopicRunTerminalAndQueue(
                runId, runState, terminalUpdate, outbox))
            throw new InvalidOperationException(
                "The terminal topic run state could not be persisted.");
        var persisted = store.GetInboundTopicRun(runId);
        if (persisted is null
            || !TopicRunProtocol.TryParseUpdate(
                persisted.TerminalUpdateJson, out var winner))
            throw new InvalidOperationException(
                "The persisted terminal topic update could not be read.");
        return winner;
    }

    private static bool ControlMatches(
        MeshDb.ReceivedTopicControlItem existing,
        string sourceDeviceId,
        TopicRunUpdatePayload update,
        string plaintext)
        => string.Equals(existing.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
           && string.Equals(existing.RunId, update.RunId, StringComparison.Ordinal)
           && string.Equals(existing.ThreadId, update.ThreadId, StringComparison.Ordinal)
           && string.Equals(
               existing.ControlKind,
               TopicControlProtocol.ControlPurpose(update),
               StringComparison.Ordinal)
           && string.Equals(existing.UpdateJson, plaintext, StringComparison.Ordinal);
}

public interface ITopicControlOutboxStore
{
    bool SetDeviceEnvelopeOutboxAttempt(
        string envelopeId,
        string outboxState,
        DateTimeOffset attemptedAt,
        string? error = null);

    bool DeleteDeviceEnvelopeOutbox(string envelopeId);

    bool TryRecoverDeadLetteredDeviceEnvelope(
        string envelopeId,
        DateTimeOffset recoveredAt,
        int maximumRecoveryCount);
}

public enum TopicControlRecoveryKind
{
    Recovered,
    NotDeadLettered,
    NotReceiptGated,
    RecoveryWindowExpired,
    RecoveryLimitReached,
    ConcurrentStateChange
}

public sealed record TopicControlRecoveryResult(
    TopicControlRecoveryKind Kind,
    string EnvelopeId,
    int RecoveryCount)
{
    public bool Recovered => Kind == TopicControlRecoveryKind.Recovered;
}

public sealed record TopicControlRecoveryStatus(
    string EnvelopeId,
    string RunId,
    string ControlKind,
    string State,
    int RecoveryCount,
    bool RecoveryEligible,
    string? StatusCode);

public sealed record TopicControlRecoveryBatchResult(
    int Recovered,
    int Deferred,
    IReadOnlyList<TopicControlRecoveryResult> Results);

public sealed class TopicControlOutboxRecovery
{
    private readonly ITopicControlOutboxStore store;
    private readonly TimeProvider timeProvider;

    public TopicControlOutboxRecovery(
        ITopicControlOutboxStore store,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public TopicControlRecoveryResult Recover(MeshDb.DeviceEnvelopeOutboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TopicTransportPolicy.IsReceiptGatedControl(item))
            return new(TopicControlRecoveryKind.NotReceiptGated, item.EnvelopeId, item.RecoveryCount);
        if (!string.Equals(item.State, TopicOutboxStates.DeadLetter, StringComparison.Ordinal))
            return new(TopicControlRecoveryKind.NotDeadLettered, item.EnvelopeId, item.RecoveryCount);
        if (item.RecoveryCount >= TopicTransportPolicy.MaximumControlRecoveryCount)
            return new(
                TopicControlRecoveryKind.RecoveryLimitReached,
                item.EnvelopeId,
                item.RecoveryCount);

        var now = timeProvider.GetUtcNow();
        if (now >= item.CreatedAt + TopicTransportPolicy.DeadLetterRecoveryWindow)
            return new(
                TopicControlRecoveryKind.RecoveryWindowExpired,
                item.EnvelopeId,
                item.RecoveryCount);
        return store.TryRecoverDeadLetteredDeviceEnvelope(
                item.EnvelopeId,
                now,
                TopicTransportPolicy.MaximumControlRecoveryCount)
            ? new(TopicControlRecoveryKind.Recovered, item.EnvelopeId, item.RecoveryCount + 1)
            : new(
                TopicControlRecoveryKind.ConcurrentStateChange,
                item.EnvelopeId,
                item.RecoveryCount);
    }
}

public sealed class TopicControlOutboxDelivery
{
    private readonly ITopicControlOutboxStore store;
    private readonly ITopicEnvelopeTransport transport;
    private readonly TimeProvider timeProvider;

    public TopicControlOutboxDelivery(
        ITopicControlOutboxStore store,
        ITopicEnvelopeTransport transport,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<MeshSendResult?> TrySendAsync(
        MeshDb.DeviceEnvelopeOutboxItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = timeProvider.GetUtcNow();
        if (TopicTransportPolicy.IsControlDeliveryExpired(item, now))
        {
            DeadLetter(item, now, "control_receipt_expired");
            return MeshSendResult.Reject("control_receipt_expired");
        }
        if (!TopicTransportPolicy.ShouldRetryDeviceEnvelope(item, now))
            return null;

        var result = await transport.SendAsync(
            item.TargetDeviceId,
            item.Kind,
            item.Plaintext,
            item.EnvelopeId,
            item.PushHint,
            cancellationToken).ConfigureAwait(false);
        if (result?.Accepted == true)
        {
            if (TopicTransportPolicy.IsReceiptGatedControl(item))
                SetAttempt(item, TopicOutboxStates.RelayQueued, now);
            else if (!store.DeleteDeviceEnvelopeOutbox(item.EnvelopeId))
                throw new InvalidOperationException(
                    "The delivered device envelope could not be removed.");
        }
        else if (result is not null && TopicTransportPolicy.IsPermanentRejection(result.Code))
        {
            if (TopicTransportPolicy.IsReceiptGatedControl(item))
                DeadLetter(item, now, "control_permanent_reject:" + result.Code);
            else if (!store.DeleteDeviceEnvelopeOutbox(item.EnvelopeId))
                throw new InvalidOperationException(
                    "The rejected device envelope could not be removed.");
        }
        else
        {
            SetAttempt(item, item.State, now, result?.Code);
        }
        return result;
    }

    private void DeadLetter(
        MeshDb.DeviceEnvelopeOutboxItem item,
        DateTimeOffset now,
        string reason)
        => SetAttempt(item, TopicOutboxStates.DeadLetter, now, reason);

    private void SetAttempt(
        MeshDb.DeviceEnvelopeOutboxItem item,
        string state,
        DateTimeOffset now,
        string? error = null)
    {
        if (!store.SetDeviceEnvelopeOutboxAttempt(
                item.EnvelopeId, state, now, error))
            throw new InvalidOperationException(
                "The device-envelope delivery outcome could not be persisted.");
    }
}

public static class TopicExecutionStatus
{
    public const string Delivered = "delivered";
    public const string PendingLocal = "pending_local";
    public const string LocalQueued = "local_queued";
    public const string RelayAccepted = "relay_accepted";

    public static bool IsRelayAccepted(string code)
        => code is Delivered or RelayAccepted or "accepted";
}

public static class TopicOutboxStates
{
    public const string Pending = "pending";
    public const string RelayAccepted = "relay_accepted";
    public const string RelayQueued = "relay_queued";
    public const string DeviceAccepted = "device_accepted";
    public const string DeviceQueued = "device_queued";
    public const string Running = "running";
    public const string CancelPending = "cancel_pending";
    public const string Expired = "expired";
    public const string DeadLetter = "dead_letter";
    public const string Failed = "failed";

    public static bool NeedsRemoteAcceptance(string state)
        => state is Pending or RelayQueued;
}

public enum TopicSendOutcomePersistenceResult
{
    Applied,
    Ignored,
    NotFound
}

public enum TopicControlReceiptPersistenceResult
{
    Applied,
    Duplicate,
    IdentityConflict,
    NotCorrelated
}

public enum TopicReceiptOutboxPersistenceKind
{
    Created,
    Reused,
    IdentityConflict
}

public sealed record TopicReceiptOutboxPersistenceResult(
    TopicReceiptOutboxPersistenceKind Kind,
    MeshDb.DeviceEnvelopeOutboxItem Item);

public static class TopicRemoteStage
{
    public const int Accepted = 10;
    public const int ExecutionQueued = 20;
    public const int Planning = 30;
    public const int Executing = 40;
    public const int Verifying = 50;
    public const int Terminal = 100;

    public static int Ordinal(TopicRunUpdatePayload update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (TopicControlProtocol.IsAcceptance(update)) return Accepted;
        if (TopicControlProtocol.IsExecutionQueued(update)) return ExecutionQueued;
        return update.Phase switch
        {
            TopicRunPhase.Queued => ExecutionQueued,
            TopicRunPhase.Planning => Planning,
            TopicRunPhase.Executing => Executing,
            TopicRunPhase.Verifying => Verifying,
            TopicRunPhase.Completed or TopicRunPhase.Failed or TopicRunPhase.Cancelled
                => Terminal,
            _ => throw new ArgumentOutOfRangeException(nameof(update))
        };
    }

    public static string Name(TopicRunUpdatePayload update)
        => TopicControlProtocol.IsAcceptance(update)
            ? "accepted"
            : TopicControlProtocol.IsExecutionQueued(update)
                ? "queued"
                : update.Phase.ToString().ToLowerInvariant();
}

public enum RemoteTopicUpdatePersistenceResult
{
    Applied,
    Ignored,
    Duplicate,
    IdentityConflict,
    NotCorrelated,
    PersistenceFailed
}

public static class InboundTopicRunStates
{
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

public static class TopicTransportPolicy
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(14);
    public static readonly TimeSpan DedupRetention = TimeSpan.FromDays(30);
    /// <summary>
    /// A UI submission remains fenced for the full protocol deduplication window after terminal
    /// observation. UI-journal compaction therefore cannot make a supported replay create a new run.
    /// </summary>
    public static readonly TimeSpan TriggerLedgerRetention = DedupRetention;
    /// <summary>
    /// Hard upper bound for retrying a receipt-gated acceptance or terminal control. The bound is
    /// measured from the control's durable local creation timestamp, never from a peer timestamp.
    /// </summary>
    public static readonly TimeSpan ControlDeliveryLifetime = TimeSpan.FromDays(14);
    public static readonly TimeSpan RecoveredControlDeliveryLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan DeadLetterRecoveryWindow = TimeSpan.FromDays(21);
    public const int MaximumControlRecoveryCount = 1;
    /// <summary>
    /// A requester retains a terminal correlation for longer than any executor may retry a control.
    /// Therefore a delayed acceptance/terminal control can still be correlated and receipted for its
    /// complete initial plus recovered delivery lifetime.
    /// </summary>
    public static readonly TimeSpan TerminalCorrelationRetention = DedupRetention;
    public static readonly TimeSpan RemoteAcceptanceRetryInterval = TimeSpan.FromSeconds(2);

    public static bool IsReceiptGatedControl(MeshDb.DeviceEnvelopeOutboxItem item)
        => string.Equals(item.Kind, MeshKinds.TopicRunUpdate, StringComparison.Ordinal)
           && TopicRunProtocol.TryParseUpdate(item.Plaintext, out var update)
           && TopicControlProtocol.RequiresPersistenceReceipt(update);

    public static bool IsControlDeliveryExpired(
        MeshDb.DeviceEnvelopeOutboxItem item,
        DateTimeOffset localNow)
        => IsReceiptGatedControl(item)
           && localNow >= (item.RecoveryStartedAt is { } recoveredAt
               ? recoveredAt + RecoveredControlDeliveryLifetime
               : item.CreatedAt + ControlDeliveryLifetime);

    public static bool ShouldRetryDeviceEnvelope(
        MeshDb.DeviceEnvelopeOutboxItem item,
        DateTimeOffset localNow)
        => item.State is not TopicOutboxStates.DeadLetter and not TopicOutboxStates.Expired
           && !IsControlDeliveryExpired(item, localNow)
           && (item.LastAttemptAt is null
               || localNow >= item.LastAttemptAt.Value + RemoteAcceptanceRetryInterval);

    public static bool IsPermanentRejection(string code)
        => code is "invalid_signature"
            or "message_too_large"
            or "invalid_push_hint"
            or "target_device_unknown"
            or "sync_target_unknown"
            or "device_revoked";

    public static bool ShouldAttemptRequestDelivery(
        string state,
        DateTimeOffset updatedAt,
        DateTimeOffset now)
        => state == TopicOutboxStates.Pending
           || state == TopicOutboxStates.RelayQueued
           && now - updatedAt >= RemoteAcceptanceRetryInterval;
}

public static class TopicAcceptancePolicy
{
    public static TopicRunUpdatePayload Create(
        TopicRunRequestPayload request,
        DateTimeOffset acceptedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TopicRunUpdatePayload(
            request.RunId,
            request.ThreadId,
            TopicRunPhase.Queued,
            Status: "Accepted",
            Timestamp: acceptedAt,
            TriggerLineId: request.TriggerLineId);
    }
}

public static class TopicControlProtocol
{
    public const string AcceptedStatus = "Accepted";
    public const string ExecutionQueuedStatus = "Queued";
    public const string AcceptanceReceiptStatus = "AcceptedReceipt";
    public const string TerminalReceiptStatus = "TerminalReceipt";

    public static bool IsAcceptance(TopicRunUpdatePayload update)
        => update.Phase == TopicRunPhase.Queued
           && string.Equals(update.Status, AcceptedStatus, StringComparison.Ordinal);

    public static bool IsExecutionQueued(TopicRunUpdatePayload update)
        => update.Phase == TopicRunPhase.Queued
           && string.Equals(update.Status, ExecutionQueuedStatus, StringComparison.Ordinal);

    public static bool IsTerminal(TopicRunUpdatePayload update)
        => update.Phase is TopicRunPhase.Completed
            or TopicRunPhase.Failed
            or TopicRunPhase.Cancelled;

    public static bool RequiresPersistenceReceipt(TopicRunUpdatePayload update)
        => IsAcceptance(update) || IsTerminal(update) && !IsReceipt(update);

    public static bool IsReceipt(TopicRunUpdatePayload update)
        => string.Equals(update.Status, AcceptanceReceiptStatus, StringComparison.Ordinal)
           || string.Equals(update.Status, TerminalReceiptStatus, StringComparison.Ordinal);

    public static TopicRunUpdatePayload CreateReceipt(
        TopicRunUpdatePayload control,
        DateTimeOffset receivedAt)
    {
        _ = receivedAt;
        return CreateReceipt(control);
    }

    public static TopicRunUpdatePayload CreateReceipt(TopicRunUpdatePayload control)
    {
        if (!RequiresPersistenceReceipt(control))
            throw new ArgumentException(
                "Only an acceptance or terminal control can be acknowledged.",
                nameof(control));
        return new TopicRunUpdatePayload(
            control.RunId,
            control.ThreadId,
            control.Phase,
            Status: IsAcceptance(control)
                ? AcceptanceReceiptStatus
                : TerminalReceiptStatus,
            // A stable receipt id must always describe identical durable plaintext. The source
            // control timestamp is authenticated semantic input; a receiver's wall clock is not.
            Timestamp: control.Timestamp,
            TriggerLineId: control.TriggerLineId);
    }

    public static string ControlPurpose(TopicRunUpdatePayload update)
    {
        if (IsAcceptance(update)) return "topic.accepted";
        if (IsExecutionQueued(update)) return "topic.execution-queued";
        if (IsTerminal(update) && !IsReceipt(update)) return "topic.terminal";
        if (string.Equals(
                update.Status, AcceptanceReceiptStatus, StringComparison.Ordinal))
            return "topic.accepted-receipt";
        if (string.Equals(
                update.Status, TerminalReceiptStatus, StringComparison.Ordinal))
            return "topic.terminal-receipt";
        return "topic.update";
    }

    public static string AcknowledgedPurpose(TopicRunUpdatePayload receipt)
    {
        if (string.Equals(
                receipt.Status, AcceptanceReceiptStatus, StringComparison.Ordinal))
            return "topic.accepted";
        if (string.Equals(
                receipt.Status, TerminalReceiptStatus, StringComparison.Ordinal))
            return "topic.terminal";
        throw new ArgumentException("The update is not a control receipt.", nameof(receipt));
    }

    public static string EnvelopeId(string purpose, string runId)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{purpose}\0{runId}"))).ToLowerInvariant();
}
/// <summary>Terminal result from one local topic turn.</summary>
public sealed record TopicRunCompletion(
    string RunId,
    string ThreadId,
    TopicRunPhase Phase,
    DateTimeOffset CompletedAt,
    string? Error = null,
    string? FailureCode = null);

/// <summary>Runs exactly one agent turn locally with progress and cancellation.</summary>
public interface ITopicTurnRunner
{
    Task<TopicRunCompletion> ExecuteAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload> progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? onStarted = null);
}

/// <summary>Sends targeted topic requests and cancellation to remote agent-ready devices.</summary>
public interface IDeviceTopicTransport
{
    Task<TopicDispatchResult> DispatchAsync(
        string targetDeviceId,
        TopicRunRequestPayload request,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);

    Task<bool> CancelAsync(
        string targetDeviceId,
        TopicRunCancelPayload cancel,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListDevicesAsync(
        CancellationToken cancellationToken)
        => ListEligibleDevicesAsync(cancellationToken);

    Task<TopicDispatchResult> DispatchPersistedAsync(
        MeshDb.TopicOutboxItem item,
        CancellationToken cancellationToken)
        => DispatchAsync(
            item.TargetDeviceId,
            item.Request,
            item.Attachments,
            cancellationToken);
}

/// <summary>
/// Testable boundary at the final targeted-envelope send. Production leaves this unset and uses
/// SignalR; deterministic durability tests can delay or drop application receipts without a relay.
/// </summary>
public interface ITopicEnvelopeTransport
{
    Task<MeshSendResult?> SendAsync(
        string targetDeviceId,
        string kind,
        string plaintext,
        string envelopeId,
        string? pushHint,
        CancellationToken cancellationToken);
}

internal sealed record TopicEnvelopeSendAttempt(
    string TargetDeviceId,
    string Kind,
    string Plaintext,
    string EnvelopeId,
    string? PushHint);

internal interface ITopicEnvelopeTestFaultScheduler
{
    Task<MeshSendResult?> SendAsync(
        TopicEnvelopeSendAttempt attempt,
        Func<TopicEnvelopeSendAttempt, CancellationToken, Task<MeshSendResult?>> send,
        CancellationToken cancellationToken);
}

/// <summary>Routes topic execution through a local runner or targeted device transport.</summary>
public interface ITopicExecutionRouter
{
    Task<TopicDispatchResult> SubmitAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload>? progress,
        CancellationToken cancellationToken,
        TopicSendHandoffContext? handoffContext = null);

    Task<bool> CancelQueuedAsync(
        string threadId,
        string runId,
        string lineId,
        CancellationToken cancellationToken);

    Task<bool> StopAsync(
        string threadId,
        string runId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListDevicesAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Accepts progress synchronously, then processes every report in FIFO order. Completion drains all
/// accepted reports before returning so a terminal update cannot overtake planning, tool, or stream updates.
/// </summary>
internal sealed class OrderedAsyncProgress<T> : IProgress<T>
{
    private readonly Channel<T> queue = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly Func<T, Task> handler;
    private readonly Action<Exception>? onError;
    private readonly Task drainTask;
    private int completed;

    public OrderedAsyncProgress(Func<T, Task> handler, Action<Exception>? onError = null)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.onError = onError;
        drainTask = DrainAsync();
    }

    public void Report(T value)
    {
        if (Volatile.Read(ref completed) != 0 || !queue.Writer.TryWrite(value))
            throw new InvalidOperationException("Progress has already completed.");
    }

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
            queue.Writer.TryComplete();
        await drainTask.ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        await foreach (var value in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await handler(value).ConfigureAwait(false);
            }
            catch (Exception ex) when (onError is not null)
            {
                onError(ex);
            }
        }
    }
}
