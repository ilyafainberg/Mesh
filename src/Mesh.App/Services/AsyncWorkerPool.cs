using System.Threading.Channels;

namespace Mesh.App.Services;

/// <summary>Leases a bounded set of reusable workers to concurrent callers.</summary>
internal sealed class AsyncWorkerPool<T>
{
    private readonly Channel<T> available;

    public AsyncWorkerPool(IEnumerable<T> workers)
    {
        var items = workers.ToArray();
        if (items.Length == 0) throw new ArgumentException("At least one worker is required.", nameof(workers));
        available = Channel.CreateBounded<T>(new BoundedChannelOptions(items.Length)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        foreach (var worker in items)
            if (!available.Writer.TryWrite(worker))
                throw new InvalidOperationException("Could not initialize the worker pool.");
    }

    public async Task<TResult> UseAsync<TResult>(
        Func<T, Task<TResult>> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        var worker = await available.Reader.ReadAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(worker).ConfigureAwait(false);
        }
        finally
        {
            if (!available.Writer.TryWrite(worker))
                throw new InvalidOperationException("Could not return a worker to the pool.");
        }
    }
}
