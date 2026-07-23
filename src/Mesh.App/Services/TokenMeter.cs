namespace Mesh.App.Services;

/// <summary>
/// Sink that model providers report token usage to after each upstream call. It folds the counts
/// into the active identity's running total via <see cref="AppState.AddTokens"/>, which resets to
/// zero whenever the selected model changes (the counter is only meaningful per model). Tokens are
/// the primary cost currency, so this drives the live counter shown in the UI.
///
/// It also supports an optional ambient attribution scope (see <see cref="BeginScope"/>): while a
/// scope is active on the current async flow, usage is additionally reported to that scope's sink.
/// This lets the guest-reply path attribute a contact's token spend to that contact without racing
/// across concurrent inbound replies (the scope flows with <see cref="AsyncLocal{T}"/>).
/// </summary>
public sealed class TokenMeter(AppState state)
{
    internal sealed record UsageContext(
        string ModelKey,
        SynchronizationContext? DispatchContext,
        Action<long, long>? ScopedSink);

    private readonly AsyncLocal<Action<long, long>?> scopedSink = new();
    private readonly AsyncLocal<SynchronizationContext?> dispatchContext = new();

    /// <summary>Records usage for the currently selected model. Zero or negative counts are ignored.</summary>
    public void Record(long promptTokens, long completionTokens)
        => Record(promptTokens, completionTokens, CaptureContext());

    internal UsageContext CaptureContext()
        => new(state.CurrentModelKey(), dispatchContext.Value, scopedSink.Value);

    internal void Record(
        long promptTokens,
        long completionTokens,
        UsageContext context)
    {
        if (promptTokens <= 0 && completionTokens <= 0) return;
        var p = Math.Max(0, promptTokens);
        var c = Math.Max(0, completionTokens);
        void Apply()
        {
            state.AddTokens(context.ModelKey, p, c);
            context.ScopedSink?.Invoke(p, c);
        }
        if (context.DispatchContext is null
            || SynchronizationContext.Current == context.DispatchContext)
            Apply();
        else
            context.DispatchContext.Post(
                static callback => ((Action)callback!).Invoke(),
                (Action)Apply);
    }

    /// <summary>
    /// Begins an ambient attribution scope for the current async flow. Usage recorded until the
    /// returned handle is disposed is also forwarded to <paramref name="sink"/>. Used to attribute
    /// guest-reply tokens to the requesting contact.
    /// </summary>
    public IDisposable BeginScope(Action<long, long> sink)
    {
        var previous = scopedSink.Value;
        scopedSink.Value = sink;
        return new Scope(this, previous);
    }

    internal IDisposable BeginDispatchContext(SynchronizationContext? context)
    {
        var previous = dispatchContext.Value;
        dispatchContext.Value = context;
        return new DispatchScope(this, previous);
    }

    private sealed class Scope(TokenMeter meter, Action<long, long>? previous) : IDisposable
    {
        public void Dispose() => meter.scopedSink.Value = previous;
    }

    private sealed class DispatchScope(
        TokenMeter meter,
        SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => meter.dispatchContext.Value = previous;
    }
}
