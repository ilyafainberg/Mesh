using Mesh.App.Domain;
using Mesh.Shared;
using System.Security.Cryptography;
using System.Text;

namespace Mesh.App.Services;

public sealed partial class AppState
{
    internal static Action<TopicProjectionCheckpoint>? TopicProjectionCheckpointHook { get; set; }

    public TopicRunBeginResult BeginTopicRun(TopicRunBeginCommand command)
        => BeginTopicRunCore(command, null, null);

    internal TopicRunBeginResult AuthorizeAndBeginTopicRun(
        TopicSendSnapshot snapshot,
        TopicSendRetryAuthorization authorization,
        TopicRunBeginCommand command)
        => BeginTopicRunCore(command, snapshot, authorization);

    private TopicRunBeginResult BeginTopicRunCore(
        TopicRunBeginCommand command,
        TopicSendSnapshot? snapshot,
        TopicSendRetryAuthorization? authorization)
    {
        ArgumentNullException.ThrowIfNull(command);
        TopicRunBeginResult result;
        OwnThread? thread = null;
        ChatLine? addedLine = null;
        lock (profileSyncGate)
        {
            var databaseIdentity = Volatile.Read(ref activeDatabaseIdentity);
            var validatedDb = databaseIdentity?.Database;
            long? expectedObservationEpoch = null;
            if (authorization is not null)
            {
                if (snapshot is null
                    || !ValidateAndConsumeTopicSendAuthorizationLocked(
                        snapshot,
                        authorization,
                        command,
                        databaseIdentity))
                {
                    RecordAtomicBeginDiagnostic(
                        snapshot,
                        authorization,
                        "reconcile_required");
                    return new TopicRunBeginResult(false, false, "reconcile_required");
                }
                expectedObservationEpoch = authorization.ObservationEpoch;
            }

            if (validatedDb is null || !ReferenceEquals(validatedDb, activeDb))
            {
                result = new TopicRunBeginResult(false, false, "persistence_unavailable");
            }

            else
            {
                try
                {
                    result = validatedDb.ExecuteDurableWrite(
                        () => validatedDb.BeginTopicRun(
                            command,
                            expectedObservationEpoch: expectedObservationEpoch));
                    if (authorization is not null)
                        RecordAtomicBeginDiagnostic(snapshot, authorization, result.Code);
                }
                catch (Exception exception)
                {
                    RuntimeDiagnostics.Current?.RecordEvent(
                        "topic-begin-run-persistence-failed",
                        BeginDiagnostic(command, "persistence_failed", false)
                        + $";exception={exception.GetType().FullName}");
                    return new TopicRunBeginResult(false, false, "persistence_failed");
                }
            }

            if (!result.DurableCommitted)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-begin-run-persistence-failed",
                    BeginDiagnostic(command, result.Code, false));
                return result;
            }

            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-begin-run-committed",
                BeginDiagnostic(
                    command, result.Code, false, result.TriggerId, result.AuthoritativeRunId));
            try
            {
                TopicProjectionCheckpointHook?.Invoke(
                    TopicProjectionCheckpoint.AfterCommitBeforeProjection);
                var draft = result.AuthoritativeDraft ?? command.Draft;
                thread = Profile.OwnThreads.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, draft.ThreadId, StringComparison.Ordinal));
                if (thread is null)
                {
                    var persisted = validatedDb!.LoadProfile()?.OwnThreads.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, draft.ThreadId, StringComparison.Ordinal));
                    if (persisted is not null)
                    {
                        Profile.OwnThreads.Add(persisted);
                        thread = persisted;
                    }
                }
                if (thread is null)
                    throw new InvalidOperationException("Persisted topic thread was not found.");

                if (result.Code != "already_completed")
                {
                    var queuedBehindActiveRun = thread.ExecutionRunId is not null
                                                && !string.Equals(
                                                    thread.ExecutionRunId,
                                                    draft.RunId,
                                                    StringComparison.Ordinal);
                    thread.ExecutionDeviceId = command.Target.DeviceId;
                    thread.ExecutionDeviceName = command.Target.DeviceName;
                    thread.ExecutionDevicePlatform = command.Target.Platform;
                    if (!queuedBehindActiveRun)
                    {
                        thread.ExecutionAt = draft.TriggerAt;
                        thread.ExecutionRunId = draft.RunId;
                    }
                    thread.LastActivityAt = ActivityTimestamp.Advance(
                        thread.LastActivityAt, draft.TriggerAt);
                    terminalRemoteRuns.Remove(draft.ThreadId + "\0" + draft.RunId);
                    queuedTopicRuns.MarkWaiting(
                        draft.ThreadId,
                        draft.RunId,
                        draft.TriggerLineId,
                        TopicQueueStage.Sending);
                    if (command.Mode == TopicRunBeginMode.Remote
                        && !queuedBehindActiveRun)
                    {
                        remoteRuns[draft.ThreadId] = new RemoteRunProjection
                        {
                            RunId = draft.RunId,
                            ThreadId = draft.ThreadId,
                            Phase = command.InitialProjection.Phase,
                            Status = command.InitialProjection.Status,
                            Queued = command.InitialProjection.Queued,
                            Timestamp = command.InitialProjection.Timestamp
                        };
                    }
                }

                if (!thread.Lines.Any(line =>
                        string.Equals(line.Id, draft.TriggerLineId, StringComparison.Ordinal)))
                {
                    addedLine = new ChatLine
                    {
                        Id = draft.TriggerLineId,
                        Role = "user",
                        Text = draft.Prompt,
                        SenderHandle = draft.TriggerHandle,
                        At = draft.TriggerAt,
                        Attachments = draft.Attachments?
                            .Select(CloneBeginAttachment).ToList() ?? []
                    };
                    thread.Lines.Add(addedLine);
                }
                TopicProjectionCheckpointHook?.Invoke(
                    TopicProjectionCheckpoint.AfterProjection);
                result = result with
                {
                    ProjectionApplied = true,
                    ProjectionError = null
                };
            }
            catch (Exception exception)
            {
                result = result with
                {
                    ProjectionApplied = false,
                    ProjectionError = "projection_deferred"
                };
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-begin-run-projection-deferred",
                    BeginDiagnostic(
                        command,
                        "projection_deferred",
                        false,
                        result.TriggerId,
                        result.AuthoritativeRunId)
                    + $";exception={exception.GetType().FullName}");
            }
        }

        if (result.ProjectionApplied && thread is not null)
        {
            var draft = result.AuthoritativeDraft ?? command.Draft;
            if (addedLine is not null)
                EmitLineUpsert("topic.line", draft.ThreadId, addedLine);
            EmitTopicUpsert(thread);
            NotifyChanged();
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-begin-run",
                BeginDiagnostic(
                    command, result.Code, false, result.TriggerId, result.AuthoritativeRunId));
        }
        return result;
    }

    private bool ValidateAndConsumeTopicSendAuthorizationLocked(
        TopicSendSnapshot snapshot,
        TopicSendRetryAuthorization authorization,
        TopicRunBeginCommand command,
        ActiveDatabaseIdentity? databaseIdentity)
    {
        if (authorization.Version != TopicSendRetryAuthorization.CurrentVersion
            || databaseIdentity is null
            || activeId is null
            || !ReferenceEquals(databaseIdentity.Database, activeDb)
            || !string.Equals(activeId, authorization.AccountId, StringComparison.Ordinal)
            || !string.Equals(
                databaseIdentity.AccountId,
                authorization.AccountId,
                StringComparison.Ordinal)
            || !string.Equals(
                databaseIdentity.Identity,
                authorization.DatabaseIdentity,
                StringComparison.Ordinal)
            || databaseIdentity.Generation != authorization.DatabaseGeneration
            || !string.Equals(snapshot.OperationId, authorization.OperationId, StringComparison.Ordinal)
            || !string.Equals(snapshot.AccountId, authorization.AccountId, StringComparison.Ordinal)
            || snapshot.ComposerRevision != authorization.ComposerRevision
            || !string.Equals(
                authorization.SnapshotIdentity,
                RetrySnapshotIdentity(snapshot),
                StringComparison.Ordinal)
            || !string.Equals(
                command.Draft.TriggerOperationId,
                authorization.OperationId,
                StringComparison.Ordinal)
            || !string.Equals(command.Draft.RunId, snapshot.RunId, StringComparison.Ordinal)
            || !string.Equals(command.Draft.ThreadId, snapshot.ThreadId, StringComparison.Ordinal)
            || !string.Equals(command.Draft.TriggerLineId, snapshot.LineId, StringComparison.Ordinal)
            || !string.Equals(command.Target.DeviceId, snapshot.TargetDeviceId, StringComparison.Ordinal)
            || !issuedTopicSendAuthorizations.Remove(authorization.Nonce, out var issued)
            || issued != authorization)
            return false;
        return true;
    }

    private static string RetrySnapshotIdentity(TopicSendSnapshot snapshot)
        => TopicSendSnapshot.StableId(
            "retry-snapshot",
            string.Join(
                "\0",
                snapshot.OperationId,
                snapshot.AccountId,
                snapshot.ThreadId,
                snapshot.TargetDeviceId,
                snapshot.DraftFingerprint));

    private void RecordAtomicBeginDiagnostic(
        TopicSendSnapshot? snapshot,
        TopicSendRetryAuthorization authorization,
        string result)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "topic-send-atomic-begin",
            $"operation={DiagnosticHash(snapshot?.OperationId ?? authorization.OperationId)};"
            + $"account={DiagnosticHash(authorization.AccountId)};"
            + $"database={DiagnosticHash(authorization.DatabaseIdentity)};"
            + $"token={DiagnosticHash(authorization.Nonce)};"
            + $"epoch={DiagnosticHash(authorization.ObservationEpoch.ToString(
                System.Globalization.CultureInfo.InvariantCulture))};"
            + $"result={DiagnosticSafe(result)}");

    private static string DiagnosticHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..16];

    private static string DiagnosticSafe(string value)
        => new(value.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(48).ToArray());

    public TopicRunTriggerLookupResult QueryTopicRunTrigger(
        string operationId,
        string expectedRunId,
        string expectedThreadId,
        string expectedTriggerLineId,
        string expectedTargetDeviceId,
        string? expectedAccountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTriggerLineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTargetDeviceId);

        lock (profileSyncGate)
        {
            var databaseIdentity = Volatile.Read(ref activeDatabaseIdentity);
            var observationVersion = 0L;
            var observedAt = timeProvider.GetUtcNow();
            TopicRunTriggerLookupResult Scoped(
                TopicRunTriggerLookupKind kind,
                string? runId = null,
                string? triggerLineId = null,
                string? outboxId = null,
                bool terminal = false,
                string? detail = null,
                string? reason = null)
                => new(
                    kind,
                    runId,
                    triggerLineId,
                    outboxId,
                    terminal,
                    detail,
                    reason,
                    activeId,
                    databaseIdentity?.Identity,
                    databaseIdentity?.Generation ?? 0,
                    observationVersion,
                    observedAt);
            if (databaseIdentity is null || activeId is null)
                return Scoped(
                    TopicRunTriggerLookupKind.Unavailable,
                    detail: "The account database is not available.",
                    reason: "database_unavailable");
            if (!string.Equals(
                    databaseIdentity.AccountId,
                    activeId,
                    StringComparison.Ordinal))
                return Scoped(
                    TopicRunTriggerLookupKind.Unavailable,
                    detail: "The account database identity is changing.",
                    reason: "database_identity_unavailable");
            if (expectedAccountId is not null
                && !string.Equals(
                    databaseIdentity.AccountId,
                    expectedAccountId,
                    StringComparison.Ordinal))
                return Scoped(
                    TopicRunTriggerLookupKind.Unavailable,
                    detail: "The active account does not match the send journal.",
                    reason: "account_mismatch");

            MeshDb.TopicRunTriggerObservation observation;
            try
            {
                observation = databaseIdentity.Database.ExecuteDurableWrite(
                    () => databaseIdentity.Database.ObserveTopicRunTrigger(operationId));
                observationVersion = observation.Epoch;
            }
            catch (Exception exception)
            {
                return Scoped(
                    TopicRunTriggerLookupKind.QueryFailed,
                    detail: "The durable trigger ledger query failed.",
                    reason: exception.GetType().Name);
            }
            var trigger = observation.Trigger;
            if (trigger is null)
                return Scoped(
                    TopicRunTriggerLookupKind.NotFound,
                    reason: "authoritative_not_found");

            var expectedTriggerId = TopicRunTriggerIdentity.For(
                expectedThreadId,
                expectedTriggerLineId,
                operationId);
            if (string.IsNullOrWhiteSpace(trigger.TriggerId)
                || string.IsNullOrWhiteSpace(trigger.RunId)
                || string.IsNullOrWhiteSpace(trigger.ThreadId)
                || string.IsNullOrWhiteSpace(trigger.TriggerLineId)
                || string.IsNullOrWhiteSpace(trigger.TargetDeviceId)
                || string.IsNullOrWhiteSpace(trigger.PayloadHash)
                || !string.Equals(
                    trigger.TriggerId,
                    expectedTriggerId,
                    StringComparison.Ordinal))
                return Scoped(
                    TopicRunTriggerLookupKind.Corrupt,
                    detail: "The durable trigger ledger row is structurally invalid.");

            var outbox = observation.Outbox;
            if (!string.Equals(
                    trigger.ThreadId,
                    expectedThreadId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    trigger.TriggerLineId,
                    expectedTriggerLineId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    trigger.TargetDeviceId,
                    expectedTargetDeviceId,
                    StringComparison.Ordinal)
                || outbox is not null
                && (!string.Equals(
                        outbox.RunId,
                        trigger.RunId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        outbox.ThreadId,
                        trigger.ThreadId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        outbox.TriggerLineId,
                        trigger.TriggerLineId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        outbox.TargetDeviceId,
                        trigger.TargetDeviceId,
                        StringComparison.Ordinal)))
                return Scoped(
                    TopicRunTriggerLookupKind.Conflict,
                    trigger.RunId,
                    trigger.TriggerLineId,
                    outbox?.RunId,
                    trigger.TerminalAt is not null,
                    "The durable trigger ledger conflicts with the UI send journal.");

            return Scoped(
                TopicRunTriggerLookupKind.Found,
                trigger.RunId,
                trigger.TriggerLineId,
                outbox?.RunId,
                trigger.TerminalAt is not null);
        }
    }

    internal TopicSendRetryAuthorization? IssueTopicSendRetryAuthorization(
        TopicSendSnapshot snapshot,
        TopicRunTriggerLookupResult observation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Kind != TopicRunTriggerLookupKind.NotFound
            || observation.ObservationVersion <= 0
            || observation.ObservedAt == default
            || string.IsNullOrWhiteSpace(observation.AccountId)
            || string.IsNullOrWhiteSpace(observation.DatabaseIdentity))
            return null;

        lock (profileSyncGate)
        {
            var current = Volatile.Read(ref activeDatabaseIdentity);
            if (current is null
                || !ReferenceEquals(current.Database, activeDb)
                || !string.Equals(current.AccountId, observation.AccountId, StringComparison.Ordinal)
                || !string.Equals(current.AccountId, snapshot.AccountId, StringComparison.Ordinal)
                || !string.Equals(
                    current.Identity,
                    observation.DatabaseIdentity,
                    StringComparison.Ordinal)
                || current.Generation != observation.DatabaseGeneration)
                return null;

            long currentEpoch;
            try
            {
                currentEpoch = current.Database.ExecuteDurableWrite(
                    current.Database.GetTopicTriggerEpoch);
            }

            catch
            {
                return null;
            }
            if (currentEpoch != observation.ObservationVersion)
                return null;

            if (issuedTopicSendAuthorizations.Count >= 512)
                issuedTopicSendAuthorizations.Clear();
            var authorization = new TopicSendRetryAuthorization(
                TopicSendRetryAuthorization.CurrentVersion,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                snapshot.OperationId,
                RetrySnapshotIdentity(snapshot),
                current.AccountId,
                current.Identity,
                current.Generation,
                observation.ObservationVersion,
                observation.ObservedAt,
                snapshot.ComposerRevision);
            issuedTopicSendAuthorizations.Add(authorization.Nonce, authorization);
            return authorization;
        }
    }

    internal void InvalidateTopicSendRetryAuthorization(
        TopicSendRetryAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (profileSyncGate)
        {
            if (issuedTopicSendAuthorizations.TryGetValue(
                    authorization.Nonce,
                    out var issued)
                && issued == authorization)
                issuedTopicSendAuthorizations.Remove(authorization.Nonce);
        }
    }

    public void CompleteLocalTopicRun(string runId, DateTimeOffset terminalAt)
    {
        lock (profileSyncGate)
            activeDb?.ExecuteDurableWrite(
                () => activeDb.CompleteLocalTopicRun(runId, terminalAt));
    }

    private static ChatAttachment CloneBeginAttachment(ChatAttachment attachment)
        => new(attachment.Name, attachment.MimeType, attachment.Data.ToArray());

    internal static string BeginDiagnostic(
        TopicRunBeginCommand command,
        string result,
        bool transportEntered,
        string? triggerId = null,
        string? runId = null)
        => $"operation={StableDiagnosticId(
               triggerId ?? TopicRunTriggerIdentity.For(
                   command.Draft.ThreadId,
                   command.Draft.TriggerLineId,
                   command.Draft.TriggerOperationId))}"
           + $";run={StableDiagnosticId(runId ?? command.Draft.RunId)}"
           + $";envelope={StableDiagnosticId(command.Request?.RunId ?? "local")}"
           + $";mode={command.Mode.ToString().ToLowerInvariant()}"
           + $";result={result};transport_entered={transportEntered.ToString().ToLowerInvariant()}";

    internal static string StableDiagnosticId(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..16];

    internal MeshDb.TopicTransportAttempt? BeginTopicTransportAttempt(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.ExecuteDurableWrite(
                () => activeDb.BeginTopicTransportAttempt(runId));
    }

    public bool SaveTopicOutbox(MeshDb.TopicOutboxItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(() => activeDb.UpsertTopicOutbox(item));
            return true;
        }
    }

    public MeshDb.TopicOutboxItem? GetTopicOutbox(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.GetTopicOutbox(runId);
    }

    public IReadOnlyList<MeshDb.TopicOutboxItem> ListTopicOutbox()
    {
        lock (profileSyncGate)
            return activeDb?.ListTopicOutbox() ?? [];
    }

    public bool SetTopicOutboxState(string runId, string outboxState, string? error = null)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(
                () => activeDb.SetTopicOutboxState(runId, outboxState, error));
            return true;
        }
    }

    public TopicSendOutcomePersistenceResult ApplyTopicRequestSendOutcome(
        string runId,
        string outboxState,
        string? error = null)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return TopicSendOutcomePersistenceResult.NotFound;
            return activeDb.ExecuteDurableWrite(
                () => activeDb.ApplyTopicRequestSendOutcome(runId, outboxState, error));
        }
    }

    public bool DeleteTopicOutbox(string runId)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(() => activeDb.DeleteTopicOutbox(runId));
            return true;
        }
    }

    public bool CompleteTopicOutbox(string runId, DateTimeOffset terminalAt)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(() => activeDb.CompleteTopicOutbox(runId, terminalAt));
            return true;
        }
    }

    public bool IsRetainedTopicRunCorrelation(
        string runId,
        string threadId,
        string sourceDeviceId)
    {
        lock (profileSyncGate)
        {
            var correlation = activeDb?.GetTopicRunCorrelation(runId);
            return correlation?.TerminalAt is not null
                   && string.Equals(correlation.ThreadId, threadId, StringComparison.Ordinal)
                   && string.Equals(
                       correlation.TargetDeviceId, sourceDeviceId, StringComparison.Ordinal);
        }
    }

    public bool TryAcceptInboundTopicRun(MeshDb.InboundTopicRunItem item)
    {
        lock (profileSyncGate)
            return activeDb?.ExecuteDurableWrite(
                () => activeDb.TryAddInboundTopicRun(item)) == true;
    }

    public bool TryAcceptInboundTopicRunAndQueueAcceptance(
        MeshDb.InboundTopicRunItem item,
        MeshDb.DeviceEnvelopeOutboxItem acceptance)
    {
        lock (profileSyncGate)
            return activeDb?.ExecuteDurableWrite(
                () => activeDb.TryAddInboundTopicRunAndQueueAcceptance(
                    item, acceptance)) == true;
    }

    public MeshDb.InboundTopicRunItem? GetInboundTopicRun(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.GetInboundTopicRun(runId);
    }

    public MeshDb.InboundTopicCancellationItem? GetInboundTopicCancellation(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.GetInboundTopicCancellation(runId);
    }

    public bool SaveInboundTopicCancellation(MeshDb.InboundTopicCancellationItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            if (activeDb.ExecuteDurableWrite(
                    () => activeDb.TryAddInboundTopicCancellation(item))) return true;
            var existing = activeDb.GetInboundTopicCancellation(item.RunId);
            return existing is not null
                   && string.Equals(existing.SourceDeviceId, item.SourceDeviceId, StringComparison.Ordinal)
                   && string.Equals(existing.ThreadId, item.ThreadId, StringComparison.Ordinal)
                   && string.Equals(existing.TerminalUpdateJson, item.TerminalUpdateJson, StringComparison.Ordinal);
        }
    }

    public IReadOnlyList<MeshDb.InboundTopicRunItem> ListInboundTopicRuns(params string[] states)
    {
        lock (profileSyncGate)
            return activeDb?.ListInboundTopicRuns(states) ?? [];
    }

    public bool SetInboundTopicRunState(string runId, string runState)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            return activeDb.ExecuteDurableWrite(
                () => activeDb.SetInboundTopicRunState(runId, runState));
        }
    }

    public bool SetInboundTopicRunTerminal(
        string runId,
        string runState,
        TopicRunUpdatePayload terminalUpdate)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            return activeDb.ExecuteDurableWrite(
                () => activeDb.SetInboundTopicRunTerminal(runId, runState, terminalUpdate));
        }
    }
    public bool SetInboundTopicRunTerminalAndQueue(
        string runId,
        string runState,
        TopicRunUpdatePayload terminalUpdate,
        MeshDb.DeviceEnvelopeOutboxItem outbox)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            return activeDb.ExecuteDurableWrite(
                () => activeDb.SetInboundTopicRunTerminalAndQueue(
                    runId, runState, terminalUpdate, outbox));
        }
    }
    public bool SaveInboundRejection(MeshDb.InboundRejectionItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(() => activeDb.UpsertInboundRejection(item));
            return true;
        }
    }
    public bool SaveDeviceEnvelopeOutbox(MeshDb.DeviceEnvelopeOutboxItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(() => activeDb.UpsertDeviceEnvelopeOutbox(item));
            var persisted = activeDb.GetDeviceEnvelopeOutbox(item.EnvelopeId);
            return persisted is not null
                   && string.Equals(
                       persisted.TargetDeviceId, item.TargetDeviceId, StringComparison.Ordinal)
                   && string.Equals(persisted.Kind, item.Kind, StringComparison.Ordinal)
                   && string.Equals(persisted.Plaintext, item.Plaintext, StringComparison.Ordinal)
                   && string.Equals(persisted.PushHint, item.PushHint, StringComparison.Ordinal);
        }
    }

    public bool ReplaceDeviceEnvelopeOutboxForTargetAndKind(
        MeshDb.DeviceEnvelopeOutboxItem item,
        Func<MeshDb.DeviceEnvelopeOutboxItem, bool>? shouldReplaceExisting = null)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ReplaceDeviceEnvelopeOutboxForTargetAndKind(item, shouldReplaceExisting);
            return true;
        }
    }

    public TopicReceiptOutboxPersistenceResult GetOrCreateTopicReceiptOutbox(
        MeshDb.DeviceEnvelopeOutboxItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null)
                throw new InvalidOperationException("The active durable store is unavailable.");
            return activeDb.ExecuteDurableWrite(
                () => activeDb.GetOrCreateTopicReceiptOutbox(item));
        }
    }

    public MeshDb.DeviceEnvelopeOutboxItem? GetDeviceEnvelopeOutbox(string envelopeId)
    {
        lock (profileSyncGate)
            return activeDb?.GetDeviceEnvelopeOutbox(envelopeId);
    }

    public IReadOnlyList<MeshDb.DeviceEnvelopeOutboxItem> ListDeviceEnvelopeOutbox()
    {
        lock (profileSyncGate)
            return activeDb?.ListDeviceEnvelopeOutbox() ?? [];
    }

    public bool DeleteDeviceEnvelopeOutbox(string envelopeId)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(() => activeDb.DeleteDeviceEnvelopeOutbox(envelopeId));
            return true;
        }
    }

    public bool SetDeviceEnvelopeOutboxAttempt(
        string envelopeId,
        string outboxState,
        DateTimeOffset attemptedAt,
        string? error = null)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(
                () => activeDb.SetDeviceEnvelopeOutboxAttempt(
                    envelopeId, outboxState, attemptedAt, error));
            return true;
        }
    }

    public bool TryRecoverDeadLetteredDeviceEnvelope(
        string envelopeId,
        DateTimeOffset recoveredAt,
        int maximumRecoveryCount)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            return activeDb.ExecuteDurableWrite(() =>
                activeDb.TryRecoverDeadLetteredDeviceEnvelope(
                    envelopeId, recoveredAt, maximumRecoveryCount));
        }
    }

    public TopicControlReceiptPersistenceResult ApplyTopicControlReceipt(
        TopicRunUpdatePayload receipt,
        string sourceDeviceId,
        string acknowledgedEnvelopeId)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null)
                return TopicControlReceiptPersistenceResult.NotCorrelated;
            return activeDb.ExecuteDurableWrite(() =>
                activeDb.ApplyTopicControlReceipt(
                    receipt, sourceDeviceId, acknowledgedEnvelopeId));
        }
    }

    public bool SaveReceivedTopicControl(MeshDb.ReceivedTopicControlItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            if (activeDb.ExecuteDurableWrite(
                    () => activeDb.TryAddReceivedTopicControl(item))) return true;
            var existing = activeDb.GetReceivedTopicControl(item.EnvelopeId);
            return existing is not null
                   && string.Equals(
                       existing.SourceDeviceId, item.SourceDeviceId, StringComparison.Ordinal)
                   && string.Equals(existing.RunId, item.RunId, StringComparison.Ordinal)
                   && string.Equals(existing.ThreadId, item.ThreadId, StringComparison.Ordinal)
                   && string.Equals(
                       existing.ControlKind, item.ControlKind, StringComparison.Ordinal)
                   && string.Equals(existing.UpdateJson, item.UpdateJson, StringComparison.Ordinal);
        }
    }

    public MeshDb.ReceivedTopicControlItem? GetReceivedTopicControl(string envelopeId)
    {
        lock (profileSyncGate)
            return activeDb?.GetReceivedTopicControl(envelopeId);
    }

    public bool SaveDeferredTopicRunUpdate(string envelopeId, TopicRunUpdatePayload update)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(
                () => activeDb.SaveDeferredTopicRunUpdate(
                    envelopeId, update, timeProvider.GetUtcNow()));
            return true;
        }
    }

    public IReadOnlyList<MeshDb.DeferredTopicRunUpdate> ListDeferredTopicRunUpdates()
    {
        lock (profileSyncGate)
            return activeDb?.ListDeferredTopicRunUpdates() ?? [];
    }

    public bool DeleteDeferredTopicRunUpdate(string envelopeId)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.ExecuteDurableWrite(
                () => activeDb.DeleteDeferredTopicRunUpdate(envelopeId));
            return true;
        }
    }

    public void DeleteDeferredTopicRunUpdates(string runId)
    {
        lock (profileSyncGate)
            activeDb?.ExecuteDurableWrite(() => activeDb.DeleteDeferredTopicRunUpdates(runId));
    }

    private void RehydrateTopicExecutionState()
    {
        lock (profileSyncGate)
        {
            queuedTopicRuns.Clear();
            if (activeDb is null) return;
            var now = timeProvider.GetUtcNow();
            var dedupCutoff = now - TopicTransportPolicy.DedupRetention;
            activeDb.ExecuteDurableWrite(() =>
            {
                activeDb.PruneInboundTopicRuns(dedupCutoff);
                activeDb.PruneInboundTopicCancellations(dedupCutoff);
                activeDb.PruneInboundRejections(dedupCutoff);
                activeDb.PruneReceivedTopicControls(dedupCutoff);
                new TopicCorrelationMaintenance(activeDb, timeProvider)
                    .PruneTerminalCorrelations();
            });
            foreach (var control in activeDb.ListReceivedTopicControls())
            {
                if (!TopicRunProtocol.TryParseUpdate(control.UpdateJson, out var update)
                    || !TopicControlProtocol.IsTerminal(update)
                    || TopicControlProtocol.IsReceipt(update))
                    continue;
                _ = RetireExecutionRunLocked(
                    control.ThreadId, control.RunId, AgentRunPhase.Completed);
            }
            var repairedAnswers = 0;
            foreach (var item in activeDb.ListTopicOutbox())
            {
                var thread = Profile.OwnThreads.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, item.ThreadId, StringComparison.Ordinal));
                var answer = thread?.Lines.FirstOrDefault(line =>
                    !line.Internal
                    && string.Equals(line.Role, "assistant", StringComparison.Ordinal)
                    && string.Equals(line.ReplyToLineId, item.TriggerLineId, StringComparison.Ordinal));
                if (thread is not null && answer is not null)
                {
                    var answerAt = answer.At == default ? item.UpdatedAt : answer.At;
                    if (!activeDb.ExecuteDurableWrite(
                            () => activeDb.CompleteOwnThreadRunAndDeleteTopicOutbox(
                                thread.Id,
                                item.RunId,
                                item.TriggerLineId,
                                thread.ExecutionDeviceId,
                                thread.ExecutionDeviceName,
                                thread.ExecutionDevicePlatform,
                                thread.ExecutionAt ?? answerAt,
                                ActivityTimestamp.Advance(thread.LastActivityAt, answerAt))))
                        continue;
                    _ = RetireExecutionRunLocked(
                        thread.Id, item.RunId, AgentRunPhase.Completed);
                    repairedAnswers++;
                    continue;
                }
                var stage = item.State switch
                {
                    TopicOutboxStates.RelayQueued => TopicQueueStage.Relay,
                    TopicOutboxStates.DeviceAccepted => TopicQueueStage.Device,
                    TopicOutboxStates.DeviceQueued => TopicQueueStage.Device,
                    TopicOutboxStates.CancelPending => TopicQueueStage.Cancelling,
                    TopicOutboxStates.Expired => TopicQueueStage.Expired,
                    TopicOutboxStates.Failed => TopicQueueStage.Failed,
                    _ => TopicQueueStage.Sending
                };
                queuedTopicRuns.MarkWaiting(
                    item.ThreadId, item.RunId, item.TriggerLineId, stage);
                if (item.State == TopicOutboxStates.Running)
                    queuedTopicRuns.MarkStarted(item.ThreadId, item.RunId);
            }
            foreach (var correlation in activeDb.ListRetainedTopicRunCorrelations())
            {
                if (!TopicRunProtocol.IsValidIdentifier(correlation.TriggerLineId)
                    || activeDb.GetTopicOutbox(correlation.RunId) is not null)
                    continue;
                var thread = Profile.OwnThreads.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, correlation.ThreadId, StringComparison.Ordinal));
                var answer = thread?.Lines.FirstOrDefault(line =>
                    !line.Internal
                    && string.Equals(line.Role, "assistant", StringComparison.Ordinal)
                    && string.Equals(
                        line.ReplyToLineId, correlation.TriggerLineId, StringComparison.Ordinal));
                if (thread is null
                    || answer is null && correlation.TerminalAt is null)
                    continue;
                var projectionMatches = remoteRuns.TryGetValue(thread.Id, out var projection)
                                        && string.Equals(
                                            projection.RunId, correlation.RunId, StringComparison.Ordinal);
                if (correlation.TerminalAt is not null
                    && thread.ExecutionRunId is null
                    && !projectionMatches)
                    continue;
                var answerAt = answer is not null && answer.At != default
                    ? answer.At
                    : correlation.TerminalEventAt
                      ?? correlation.TerminalAt
                      ?? correlation.CreatedAt;
                if (!activeDb.ExecuteDurableWrite(
                        () => activeDb.CompleteOwnThreadRunAndDeleteTopicOutbox(
                            thread.Id,
                            correlation.RunId,
                            correlation.TriggerLineId!,
                            thread.ExecutionDeviceId,
                            thread.ExecutionDeviceName,
                            thread.ExecutionDevicePlatform,
                            thread.ExecutionAt ?? answerAt,
                            ActivityTimestamp.Advance(thread.LastActivityAt, answerAt))))
                    continue;
                _ = RetireExecutionRunLocked(
                    thread.Id, correlation.RunId, AgentRunPhase.Completed);
                repairedAnswers++;
            }
            if (repairedAnswers > 0)
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-startup-repair",
                    $"reconciled={repairedAnswers};result=converged");
        }
    }
}

public sealed class AppStateTopicSendReconciliationQuery(AppState state)
    : ITopicSendReconciliationQuery,
      ITopicSendReconciliationAvailability,
      ITopicSendAuthorizationAuthority
{
    public event Action? AvailabilityChanged
    {
        add => state.Changed += value;
        remove => state.Changed -= value;
    }

    public ValueTask<TopicSendReconciliationResult> QueryAsync(
        TopicSendSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(snapshot.AccountId))
            return ValueTask.FromResult(new TopicSendReconciliationResult(
                TopicSendReconciliationKind.Unavailable,
                "The send journal does not identify an account.",
                DiagnosticReason: "journal_account_unknown"));
        var lookup = state.QueryTopicRunTrigger(
            snapshot.OperationId,
            snapshot.RunId,
            snapshot.ThreadId,
            snapshot.LineId,
            snapshot.TargetDeviceId,
            snapshot.AccountId);
        return ValueTask.FromResult(new TopicSendReconciliationResult(
            lookup.Kind switch
            {
                TopicRunTriggerLookupKind.Found when lookup.Terminal =>
                    TopicSendReconciliationKind.Completed,
                TopicRunTriggerLookupKind.Found =>
                    TopicSendReconciliationKind.Accepted,
                TopicRunTriggerLookupKind.NotFound =>
                    TopicSendReconciliationKind.NotFound,
                TopicRunTriggerLookupKind.Conflict =>
                    TopicSendReconciliationKind.Conflict,
                TopicRunTriggerLookupKind.Corrupt =>
                    TopicSendReconciliationKind.Corrupt,
                TopicRunTriggerLookupKind.Unavailable =>
                    TopicSendReconciliationKind.Unavailable,
                TopicRunTriggerLookupKind.QueryFailed =>
                    TopicSendReconciliationKind.QueryFailed,
                _ => TopicSendReconciliationKind.Unknown
            },
            lookup.Detail,
            lookup.RunId,
            lookup.TriggerLineId,
            lookup.OutboxId,
            lookup.Reason,
            lookup.AccountId,
            lookup.DatabaseIdentity,
            lookup.DatabaseGeneration,
            lookup.ObservationVersion,
            lookup.ObservedAt));
    }

    public bool TryConsume(
        TopicSendAuthorizationScope scope,
        Func<bool> consume)
        => state.TryConsumeTopicSendAuthorization(scope, consume);

    // Lock order: AppState's profile gate is entered only after coordinator state
    // has been snapshotted and released; no coordinator callback runs under it.
    public bool TryConsume(TopicSendAuthorizationScope scope)
        => state.TryConsumeTopicSendAuthorization(scope, static () => true);

    public bool IsCurrent(TopicSendAuthorizationScope scope)
        => state.IsCurrentTopicSendAuthorization(scope);

    public TopicSendRetryAuthorization? IssueRetryAuthorization(
        TopicSendSnapshot snapshot,
        TopicSendReconciliationResult observation)
        => state.IssueTopicSendRetryAuthorization(
            snapshot,
            new TopicRunTriggerLookupResult(
                TopicRunTriggerLookupKind.NotFound,
                Detail: observation.Detail,
                Reason: observation.DiagnosticReason,
                AccountId: observation.AccountId,
                DatabaseIdentity: observation.DatabaseIdentity,
                DatabaseGeneration: observation.DatabaseGeneration,
                ObservationVersion: observation.ObservationVersion,
                ObservedAt: observation.ObservedAt));

    public void InvalidateRetryAuthorization(TopicSendRetryAuthorization authorization)
        => state.InvalidateTopicSendRetryAuthorization(authorization);

    public TopicRunBeginResult AuthorizeAndBeginTopicRun(
        TopicSendSnapshot snapshot,
        TopicSendRetryAuthorization authorization,
        TopicRunBeginCommand command)
        => state.AuthorizeAndBeginTopicRun(snapshot, authorization, command);
}
