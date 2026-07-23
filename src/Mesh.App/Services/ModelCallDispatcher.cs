namespace Mesh.App.Services;

/// <summary>
/// Starts provider work on the thread pool so synchronous setup before a provider's first await
/// can never block the UI dispatcher.
/// </summary>
internal static class ModelCallDispatcher
{
    public static Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Task.Run(call, ct);
    }

    public static IProgress<T>? MarshalProgress<T>(
        IProgress<T>? progress,
        SynchronizationContext? context)
        => progress is null || context is null
            ? progress
            : new ContextProgress<T>(progress, context);

    private sealed class ContextProgress<T>(
        IProgress<T> inner,
        SynchronizationContext context) : IProgress<T>
    {
        public void Report(T value)
        {
            if (SynchronizationContext.Current == context)
            {
                inner.Report(value);
                return;
            }
            context.Post(static state =>
            {
                var (progress, item) = ((IProgress<T>, T))state!;
                progress.Report(item);
            }, (inner, value));
        }
    }
}
