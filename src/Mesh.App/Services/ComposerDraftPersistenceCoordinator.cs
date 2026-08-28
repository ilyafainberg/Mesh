namespace Mesh.App.Services;

internal enum ComposerDraftKind
{
    Conversation,
    Topic
}

internal sealed record ComposerDraftPersistenceFailure(
    ComposerDraftKind Kind,
    string EntityId,
    string Message,
    Exception Exception);

internal enum ComposerDraftMutationResult
{
    Persisted,
    AlreadyPersisted,
    Superseded
}

internal static class ComposerDraftRevision
{
    private static long last = DateTime.UtcNow.Ticks;

    public static long New()
    {
        while (true)
        {
            var observed = Volatile.Read(ref last);
            var candidate = Math.Max(DateTime.UtcNow.Ticks, observed + 1);
            if (Interlocked.CompareExchange(ref last, candidate, observed) == observed)
                return candidate;
        }
    }
}

internal sealed class ComposerDraftPersistenceCoordinator : IAsyncDisposable
{
    private readonly record struct DraftKey(
        MeshDb Db,
        ComposerDraftKind Kind,
        string EntityId);

    private sealed record PendingDraft(
        DraftKey Key,
        string Text,
        MeshDb.TopicComposerSnapshot? TopicSnapshot,
        long Revision,
        long Sequence,
        long DueAt);

    private enum WorkerState
    {
        Running,
        Disposing,
        Disposed
    }

    private sealed class MutationWaiter(
        DraftKey key,
        long revision,
        CancellationToken cancellationToken)
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource<ComposerDraftMutationResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration cancellationRegistration;
        private bool finished;

        public DraftKey Key { get; } = key;
        public long Revision { get; } = revision;
        public Task<ComposerDraftMutationResult> Task => completion.Task;

        public void AttachCancellation(ComposerDraftPersistenceCoordinator owner)
        {
            if (!cancellationToken.CanBeCanceled) return;
            var registration = cancellationToken.Register(
                static state =>
                {
                    var (coordinator, waiter, token) =
                        ((ComposerDraftPersistenceCoordinator, MutationWaiter, CancellationToken))state!;
                    coordinator.CancelWaiter(waiter, token);
                },
                (owner, this, cancellationToken));
            lock (gate)
            {
                if (finished)
                {
                    registration.Dispose();
                    return;
                }
                cancellationRegistration = registration;
            }
        }

        public void Complete(ComposerDraftMutationResult result)
        {
            CancellationTokenRegistration registration;
            lock (gate)
            {
                if (finished) return;
                finished = true;
                registration = cancellationRegistration;
                cancellationRegistration = default;
            }
            registration.Dispose();
            completion.TrySetResult(result);
        }

        public void Fail(Exception exception)
        {
            CancellationTokenRegistration registration;
            lock (gate)
            {
                if (finished) return;
                finished = true;
                registration = cancellationRegistration;
                cancellationRegistration = default;
            }
            registration.Dispose();
            completion.TrySetException(exception);
        }

        public void Cancel(CancellationToken cancellationToken)
        {
            CancellationTokenRegistration registration;
            lock (gate)
            {
                if (finished) return;
                finished = true;
                registration = cancellationRegistration;
                cancellationRegistration = default;
            }
            registration.Dispose();
            completion.TrySetCanceled(cancellationToken);
        }
    }

    private sealed class ScheduledMutationCapture(
        ComposerDraftKind kind,
        string entityId,
        string text,
        CancellationToken cancellationToken)
    {
        public ComposerDraftKind Kind { get; } = kind;
        public string EntityId { get; } = entityId;
        public string Text { get; } = text;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public MutationWaiter? Waiter { get; set; }
    }

    private static readonly AsyncLocal<ScheduledMutationCapture?> ScheduledMutation = new();
    private readonly object gate = new();
    private readonly Dictionary<DraftKey, PendingDraft> pending = new();
    private readonly Dictionary<DraftKey, PendingDraft> latest = new();
    private readonly Dictionary<DraftKey, ComposerDraftPersistenceFailure> failures = new();
    private readonly List<MutationWaiter> mutationWaiters = [];
    private readonly SortedSet<long> outstanding = [];
    private readonly HashSet<long> staging = [];
    private readonly List<(long Target, TaskCompletionSource Completion)> flushWaiters = [];
    private readonly SemaphoreSlim signal = new(0, int.MaxValue);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan debounce;
    private readonly Func<ComposerDraftKind, string, long, Task>? beforeWrite;
    private readonly Action<ComposerDraftKind, string, long>? afterScheduled;
    private readonly Task worker;
    private readonly Action<ComposerDraftPersistenceFailure?> failureChanged;
    private Task? disposal;
    private WorkerState state;
    private long flushThrough;
    private long sequence;
    private long persistedWriteCount;

    public ComposerDraftPersistenceCoordinator(
        Action<ComposerDraftPersistenceFailure?> failureChanged,
        TimeSpan? debounce = null,
        TimeProvider? timeProvider = null,
        Func<ComposerDraftKind, string, long, Task>? beforeWrite = null,
        Action<ComposerDraftKind, string, long>? afterScheduled = null)
    {
        this.failureChanged = failureChanged ?? throw new ArgumentNullException(nameof(failureChanged));
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(100);
        if (this.debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.beforeWrite = beforeWrite;
        this.afterScheduled = afterScheduled;
        worker = Task.Run(RunWorkerAsync);
    }

    internal long PersistedWriteCount => Interlocked.Read(ref persistedWriteCount);

    internal static Task<ComposerDraftMutationResult> ScheduleAndAwaitAsync(
        ComposerDraftKind kind,
        string entityId,
        string text,
        Action schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schedule);
        cancellationToken.ThrowIfCancellationRequested();
        if (ScheduledMutation.Value is not null)
            throw new InvalidOperationException("A composer draft mutation is already being scheduled.");

        var capture = new ScheduledMutationCapture(kind, entityId, text, cancellationToken);
        ScheduledMutation.Value = capture;
        try
        {
            schedule();
        }
        finally
        {
            ScheduledMutation.Value = null;
        }

        return capture.Waiter?.Task
               ?? Task.FromException<ComposerDraftMutationResult>(
                   new InvalidOperationException(
                       $"The {kind} draft mutation for '{entityId}' was not scheduled."));
    }

    public void Schedule(
        MeshDb db,
        ComposerDraftKind kind,
        string entityId,
        string text,
        long? durableRevision = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(text);

        var revision = durableRevision ?? ComposerDraftRevision.New();
        var topicSnapshot = kind == ComposerDraftKind.Topic
            ? MeshDb.TopicComposerSnapshot.TextOnly(text)
            : null;

        long scheduled;
        PendingDraft draft;
        List<MutationWaiter>? superseded = null;
        MutationWaiter? waiter = null;
        ComposerDraftPersistenceFailure? stageFailure = null;
        lock (gate)
        {
            ThrowIfNotRunning();
            scheduled = ++sequence;
            draft = new PendingDraft(
                new DraftKey(db, kind, entityId),
                text,
                topicSnapshot,
                revision,
                scheduled,
                topicSnapshot is null ? DebounceDeadline() : long.MaxValue);
            superseded = mutationWaiters
                .Where(candidate => candidate.Key == draft.Key
                                    && candidate.Revision != revision)
                .ToList();
            foreach (var candidate in superseded)
                mutationWaiters.Remove(candidate);
            ReplacePending(draft);
            if (topicSnapshot is not null)
                staging.Add(draft.Sequence);
            latest[draft.Key] = draft;
            var capture = ScheduledMutation.Value;
            if (capture is not null
                && capture.Kind == kind
                && string.Equals(capture.EntityId, entityId, StringComparison.Ordinal)
                && string.Equals(capture.Text, text, StringComparison.Ordinal))
            {
                waiter = new MutationWaiter(draft.Key, revision, capture.CancellationToken);
                mutationWaiters.Add(waiter);
                capture.Waiter = waiter;
            }
        }
        afterScheduled?.Invoke(kind, entityId, revision);
        if (topicSnapshot is not null)
        {
            try
            {
                db.StagePendingTopicSnapshot(entityId, topicSnapshot, revision);
            }
            catch (Exception exception)
            {
                stageFailure = RecordStageFailure(
                    new DraftKey(db, kind, entityId),
                    revision,
                    exception);
            }
            finally
            {
                lock (gate)
                    CompleteStaging(draft);
            }
        }
        foreach (var candidate in superseded)
            candidate.Complete(ComposerDraftMutationResult.Superseded);
        if (stageFailure is not null) failureChanged(stageFailure);
        waiter?.AttachCancellation(this);
        signal.Release();
    }

    public void ScheduleTopicSnapshot(
        MeshDb db,
        string entityId,
        MeshDb.TopicComposerSnapshot snapshot,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        snapshot = snapshot with { Attachments = snapshot.Attachments.ToArray() };

        long scheduled;
        PendingDraft draft;
        List<MutationWaiter> superseded;
        ComposerDraftPersistenceFailure? stageFailure = null;
        lock (gate)
        {
            ThrowIfNotRunning();
            scheduled = ++sequence;
            draft = new PendingDraft(
                new DraftKey(db, ComposerDraftKind.Topic, entityId),
                snapshot.Text,
                snapshot,
                revision,
                scheduled,
                long.MaxValue);
            superseded = mutationWaiters
                .Where(candidate => candidate.Key == draft.Key
                                    && candidate.Revision != revision)
                .ToList();
            foreach (var candidate in superseded)
                mutationWaiters.Remove(candidate);
            ReplacePending(draft);
            staging.Add(draft.Sequence);
            latest[draft.Key] = draft;
            var capture = ScheduledMutation.Value;
            if (capture is not null
                && capture.Kind == ComposerDraftKind.Topic
                && string.Equals(capture.EntityId, entityId, StringComparison.Ordinal)
                && string.Equals(capture.Text, snapshot.Text, StringComparison.Ordinal))
            {
                var waiter = new MutationWaiter(
                    draft.Key,
                    revision,
                    capture.CancellationToken);
                mutationWaiters.Add(waiter);
                capture.Waiter = waiter;
            }
        }
        afterScheduled?.Invoke(ComposerDraftKind.Topic, entityId, revision);
        try
        {
            db.StagePendingTopicSnapshot(entityId, snapshot, revision);
        }
        catch (Exception exception)
        {
            stageFailure = RecordStageFailure(
                new DraftKey(db, ComposerDraftKind.Topic, entityId),
                revision,
                exception);
        }
        finally
        {
            lock (gate)
                CompleteStaging(draft);
        }
        foreach (var candidate in superseded)
            candidate.Complete(ComposerDraftMutationResult.Superseded);
        if (stageFailure is not null) failureChanged(stageFailure);
        signal.Release();
    }

    public Task<MeshDb.ComposerDraftClearResult> ResolveTopicCleanupAsync(
        MeshDb db,
        string entityId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        if (expectedRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        return Task.Run(
            () =>
            {
                lock (gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    latest.TryGetValue(
                        new DraftKey(db, ComposerDraftKind.Topic, entityId),
                        out var candidate);
                    var currentCandidate = candidate is null
                        ? null
                        : new MeshDb.ComposerDraft(
                            candidate.Text,
                            candidate.Revision,
                            TopicSnapshot: candidate.TopicSnapshot);
                    return db.ResolveTopicDraftCleanup(
                        entityId,
                        expectedRevision,
                        currentCandidate);
                }
            },
            cancellationToken);
    }

    public bool TryGetLatest(
        MeshDb db,
        ComposerDraftKind kind,
        string entityId,
        out string text)
    {
        lock (gate)
        {
            if (latest.TryGetValue(new DraftKey(db, kind, entityId), out var draft))
            {
                text = draft.Text;
                return true;
            }
        }
        text = "";
        return false;
    }

    public bool TryGetLatestState(
        MeshDb db,
        ComposerDraftKind kind,
        string entityId,
        out MeshDb.ComposerDraft? draft)
    {
        lock (gate)
        {
            if (latest.TryGetValue(new DraftKey(db, kind, entityId), out var found))
            {
                draft = new MeshDb.ComposerDraft(
                    found.Text,
                    found.Revision,
                    TopicSnapshot: found.TopicSnapshot);
                return true;
            }
        }
        draft = null;
        return false;
    }

    public ComposerDraftPersistenceFailure? GetFailure(
        MeshDb db,
        ComposerDraftKind kind,
        string entityId)
    {
        lock (gate)
            return failures.GetValueOrDefault(new DraftKey(db, kind, entityId));
    }

    public bool Retry(MeshDb db, ComposerDraftKind kind, string entityId)
    {
        long scheduled;
        var clearedFailure = false;
        lock (gate)
        {
            ThrowIfNotRunning();
            var key = new DraftKey(db, kind, entityId);
            if (!latest.TryGetValue(key, out var draft)
                || !failures.ContainsKey(key))
                return false;

            scheduled = ++sequence;
            var retry = draft with
            {
                Sequence = scheduled,
                DueAt = DebounceDeadline()
            };
            ReplacePending(retry);
            latest[key] = retry;
            clearedFailure = failures.Remove(key);
        }
        if (clearedFailure) failureChanged(null);
        signal.Release();
        return true;
    }

    public void Forget(MeshDb db, ComposerDraftKind kind, string entityId)
    {
        var changed = false;
        lock (gate)
        {
            var key = new DraftKey(db, kind, entityId);
            if (pending.Remove(key, out var forgotten))
            {
                outstanding.Remove(forgotten.Sequence);
                staging.Remove(forgotten.Sequence);
                CompleteFlushWaiters();
            }
            latest.Remove(key);
            changed = failures.Remove(key);
        }
        if (changed)
            failureChanged(null);
        if (kind == ComposerDraftKind.Topic)
            db.ClearPendingTopicSnapshot(entityId, long.MaxValue);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task flush;
        lock (gate)
        {
            if (state == WorkerState.Disposed)
                throw new ObjectDisposedException(nameof(ComposerDraftPersistenceCoordinator));
            var target = sequence;
            flushThrough = Math.Max(flushThrough, target);
            if (!outstanding.Any(item => item <= target))
                flush = Task.CompletedTask;
            else
            {
                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                flushWaiters.Add((target, completion));
                flush = completion.Task;
            }
        }
        signal.Release();
        await AwaitFlushAsync(flush, cancellationToken).ConfigureAwait(false);
        ComposerDraftPersistenceFailure? failure;
        lock (gate)
            failure = failures.Values.FirstOrDefault();
        if (failure is not null)
            throw new InvalidOperationException(
                $"Composer draft persistence failed for {failure.Kind} '{failure.EntityId}'.",
                failure.Exception);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null)
                return new ValueTask(disposal);
            if (state == WorkerState.Disposed)
                return ValueTask.CompletedTask;
            state = WorkerState.Disposing;
            flushThrough = sequence;
            disposal = DisposeCoreAsync();
        }
        signal.Release();
        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync()
    {
        await worker.ConfigureAwait(false);
        List<MutationWaiter> abandoned;
        ComposerDraftPersistenceFailure? failure;
        lock (gate)
        {
            state = WorkerState.Disposed;
            abandoned = mutationWaiters.ToList();
            mutationWaiters.Clear();
            failure = failures.Values.FirstOrDefault();
        }
        foreach (var waiter in abandoned)
            waiter.Fail(new ObjectDisposedException(nameof(ComposerDraftPersistenceCoordinator)));

        if (failure is not null)
            throw new InvalidOperationException(
                "Composer draft persistence failed during disposal.",
                failure.Exception);
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            var batch = TakeReadyBatch(out var delay, out var stop);
            if (stop)
                return;
            if (batch.Count == 0)
            {
                if (delay is null)
                    await signal.WaitAsync().ConfigureAwait(false);
                else
                    await WaitForSignalOrDelayAsync(delay.Value).ConfigureAwait(false);
                continue;
            }

            foreach (var draft in batch)
            {
                try
                {
                    if (beforeWrite is not null)
                        await beforeWrite(
                                draft.Key.Kind,
                                draft.Key.EntityId,
                                draft.Revision)
                            .ConfigureAwait(false);
                    var result = await PersistAsync(
                            draft,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (result == ComposerDraftMutationResult.Persisted)
                        Interlocked.Increment(ref persistedWriteCount);
                    CompleteWaiter(draft, result);
                    var cleared = false;
                    lock (gate)
                    {
                        if (latest.TryGetValue(draft.Key, out var current))
                        {
                            if (current.Sequence == draft.Sequence)
                                latest.Remove(draft.Key);
                            if (SameMutation(current, draft))
                                cleared = failures.Remove(draft.Key);
                        }
                    }
                    if (cleared)
                        failureChanged(null);
                }
                catch (Exception ex)
                {
                    FailWaiter(draft, ex);
                    var failure = new ComposerDraftPersistenceFailure(
                        draft.Key.Kind,
                        draft.Key.EntityId,
                        "Draft couldn't be saved yet. Your text is still here; retry when storage is available.",
                        ex);
                    var visible = false;
                    lock (gate)
                    {
                        if (latest.TryGetValue(draft.Key, out var current)
                            && current.Sequence == draft.Sequence)
                        {
                            failures[draft.Key] = failure;
                            visible = true;
                        }
                    }
                    if (visible)
                        failureChanged(failure);
                }
                lock (gate)
                {
                    outstanding.Remove(draft.Sequence);
                    CompleteFlushWaiters();
                }
            }
        }
    }

    private Task<ComposerDraftMutationResult> PersistAsync(
        PendingDraft draft,
        CancellationToken cancellationToken)
        => draft.Key.Kind switch
        {
            ComposerDraftKind.Conversation => draft.Key.Db.TrySetConversationDraftAsync(
                draft.Key.EntityId,
                draft.Text,
                draft.Revision,
                () => IsCurrent(draft),
                cancellationToken),
            ComposerDraftKind.Topic => PersistTopicSnapshotAsync(
                draft,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(draft))
        };

    private async Task<ComposerDraftMutationResult> PersistTopicSnapshotAsync(
        PendingDraft draft,
        CancellationToken cancellationToken)
    {
        var snapshot = draft.TopicSnapshot
                       ?? MeshDb.TopicComposerSnapshot.TextOnly(draft.Text);
        var persisted = await draft.Key.Db.TrySetTopicDraftAsync(
                draft.Key.EntityId,
                snapshot,
                draft.Revision,
                () => IsCurrent(draft),
                cancellationToken)
            .ConfigureAwait(false);
        return persisted;
    }

    private void CompleteWaiter(
        PendingDraft draft,
        ComposerDraftMutationResult result)
    {
        List<MutationWaiter> waiters;
        lock (gate)
        {
            waiters = mutationWaiters
                .Where(candidate => candidate.Key == draft.Key
                                    && candidate.Revision == draft.Revision)
                .ToList();
            foreach (var waiter in waiters)
                mutationWaiters.Remove(waiter);
        }
        foreach (var waiter in waiters)
            waiter.Complete(result);
    }

    private void FailWaiter(PendingDraft draft, Exception exception)
    {
        List<MutationWaiter> waiters;
        lock (gate)
        {
            waiters = mutationWaiters
                .Where(candidate => candidate.Key == draft.Key
                                    && candidate.Revision == draft.Revision)
                .ToList();
            foreach (var waiter in waiters)
                mutationWaiters.Remove(waiter);
        }
        foreach (var waiter in waiters)
            waiter.Fail(exception);
    }

    private void CancelWaiter(MutationWaiter waiter, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!mutationWaiters.Remove(waiter))
                return;
        }
        waiter.Cancel(cancellationToken);
    }

    private bool IsCurrent(PendingDraft draft)
    {
        lock (gate)
            return latest.TryGetValue(draft.Key, out var current)
                   && current.Sequence == draft.Sequence;
    }

    private static bool SameMutation(PendingDraft left, PendingDraft right)
        => left.Revision == right.Revision
           && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
           && (left.TopicSnapshot is null && right.TopicSnapshot is null
               || left.TopicSnapshot is not null
               && right.TopicSnapshot is not null
               && MeshDb.TopicComposerSnapshotsEqual(
                   left.TopicSnapshot,
                   right.TopicSnapshot));

    private void ReplacePending(PendingDraft draft)
    {
        if (pending.TryGetValue(draft.Key, out var replaced))
            outstanding.Remove(replaced.Sequence);
        pending[draft.Key] = draft;
        outstanding.Add(draft.Sequence);
        CompleteFlushWaiters();
    }

    private void CompleteStaging(PendingDraft draft)
    {
        staging.Remove(draft.Sequence);
        if (!pending.TryGetValue(draft.Key, out var pendingDraft)
            || pendingDraft.Sequence != draft.Sequence)
            return;

        var armed = pendingDraft with { DueAt = DebounceDeadline() };
        pending[draft.Key] = armed;
        if (latest.TryGetValue(draft.Key, out var latestDraft)
            && latestDraft.Sequence == draft.Sequence)
            latest[draft.Key] = armed;
    }

    private ComposerDraftPersistenceFailure? RecordStageFailure(
        DraftKey key,
        long revision,
        Exception exception)
    {
        var failure = new ComposerDraftPersistenceFailure(
            key.Kind,
            key.EntityId,
            "Draft couldn't be staged yet. Your complete snapshot is retained for retry.",
            exception);
        lock (gate)
        {
            if (!latest.TryGetValue(key, out var current)
                || current.Revision != revision)
                return null;
            failures[key] = failure;
            return failure;
        }
    }

    private List<PendingDraft> TakeReadyBatch(
        out TimeSpan? delay,
        out bool stop)
    {
        lock (gate)
        {
            stop = state == WorkerState.Disposing && outstanding.Count == 0;
            if (stop)
            {
                CompleteFlushWaiters();
                delay = null;
                return [];
            }

            var now = timeProvider.GetTimestamp();
            var force = state == WorkerState.Disposing;
            var batch = pending.Values
                .Where(item => !staging.Contains(item.Sequence)
                               && (force
                                   || item.Sequence <= flushThrough
                                   || item.DueAt <= now))
                .OrderBy(item => item.Sequence)
                .ToList();
            foreach (var item in batch)
            {
                if (pending.TryGetValue(item.Key, out var current)
                    && current.Sequence == item.Sequence)
                    pending.Remove(item.Key);
            }
            if (batch.Count > 0)
            {
                delay = null;
                return batch;
            }

            var readyToTime = pending.Values
                .Where(item => !staging.Contains(item.Sequence))
                .ToList();
            delay = readyToTime.Count == 0
                ? null
                : TimestampDelay(now, readyToTime.Min(item => item.DueAt));
            return [];
        }
    }

    private async Task WaitForSignalOrDelayAsync(TimeSpan delay)
    {
        using var signalCancellation = new CancellationTokenSource();
        using var delayCancellation = new CancellationTokenSource();
        var wake = signal.WaitAsync(signalCancellation.Token);
        var timer = Task.Delay(delay, timeProvider, delayCancellation.Token);
        var completed = await Task.WhenAny(wake, timer).ConfigureAwait(false);
        if (ReferenceEquals(completed, wake))
        {
            delayCancellation.Cancel();
            try { await timer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        else
        {
            signalCancellation.Cancel();
            try { await wake.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private long DebounceDeadline()
    {
        var now = timeProvider.GetTimestamp();
        if (debounce == TimeSpan.Zero)
            return now;
        var timestampTicks = checked(
            debounce.Ticks * timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond);
        return checked(now + Math.Max(1, timestampTicks));
    }

    private TimeSpan TimestampDelay(long now, long deadline)
    {
        if (deadline <= now)
            return TimeSpan.Zero;
        return TimeSpan.FromTicks(checked(
            (deadline - now) * TimeSpan.TicksPerSecond / timeProvider.TimestampFrequency));
    }

    private void CompleteFlushWaiters()
    {
        for (var index = flushWaiters.Count - 1; index >= 0; index--)
        {
            var waiter = flushWaiters[index];
            if (outstanding.Any(item => item <= waiter.Target))
                continue;
            flushWaiters.RemoveAt(index);
            waiter.Completion.TrySetResult();
        }
    }

    private async Task AwaitFlushAsync(Task flush, CancellationToken cancellationToken)
    {
        try
        {
            await flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (gate)
                flushWaiters.RemoveAll(item => ReferenceEquals(item.Completion.Task, flush));
            throw;
        }
    }

    private void ThrowIfNotRunning()
    {
        if (state != WorkerState.Running)
            throw new ObjectDisposedException(nameof(ComposerDraftPersistenceCoordinator));
    }
}
