using System.Diagnostics;
using System.Text;

namespace Mesh.App.Services;

/// <summary>
/// Records privacy-safe managed operation and wait ownership when work exceeds its expected duration.
/// Names passed here must be fixed technical identifiers, never user content or protocol payloads.
/// </summary>
internal static class ManagedOperationDiagnostics
{
    private static readonly AsyncLocal<Operation?> Current = new();
    private static long nextId;

    internal static string CurrentOperation
        => Current.Value is { } operation
            ? $"{operation.Name}#{operation.Id}"
            : "untracked";

    internal static IDisposable Begin(
        string name,
        TimeSpan? stallThreshold = null,
        [System.Runtime.CompilerServices.CallerMemberName] string callSite = "")
    {
        var parent = Current.Value;
        var operation = new Operation(
            Interlocked.Increment(ref nextId),
            TechnicalName(name),
            TechnicalName(callSite),
            parent,
            Stopwatch.StartNew(),
            Environment.CurrentManagedThreadId);
        Current.Value = operation;
        operation.Watch(stallThreshold ?? TimeSpan.FromSeconds(5));
        return new OperationScope(operation);
    }

    internal static IDisposable Wait(
        string resource,
        Func<string?> owner,
        TimeSpan? stallThreshold = null,
        [System.Runtime.CompilerServices.CallerMemberName] string callSite = "")
    {
        ArgumentNullException.ThrowIfNull(owner);
        var wait = new WaitState(
            TechnicalName(resource),
            TechnicalName(callSite),
            owner,
            Stopwatch.StartNew(),
            CaptureStack(),
            Environment.CurrentManagedThreadId);
        var operation = Current.Value;
        operation?.SetWait(wait);
        wait.Watch(operation, stallThreshold ?? TimeSpan.FromSeconds(5));
        return new WaitScope(operation, wait);
    }

    private static string TechnicalName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var builder = new StringBuilder(Math.Min(value.Length, 96));
        foreach (var character in value)
        {
            if (builder.Length == 96) break;
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':'
                ? character
                : '_');
        }
        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string CaptureStack()
    {
        var frames = new StackTrace(fNeedFileInfo: false).GetFrames();
        if (frames is null) return "unavailable";
        return string.Join(
            "<-",
            frames.Take(48).Select(frame =>
            {
                var method = frame.GetMethod();
                return TechnicalName(
                    $"{method?.DeclaringType?.FullName}.{method?.Name}");
            }));
    }

    private static async Task DelayAndRecordAsync(
        TimeSpan threshold,
        Func<bool> isComplete,
        string category,
        Func<string> message)
    {
        try
        {
            await Task.Delay(threshold).ConfigureAwait(false);
            if (!isComplete())
                RuntimeDiagnostics.Current?.RecordEvent(category, message());
        }
        catch
        {
            // Diagnostics must never fault an unobserved task or affect the operation being measured.
        }
    }

    private sealed class Operation(
        long id,
        string name,
        string callSite,
        Operation? parent,
        Stopwatch elapsed,
        int managedThreadId)
    {
        private WaitState? wait;
        private int complete;

        internal long Id { get; } = id;
        internal string Name { get; } = name;
        internal Operation? Parent { get; } = parent;
        internal bool IsComplete => Volatile.Read(ref complete) != 0;

        internal void SetWait(WaitState? value) => Volatile.Write(ref wait, value);

        internal void Complete() => Interlocked.Exchange(ref complete, 1);

        internal void Watch(TimeSpan threshold)
        {
            _ = DelayAndRecordAsync(
                threshold,
                () => IsComplete,
                "managed-stall",
                () =>
                {
                    var currentWait = Volatile.Read(ref wait);
                    var chain = Parent is null
                        ? $"{Name}#{Id}"
                        : $"{Parent.Name}#{Parent.Id}->{Name}#{Id}";
                    return $"operation={Name};operation_id={Id};call_site={callSite};"
                           + $"elapsed_ms={elapsed.ElapsedMilliseconds};managed_thread={managedThreadId};"
                           + $"chain={chain};wait={(currentWait?.Description ?? "none")}";
                });
        }
    }

    private sealed class WaitState(
        string resource,
        string callSite,
        Func<string?> owner,
        Stopwatch elapsed,
        string stack,
        int managedThreadId)
    {
        private int complete;

        internal bool IsComplete => Volatile.Read(ref complete) != 0;
        internal string Description
            => $"{resource};owner={TechnicalName(owner())};wait_ms={elapsed.ElapsedMilliseconds}";

        internal void Complete() => Interlocked.Exchange(ref complete, 1);

        internal void Watch(Operation? operation, TimeSpan threshold)
        {
            _ = DelayAndRecordAsync(
                threshold,
                () => IsComplete,
                "managed-wait-stall",
                () => $"resource={resource};owner={TechnicalName(owner())};"
                      + $"operation={(operation is null ? "untracked" : $"{operation.Name}#{operation.Id}")};"
                      + $"call_site={callSite};wait_ms={elapsed.ElapsedMilliseconds};"
                      + $"managed_thread={managedThreadId};stack={stack}");
        }
    }

    private sealed class OperationScope(Operation operation) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            operation.Complete();
            if (ReferenceEquals(Current.Value, operation))
                Current.Value = operation.Parent;
        }
    }

    private sealed class WaitScope(Operation? operation, WaitState wait) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            wait.Complete();
            operation?.SetWait(null);
        }
    }
}
