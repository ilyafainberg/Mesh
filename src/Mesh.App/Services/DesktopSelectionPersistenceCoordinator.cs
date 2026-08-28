namespace Mesh.App.Services;

internal sealed class DesktopSelectionPersistenceCoordinator : IAsyncDisposable
{
    private sealed record Snapshot(
        MeshDb Db,
        string? TopicId,
        string? ConversationKey);

    private readonly object gate = new();
    private readonly ProfilePersistenceCoordinator<Snapshot> worker;

    public DesktopSelectionPersistenceCoordinator()
    {
        worker = new ProfilePersistenceCoordinator<Snapshot>(
            PersistAsync,
            TimeSpan.FromMilliseconds(50));
    }

    public void SetTopic(MeshDb db, string? topicId)
    {
        ArgumentNullException.ThrowIfNull(db);
        lock (gate)
        {
            db.StageLastDesktopTopicId(topicId);
            Schedule(db);
        }
    }

    public void SetConversation(MeshDb db, string? conversationKey)
    {
        ArgumentNullException.ThrowIfNull(db);
        lock (gate)
        {
            db.StageLastDesktopConversationKey(conversationKey);
            Schedule(db);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => worker.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => worker.DisposeAsync();

    private void Schedule(MeshDb db)
        => worker.Schedule(new Snapshot(
            db,
            db.GetLastDesktopTopicId(),
            db.GetLastDesktopConversationKey()));

    private static async Task PersistAsync(
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        using var operation = ManagedOperationDiagnostics.Begin("desktop-selection.persist");
        try
        {
            await snapshot.Db.ExecuteDurableWriteAsync(
                () =>
                {
                    snapshot.Db.SetLastDesktopTopicId(snapshot.TopicId);
                    snapshot.Db.SetLastDesktopConversationKey(snapshot.ConversationKey);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordException(
                "desktop-selection-persistence",
                exception);
            throw;
        }
    }
}
