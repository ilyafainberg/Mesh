using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Minimal seam that moves blocking store work off the caller (UI) thread.
/// Kept deliberately small: it exists so that stores never touch SQLite synchronously
/// on the calling thread and so the off-thread behaviour is testable behaviourally
/// (by observing the executing thread) without inspecting source.
/// </summary>
public interface IStoreScheduler
{
    /// <summary>Runs <paramref name="work"/> off the caller thread and returns its result.</summary>
    Task<T> RunAsync<T>(Func<T> work, CancellationToken ct);

    /// <summary>Runs <paramref name="work"/> off the caller thread.</summary>
    Task RunAsync(Action work, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IStoreScheduler"/> that dispatches to the thread pool via
/// <see cref="Task.Run(System.Action, CancellationToken)"/>. Because MeshDb hands out a
/// dedicated SQLite connection per thread, running each unit of work on a pool thread is
/// safe and keeps the caller thread free.
/// </summary>
public sealed class TaskRunStoreScheduler : IStoreScheduler
{
    public static readonly TaskRunStoreScheduler Shared = new();

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct) => Task.Run(work, ct);

    public Task RunAsync(Action work, CancellationToken ct) => Task.Run(work, ct);
}

/// <summary>
/// Contract for durable ask-user prompt storage. All state transitions are fenced so that
/// concurrent resolvers produce exactly-once behaviour: the first writer wins and all callers
/// receive the current prompt state rather than a success/failure ambiguity.
/// Every method executes its SQLite work off the caller thread via an <see cref="IStoreScheduler"/>.
/// </summary>
public interface IAskUserStore
{
    /// <summary>
    /// Persists a new prompt. Validates non-blank identity/question fields, 2-5 unique
    /// options with non-blank ids/titles, recommended-index bounds and sane counters.
    /// </summary>
    Task<AskUserPrompt> CreateAsync(AskUserPrompt prompt, CancellationToken ct = default);

    Task<AskUserPrompt?> GetAsync(string promptId, CancellationToken ct = default);

    Task<IReadOnlyList<AskUserPrompt>> ListPendingAsync(
        string threadId, CancellationToken ct = default);

    /// <summary>
    /// Atomically resolves the prompt. The underlying transaction first expires an
    /// at-or-past-deadline pending prompt, then resolves only a still-pending row via a
    /// <c>WHERE state='pending'</c> fence. Returns the current row regardless of who won;
    /// callers check <c>Selection</c>/<c>ResolutionDeviceId</c> to confirm their resolution
    /// was applied. Re-issuing the same <paramref name="idempotencyToken"/> after a win
    /// returns the winner. The <paramref name="selection"/> must match one of the prompt's
    /// option ids and prompt/device/idempotency values must be non-blank.
    /// </summary>
    Task<AskUserPrompt> ResolveAsync(
        string promptId,
        string selection,
        string resolutionDeviceId,
        string idempotencyToken,
        CancellationToken ct = default);

    Task<AskUserPrompt> ExpireAsync(string promptId, CancellationToken ct = default);

    Task<AskUserPrompt> CancelAsync(string promptId, CancellationToken ct = default);

    Task SaveSuspendedContextAsync(
        SuspendedAgentContext context, CancellationToken ct = default);

    Task<SuspendedAgentContext?> GetSuspendedContextAsync(
        string contextId, CancellationToken ct = default);

    /// <summary>
    /// Marks the context resumed exactly once. Returns true for the single caller whose
    /// fenced UPDATE (resumed_at IS NULL and unexpired) affected the row, false otherwise.
    /// </summary>
    Task<bool> MarkContextResumedAsync(
        string contextId, DateTimeOffset resumedAt, CancellationToken ct = default);
}

/// <summary>
/// SQLite-backed implementation of <see cref="IAskUserStore"/>.
/// Uses the per-thread connection managed by <see cref="MeshDb"/> and defers all SQLite
/// work to an <see cref="IStoreScheduler"/> so nothing runs on the caller thread.
/// </summary>
public sealed class AskUserStore(MeshDb db, IStoreScheduler? scheduler = null) : IAskUserStore
{
    private readonly IStoreScheduler _scheduler = scheduler ?? TaskRunStoreScheduler.Shared;

    public Task<AskUserPrompt> CreateAsync(AskUserPrompt prompt, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(prompt);
            prompt.EnsureValidForCreate();
            db.InsertAskUserPrompt(prompt);
            return prompt;
        }, ct);

    public Task<AskUserPrompt?> GetAsync(string promptId, CancellationToken ct = default)
        => _scheduler.RunAsync(() => db.GetAskUserPrompt(promptId), ct);

    public Task<IReadOnlyList<AskUserPrompt>> ListPendingAsync(
        string threadId, CancellationToken ct = default)
        => _scheduler.RunAsync(() => db.ListPendingAskUserPrompts(threadId), ct);

    public Task<AskUserPrompt> ResolveAsync(
        string promptId,
        string selection,
        string resolutionDeviceId,
        string idempotencyToken,
        CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            RequireNonBlank(promptId, nameof(promptId));
            RequireNonBlank(selection, nameof(selection));
            RequireNonBlank(resolutionDeviceId, nameof(resolutionDeviceId));
            RequireNonBlank(idempotencyToken, nameof(idempotencyToken));

            var prompt = db.GetAskUserPrompt(promptId)
                ?? throw new InvalidOperationException($"Ask-user prompt '{promptId}' not found.");
            if (!prompt.Options.Any(o => string.Equals(o.Id, selection, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"Selection '{selection}' does not match any option id.", nameof(selection));

            return db.ResolveAskUserPrompt(promptId, selection, resolutionDeviceId, idempotencyToken);
        }, ct);

    public Task<AskUserPrompt> ExpireAsync(string promptId, CancellationToken ct = default)
        => _scheduler.RunAsync(() => db.ExpireAskUserPrompt(promptId), ct);

    public Task<AskUserPrompt> CancelAsync(string promptId, CancellationToken ct = default)
        => _scheduler.RunAsync(() => db.CancelAskUserPrompt(promptId), ct);

    public Task SaveSuspendedContextAsync(
        SuspendedAgentContext context, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(context);
            context.EnsureValid();
            db.SaveSuspendedContext(context);
        }, ct);

    public Task<SuspendedAgentContext?> GetSuspendedContextAsync(
        string contextId, CancellationToken ct = default)
        => _scheduler.RunAsync(() => db.GetSuspendedContext(contextId), ct);

    public Task<bool> MarkContextResumedAsync(
        string contextId, DateTimeOffset resumedAt, CancellationToken ct = default)
        => _scheduler.RunAsync(() => db.MarkContextResumed(contextId, resumedAt), ct);

    private static void RequireNonBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} must be non-blank.", name);
    }
}
