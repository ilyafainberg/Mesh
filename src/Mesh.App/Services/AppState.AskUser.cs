using System.Collections.Concurrent;
using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Ask-user surface of <see cref="AppState"/>. Owns the durable store access around the private
/// <c>activeDb</c>, the in-process <see cref="AskUserInteractionCoordinator"/>, the in-memory view of
/// pending/resolved prompts, and the outbound events that a later device-sync workstream consumes.
/// All durable work runs off the caller thread through <see cref="AskUserStore"/>.
/// </summary>
public sealed partial class AppState
{
    /// <summary>Raised after a prompt is created locally so device-sync can queue and push it.</summary>
    public event Action<AskUserPrompt>? AskUserPromptCreated;

    /// <summary>Raised after a prompt reaches its resolved state so device-sync can broadcast it.</summary>
    public event Action<AskUserPrompt>? AskUserPromptResolved;

    private readonly AskUserInteractionCoordinator askUserCoordinator = new();
    private readonly ConcurrentDictionary<string, AskUserPrompt> askUserPrompts = new(StringComparer.Ordinal);

    private MeshDb? askUserStoreDb;
    private AskUserStore? askUserStore;
    private Func<SuspendedAgentContext, AskUserPrompt, CancellationToken, Task>? askUserResumeHandler;
    private string? focusedAskUserPromptId;

    /// <summary>Deterministic suspended-context id for a prompt, so resolution can find it directly.</summary>
    private static string ContextIdFor(string promptId) => "ctx-" + promptId;

    /// <summary>The prompt id currently focused by a deep link, for highlight in the bubble UI.</summary>
    public string? FocusedAskUserPromptId => focusedAskUserPromptId;

    /// <summary>
    /// Registers the continuation used when a resolution arrives with no live in-process waiter
    /// (for example after a restart). AgentService supplies it; only one continuation ever runs per
    /// context because it is fenced by <see cref="AskUserStore.MarkContextResumedAsync"/>.
    /// </summary>
    public void SetAskUserResumeHandler(
        Func<SuspendedAgentContext, AskUserPrompt, CancellationToken, Task> handler)
        => askUserResumeHandler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>The visual bubbles to render for a Me thread, ordered oldest-first.</summary>
    public IReadOnlyList<AskUserBubbleView> AskUserPromptsFor(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return Array.Empty<AskUserBubbleView>();
        var now = DateTimeOffset.UtcNow;
        return askUserPrompts.Values
            .Where(p => string.Equals(p.ThreadId, threadId, StringComparison.Ordinal))
            .OrderBy(p => p.CreatedAt)
            .Select(p => AskUserBubbleView.From(p, now))
            .ToList();
    }

    /// <summary>Sets (or clears) the deep-link focused prompt and refreshes the UI.</summary>
    public void FocusAskUserPrompt(string? promptId)
    {
        focusedAskUserPromptId = promptId;
        NotifyChanged();
    }

    /// <summary>
    /// Resolves the AskUserStore bound to the current active profile database, rebuilding it if the
    /// account (and therefore the underlying <c>activeDb</c>) has changed. The lock is only held to
    /// select/build the instance, never across durable awaits.
    /// </summary>
    private AskUserStore? ResolveAskUserStore()
    {
        lock (profileSyncGate)
        {
            var db = activeDb;
            if (db is null) return null;
            if (!ReferenceEquals(db, askUserStoreDb))
            {
                askUserStoreDb = db;
                askUserStore = new AskUserStore(db);
            }
            return askUserStore;
        }
    }

    private void UpsertAskUserView(AskUserPrompt prompt) => askUserPrompts[prompt.PromptId] = prompt;

    /// <summary>
    /// The suspend-the-run body invoked by the internal ask_user tool. Persists the prompt and its
    /// opaque suspended context, surfaces the bubble, then awaits the first durable resolution and
    /// returns the chosen option to the model tool loop so the SAME run continues. A cancelled run
    /// leaves the prompt pending and durable rather than corrupting it.
    /// </summary>
    public async Task<string> RunAskUserToolAsync(AskUserToolRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = ResolveAskUserStore()
            ?? throw new InvalidOperationException("No active profile database for ask_user.");

        var now = DateTimeOffset.UtcNow;
        var promptId = Guid.NewGuid().ToString("n");
        var options = request.Options.ToList();
        var prompt = new AskUserPrompt(
            PromptId: promptId,
            ThreadId: request.ThreadId,
            RunId: request.RunId,
            Question: request.Question,
            Options: options,
            RecommendedIndex: request.RecommendedIndex,
            State: AskUserState.Pending,
            Selection: null,
            OriginDeviceId: LocalDeviceId(),
            ResolutionDeviceId: null,
            CreatedAt: now,
            ExpiresAt: request.ExpiresAt,
            ResolvedAt: null,
            Revision: 1,
            Version: 1);
        await store.CreateAsync(prompt, ct).ConfigureAwait(false);

        var contextId = ContextIdFor(promptId);
        var contextJson = JsonSerializer.Serialize(new AskUserResumePayload(
            request.ThreadId, request.RunId, request.TriggerLineId, request.Question));
        var context = new SuspendedAgentContext(
            contextId,
            promptId,
            request.ThreadId,
            request.RunId,
            contextJson,
            now,
            request.ExpiresAt?.AddDays(7),
            null);
        await store.SaveSuspendedContextAsync(context, ct).ConfigureAwait(false);

        UpsertAskUserView(prompt);
        AskUserPromptCreated?.Invoke(prompt);
        NotifyChanged();

        // Await the durable resolution. Cancellation (Stop button / shutdown) propagates so the run
        // aborts without resolving; the prompt remains pending and is recovered on the next launch.
        try
        {
            var wait = askUserCoordinator.WaitAsync(promptId, ct);
            if (prompt.ExpiresAt is { } deadline)
            {
                var expiry = WaitUntilAsync(deadline, ct);
                if (await Task.WhenAny(wait, expiry).ConfigureAwait(false) == expiry)
                {
                    await expiry.ConfigureAwait(false);
                    var expired = await store.ExpireAsync(promptId, ct).ConfigureAwait(false);
                    await ApplyAskUserResolvedAsync(expired, ct).ConfigureAwait(false);
                }
            }

            var resolved = await wait.ConfigureAwait(false);
            var won = await store
                .MarkContextResumedAsync(contextId, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
            if (!won)
                throw new OperationCanceledException(
                    "The ask-user run was already resumed by another execution path.");
            UpsertAskUserView(resolved);
            return BuildAskUserToolResult(resolved);
        }
        finally
        {
            askUserCoordinator.Complete(promptId);
        }
    }

    /// <summary>
    /// Resolves a prompt from a local UI click. First-writer-wins in the store; only one click can win.
    /// Returns true when this call is the one that resolved the prompt to the given option.
    /// </summary>
    public async Task<bool> ResolveAskUserPromptAsync(
        string promptId, string optionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId) || string.IsNullOrWhiteSpace(optionId)) return false;
        var store = ResolveAskUserStore();
        if (store is null) return false;

        var deviceId = LocalDeviceId() ?? "local";
        var idempotencyToken = promptId + ":" + optionId;
        var resolved = await store
            .ResolveAsync(promptId, optionId, deviceId, idempotencyToken, ct)
            .ConfigureAwait(false);

        await ApplyAskUserResolvedAsync(resolved, ct).ConfigureAwait(false);
        return resolved.State == AskUserState.Resolved
            && string.Equals(resolved.Selection, optionId, StringComparison.Ordinal)
            && string.Equals(resolved.ResolutionDeviceId, deviceId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reloads pending prompts for every Me thread from the durable store so restored bubbles appear
    /// after a restart. Idempotent: repeated calls simply refresh the in-memory view.
    /// </summary>
    public async Task RecoverPendingAskUserPromptsAsync(CancellationToken ct = default)
    {
        var store = ResolveAskUserStore();
        if (store is null) return;

        var pending = await store.ListAllPendingAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var prompt in pending)
        {
            if (prompt.ExpiresAt is { } deadline && deadline <= now)
            {
                var expired = await store.ExpireAsync(prompt.PromptId, ct).ConfigureAwait(false);
                await ApplyAskUserResolvedAsync(expired, ct).ConfigureAwait(false);
            }
            else
            {
                UpsertAskUserView(prompt);
            }
        }
        NotifyChanged();
    }

    /// <summary>Returns every unexpired pending prompt for reconnect rebroadcast.</summary>
    public async Task<IReadOnlyList<AskUserPrompt>> ListAllPendingAskUserPromptsAsync(
        CancellationToken ct = default)
    {
        var store = ResolveAskUserStore();
        if (store is null) return Array.Empty<AskUserPrompt>();
        var now = DateTimeOffset.UtcNow;
        var pending = (await store.ListAllPendingAsync(ct).ConfigureAwait(false))
            .Where(prompt => prompt.ExpiresAt is null || prompt.ExpiresAt > now)
            .ToList();
        foreach (var prompt in pending) UpsertAskUserView(prompt);
        return pending;
    }

    /// <summary>Returns resolutions won by this device for durable reconnect rebroadcast.</summary>
    public async Task<IReadOnlyList<AskUserPrompt>> ListResolvedAskUserPromptsAsync(
        string resolutionDeviceId, CancellationToken ct = default)
    {
        var store = ResolveAskUserStore();
        return store is null
            ? Array.Empty<AskUserPrompt>()
            : await store.ListResolvedByDeviceAsync(resolutionDeviceId, ct).ConfigureAwait(false);
    }

    /// <summary>Loads a single prompt into the view (used by the deep-link handler). </summary>
    public async Task<AskUserPrompt?> LoadAskUserPromptAsync(string promptId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId)) return null;
        var store = ResolveAskUserStore();
        if (store is null) return askUserPrompts.TryGetValue(promptId, out var cached) ? cached : null;
        var prompt = await store.GetAsync(promptId, ct).ConfigureAwait(false);
        if (prompt is not null)
        {
            UpsertAskUserView(prompt);
            NotifyChanged();
        }
        return prompt;
    }

    // ---- device-sync inbound seams (idempotent insert, atomic resolution) -------------------------

    /// <summary>
    /// Applies a prompt received from another device. Idempotent: an already-present prompt is a no-op
    /// that returns false; a genuinely new prompt is inserted and surfaced, returning true.
    /// </summary>
    public async Task<bool> ReceiveRemoteAskUserPromptAsync(
        AskUserPrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var store = ResolveAskUserStore();
        if (store is null) return false;

        var existing = await store.GetAsync(prompt.PromptId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            UpsertAskUserView(existing);
            NotifyChanged();
            return false;
        }

        var created = await store.CreateAsync(prompt, ct).ConfigureAwait(false);
        UpsertAskUserView(created);
        AskUserPromptCreated?.Invoke(created);
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Applies a resolution received from another device using the same atomic first-writer-wins store
    /// path as a local click. Returns true when this resolution is the one that resolved the prompt.
    /// </summary>
    public async Task<bool> ReceiveRemoteAskUserResolutionAsync(
        string promptId,
        string optionId,
        string resolutionDeviceId,
        string idempotencyToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId) || string.IsNullOrWhiteSpace(optionId)) return false;
        var store = ResolveAskUserStore();
        if (store is null) return false;

        var device = string.IsNullOrWhiteSpace(resolutionDeviceId) ? "remote" : resolutionDeviceId;
        var token = string.IsNullOrWhiteSpace(idempotencyToken) ? promptId + ":" + optionId : idempotencyToken;
        var resolved = await store
            .ResolveAsync(promptId, optionId, device, token, ct)
            .ConfigureAwait(false);

        await ApplyAskUserResolvedAsync(resolved, ct).ConfigureAwait(false);
        return resolved.State == AskUserState.Resolved
            && string.Equals(resolved.Selection, optionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shared post-resolution application: refresh the view, raise the outbound event, hand the result
    /// to a live in-process waiter, or otherwise take the durable exactly-once resume path.
    /// </summary>
    private async Task ApplyAskUserResolvedAsync(AskUserPrompt resolved, CancellationToken ct)
    {
        UpsertAskUserView(resolved);
        if (resolved.State == AskUserState.Resolved)
            AskUserPromptResolved?.Invoke(resolved);
        NotifyChanged();

        // A live waiter (the suspended run) consumes the context itself once it wakes.
        if (askUserCoordinator.TrySignalResolved(resolved)) return;

        var store = ResolveAskUserStore();
        if (store is null) return;
        var contextId = ContextIdFor(resolved.PromptId);
        var context = await store.GetSuspendedContextAsync(contextId, ct).ConfigureAwait(false);
        if (context is null) return;

        // Consume the context exactly once regardless of outcome so it can never resume twice.
        var won = await store.MarkContextResumedAsync(contextId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (won && askUserResumeHandler is { } handler)
        {
            await handler(context, resolved, ct).ConfigureAwait(false);
        }
    }

    private static string BuildAskUserToolResult(AskUserPrompt resolved)
    {
        if (resolved.State == AskUserState.Resolved)
        {
            var option = resolved.Options.FirstOrDefault(o =>
                string.Equals(o.Id, resolved.Selection, StringComparison.Ordinal));
            var text = $"The owner selected \"{option?.Title ?? resolved.Selection}\" (id: {resolved.Selection}).";
            if (!string.IsNullOrWhiteSpace(option?.Description))
                text += " " + option!.Description;
            return text;
        }
        if (resolved.State == AskUserState.Expired)
            return "The owner did not answer in time; the prompt expired. Proceed without their decision.";
        return "The ask-user prompt was cancelled without an answer. Proceed without their decision.";
    }

    private static async Task WaitUntilAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(
                    remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining,
                    ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Opaque payload persisted inside the suspended context to recover a run after restart.</summary>
    private sealed record AskUserResumePayload(
        string ThreadId, string RunId, string? TriggerLineId, string Question);
}
